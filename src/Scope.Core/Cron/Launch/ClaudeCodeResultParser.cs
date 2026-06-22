namespace Scope.Core.Cron.Launch;

using System.Text.Json;

/// <summary>
/// Content-free distillation of a Claude Code <c>--output-format json</c> result object:
/// trace-linkage session id, a usage rollup, the run cost, and any error flag/subtype for
/// diagnostics. No agent message content (the <c>result</c> field) is retained here; the raw
/// result JSON stays only in local artifacts.
/// </summary>
public sealed record ClaudeCodeResultSummary(
    string? SessionId,
    HarnessUsage? Usage,
    double? TotalCostUsd,
    bool IsError,
    string? ErrorDetail);

/// <summary>
/// Parses the single Claude Code <c>-p --output-format json</c> result object, reading only stable
/// content-free envelope fields: <c>session_id</c>, the <c>usage</c> token block, <c>total_cost_usd</c>,
/// and the <c>is_error</c>/<c>subtype</c> diagnostics. The free-form <c>result</c> message body is
/// never read. Uses <see cref="JsonDocument"/> (no reflection) for AOT friendliness. Malformed or
/// absent JSON yields an empty summary so a host format drift never crashes a run; the process exit
/// code remains the source of truth for success or failure.
/// </summary>
public static class ClaudeCodeResultParser
{
    /// <summary>
    /// Parses a single Claude Code result JSON object. When the payload is empty, not an object, or
    /// not valid JSON, an empty (no-session, no-usage) summary is returned with <c>IsError=false</c>.
    /// </summary>
    public static ClaudeCodeResultSummary Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ClaudeCodeResultSummary(null, null, null, false, null);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new ClaudeCodeResultSummary(null, null, null, false, null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ClaudeCodeResultSummary(null, null, null, false, null);

            var sessionId = ReadSessionId(root);
            var usage = ReadUsage(root);
            var cost = ReadCost(root);
            var (isError, errorDetail) = ReadError(root);

            return new ClaudeCodeResultSummary(sessionId, usage, cost, isError, errorDetail);
        }
    }

    private static string? ReadSessionId(JsonElement root)
    {
        if (TryGetString(root, "session_id", out var snake))
            return snake;
        if (TryGetString(root, "sessionId", out var camel))
            return camel;
        return null;
    }

    private static HarnessUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var input = ReadLong(usage, "input_tokens") ?? ReadLong(usage, "inputTokens");
        // Claude reports cache reads and cache creation separately; sum them into the cached bucket.
        var cacheRead = ReadLong(usage, "cache_read_input_tokens") ?? ReadLong(usage, "cacheReadInputTokens");
        var cacheCreation = ReadLong(usage, "cache_creation_input_tokens") ?? ReadLong(usage, "cacheCreationInputTokens");
        var cached = SumNullable(cacheRead, cacheCreation);
        var output = ReadLong(usage, "output_tokens") ?? ReadLong(usage, "outputTokens");

        if (input is null && cached is null && output is null)
            return null;

        long? total = null;
        if (input is not null || output is not null)
            total = (input ?? 0) + (output ?? 0);

        return new HarnessUsage(input, cached, output, total);
    }

    private static double? ReadCost(JsonElement root)
    {
        if (root.TryGetProperty("total_cost_usd", out var snake) && snake.ValueKind == JsonValueKind.Number
            && snake.TryGetDouble(out var snakeValue))
            return snakeValue;
        if (root.TryGetProperty("totalCostUsd", out var camel) && camel.ValueKind == JsonValueKind.Number
            && camel.TryGetDouble(out var camelValue))
            return camelValue;
        return null;
    }

    private static (bool IsError, string? Detail) ReadError(JsonElement root)
    {
        var isError = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;

        string? detail = null;
        if (TryGetString(root, "subtype", out var subtype)
            && !string.Equals(subtype, "success", StringComparison.OrdinalIgnoreCase))
            detail = subtype;

        if (TryGetString(root, "error", out var errorString))
            detail = errorString;
        else if (root.TryGetProperty("error", out var errorObj) && errorObj.ValueKind == JsonValueKind.Object
            && TryGetString(errorObj, "message", out var errorMessage))
            detail = errorMessage;

        return (isError, detail);
    }

    private static long? SumNullable(long? a, long? b)
    {
        if (a is null && b is null)
            return null;
        return (a ?? 0) + (b ?? 0);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                value = text;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out var value))
            return value;
        return null;
    }
}
