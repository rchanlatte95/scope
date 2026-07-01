namespace Scope.Proxy;

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Serilog;

/// <summary>
/// <see cref="IWireMeter"/> implementation for the OpenAI Responses API
/// (<c>/v1/responses</c>). Parses usage from <c>response.completed</c> SSE events and
/// non-streaming JSON response bodies, applying the Scope disjoint-bucket token mapping:
/// <c>InputTokens = max(0, input_tokens - cached_tokens)</c>,
/// <c>CacheReadTokens = min(cached_tokens, input_tokens)</c>,
/// <c>CacheCreationTokens = null</c> (OpenAI has no cache-write surcharge),
/// <c>OutputTokens = output_tokens</c> (reasoning_tokens already included; NOT added
/// separately — that would double-count).
///
/// <para>Capture tee: when <paramref name="captureFilePath"/> is set, each proxied
/// response body is appended as a labeled block to that file for operator inspection.
/// The tee never affects parsing or forwarding.</para>
/// </summary>
internal sealed class OpenAiResponsesMeter(string? captureFilePath) : IWireMeter
{
    private static readonly string[] Endpoints = ["/v1/responses"];

    /// <inheritdoc/>
    public bool IsMetered(string endpoint) =>
        endpoint.Equals("/v1/responses", StringComparison.Ordinal);

    /// <inheritdoc/>
    public string[] KnownEndpoints => Endpoints;

    /// <inheritdoc/>
    public IWireSseScanner CreateSseScanner() => new OpenAiResponsesSseScanner(captureFilePath);

    /// <inheritdoc/>
    public ResponseUsage ParseNonStreaming(BoundedTeeBuffer responseTee, string? contentEncoding)
    {
        if (responseTee.Truncated || responseTee.TotalBytes == 0)
            return new ResponseUsage { Failed = true };

        // Optional capture tee for operator inspection (non-fatal; never affects row).
        if (captureFilePath is not null)
            TryCaptureBytes(captureFilePath, responseTee.ToArray(), streaming: false);

        try
        {
            var bytes = Decode(responseTee.ToArray(), contentEncoding);
            using var doc = JsonDocument.Parse(bytes);
            return ParseResponseObject(doc.RootElement);
        }
        catch
        {
            return new ResponseUsage { Failed = true };
        }
    }

    /// <inheritdoc/>
    public RequestSummary SummarizeRequest(BoundedTeeBuffer requestTee) => default;

