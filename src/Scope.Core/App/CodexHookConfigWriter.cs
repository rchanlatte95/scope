namespace Scope.Core.App;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Writes Scope's project-local Codex hook config. The generated commands are fixed Scope-owned
/// hook commands only; caller input is limited to the target file path.
/// </summary>
public static class CodexHookConfigWriter
{
    private const string HooksKey = "hooks";

    private static readonly (string EventName, string Command)[] ScopeHooks =
    [
        ("SessionStart", "scope log-codex-session-start"),
        ("PostToolUse", "scope log-codex-tool-call"),
        ("UserPromptSubmit", "scope log-codex-user-submit"),
    ];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static ScopeCommandResult Write(string hooksPath)
    {
        var dir = Path.GetDirectoryName(hooksPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(hooksPath) || string.IsNullOrWhiteSpace(File.ReadAllText(hooksPath)))
            return CreateNew(hooksPath);

        return MergeInto(hooksPath, File.ReadAllText(hooksPath));
    }

    public static JsonArray BuildHookEntry(string command)
    {
        var handler = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["commandWindows"] = command,
            ["timeout"] = 30,
            ["statusMessage"] = "Recording Scope trace"
        };

        var hooks = new JsonArray();
        ((IList<JsonNode?>)hooks).Add(handler);

        var entry = new JsonArray();
        ((IList<JsonNode?>)entry).Add(new JsonObject
        {
            ["matcher"] = "",
            ["hooks"] = hooks
        });
        return entry;
    }

    private static ScopeCommandResult CreateNew(string hooksPath)
    {
        var hooks = new JsonObject();
        foreach (var (eventName, command) in ScopeHooks)
            hooks[eventName] = BuildHookEntry(command);

        var root = new JsonObject { [HooksKey] = hooks };
        File.WriteAllText(hooksPath, root.ToJsonString(WriteOptions));
        return ScopeCommandResult.Success(stderr: $"Created {hooksPath} with Codex Scope hooks");
    }

    private static ScopeCommandResult MergeInto(string hooksPath, string existing)
    {
        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(existing);
        }
        catch (JsonException ex)
        {
            return new ScopeCommandResult(
                (int)ScopeExitCode.JsonDeserializeFailure,
                Stderr: $"Failed to parse {hooksPath}: {ex.Message}");
        }

        if (rootNode is not JsonObject root)
        {
            return new ScopeCommandResult(
                (int)ScopeExitCode.JsonDeserializeFailure,
                Stderr: $"Expected a JSON object in {hooksPath}");
        }

        if (root[HooksKey] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root[HooksKey] = hooks;
        }

        var added = new List<string>();
        foreach (var (eventName, command) in ScopeHooks)
        {
            if (hooks[eventName] is JsonArray existingEntry && existingEntry.ToJsonString().Contains(command, StringComparison.Ordinal))
                continue;

            hooks[eventName] = BuildHookEntry(command);
            added.Add(eventName);
        }

        if (added.Count == 0)
            return ScopeCommandResult.Success(stderr: $"No changes needed: {hooksPath} already has the Scope Codex hooks.");

        File.WriteAllText(hooksPath, root.ToJsonString(WriteOptions));
        return ScopeCommandResult.Success(stderr: $"Updated {hooksPath} with {string.Join(", ", added)} Codex hook(s)");
    }
}
