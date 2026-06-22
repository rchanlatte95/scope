namespace Scope.Core.Cron.Launch;

using System.Text.Json;

/// <summary>
/// Content-free distillation of a Codex JSONL event stream: trace-linkage identifiers, usage
/// rollups, and any error/turn-failure text for diagnostics. No agent message content or command
/// output is retained here; raw content stays only in local artifacts.
/// </summary>
public sealed record CodexEventSummary(
    string? ThreadId,
    HarnessUsage? Usage,
    string? ErrorDetail);

/// <summary>
/// Parses Codex <c>--json</c> JSONL output, reading only stable envelope fields and usage blocks.
/// Unknown or malformed lines are skipped so a host format drift never crashes a run; the process
/// exit code remains the source of truth for success or failure.
/// </summary>
public static class CodexEventStreamParser
{
    /// <summary>
    /// Scans JSONL lines and extracts thread id, last-seen usage totals, and the first error or
    /// turn-failure detail. Uses <see cref="JsonDocument"/> (no reflection) for AOT friendliness.
    /// </summary>
    public static CodexEventSummary Parse(string jsonl)
    {
        if (string.IsNullOrWhiteSpace(jsonl))
            return new CodexEventSummary(null, null, null);

        string? threadId = null;
        HarnessUsage? usage = null;
        string? errorDetail = null;

        foreach (var rawLine in jsonl.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '{')
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                threadId ??= ReadThreadId(root);

                var lineUsage = ReadUsage(root);
                if (lineUsage is not null)
                    usage = lineUsage;

                errorDetail ??= ReadErrorDetail(root);
            }
        }

        return new CodexEventSummary(threadId, usage, errorDetail);
    }

    private static string? ReadThreadId(JsonElement root)
    {
        if (TryGetString(root, "thread_id", out var direct))
            return direct;
        if (TryGetString(root, "threadId", out var camel))
            return camel;
        if (root.TryGetProperty("thread", out var thread) && thread.ValueKind == JsonValueKind.Object
            && TryGetString(thread, "id", out var nested))
            return nested;
        return null;
    }

    private static HarnessUsage? ReadUsage(JsonElement root)
    {
        if (!TryFindUsageObject(root, out var usage))
            return null;

        var input = ReadLong(usage, "input_tokens") ?? ReadLong(usage, "inputTokens") ?? ReadLong(usage, "prompt_tokens");
        var cached = ReadLong(usage, "cached_input_tokens") ?? ReadLong(usage, "cachedInputTokens") ?? ReadLong(usage, "cache_read_input_tokens");
        var output = ReadLong(usage, "output_tokens") ?? ReadLong(usage, "outputTokens") ?? ReadLong(usage, "completion_tokens");
        var total = ReadLong(usage, "total_tokens") ?? ReadLong(usage, "totalTokens");

        if (input is null && cached is null && output is null && total is null)
            return null;

        if (total is null && (input is not null || output is not null))
            total = (input ?? 0) + (output ?? 0);

        return new HarnessUsage(input, cached, output, total);
    }

    private static bool TryFindUsageObject(JsonElement root, out JsonElement usage)
    {
        if (root.TryGetProperty("usage", out usage) && usage.ValueKind == JsonValueKind.Object)
            return true;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("usage", out usage)
                && usage.ValueKind == JsonValueKind.Object)
                return true;
        }

        usage = default;
        return false;
    }

    private static string? ReadErrorDetail(JsonElement root)
    {
        var type = TryGetString(root, "type", out var t) ? t : null;
        var isErrorLike = type is not null
            && (type.Contains("error", StringComparison.OrdinalIgnoreCase)
                || type.Contains("failed", StringComparison.OrdinalIgnoreCase));

        if (TryGetString(root, "error", out var errorString))
            return errorString;

        if (root.TryGetProperty("error", out var errorObj) && errorObj.ValueKind == JsonValueKind.Object
            && TryGetString(errorObj, "message", out var errorMessage))
            return errorMessage;

        if (isErrorLike && TryGetString(root, "message", out var typedMessage))
            return typedMessage;

        return null;
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
