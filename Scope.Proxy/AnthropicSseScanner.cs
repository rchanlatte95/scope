namespace Scope.Proxy;

using System.Text;
using System.Text.Json;

/// <summary>
/// Incremental SSE line scanner for Anthropic /v1/messages streaming responses.
/// Fed raw response bytes chunk-by-chunk during the proxy copy loop; never
/// buffers the whole body and never throws (any internal failure flips
/// <see cref="Failed"/> and the meter row is abandoned, forwarding unaffected).
///
/// Reads exactly three event types:
///   message_start        -> model, message id, input/cache token counts
///   content_block_start  -> tool_use content block ids
///   message_delta        -> cumulative output_tokens, stop_reason
/// </summary>
internal sealed class AnthropicSseScanner : IWireSseScanner
{
    private const int MaxLineBytes = 1024 * 1024;

    private readonly MemoryStream _line = new();
    private bool _lineOverflow;
    private string? _currentEvent;

    public bool Failed { get; private set; }
    public bool SawMessageStart { get; private set; }
    public string? Model { get; private set; }
    public string? MessageId { get; private set; }
    public string? StopReason { get; private set; }
    public long? InputTokens { get; private set; }
    public long? OutputTokens { get; private set; }
    public long? CacheCreationTokens { get; private set; }
    public long? CacheReadTokens { get; private set; }
    public List<string> ToolUseIds { get; } = [];

    /// <summary>Feeds one chunk of raw SSE bytes. Never throws.</summary>
    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (Failed) return;
        try
        {
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
        if (span.Length > 0 && span[^1] == (byte)'\r')
            span = span[..^1];

        if (span.Length == 0)
        {
            // Blank line terminates an SSE event.
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
        if (eventName is not ("message_start" or "content_block_start" or "message_delta"))
            return;

        var data = line["data:".Length..].TrimStart(' ');
        try
        {
            using var doc = JsonDocument.Parse(data);
            HandleEvent(eventName, doc.RootElement);
        }
        catch
        {
            // A relevant event we cannot parse means the meter cannot be trusted.
            Failed = true;
        }
    }

    private void HandleEvent(string eventName, JsonElement root)
    {
        switch (eventName)
        {
            case "message_start":
                if (root.TryGetProperty("message", out var message))
                {
                    SawMessageStart = true;
                    if (message.TryGetProperty("id", out var id)) MessageId = id.GetString();
                    if (message.TryGetProperty("model", out var model)) Model = model.GetString();
                    if (message.TryGetProperty("usage", out var usage)) ReadUsage(usage);
                }
                break;

            case "content_block_start":
                if (root.TryGetProperty("content_block", out var block)
                    && block.TryGetProperty("type", out var type)
                    && string.Equals(type.GetString(), "tool_use", StringComparison.Ordinal)
                    && block.TryGetProperty("id", out var toolId)
                    && toolId.GetString() is { } tid)
                {
                    ToolUseIds.Add(tid);
                }
                break;

            case "message_delta":
                if (root.TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("stop_reason", out var stop)
                    && stop.ValueKind == JsonValueKind.String)
                {
                    StopReason = stop.GetString();
                }
                if (root.TryGetProperty("usage", out var deltaUsage)) ReadUsage(deltaUsage);
                break;
        }
    }

    private void ReadUsage(JsonElement usage)
    {
        if (TryReadLong(usage, "input_tokens", out var input)) InputTokens = input;
        if (TryReadLong(usage, "output_tokens", out var output)) OutputTokens = output;
        if (TryReadLong(usage, "cache_creation_input_tokens", out var cc)) CacheCreationTokens = cc;
        if (TryReadLong(usage, "cache_read_input_tokens", out var cr)) CacheReadTokens = cr;
    }

    private static bool TryReadLong(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var prop)
               && prop.ValueKind == JsonValueKind.Number
               && prop.TryGetInt64(out value);
    }

    // -------------------------------------------------------------------------
    // IWireSseScanner implementation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Provider-agnostic alias for <see cref="SawMessageStart"/>. True when the
    /// Anthropic <c>message_start</c> event (which carries usage) was observed.
    /// The <see cref="Forwarder"/> uses this via the <see cref="IWireSseScanner"/>
    /// interface to avoid a hard dependency on the Anthropic-specific property name.
    /// </summary>
    public bool SawStart => SawMessageStart;

    /// <summary>No-op: AnthropicSseScanner holds no unmanaged resources.</summary>
    public void Dispose() { /* no-op */ GC.SuppressFinalize(this); }
}
