namespace Scope.Proxy;

using Scope.Core.Telemetry;
using Scope.Core.Utilities;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// Bounded byte buffer used to tee request bodies and non-streaming response
/// bodies for post-copy metering. Bytes beyond the cap are counted but not
/// retained (the buffer is then marked truncated and parsing is skipped).
/// </summary>
internal sealed class BoundedTeeBuffer(int capBytes = BoundedTeeBuffer.DefaultCap)
{
    public const int DefaultCap = 2 * 1024 * 1024;

    private readonly MemoryStream _buffer = new();

    public long TotalBytes { get; private set; }
    public bool Truncated { get; private set; }

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        TotalBytes += chunk.Length;
        if (Truncated) return;

        var room = capBytes - (int)_buffer.Length;
        if (chunk.Length <= room)
        {
            _buffer.Write(chunk);
        }
        else
        {
            if (room > 0) _buffer.Write(chunk[..room]);
            Truncated = true;
        }
    }

    public byte[] ToArray() => _buffer.ToArray();
}

/// <summary>
/// Summary extracted from a /v1/messages request body: content-free hashes and
/// counts only. All-null when the body was truncated or unparseable; request
/// summary failures never abandon the meter row (they are enrichment, not usage).
/// </summary>
internal readonly record struct RequestSummary(
    string? SystemHash,
    string? FirstUserHash,
    int? MessageCount);

/// <summary>
/// Result of parsing the response side of a metered call.
/// <see cref="Failed"/> true means the meter row must be abandoned.
/// </summary>
internal sealed class ResponseUsage
{
    public bool Failed { get; init; }
    public string? Model { get; init; }
    public string? MessageId { get; init; }
    public string? StopReason { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CacheCreationTokens { get; init; }
    public long? CacheReadTokens { get; init; }
    public List<string> ToolUseIds { get; init; } = [];
}

/// <summary>
/// Anthropic metering helpers: request summarization, non-streaming response
/// parsing, and wire_calls record assembly. Pure functions over teed bytes;
/// no method here may throw into the forwarding path.
/// </summary>
internal static class AnthropicMeter
{
    /// <summary>
    /// Extracts content-free hashes and message count from a request body.
    /// Returns an empty summary on truncation or parse failure.
    /// </summary>
    public static RequestSummary SummarizeRequest(BoundedTeeBuffer requestTee)
    {
        if (requestTee.Truncated || requestTee.TotalBytes == 0)
            return default;

        try
        {
            using var doc = JsonDocument.Parse(requestTee.ToArray());
            var root = doc.RootElement;

            string? systemHash = null;
            if (root.TryGetProperty("system", out var system))
                systemHash = HashOfRawJson(system);

            string? firstUserHash = null;
            int? messageCount = null;
            if (root.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array)
            {
                messageCount = messages.GetArrayLength();
                foreach (var msg in messages.EnumerateArray())
                {
                    if (msg.TryGetProperty("role", out var role)
                        && string.Equals(role.GetString(), "user", StringComparison.Ordinal)
                        && msg.TryGetProperty("content", out var content))
                    {
                        firstUserHash = HashOfRawJson(content);
                        break;
                    }
                }
            }

            return new RequestSummary(systemHash, firstUserHash, messageCount);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Parses a non-streaming /v1/messages response body (decompressing
    /// gzip/deflate/br when needed). Returns Failed=true when the body cannot
    /// be parsed, which abandons the meter row.
    /// </summary>
    public static ResponseUsage ParseMessageResponse(BoundedTeeBuffer responseTee, string? contentEncoding)
    {
        if (responseTee.Truncated)
            return new ResponseUsage { Failed = true };

        try
        {
            var bytes = Decode(responseTee.ToArray(), contentEncoding);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;

            var usage = new ResponseUsage
            {
                Model = ReadString(root, "model"),
                MessageId = ReadString(root, "id"),
                StopReason = ReadString(root, "stop_reason"),
                InputTokens = ReadUsageLong(root, "input_tokens"),
                OutputTokens = ReadUsageLong(root, "output_tokens"),
                CacheCreationTokens = ReadUsageLong(root, "cache_creation_input_tokens"),
                CacheReadTokens = ReadUsageLong(root, "cache_read_input_tokens"),
            };

            if (root.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var type)
                        && string.Equals(type.GetString(), "tool_use", StringComparison.Ordinal)
                        && block.TryGetProperty("id", out var id)
                        && id.GetString() is { } tid)
                    {
                        usage.ToolUseIds.Add(tid);
                    }
                }
            }

            return usage;
        }
        catch
        {
            return new ResponseUsage { Failed = true };
        }
    }

    /// <summary>
    /// Assembles a wire_calls record. Returns null (abandon) when a successful
    /// response yielded no trustworthy usage. Non-2xx responses are recorded
    /// with status and latency but null usage fields (error bodies carry no
    /// usage block; that is not a parse failure).
    /// </summary>
    public static WireCallRecord? BuildRecord(
        string endpoint,
        int status,
        bool stream,
        DateTime startedAtUtc,
        long? ttftMs,
        long totalMs,
        long requestBytes,
        long responseBytes,
        RequestSummary requestSummary,
        ResponseUsage? usage,
        string? ccSessionId = null,
        string? ccAgentId = null,
        string? ccParentAgentId = null)
    {
        var success = status is >= 200 and < 300;
        if (success && (usage is null || usage.Failed))
            return null;

        var u = success ? usage : null;
        return new WireCallRecord(
            Id: Guid.GetGuid7String(),
            StartedAt: startedAtUtc.ToString("o"),
            TtftMs: ttftMs,
            TotalMs: totalMs,
            Endpoint: endpoint,
            Status: status,
            Stream: stream,
            Model: u?.Model,
            MessageId: u?.MessageId,
            StopReason: u?.StopReason,
            InputTokens: u?.InputTokens,
            OutputTokens: u?.OutputTokens,
            CacheCreationTokens: u?.CacheCreationTokens,
            CacheReadTokens: u?.CacheReadTokens,
            ToolUseIdsJson: u is { ToolUseIds.Count: > 0 }
                ? JsonSerializer.Serialize(u.ToolUseIds, ProxyJsonContext.Default.ListString)
                : null,
            SystemHash: requestSummary.SystemHash,
            FirstUserHash: requestSummary.FirstUserHash,
            MessageCount: requestSummary.MessageCount,
            RequestBytes: requestBytes,
            ResponseBytes: responseBytes,
            CcSessionId: ccSessionId,
            CcAgentId: ccAgentId,
            CcParentAgentId: ccParentAgentId);
    }

    private static string HashOfRawJson(JsonElement element) =>
        Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(element.GetRawText())));

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static long? ReadUsageLong(JsonElement root, string name) =>
        root.TryGetProperty("usage", out var usage)
        && usage.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.Number
        && prop.TryGetInt64(out var value)
            ? value
            : null;

    private static byte[] Decode(byte[] bytes, string? contentEncoding)
    {
        if (string.IsNullOrEmpty(contentEncoding) ||
            contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            return bytes;
        }

        using var source = new MemoryStream(bytes);
        using Stream decoder = contentEncoding.ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(source, CompressionMode.Decompress),
            "deflate" => new ZLibStream(source, CompressionMode.Decompress),
            "br" => new BrotliStream(source, CompressionMode.Decompress),
            _ => throw new NotSupportedException($"Unsupported content-encoding '{contentEncoding}'."),
        };
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }
}
