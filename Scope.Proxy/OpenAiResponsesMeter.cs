namespace Scope.Proxy;

using Serilog;

/// <summary>
/// Phase A: minimal <see cref="IWireMeter"/> stub for the OpenAI Responses API
/// (<c>/v1/responses</c>). Routes and tees traffic for <c>--provider openai</c>; does NOT
/// parse usage or write wire_calls rows yet. Full parsing (usage extraction, call_id
/// collection, token mapping) is Phase B, built against REAL captured traffic after the
/// blocking gates G-baseurl and G-callid pass (T-2026-06-29-08).
///
/// <para>Capture mode: when <see cref="ProxyServerOptions.CaptureFilePath"/> is set (or the
/// <c>SCOPE_PROXY_CAPTURE_FILE</c> environment variable), each proxied response body is
/// appended as a labeled block to that file. This is the primary deliverable of Phase A:
/// giving an operator a capture proxy to collect real Codex wire traffic for use as
/// fixtures in Phase B.</para>
///
/// <para>To capture Codex traffic:</para>
/// <code>
/// scope-proxy --provider openai --capture-file /tmp/codex_capture.txt [--port 5136] [--token ...]
/// # Then set the Codex base URL to: http://127.0.0.1:{port}/t/{token}
/// # and run: codex exec "..."
/// </code>
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
    public IWireSseScanner CreateSseScanner() => new OpenAiCaptureScanner(captureFilePath);

    /// <inheritdoc/>
    public ResponseUsage ParseNonStreaming(BoundedTeeBuffer responseTee, string? contentEncoding)
    {
        // Phase A: capture the raw body for later fixture recording; return Failed so no
        // wire_calls row is written until the Phase B parser is built and verified.
        if (captureFilePath is not null && !responseTee.Truncated && responseTee.TotalBytes > 0)
            TryCaptureBytes(captureFilePath, responseTee.ToArray(), streaming: false);
        return new ResponseUsage { Failed = true };
    }

    /// <inheritdoc/>
    public RequestSummary SummarizeRequest(BoundedTeeBuffer requestTee) => default;

    /// <summary>
    /// Appends <paramref name="bytes"/> to the capture file as a labeled block.
    /// Never throws; capture failures are logged at debug level and never affect forwarding.
    /// </summary>
    internal static void TryCaptureBytes(string path, byte[] bytes, bool streaming)
    {
        try
        {
            var label = System.Text.Encoding.UTF8.GetBytes(
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
}

/// <summary>
/// Phase A SSE capture scanner: accumulates streamed /v1/responses bytes in memory and
/// flushes them to the capture file on <see cref="Dispose"/>. Returns
/// <see cref="SawStart"/> = false so the Forwarder's usage block sets
/// <c>Failed = true</c> and no wire_calls row is written.
/// </summary>
internal sealed class OpenAiCaptureScanner(string? captureFilePath) : IWireSseScanner
{
    private readonly System.IO.MemoryStream _buf = new();
    private bool _disposed;

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (_disposed) return;
        try { _buf.Write(chunk); }
        catch { /* capture failure must never break forwarding */ }
    }

    /// <summary>False: the scanner did not encounter any internal error.</summary>
    public bool Failed => false;

    /// <summary>
    /// False: Phase A does not parse the stream so the terminal usage event is never observed.
    /// The Forwarder evaluates <c>Failed || !SawStart</c> -> true -> usage.Failed -> no row.
    /// </summary>
    public bool SawStart => false;

    // All usage fields null (Phase A: no parse).
    public string? Model => null;
    public string? MessageId => null;
    public string? StopReason => null;
    public long? InputTokens => null;
    public long? OutputTokens => null;
    public long? CacheCreationTokens => null;
    public long? CacheReadTokens => null;
    public List<string> ToolUseIds { get; } = [];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (captureFilePath is not null && _buf.Length > 0)
            OpenAiResponsesMeter.TryCaptureBytes(captureFilePath, _buf.ToArray(), streaming: true);
        _buf.Dispose();
        GC.SuppressFinalize(this);
    }
}
