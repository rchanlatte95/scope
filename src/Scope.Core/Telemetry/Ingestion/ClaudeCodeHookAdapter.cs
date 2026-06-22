namespace Scope.Core.Telemetry.Ingestion;

using System.Text.Json;
using Scope.Core.App;
using System.Globalization;

/// <summary>
/// Translates raw Claude Code PostToolUse hook payloads (read from stdin by
/// <c>scope log-tool-call</c>) into the canonical <see cref="ToolCallEvent"/>.
/// All Claude-Code-payload-shape knowledge is confined to this adapter (host decoupling):
/// every field is optional, an absent field degrades to the pre-adapter behavior, and
/// parsing never throws. Full-fidelity fields (agent name, child agent id, token usage)
/// are extracted from the raw JSON BEFORE summary truncation (DF-01 lesson).
/// Field shapes verified against real captured payloads from Claude Code 2.1.170
/// (e2e-results/raw-posttooluse.jsonl; committed samples in
/// test/Scope.Tests/Fixtures/HookPayloads/).
/// </summary>
public static class ClaudeCodeHookAdapter
{
    private const int SummaryMaxLength = 500;

    /// <summary>
    /// Top-level payload fields whose presence is recorded as the host's capability set,
    /// so a future host version that drops a field degrades visibly in audit output.
    /// </summary>
    private static readonly string[] CapabilityFields =
        ["session_id", "transcript_path", "permission_mode", "agent_id", "agent_type", "tool_use_id", "duration_ms"];

    /// <summary>
    /// The complete universe of capability field names for the Claude Code adapter (7 top-level fields
    /// + "tool_response"). Exposed as a public constant so callers can emit the universe alongside the
    /// present-field list, making capability drift directly observable without re-deriving the host
    /// shape outside this file. Host-shape knowledge belongs here only (platform-independence rule).
    /// </summary>
    public static readonly IReadOnlyList<string> CapabilityUniverse =
        [.. CapabilityFields, "tool_response"];

    /// <summary>
    /// A single user-role turn extracted from a Claude Code API request body's
    /// <c>messages[]</c> array. <see cref="TurnIndex"/> is the 0-based ordinal among
    /// user-role messages ONLY (not the raw array index), so turn 0 is the initiating
    /// prompt regardless of any leading non-user messages. <see cref="Content"/> is the
    /// flattened user text. Host-shape knowledge stays in this adapter.
    /// </summary>
    public readonly record struct UserTurnExtract(int TurnIndex, string Content);

