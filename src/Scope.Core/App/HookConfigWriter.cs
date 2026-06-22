namespace Scope.Core.App;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Manages the Scope hook entries in <c>.claude/settings.json</c> (the generated source of truth
/// written by <c>scope init claude</c>, which <c>scripts/setup.ps1</c> invokes). Handles create,
/// merge, and idempotent update scenarios.
///
/// Registers three ambient-capture hooks:
/// <list type="bullet">
///   <item><c>PostToolUse</c> -> <c>scope log-tool-call</c> (tool/agent activity + payload capture).</item>
///   <item><c>UserPromptSubmit</c> -> <c>scope log-user-submit</c> (PRIVILEGED human submits, capture
///     path B; T-2026-06-16-19/ST-04). Fires on every human message, before any tool, independent of
///     payload capture.</item>
///   <item><c>SessionStart</c> -> <c>scope log-session-start</c> (T-2026-06-19-03 Wave 2). Carries the
///     AUTHORITATIVE generation-boundary <c>source</c> (clear/compact/resume/startup) that the Wave 1
///     session-id-change FALLBACK cannot distinguish: <c>/clear</c> rolls a new generation; <c>/compact</c>
///     and <c>/resume</c> continue the current one (the fallback alone false-splits neither, since compact
///     preserves the session_id, but the source makes the no-roll explicit and proof-stamped).</item>
/// </list>
///
/// DEPLOY GAP (carry forward): wiring these hooks here only takes effect once the GLOBAL
/// <c>scope</c> tool that runs the hook is rebuilt and reinstalled (<c>scripts/install.ps1</c>) AND
/// <c>scope init claude</c> / <c>scripts/setup.ps1</c> is re-run to regenerate <c>.claude/settings.json</c>.
/// A pre-ST-04 global tool lacks the <c>log-user-submit</c> subcommand, and a pre-Wave-2 global tool lacks
/// the <c>log-session-start</c> subcommand, so a settings.json wired to either before the matching tool is
/// installed would fail that hook with a missing-subcommand error. Until the global tool is rebuilt +
/// reinstalled + re-<c>init</c>ed, generation splitting stays FALLBACK-ONLY (session-id-change), which is
/// already correct on <c>/clear</c>; the SessionStart source is an additive precision/proof layer.
/// Notification/Stop are intentionally NOT registered: their payloads carry no machine-decidable
/// human-intervention signal (see <c>ClaudeCodeHookAdapter.ParseNotification</c>/<c>ParseStop</c>), so
/// registering them would only add noise.
/// </summary>
public static class HookConfigWriter
{
    private const string HooksKey = "hooks";
    private const string PostToolUseKey = "PostToolUse";
    private const string PostToolUseCommand = "scope log-tool-call";
    private const string UserPromptSubmitKey = "UserPromptSubmit";
    private const string UserPromptSubmitCommand = "scope log-user-submit";
    private const string SessionStartKey = "SessionStart";
    private const string SessionStartCommand = "scope log-session-start";

    /// <summary>The Claude Code hook events Scope registers, paired with the command each invokes.</summary>
    private static readonly (string EventName, string Command)[] ScopeHooks =
    [
        (PostToolUseKey, PostToolUseCommand),
        (UserPromptSubmitKey, UserPromptSubmitCommand),
        (SessionStartKey, SessionStartCommand),
    ];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Writes or updates the Scope hook entries in a <c>.claude/settings.json</c> file.
    /// Creates the <c>.claude</c> directory and file if they don't exist. Idempotent: an entry already
    /// pointing at the Scope command is left untouched; a missing entry is added.
    /// </summary>
    public static ScopeCommandResult Write(string settingsPath)
    {
        var dir = Path.GetDirectoryName(settingsPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(settingsPath))
            return CreateNew(settingsPath);

        var existing = File.ReadAllText(settingsPath);
        if (string.IsNullOrWhiteSpace(existing))
            return CreateNew(settingsPath);

        return MergeInto(settingsPath, existing);
    }

    /// <summary>
    /// Builds the Scope <c>PostToolUse</c> hook entry as a <see cref="JsonArray"/>. Retained for
    /// backward compatibility; new code should prefer <see cref="BuildHookEntry(string)"/>.
    /// </summary>
    public static JsonArray BuildHookEntry() => BuildHookEntry(PostToolUseCommand);

    /// <summary>
    /// Builds a single-matcher hook entry array that invokes <paramref name="command"/>.
    /// </summary>
    public static JsonArray BuildHookEntry(string command)
    {
        // Cast through IList<JsonNode?> to bind to the non-generic Add(JsonNode?) and
        // avoid the reflection/AOT-unsafe generic JsonArray.Add<T>(T) (IL2026/IL3050).
        var innerHooks = new JsonArray();
        ((IList<JsonNode?>)innerHooks).Add(new JsonObject
        {
            ["type"] = "command",
            ["command"] = command
        });

        var entry = new JsonArray();
        ((IList<JsonNode?>)entry).Add(new JsonObject
        {
            ["matcher"] = "",
            ["hooks"] = innerHooks
        });

        return entry;
    }

    private static ScopeCommandResult CreateNew(string settingsPath)
    {
        var hooks = new JsonObject();
        foreach (var (eventName, command) in ScopeHooks)
            hooks[eventName] = BuildHookEntry(command);

        var root = new JsonObject { [HooksKey] = hooks };

        File.WriteAllText(settingsPath, root.ToJsonString(WriteOptions));
        return ScopeCommandResult.Success(stderr: $"Created {settingsPath} with {DescribeHooks()} hooks");
    }

    private static ScopeCommandResult MergeInto(string settingsPath, string existing)
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
                Stderr: $"Failed to parse {settingsPath}: {ex.Message}");
        }

        if (rootNode is not JsonObject root)
        {
            return new ScopeCommandResult(
                (int)ScopeExitCode.JsonDeserializeFailure,
                Stderr: $"Expected a JSON object in {settingsPath}");
        }

        if (root[HooksKey] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root[HooksKey] = hooks;
        }

        var added = new List<string>();
        foreach (var (eventName, command) in ScopeHooks)
        {
            if (hooks[eventName] is JsonArray existingEntry && HasScopeHook(existingEntry, command))
                continue;

            hooks[eventName] = BuildHookEntry(command);
            added.Add(eventName);
        }

        if (added.Count == 0)
            return ScopeCommandResult.Success(stderr: $"No changes needed: {settingsPath} already has the Scope hooks.");

        File.WriteAllText(settingsPath, root.ToJsonString(WriteOptions));
        return ScopeCommandResult.Success(stderr: $"Updated {settingsPath} with {string.Join(", ", added)} hook(s)");
    }

    private static bool HasScopeHook(JsonArray hookArray, string command)
        => hookArray.ToJsonString().Contains(command, StringComparison.Ordinal);

    private static string DescribeHooks()
        => string.Join(", ", ScopeHooks.Select(h => h.EventName));
}