    /// <summary>
    /// Maps an OpenAI <c>response</c> JSON object to a <see cref="ResponseUsage"/>,
    /// applying the Scope disjoint-bucket token mapping. Returns
    /// <see cref="ResponseUsage.Failed"/> = true when the <c>usage</c> block is absent
    /// or mandatory token fields cannot be read.
    /// </summary>
    internal static ResponseUsage ParseResponseObject(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out var usageEl))
            return new ResponseUsage { Failed = true };

        if (!TryReadLong(usageEl, "input_tokens", out var inputRaw)
            || !TryReadLong(usageEl, "output_tokens", out var outputTokens))
            return new ResponseUsage { Failed = true };

        // cached_tokens lives inside input_tokens_details; absent means 0.
        long cachedTokens = 0;
        if (usageEl.TryGetProperty("input_tokens_details", out var details))
            TryReadLong(details, "cached_tokens", out cachedTokens);

        // Clamp: bad upstream data must not produce cached_tokens > input_tokens (F4).
        var cacheRead = Math.Min(cachedTokens, inputRaw);
        var inputNet  = Math.Max(0L, inputRaw - cacheRead);

        // Collect function_call call_ids from output[] (canonical source).
        var toolUseIds = new List<string>();
        if (response.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type)
                    && string.Equals(type.GetString(), "function_call", StringComparison.Ordinal)
                    && item.TryGetProperty("call_id", out var callIdEl)
                    && callIdEl.GetString() is { } cid)
                {
                    toolUseIds.Add(cid);
                }
            }
        }

        return new ResponseUsage
        {
            Model         = ReadString(response, "model"),
            MessageId     = ReadString(response, "id"),
            StopReason    = ReadString(response, "status"),
            InputTokens   = inputNet,
            OutputTokens  = outputTokens,
            CacheCreationTokens = null,   // OpenAI has no cache-write surcharge
            CacheReadTokens     = cacheRead,
            ToolUseIds    = toolUseIds,
        };
    }

    /// <summary>
    /// Appends <paramref name="bytes"/> to the capture file as a labeled block.
    /// Never throws; capture failures are logged at debug level and never affect forwarding.
    /// </summary>
    internal static void TryCaptureBytes(string path, byte[] bytes, bool streaming)
    {
        try
        {
            var label = Encoding.UTF8.GetBytes(
                $"\n=== {(streaming ? "STREAM" : "RESPONSE")} {DateTime.UtcNow:o} length={bytes.Length} ===\n");
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            fs.Write(label);
            fs.Write(bytes);
            fs.Write("\n"u8);
        }
        catch (Exception ex)
        {
            Log.Debug("OpenAI capture write failed (non-fatal): {Message}", ex.Message);
        }
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool TryReadLong(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var prop)
               && prop.ValueKind == JsonValueKind.Number
               && prop.TryGetInt64(out value);
    }

    private static byte[] Decode(byte[] bytes, string? contentEncoding)
    {
        if (string.IsNullOrEmpty(contentEncoding)
            || contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
            return bytes;

        using var source  = new MemoryStream(bytes);
        using Stream decoder = contentEncoding.ToLowerInvariant() switch
        {
            "gzip"    => new GZipStream(source, CompressionMode.Decompress),
            "deflate" => new ZLibStream(source, CompressionMode.Decompress),
            "br"      => new BrotliStream(source, CompressionMode.Decompress),
            _         => throw new NotSupportedException(
                             $"Unsupported content-encoding '{contentEncoding}'."),
        };
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>
/// Incremental SSE scanner for OpenAI <c>/v1/responses</c> streaming responses.
/// Fed raw response bytes chunk-by-chunk during the proxy copy loop.
///
/// <para>Usage comes exclusively from the terminal <c>response.completed</c> event's
/// <c>response.usage</c> object. <see cref="SawStart"/> is set true only after that event
/// is successfully parsed, so streams truncated before <c>response.completed</c> yield
/// <see cref="SawStart"/> = false and the <see cref="Forwarder"/> abandons the meter row
/// (it evaluates <c>Failed || !SawStart</c>).</para>
///
/// <para>Function-call <c>call_id</c> values are collected from
/// <c>response.output_item.done</c> events (eagerly, as they stream in) and from
/// <c>response.completed</c>'s <c>response.output[]</c> array (canonical); duplicates
/// are suppressed.</para>
///
/// <para>Never throws from <see cref="Feed"/> or <see cref="Dispose"/>; any internal
/// failure sets <see cref="Failed"/> and the meter row is abandoned, forwarding
/// unaffected.</para>
/// </summary>
internal sealed class OpenAiResponsesSseScanner : IWireSseScanner
{
    private const int MaxLineBytes = 1024 * 1024;

    private readonly MemoryStream _line = new();
    private readonly MemoryStream? _captureBuffer;
    private readonly string? _captureFilePath;
    private bool _lineOverflow;
    private string? _currentEvent;
    private bool _disposed;

    public bool Failed  { get; private set; }

    /// <summary>
    /// True when the terminal <c>response.completed</c> event was observed and its
    /// usage parsed successfully. The <see cref="Forwarder"/> evaluates
    /// <c>Failed || !SawStart</c>; false here means no wire_calls row is written.
    /// </summary>
    public bool SawStart { get; private set; }

    public string? Model       { get; private set; }
    public string? MessageId   { get; private set; }
    public string? StopReason  { get; private set; }
    public long? InputTokens   { get; private set; }
    public long? OutputTokens  { get; private set; }

    /// <summary>Always null: OpenAI has no cache-write surcharge.</summary>
    public long? CacheCreationTokens => null;

    public long? CacheReadTokens { get; private set; }
    public List<string> ToolUseIds { get; } = [];

    public OpenAiResponsesSseScanner(string? captureFilePath)
    {
        _captureFilePath = captureFilePath;
        if (captureFilePath is not null)
            _captureBuffer = new MemoryStream();
    }

    /// <inheritdoc/>
    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (_disposed || Failed) return;
        try
        {
            _captureBuffer?.Write(chunk);

            foreach (var b in chunk)
            {
                if (b == (byte)'\n')
                {
                    if (!_lineOverflow) ProcessLine();
                    _line.SetLength(0);
                    _lineOverflow = false;
                }
                else if (_line.Length < MaxLineBytes)
                {
                    _line.WriteByte(b);
                }
                else
                {
                    _lineOverflow = true;
                }
            }
        }
        catch
        {
            Failed = true;
        }
    }

    private void ProcessLine()
    {
        var span = _line.GetBuffer().AsSpan(0, (int)_line.Length);
        // Strip optional trailing \r (CRLF line endings).
        if (span.Length > 0 && span[^1] == (byte)'\r')
            span = span[..^1];

        if (span.Length == 0)
        {
            // Blank line terminates an SSE event block.
            _currentEvent = null;
            return;
        }

        var line = Encoding.UTF8.GetString(span);
        if (line.StartsWith("event:", StringComparison.Ordinal))
        {
            _currentEvent = line["event:".Length..].Trim();
            return;
        }

        if (!line.StartsWith("data:", StringComparison.Ordinal))
            return;

        var eventName = _currentEvent;
        if (eventName is not ("response.completed" or "response.output_item.done"))
            return;

        var data = line["data:".Length..].TrimStart(' ');
        try
        {
            using var doc = JsonDocument.Parse(data);
            HandleEvent(eventName, doc.RootElement);
        }
        catch
        {
            // A parse failure on response.completed means usage is untrustworthy.
            // output_item.done parse failures are non-fatal: call_ids are also
            // available (canonically) from response.completed's output[].
            if (string.Equals(eventName, "response.completed", StringComparison.Ordinal))
                Failed = true;
        }
    }

    private void HandleEvent(string eventName, JsonElement root)
    {
        switch (eventName)
        {
            case "response.completed":
                if (!root.TryGetProperty("response", out var response))
                {
                    Failed = true;
                    return;
                }
                var usage = OpenAiResponsesMeter.ParseResponseObject(response);
                if (usage.Failed)
                {
                    Failed = true;
                    return;
                }
                SawStart    = true;
                Model       = usage.Model;
                MessageId   = usage.MessageId;
                StopReason  = usage.StopReason;
                InputTokens = usage.InputTokens;
                OutputTokens = usage.OutputTokens;
                CacheReadTokens = usage.CacheReadTokens;
                // Merge call_ids from response.completed's output[] (canonical).
                foreach (var id in usage.ToolUseIds)
                {
                    if (!ToolUseIds.Contains(id))
                        ToolUseIds.Add(id);
                }
                break;

            case "response.output_item.done":
                // Eagerly collect call_ids; duplicates suppressed against canonical set.
                if (root.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var type)
                    && string.Equals(type.GetString(), "function_call", StringComparison.Ordinal)
                    && item.TryGetProperty("call_id", out var callIdEl)
                    && callIdEl.GetString() is { } cid
                    && !ToolUseIds.Contains(cid))
                {
                    ToolUseIds.Add(cid);
                }
                break;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_captureFilePath is not null && _captureBuffer is { Length: > 0 })
                OpenAiResponsesMeter.TryCaptureBytes(
                    _captureFilePath, _captureBuffer.ToArray(), streaming: true);
        }
        catch { /* capture failures must never propagate */ }
        _captureBuffer?.Dispose();
        _line.Dispose();
        GC.SuppressFinalize(this);
    }
}
