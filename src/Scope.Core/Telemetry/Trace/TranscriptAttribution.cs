namespace Scope.Core.Telemetry.Trace;

// Shared input/output record types for the pure transcript-row attributor (ST-02).
// Contracts: internal design notes, types TranscriptRow,
// SessionWindow, ToolUseOwner, TranscriptOwnerAssignment.
// AOT-safe: no reflection, no source-gen context needed (pure data records, no JSON serialization here).

/// <summary>
/// Input to the pure attributor. One per transcript-source wire_calls row for a conversation.
/// <para>
/// <see cref="ToolUseIds"/> is the list of tool_use_id values carried by this transcript turn
/// (empty for text-only / assistant turns with no tool call). <see cref="StartedAtIso"/> is an
/// ISO-8601 / RFC-3339 timestamp string; unparseable values are treated as unresolvable in Tier-2.
/// </para>
/// </summary>
public sealed record TranscriptRow(
    string WireRowId,
    IReadOnlyList<string> ToolUseIds,
    string StartedAtIso,
    string MachineId);

/// <summary>
/// One per session in the conversation. Represents the temporal window of the session on a
/// particular machine. <see cref="EndIso"/> is the LAST RECORDED event timestamp — it is always
/// bounded and never "now".
/// </summary>
public sealed record SessionWindow(
    string SessionKey,
    string MachineId,
    string StartIso,
    string EndIso);

/// <summary>
/// Maps a <c>tool_use_id</c> to the session that executed it, machine-scoped.
/// </summary>
public sealed record ToolUseOwner(string SessionKey, string MachineId);

/// <summary>
/// Output of the pure attributor. A total partition over all input rows.
/// <para>
/// <see cref="OwnerByWireRowId"/>: rows that resolved to exactly one session (key = WireRowId, value = SessionKey).
/// <see cref="RemainderRowIds"/>: rows with no unambiguous owner.
/// <see cref="Anomalies"/>: human-readable diagnostic notes (e.g. multi-session ambiguity). Not load-bearing.
/// </para>
/// <para>
/// INVARIANT: <see cref="OwnerByWireRowId"/>.Keys and <see cref="RemainderRowIds"/> are DISJOINT
/// and their union equals exactly the set of all input WireRowIds.
/// </para>
/// </summary>
public sealed record TranscriptOwnerAssignment(
    IReadOnlyDictionary<string, string> OwnerByWireRowId,
    IReadOnlyList<string> RemainderRowIds,
    IReadOnlyList<string> Anomalies);
