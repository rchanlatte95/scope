namespace Scope.Proxy;

using Microsoft.AspNetCore.Http;
using Scope.Core.Telemetry;
using Serilog;
using System.Buffers;
using System.Diagnostics;

/// <summary>
/// Passthrough forwarder: meter, never gateway. Streams both directions
/// chunk-by-chunk through a single shared connection pool. Forwarded bytes are
/// identical minus hop-by-hop headers; no retries, no caching, no mutation.
/// Client cancellation aborts the upstream request immediately.
/// </summary>
internal sealed class Forwarder : IDisposable
{
    /// <summary>Headers that terminate at each hop and must not be forwarded.</summary>
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host",
    };

    private readonly HttpMessageInvoker _client;
    private readonly Uri _upstreamBase;
    private readonly WireCallDb _db;
    private readonly BudgetEnforcer _budget;
    private readonly IWireMeter _meter;
    private long _driftCount;
    private long _recordedCount;
    private long _budgetRefusedCount;
    private bool _disposed;

    public Forwarder(string upstreamBaseUrl, string telemetryDbPath, BudgetEnforcer? budget = null,
        IWireMeter? meter = null)
    {
        _upstreamBase = new Uri(upstreamBaseUrl, UriKind.Absolute);
        _db = new WireCallDb(telemetryDbPath);
        _budget = budget ?? new BudgetEnforcer();
        _meter = meter ?? new AnthropicWireMeter();
        _client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            EnableMultipleHttp2Connections = true,
        }, disposeHandler: true);
    }

    /// <summary>Count of forwarded requests to endpoints the meter does not understand.</summary>
    public long DriftCount => Interlocked.Read(ref _driftCount);

    /// <summary>Count of wire_calls rows successfully persisted.</summary>
    public long RecordedCount => Interlocked.Read(ref _recordedCount);

    /// <summary>Count of new model calls refused because their session's token budget was reached.</summary>
    public long BudgetRefusedCount => Interlocked.Read(ref _budgetRefusedCount);

    /// <summary>
    /// Forwards one request to the upstream API, streaming the response back and
    /// metering known Anthropic endpoints on the side. Metering failures never
    /// affect forwarding.
    /// </summary>
    public async Task ForwardAsync(HttpContext context, string targetPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ct = context.RequestAborted;

        // Strip an optional /sb/{session}/{budget} prefix a budgeted run embeds in its base URL. The
        // remaining path is the real upstream endpoint; the scope (when present) drives enforcement.
        // This keeps forwarding byte-identical: only the proxy-local path framing carries the budget,
        // never the forwarded request, so the content-free posture and verbatim body are preserved.
        var (scope, upstreamPath) = ParseBudgetScope(targetPath);
        var endpoint = "/" + upstreamPath.TrimStart('/');
        var endpointPathOnly = endpoint;

        var metered = HttpMethods.IsPost(context.Request.Method)
                      && _meter.IsMetered(endpointPathOnly);

        // Budget gate (the proxy's only enforcement): before forwarding a NEW metered model call,
        // refuse with a terminal, non-retryable response when this session has reached its budget.
        // Non-metered endpoints (token counting, models, batches) are never gated.
        if (metered && scope is { } s && _budget.ShouldRefuse(s))
        {
            Interlocked.Increment(ref _budgetRefusedCount);
            await WriteBudgetRefusalAsync(context, s, ct).ConfigureAwait(false);
            return;
        }

        if (!metered && !IsKnownEndpoint(endpointPathOnly, _meter.KnownEndpoints))
        {
            Interlocked.Increment(ref _driftCount);
            Log.Debug("Unknown endpoint forwarded blindly: {Method} {Endpoint}", context.Request.Method, endpointPathOnly);
        }

        // Read Claude Code's three ephemeral, content-free per-spawn identifier headers (T-2026-06-19-01
        // Phase 1). The proxy still FORWARDS them upstream byte-identically (they are NOT hop-by-hop and
        // BuildUpstreamRequest is untouched); here we additionally RECORD them so a wire row can later be
        // attributed to an interactive session or a spawned subagent. Null-tolerant: a missing header (a
        // non-CC caller, or a parent-only request) yields null and NEVER throws into the forwarding path.
        var ccSessionId = ReadOptionalHeader(context, "X-Claude-Code-Session-Id");
        var ccAgentId = ReadOptionalHeader(context, "X-Claude-Code-Agent-Id");
        var ccParentAgentId = ReadOptionalHeader(context, "X-Claude-Code-Parent-Agent-Id");

        var startedAtUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var requestTee = new BoundedTeeBuffer();

        using var upstreamRequest = BuildUpstreamRequest(context, endpoint, requestTee, metered);

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await _client.SendAsync(upstreamRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // Client went away before upstream answered; nothing to forward.
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("Upstream unreachable for {Endpoint}: {Message}", endpointPathOnly, ex.Message);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"type":"error","error":{"type":"proxy_upstream_unreachable","message":"scope-proxy could not reach the upstream API."}}""",
                ct).ConfigureAwait(false);
            return;
        }

        using (upstreamResponse)
        {
            var status = (int)upstreamResponse.StatusCode;
            context.Response.StatusCode = status;
            CopyResponseHeaders(upstreamResponse, context.Response);

            var contentType = upstreamResponse.Content.Headers.ContentType?.MediaType;
            var contentEncoding = upstreamResponse.Content.Headers.ContentEncoding.FirstOrDefault();
            var isSse = string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

            IWireSseScanner? scanner = null;
            BoundedTeeBuffer? responseTee = null;
            if (metered)
            {
                if (isSse && string.IsNullOrEmpty(contentEncoding)) scanner = _meter.CreateSseScanner();
                else responseTee = new BoundedTeeBuffer();
            }

            long responseBytes = 0;
            long? ttftMs = null;
            var responseStarted = false;
            try
            {
                var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var upstreamStreamScope = upstreamStream.ConfigureAwait(false);
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    int read;
                    while ((read = await upstreamStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        ttftMs ??= stopwatch.ElapsedMilliseconds;
                        responseBytes += read;
                        try
                        {
                            scanner?.Feed(buffer.AsSpan(0, read));
                            responseTee?.Feed(buffer.AsSpan(0, read));
                        }
                        catch { /* metering must never break forwarding */ }

                        responseStarted = true;
                        await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client aborted mid-stream. Disposing upstreamResponse aborts upstream.
                scanner?.Dispose();
                return;
            }
            catch (Exception ex) when (responseStarted)
            {
                // Upstream died mid-body. Abort so the client sees truncation,
                // never a fake-complete response.
                Log.Warning("Upstream stream failed mid-body for {Endpoint}: {Message}", endpointPathOnly, ex.Message);
                scanner?.Dispose();
                context.Abort();
                return;
            }

            stopwatch.Stop();

            if (metered)
                RecordMeterRow(endpointPathOnly, status, isSse, startedAtUtc, ttftMs,
                    stopwatch.ElapsedMilliseconds, requestTee, responseBytes, scanner, responseTee, contentEncoding, scope,
                    ccSessionId, ccAgentId, ccParentAgentId);

            scanner?.Dispose();
        }
    }

    /// <summary>Null-tolerant single-header read: returns the header value or null when absent. Never throws.</summary>
    private static string? ReadOptionalHeader(HttpContext context, string name) =>
        context.Request.Headers.TryGetValue(name, out var v) ? v.ToString() : null;

    private void RecordMeterRow(
        string endpoint, int status, bool isSse, DateTime startedAtUtc, long? ttftMs, long totalMs,
        BoundedTeeBuffer requestTee, long responseBytes,
        IWireSseScanner? scanner, BoundedTeeBuffer? responseTee, string? contentEncoding, BudgetScope? scope,
        string? ccSessionId, string? ccAgentId, string? ccParentAgentId)
    {
        try
        {
            var summary = _meter.SummarizeRequest(requestTee);
            ResponseUsage? usage = null;
            if (status is >= 200 and < 300)
            {
                usage = scanner is not null
                    ? new ResponseUsage
                    {
                        Failed = scanner.Failed || !scanner.SawStart,
                        Model = scanner.Model,
                        MessageId = scanner.MessageId,
                        StopReason = scanner.StopReason,
                        InputTokens = scanner.InputTokens,
                        OutputTokens = scanner.OutputTokens,
                        CacheCreationTokens = scanner.CacheCreationTokens,
                        CacheReadTokens = scanner.CacheReadTokens,
                        ToolUseIds = scanner.ToolUseIds,
                    }
                    : _meter.ParseNonStreaming(responseTee!, contentEncoding);
            }

            // Fold this call's total wire tokens into the session tally so the NEXT new call is gated
            // against an up-to-date cumulative total. Counts the same input+output+cache the meter sums;
            // only successful, parseable usage contributes (a failed/garbage row adds nothing).
            if (scope is { } s && usage is { Failed: false })
                _budget.AddTokens(s.SessionId, TotalWireTokens(usage));

            var record = AnthropicMeter.BuildRecord(
                endpoint, status, isSse, startedAtUtc, ttftMs, totalMs,
                requestTee.TotalBytes, responseBytes, summary, usage,
                ccSessionId, ccAgentId, ccParentAgentId);

            if (record is null)
            {
                Log.Warning("Meter row abandoned for {Endpoint} (unparseable usage); forwarding was unaffected", endpoint);
                return;
            }

            if (_db.TryInsert(record))
                Interlocked.Increment(ref _recordedCount);
        }
        catch (Exception ex)
        {
            Log.Warning("Meter row abandoned for {Endpoint}: {Message}", endpoint, ex.Message);
        }
    }

    /// <summary>
    /// Splits an optional <c>sb/{session}/{budget}/</c> prefix off the target path. When present and
    /// well-formed it yields the <see cref="BudgetScope"/> and the remaining upstream path; otherwise
    /// the scope is null and the path is returned unchanged. The prefix is proxy-local routing only and
    /// is never forwarded, so the upstream request is byte-identical for budgeted and unbudgeted runs.
    /// </summary>
    internal static (BudgetScope? Scope, string UpstreamPath) ParseBudgetScope(string targetPath)
    {
        var trimmed = targetPath.TrimStart('/');
        const string marker = "sb/";
        if (!trimmed.StartsWith(marker, StringComparison.Ordinal))
            return (null, targetPath);

        var rest = trimmed[marker.Length..];
        var firstSlash = rest.IndexOf('/');
        if (firstSlash <= 0) return (null, targetPath); // no session segment

        var session = rest[..firstSlash];
        var afterSession = rest[(firstSlash + 1)..];
        var secondSlash = afterSession.IndexOf('/');
        if (secondSlash <= 0) return (null, targetPath); // no budget segment / empty budget

        var budgetText = afterSession[..secondSlash];
        var upstream = afterSession[(secondSlash + 1)..];
        if (!long.TryParse(budgetText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var budget) || budget <= 0)
            return (null, targetPath); // malformed budget: forward unbudgeted rather than crash

        return (new BudgetScope(session, budget), "/" + upstream);
    }

    /// <summary>Total wire tokens for a call: input + output + cache_creation + cache_read (what the meter sums).</summary>
    private static long TotalWireTokens(ResponseUsage usage)
        => (usage.InputTokens ?? 0) + (usage.OutputTokens ?? 0)
           + (usage.CacheCreationTokens ?? 0) + (usage.CacheReadTokens ?? 0);

    /// <summary>
    /// Writes the TERMINAL, non-retryable budget-refusal response (decision 1). Status 403 Forbidden
    /// with an Anthropic-shaped error of type <c>scope_budget_exhausted</c>: 403 is outside the set
    /// the Anthropic SDK / Claude Code retry (they retry only 408, 409, 429, and >= 500), so the
    /// harness stops cleanly instead of looping against the wall. The body is content-free.
    /// </summary>
    private static async Task WriteBudgetRefusalAsync(HttpContext context, BudgetScope scope, CancellationToken ct)
    {
        Log.Information(
            "Budget refusal: session reached its token budget ({Budget}); refusing new model call (403 terminal).",
            scope.Budget);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            """{"type":"error","error":{"type":"scope_budget_exhausted","message":"scope-proxy: the run's token budget has been reached; no further model calls are permitted. This run is being stopped."}}""",
            ct).ConfigureAwait(false);
    }

    private HttpRequestMessage BuildUpstreamRequest(HttpContext context, string endpoint, BoundedTeeBuffer requestTee, bool metered)
    {
        var upstreamUri = new Uri(_upstreamBase, endpoint + context.Request.QueryString.Value);
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstreamUri)
        {
            Version = System.Net.HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        var hasBody = context.Request.ContentLength is > 0
                      || context.Request.Headers.ContainsKey("Transfer-Encoding");
        if (hasBody)
            request.Content = new StreamContent(new TeeReadStream(context.Request.Body, requestTee));
        else if (context.Request.ContentLength == 0)
            request.Content = new ByteArrayContent([]);

        var dropExtra = ConnectionListedHeaders(context.Request.Headers.Connection);
        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key) || dropExtra.Contains(header.Key))
                continue;
            // On metered endpoints we pin the UPSTREAM Accept-Encoding to identity so the
            // response body reaches the meter uncompressed (see remark below); skip the
            // caller's value here and re-add it after the loop.
            if (metered && string.Equals(header.Key, "Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;
            var values = header.Value.ToArray();
            if (!request.Headers.TryAddWithoutValidation(header.Key, values))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, values);
        }

        // Pin upstream Accept-Encoding to identity on metered /v1/messages.
        //
        // Why: real Claude Code (node/undici) advertises "gzip, deflate, zstd". The upstream
        // honors that and gzips even SSE streams, and may pick zstd, which the meter cannot
        // decode at all (no zstd dependency by design). Compressed SSE also defeats the
        // incremental scanner. Forcing identity guarantees the scanner sees parseable bytes
        // and removes the zstd failure mode without adding dependencies. The proxy is the
        // client of upstream, so this only changes the proxy<->upstream hop; the original
        // caller still receives a body it advertised (identity is implicitly acceptable per
        // RFC 9110 unless explicitly refused). Trade-off: the metered hop forgoes response
        // compression, which is acceptable for the small fraction of traffic that is
        // /v1/messages and is the cost of reliable wire metering.
        if (metered)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

        return request;
    }

    private static void CopyResponseHeaders(HttpResponseMessage upstream, HttpResponse response)
    {
        var dropExtra = upstream.Headers.Connection is { Count: > 0 }
            ? new HashSet<string>(upstream.Headers.Connection, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var header in upstream.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key) || dropExtra?.Contains(header.Key) == true)
                continue;
            response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in upstream.Content.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
                continue;
            response.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private static HashSet<string> ConnectionListedHeaders(Microsoft.Extensions.Primitives.StringValues connection)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in connection)
        {
            if (string.IsNullOrEmpty(value)) continue;
            foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(token);
        }
        return set;
    }

    private static bool IsKnownEndpoint(string endpoint, string[] knownEndpoints) =>
        knownEndpoints.Any(known => endpoint.Equals(known, StringComparison.Ordinal)
                                    || endpoint.StartsWith(known + "/", StringComparison.Ordinal));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Read-through stream that counts and tees bytes into a bounded buffer as the
/// upstream client consumes the request body. Forwarding sees the exact bytes.
/// </summary>
internal sealed class TeeReadStream(Stream source, BoundedTeeBuffer tee) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0) tee.Feed(buffer.Span[..read]);
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (read > 0) tee.Feed(buffer.AsSpan(offset, read));
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