    /// <summary>
    /// Parses the user-role turns from a Claude Code API request payload (the inbound
    /// <c>messages[]</c> array on a <c>/v1/messages</c> request body). Returns one
    /// <see cref="UserTurnExtract"/> per user-role message in array order, with
    /// <see cref="UserTurnExtract.TurnIndex"/> assigned 0-based among user-role messages.
    /// Content shapes handled: plain string, and the block array
    /// <c>[{"type":"text","text":...}, ...]</c> (text blocks concatenated). Any other
    /// content shape falls back to its raw JSON (tolerant). Returns an empty list when the
    /// payload is null/empty, not valid JSON, has no <c>messages[]</c> array, or contains no
    /// user-role messages. Never throws. Host-shape knowledge is confined here per the
    /// platform-independence rule.
    /// </summary>
    public static IReadOnlyList<UserTurnExtract> ParseUserMessages(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            if (!root.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array)
                return [];

            List<UserTurnExtract>? turns = null;
            var turnIndex = 0;
            foreach (var msg in messages.EnumerateArray())
            {
                if (msg.ValueKind != JsonValueKind.Object) continue;
                if (!(string.Equals(GetString(msg, "role"), "user", StringComparison.Ordinal))) continue;

                var content = ExtractMessageContent(msg);
                if (content is null) continue;
                (turns ??= []).Add(new UserTurnExtract(turnIndex++, content));
            }
            return turns ?? (IReadOnlyList<UserTurnExtract>)[];
        }
        catch { return []; }
    }

    /// <summary>
    /// A human-in-the-loop submit decoded from a Claude Code prompt-class hook payload
    /// (UserPromptSubmit / Notification / Stop). <see cref="SessionId"/> is the host-provided
    /// session candidate (tier-2 input to <c>SessionKeyResolver</c>; may be null). <see cref="Prompt"/>
    /// is the raw human text and may be null when the hook shape carries no prompt body.
    /// <see cref="Subtype"/> is a candidate <see cref="UserSubmitSubtype"/> member; for the
    /// UserPromptSubmit shape it is a provisional default that the write path may refine to
    /// <see cref="UserSubmitSubtype.InitiatingPrompt"/> vs <see cref="UserSubmitSubtype.FollowUp"/>
    /// based on whether the session already has a prior submit. Host-shape knowledge stays here.
    /// </summary>
    public sealed record UserSubmitHookEvent(string? SessionId, string? Prompt, string Subtype);

    /// <summary>
    /// Parses a Claude Code <c>UserPromptSubmit</c> hook payload into a <see cref="UserSubmitHookEvent"/>.
    /// This hook fires every time the human submits a message, BEFORE any tool runs, and does not
    /// depend on payload capture, making it the most reliable real-time human-intervention signal.
    ///
    /// Host shape (Claude Code 2.1.x): <c>{ "session_id": str, "transcript_path": str, "cwd": str,
    /// "hook_event_name": "UserPromptSubmit", "prompt": str }</c>. Only <c>session_id</c> and
    /// <c>prompt</c> are consumed; every other field is ignored (platform-independence: a future host
    /// dropping a field degrades to null, never throws).
    ///
    /// <see cref="UserSubmitHookEvent.Subtype"/> is set to <see cref="UserSubmitSubtype.FollowUp"/> as
    /// the conservative provisional default: UserPromptSubmit alone cannot tell the initiating prompt
    /// from a follow-up (it carries no turn ordinal). The CLI write path promotes it to
    /// <see cref="UserSubmitSubtype.InitiatingPrompt"/> when the session has no prior user_submit
    /// (count == 0). Returns null on null/empty/non-JSON/non-object input; never throws.
    /// </summary>
    public static UserSubmitHookEvent? ParseUserSubmit(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var sessionId = GetString(root, "session_id");
            var prompt = GetString(root, "prompt");
            // Provisional subtype; the write path refines initiating vs follow_up from session state.
            return new UserSubmitHookEvent(sessionId, prompt, UserSubmitSubtype.FollowUp);
        }
        catch { return null; }
    }

    /// <summary>
    /// The exact, host-defined literal values of the Claude Code SessionStart <c>source</c> field
    /// (docs.claude.com). ONLY these four are accepted as a generation-boundary signal; any other or
    /// missing value degrades to a null <c>OpenSource</c> so the deterministic session-id-change FALLBACK
    /// (Wave 1) governs splitting instead of an unrecognized hint. Host-shape knowledge stays in this
    /// adapter (platform-independence rule).
    /// </summary>
    public static readonly IReadOnlyList<string> SessionStartSources =
        ["startup", "resume", "clear", "compact"];

    /// <summary>
    /// Parses a Claude Code <c>SessionStart</c> hook payload (read from stdin by
    /// <c>scope log-session-start</c>) into a <see cref="HookSessionStartEvent"/>.
    ///
    /// Host shape (Claude Code, docs.claude.com): <c>{ "session_id": str, "transcript_path": str,
    /// "cwd": str, "hook_event_name": "SessionStart", "source": str, "permission_mode": str,
    /// "model": str(opt), "agent_type": str(opt), "session_title": str(opt) }</c>. <c>source</c> is one
    /// of <see cref="SessionStartSources"/>. <c>/clear</c> mints a NEW <c>session_id</c>; <c>/compact</c>
    /// PRESERVES the same <c>session_id</c>; both fire SessionStart with their <c>source</c>.
    ///
    /// DEFENSIVE: every field is optional. <see cref="HookSessionStartEvent.OpenSource"/> is the raw
    /// <c>source</c> ONLY when it is one of the four documented literals; an unknown or missing source
    /// degrades to null (a no-op hint, so the session-id-change fallback still splits correctly), never an
    /// exception. <c>model</c> is absent on <c>/clear</c> and <c>/resume</c> per the docs, so it is carried
    /// as nullable. Returns null on null/empty/non-JSON/non-object input. Never throws (the hook must
    /// always exit 0). Host-shape knowledge is confined here per the platform-independence rule.
    /// </summary>
    public static HookSessionStartEvent? ParseSessionStart(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var sessionId = GetString(root, "session_id");
            var rawSource = GetString(root, "source");
            // Only a documented literal is a valid generation-boundary hint; anything else degrades to
            // null so the fallback (a previously-unseen session_id allocates a new generation) governs.
            var openSource = rawSource is not null
                && SessionStartSources.Contains(rawSource, StringComparer.Ordinal)
                ? rawSource
                : null;
            var subtype = GetString(root, "hook_event_name") ?? "SessionStart";

            return new HookSessionStartEvent(
                sessionId,
                subtype,
                HookHostTypes.ClaudeCode,
                EventDataJson: null,
                OpenSource: openSource,
                TranscriptPath: GetString(root, "transcript_path"),
                Model: GetString(root, "model"));
        }
        catch { return null; }
    }

    /// <summary>
    /// Parses a Claude Code <c>Notification</c> hook payload for human-intervention signals
    /// (tool permission approvals/denials, redirections). STUB: returns null.
    ///
    /// Documented gap (do NOT fabricate a field shape): the only Notification payload field whose
    /// shape is verified for Claude Code 2.1.x is the human-readable <c>message</c> string (plus the
    /// common <c>session_id</c>/<c>cwd</c>/<c>transcript_path</c>/<c>hook_event_name</c> envelope).
    /// The Notification hook fires for free-form UI notices ("Claude needs your permission to use
    /// Bash", "Claude is waiting for your input") -- it is the notice that a decision is PENDING, not
    /// a structured record of the human's decision. There is NO machine-readable field that encodes
    /// approved-vs-denied, the specific tool, or an interrupt vs a redirection. Mapping
    /// permission_request -> tool_approval/tool_denial would require inferring the human's choice from
    /// English prose in <c>message</c>, which is a fabricated mapping and a correctness risk (fail-fast
    /// / proof-correct rule). The structured approval/denial signal lives in the PostToolUse path
    /// (a denied tool yields a tool_response error / no execution), which is capture path A's domain.
    /// Until a real Notification payload with a structured decision field is captured and committed to
    /// the fixtures, this returns null. Never throws.
    /// </summary>
    public static UserSubmitHookEvent? ParseNotification(string? json)
    {
        // Shape not yet machine-decidable for human-intervention subtypes; see XML doc. No guessing.
        _ = json;
        return null;
    }

    /// <summary>
    /// Parses a Claude Code <c>Stop</c> hook payload. STUB: returns null.
    ///
    /// Documented gap: the Stop hook fires when the main agent's turn FINISHES (the model stopped
    /// producing), carrying <c>{ session_id, transcript_path, cwd, hook_event_name: "Stop",
    /// stop_hook_active: bool }</c>. It is an agent-lifecycle signal, NOT a human action: a normal
    /// turn completion fires Stop, and a user ESC interrupt is not distinguishable from a clean stop
    /// in this payload (no field encodes "the human interrupted"). Mapping Stop ->
    /// <see cref="UserSubmitSubtype.Interrupt"/> would over-record an interrupt on every normal turn
    /// end, fabricating human interventions that did not happen. Until the host exposes an interrupt
    /// discriminator, this returns null. Never throws.
    /// </summary>
    public static UserSubmitHookEvent? ParseStop(string? json)
    {
        _ = json;
        return null;
    }

    /// <summary>
    /// Flattens a message's <c>content</c> into text: a plain string is used directly, a
    /// block array concatenates its <c>type=="text"</c> text blocks (same rule as
    /// <see cref="ConcatTextBlocks"/>), and any other shape falls back to raw JSON. Returns
    /// null only when the content property is absent or JSON null/undefined.
    /// </summary>
    private static string? ExtractMessageContent(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var content)) return null;
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => ConcatTextBlocks(content) ?? content.GetRawText(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => content.GetRawText(),
        };
    }

    /// <summary>
    /// Parses a raw PostToolUse JSON payload into a <see cref="ToolCallEvent"/>.
    /// Returns null when the input is empty or not valid JSON; never throws
    /// (the hook command must always exit 0).
    /// </summary>
    public static ToolCallEvent? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return ParseRoot(doc.RootElement);
        }
        catch { return null; }
    }

    private static ToolCallEvent ParseRoot(JsonElement root)
    {
        var toolName = GetString(root, "tool_name") ?? "unknown";

        string? agentName = null;
        string? inputSummary = null;
        string? requestedModel = null;
        if (root.TryGetProperty("tool_input", out var input))
        {
            var rawInput = input.ToString();
            // Extract from full JSON BEFORE truncation; Truncate() breaks the JSON (DF-01).
            if (ScopeApp.IsAgentTool(toolName))
            {
                agentName = ScopeApp.ParseAgentName(rawInput);
                // tool_input.model is the model REQUESTED for the dispatched sub-agent (a short
                // family alias like "haiku" on explicitly-model-pinned dispatches; absent when the
                // dispatch inherits the parent model). This is the ONLY model field anywhere in the
                // Claude Code PostToolUse payload. Host-shape knowledge stays in this adapter; the
                // alias->id resolution is a Scope-owned cost concept handled downstream.
                requestedModel = GetString(input, "model");
            }
            inputSummary = Truncate(rawInput);
        }

        var hasResponse = TryGetResponse(root, out var response);
        var outputSummary = hasResponse ? Truncate(ExtractOutputSummary(response)) : null;
        var agentResult = hasResponse ? ExtractAgentResult(response) : default;
        var status = DeriveToolStatus(hasResponse, response);

        return new ToolCallEvent(
            toolName,
            inputSummary,
            outputSummary,
            GetString(root, "session_id"),
            GetString(root, "dispatch_id"),
            agentName,
            AgentId: GetString(root, "agent_id"),
            AgentType: GetString(root, "agent_type"),
            ToolUseId: GetString(root, "tool_use_id"),
            DurationMs: GetInt64(root, "duration_ms"),
            ChildAgentId: agentResult.ChildAgentId,
            IsAsyncAgentDispatch: agentResult.IsAsyncDispatch,
            TotalTokens: agentResult.TotalTokens,
            InputTokens: agentResult.InputTokens,
            OutputTokens: agentResult.OutputTokens,
            Capabilities: BuildCapabilities(root, hasResponse),
            RequestedModel: requestedModel,
            Status: status,
            // transcript_path (T-2026-06-22-01 Piece 4): the path to this session's transcript JSONL,
            // consumed by the CLI hook to fold in ambient transcript cost ingestion. Optional; a host
            // version that drops it degrades to null. Host-shape knowledge stays in this adapter.
            TranscriptPath: GetString(root, "transcript_path"));
    }

    /// <summary>The two machine-readable tool-call statuses Scope derives from a PostToolUse payload.</summary>
    private const string StatusSuccess = "success";
    private const string StatusFailed = "failed";

    /// <summary>
    /// Derives a machine-readable tool-call status (OBS-12) from the PostToolUse <c>tool_response</c>.
    /// This is an ACKNOWLEDGED HEURISTIC and the only status signal the host gives: Claude Code's
    /// PostToolUse payload has no explicit status field, so the result must be inferred from the
    /// <c>tool_response</c> shape. Host-shape knowledge stays in this adapter (platform-independence rule).
    /// <list type="bullet">
    ///   <item>No <c>tool_response</c> at all -> <c>null</c> (undeterminable; never invented). PreToolUse-only
    ///         or malformed payloads carry no result to classify.</item>
    ///   <item>An error-shaped <c>tool_response</c> -> <c>"failed"</c>. Recognized error shapes (the union
    ///         of observed Claude Code conventions): a tool_result block carrying <c>is_error: true</c>; an
    ///         object with a top-level <c>error</c> or <c>errorMessage</c>; an object with
    ///         <c>interrupted: true</c>; a Bash/PowerShell shape with a nonzero <c>exitCode</c>/<c>code</c>;
    ///         or an object/string whose only payload is error-shaped (an <c>error</c> object, a string that
    ///         begins with a recognized error prefix).</item>
    ///   <item>Otherwise (a present, non-error <c>tool_response</c>) -> <c>"success"</c>.</item>
    /// </list>
    /// NOTE: "denied" (a human declining a tool permission) is a HUMAN-INTERVENTION signal, NOT a tool
    /// status, and is host-blocked today: the Notification hook exposes the pending decision only as
    /// English prose with no structured approved/denied field (see <see cref="ParseNotification"/>). It is
    /// deliberately never returned here. A denied tool typically yields no execution / an error-shaped
    /// response, which classifies as <c>"failed"</c> by the rules above; the denial-vs-error distinction is
    /// not recoverable from PostToolUse alone, so Scope does not fabricate it.
    /// </summary>
    private static string? DeriveToolStatus(bool hasResponse, JsonElement response)
    {
        if (!hasResponse) return null; // no result captured -> undeterminable, never invented
        return IsErrorShaped(response) ? StatusFailed : StatusSuccess;
    }

    /// <summary>
    /// Returns true when a <c>tool_response</c> element matches a recognized Claude Code error shape.
    /// Conservative by design (an unknown but present response is treated as success, not failed) so a
    /// normal result is never mislabeled. Tolerant and never throws.
    /// </summary>
    private static bool IsErrorShaped(JsonElement response)
    {
        switch (response.ValueKind)
        {
            case JsonValueKind.Array:
                // tool_result content-block array: any block with is_error true marks the call failed.
                foreach (var block in response.EnumerateArray())
                    if (block.ValueKind == JsonValueKind.Object && IsTrue(block, "is_error"))
                        return true;
                return false;
            case JsonValueKind.Object:
                if (IsTrue(response, "is_error")) return true;
                if (IsTrue(response, "interrupted")) return true;
                // A top-level error / errorMessage field present and non-empty marks failure.
                if (response.TryGetProperty("error", out var err)
                    && err.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False))
                    return true;
                if (GetString(response, "errorMessage") is { Length: > 0 }) return true;
                // Bash/PowerShell shape: a nonzero process exit code is a failed command.
                if (GetInt64(response, "exitCode") is { } exitCode && exitCode != 0) return true;
                if (GetInt64(response, "code") is { } code && code != 0) return true;
                return false;
            default:
                return false; // string/number/bool present response with no error marker -> success
        }
    }

    private static bool IsTrue(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Locates the tool output element. Real Claude Code payloads use <c>tool_response</c>;
    /// <c>tool_result</c> is accepted for simulated payloads (scripts/e2e-trace-test.ps1)
    /// and older shapes.
    /// </summary>
    private static bool TryGetResponse(JsonElement root, out JsonElement response)
        => root.TryGetProperty("tool_response", out response)
            || root.TryGetProperty("tool_result", out response);

    /// <summary>
    /// Tolerant output summary extraction. Observed real shapes: plain string,
    /// <c>{"text":...}</c>, <c>[{"type":"text","text":...}]</c> block arrays,
    /// <c>{"stdout":...}</c> (Bash/PowerShell), structured Agent result objects with a
    /// <c>content</c> block array, and arbitrary objects (Read, ToolSearch) which fall back
    /// to their raw JSON. Fixes DF-04 (output summaries were null on 100% of real payloads).
    /// </summary>
    private static string? ExtractOutputSummary(JsonElement response)
    {
        switch (response.ValueKind)
        {
            case JsonValueKind.String:
                return response.GetString();
            case JsonValueKind.Array:
                return ConcatTextBlocks(response) ?? response.GetRawText();
            case JsonValueKind.Object:
                if (GetString(response, "text") is { Length: > 0 } text) return text;
                if (GetString(response, "stdout") is { Length: > 0 } stdout) return stdout;
                if (response.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array
                    && ConcatTextBlocks(content) is { Length: > 0 } blocks)
                    return blocks;
                return response.GetRawText();
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            default:
                return response.GetRawText();
        }
    }

    private static string? ConcatTextBlocks(JsonElement array)
    {
        List<string>? parts = null;
        foreach (var block in array.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (!(string.Equals(GetString(block, "type"), "text", StringComparison.Ordinal))) continue;
            if (GetString(block, "text") is { Length: > 0 } text)
                (parts ??= []).Add(text);
        }
        return parts == null ? null : string.Join("\n", parts);
    }

    private readonly record struct AgentResult(
        string? ChildAgentId, bool IsAsyncDispatch, long? TotalTokens, long? InputTokens, long? OutputTokens);

    /// <summary>
    /// Extracts Agent tool result fields from a structured response object. Observed shapes:
    /// async launch <c>{isAsync:true, status:"async_launched", agentId:...}</c> and sync
    /// completion. The completion token shape has CHANGED across Claude Code versions and this
    /// adapter reads BOTH forward- and backward-compatibly (host-shape knowledge stays here):
    /// <list type="bullet">
    /// <item>Older shape (Claude Code ~2.1.170, captured 2026-06-10):
    /// <c>{status:"completed", agentId, agentType, totalTokens, usage:{input_tokens, output_tokens}}</c>.</item>
    /// <item>Newer shape (Claude Code ~2026-06-18): the top-level <c>totalTokens</c> is gone and the
    /// sub-agent total is reported as <c>usage.subagent_tokens</c> (alongside <c>usage.tool_uses</c>
    /// and <c>usage.duration_ms</c>), with no per-direction input/output split.</item>
    /// </list>
    /// Token resolution (defensive, never throws): <c>TotalTokens</c> = top-level <c>totalTokens</c>
    /// if present, else <c>usage.subagent_tokens</c> if present, else null; <c>InputTokens</c>/
    /// <c>OutputTokens</c> = <c>usage.input_tokens</c>/<c>usage.output_tokens</c> if present else null.
    /// So both payload shapes yield correct tokens and a present-usage payload never silently
    /// returns null (regression guard for the 2026-06-18 sub-agent-span token loss).
    /// The child agentId is the linkage key matching subsequent payloads' top-level agent_id.
    /// </summary>
    private static AgentResult ExtractAgentResult(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) return default;
        var childAgentId = GetString(response, "agentId");
        if (childAgentId == null) return default;

        var isAsync = (response.TryGetProperty("isAsync", out var ia) && ia.ValueKind == JsonValueKind.True)
            || string.Equals(GetString(response, "status"), "async_launched", StringComparison.Ordinal);

        long? inputTokens = null, outputTokens = null, subagentTokens = null;
        if (response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            inputTokens = GetInt64(usage, "input_tokens");
            outputTokens = GetInt64(usage, "output_tokens");
            subagentTokens = GetInt64(usage, "subagent_tokens");
        }

        // Prefer the legacy top-level totalTokens; fall back to the newer usage.subagent_tokens.
        var totalTokens = GetInt64(response, "totalTokens") ?? subagentTokens;

        return new AgentResult(childAgentId, isAsync, totalTokens, inputTokens, outputTokens);
    }

    private static string BuildCapabilities(JsonElement root, bool hasResponse)
    {
        var caps = new List<string>();
        foreach (var field in CapabilityFields)
        {
            if (root.TryGetProperty(field, out var value)
                && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                caps.Add(field);
        }
        if (hasResponse) caps.Add("tool_response");
        return string.Join(",", caps);
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
