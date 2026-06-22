namespace Scope.Core.Telemetry.Ingestion;

using System.Text.Json;
using Scope.Core.App;
using System.Globalization;

/// <summary>
/// Translates Codex lifecycle hook payloads into Scope's canonical hook events.
/// Config/event names are verified against the current Codex manual's hooks section
/// (project-local .codex/hooks.json, SessionStart/PostToolUse/UserPromptSubmit,
/// command/commandWindows handlers). Payload field parsing is intentionally tolerant and backed by
/// Scope contract samples, not captured Codex stdin fixtures; every optional field degrades to null.
/// </summary>
public static class CodexHookAdapter
{
    private const int SummaryMaxLength = 500;
    private const string CodexThreadIdEnv = "CODEX_THREAD_ID";

    private static readonly string[] CapabilityFields =
        ["thread_id", "session_id", "conversation_id", "hook_event_name", "event", "tool_name", "tool", "duration_ms", "status"];

    public static readonly IReadOnlyList<string> CapabilityUniverse =
        [.. CapabilityFields, "tool_input", "input", "tool_output", "output"];

    public static ToolCallEvent? ParsePostToolUse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var toolName = GetString(root, "tool_name")
                ?? GetString(root, "tool")
                ?? GetString(root, "name")
                ?? "unknown";

            var input = TryGetAny(root, out var inputElement, "tool_input", "input", "arguments")
                ? Truncate(inputElement.GetRawText())
                : null;
            var output = TryGetAny(root, out var outputElement, "tool_output", "output", "result")
                ? Truncate(ExtractSummary(outputElement))
                : null;

            return new ToolCallEvent(
                toolName,
                input,
                output,
                ResolveHostSession(root),
                ToolUseId: GetString(root, "tool_use_id") ?? GetString(root, "call_id"),
                DurationMs: GetInt64(root, "duration_ms"),
                Capabilities: BuildCapabilities(root),
                HostType: HookHostTypes.Codex,
                CapabilityUniverse: CapabilityUniverse);
        }
        catch { return null; }
    }

    public static HookUserSubmitEvent? ParseUserSubmit(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var prompt = GetString(root, "prompt")
                ?? GetString(root, "input")
                ?? GetString(root, "user_prompt");
            return new HookUserSubmitEvent(
                ResolveHostSession(root),
                prompt,
                UserSubmitSubtype.FollowUp,
                HookHostTypes.Codex);
        }
        catch { return null; }
    }

    public static HookSessionStartEvent? ParseSessionStart(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
                return new HookSessionStartEvent(ResolveHostSession(default), "session_start", HookHostTypes.Codex, null);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var subtype = GetString(root, "hook_event_name")
                ?? GetString(root, "event")
                ?? "session_start";
            return new HookSessionStartEvent(
                ResolveHostSession(root),
                subtype,
                HookHostTypes.Codex,
                BuildLifecycleEventData(root));
        }
        catch { return null; }
    }

    private static string? ResolveHostSession(JsonElement root)
        => GetString(root, "thread_id")
            ?? GetString(root, "session_id")
            ?? GetString(root, "conversation_id")
            ?? Environment.GetEnvironmentVariable(CodexThreadIdEnv);

    private static string BuildCapabilities(JsonElement root)
    {
        var caps = new List<string>();
        foreach (var field in CapabilityFields)
        {
            if (HasNonNull(root, field))
                caps.Add(field);
        }

        if (HasNonNull(root, "tool_input")) caps.Add("tool_input");
        else if (HasNonNull(root, "input")) caps.Add("input");

        if (HasNonNull(root, "tool_output")) caps.Add("tool_output");
        else if (HasNonNull(root, "output")) caps.Add("output");

        return string.Join(",", caps);
    }

    private static string? BuildLifecycleEventData(JsonElement root)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in CapabilityFields)
        {
            if (GetString(root, key) is { } value)
                data[key] = value;
        }

        return data.Count == 0
            ? null
            : JsonSerializer.Serialize(data, Scope.Core.Utilities.ScopeJsonSerializerContext.Default.DictionaryStringObject);
    }

    private static bool TryGetAny(JsonElement obj, out JsonElement value, params string[] names)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (obj.TryGetProperty(name, out value)
                    && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    return true;
            }
        }

        value = default;
        return false;
    }

    private static bool HasNonNull(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string? ExtractSummary(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (GetString(value, "text") is { Length: > 0 } text) return text;
            if (GetString(value, "stdout") is { Length: > 0 } stdout) return stdout;
            if (GetString(value, "stderr") is { Length: > 0 } stderr) return stderr;
        }

        return value.GetRawText();
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long? GetInt64(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    private static string? Truncate(string? s)
        => s != null && s.Length > SummaryMaxLength ? s[..SummaryMaxLength] : s;
}
