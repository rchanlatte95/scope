namespace Scope.Core.Telemetry.Trace;

/// <summary>
/// The tool-taxonomy-v1 registry from design-scope-trace-v1.xml. Maps a raw host tool name to
/// a taxonomy category id. Ships as data (single source consumed by the server and the SPA via
/// the contract), not as scattered switch statements. Resolution is host-agnostic; unmapped
/// tools resolve to "other" and are never an error (open enum, additive within v1).
/// </summary>
public static class ToolTaxonomy
{
    /// <summary>The registry version stamped on every resolved event tool block.</summary>
    public const string Version = "tool-taxonomy-v1";

    /// <summary>Mandatory fallback category for tools with no explicit mapping.</summary>
    public const string Fallback = "other";

    /// <summary>
    /// Reserved bucket id for cost that lands on a pure model turn (a text-only assistant turn /
    /// task-root row carrying no tool_use) rather than on any classifiable tool call. Used by the
    /// canonical by_tool_type cost derivation so the per-tool-type spend SUM (including this bucket)
    /// ties out to the headline window total by construction. It is NOT a host tool name;
    /// <see cref="Resolve"/> never returns it.
    /// </summary>
    public const string ModelTurn = "model_turn";

    // Exact-name map. Case-sensitive on the host tool name as stored. mcp__* prefix handled
    // separately in Resolve (code.intel for scope tools, mcp.other otherwise).
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        // shell.exec
        ["Bash"] = "shell.exec",
        ["PowerShell"] = "shell.exec",
        ["CMD"] = "shell.exec",
        ["Shell"] = "shell.exec",
        ["zsh"] = "shell.exec",
        // search.content
        ["Grep"] = "search.content",
        ["Select-String"] = "search.content",
        ["rg"] = "search.content",
        // search.files
        ["find"] = "search.files",
        ["fd"] = "search.files",
        // fs.list
        ["LS"] = "fs.list",
        // file.read
        ["Read"] = "file.read",
        ["NotebookRead"] = "file.read",
        // file.write
        ["Write"] = "file.write",
        // file.edit
        ["Edit"] = "file.edit",
        ["NotebookEdit"] = "file.edit",
        // web
        ["WebFetch"] = "web.fetch",
        ["WebSearch"] = "web.search",
        // agent.dispatch (matches IsAgentTool set)
        ["Agent"] = "agent.dispatch",
        ["Task"] = "agent.dispatch",
        ["Workflow"] = "agent.dispatch",
        ["TaskCreate"] = "agent.dispatch",
        // agent.message (matches IsMessageTool)
        ["SendMessage"] = "agent.message",
        // user.question
        ["AskUserQuestion"] = "user.question",
        // task.manage
        ["TodoWrite"] = "task.manage",
        ["TaskStop"] = "task.manage",
        ["Monitor"] = "task.manage",
        // skill.invoke
        ["Skill"] = "skill.invoke",
        // Note: Glob has no input here. It splits between search.files and fs.list, an
        // input-dependent refinement (item 4b) that only the local write/read side can do
        // because it requires inspecting the tool input. Without input we resolve it to
        // search.files (its dominant pattern-search use); a refiner may override.
        ["Glob"] = "search.files",
    };

    /// <summary>
    /// Resolves a raw host tool name to its taxonomy category id. Null/empty resolves to the
    /// fallback. mcp__scope__* tools resolve to code.intel; other mcp__* to mcp.other.
    /// </summary>
    public static string Resolve(string? rawToolName)
    {
        if (string.IsNullOrEmpty(rawToolName)) return Fallback;
        if (Map.TryGetValue(rawToolName, out var category)) return category;

        if (rawToolName.StartsWith("mcp__scope__", StringComparison.Ordinal)) return "code.intel";
        if (rawToolName.StartsWith("mcp__", StringComparison.Ordinal)) return "mcp.other";

        return Fallback;
    }
}
