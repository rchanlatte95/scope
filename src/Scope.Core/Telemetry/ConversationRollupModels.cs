namespace Scope.Core.Telemetry;

// ST-08: internal data models for the conversation-level rollup computation.
// These are not wire-contract DTOs (those live in TraceContract.cs); they are the
// intermediate compute records used by ObservabilityAuditDb.ComputeConversationRollup.

/// <summary>
/// A transcript-source wire_calls row augmented with token counts, for use in the
/// conversation rollup computation. Extends the attribution-input <see cref="Trace.TranscriptRow"/>
/// with the token buckets needed to sum per-session cost.
/// </summary>
public sealed record TranscriptWireRow(
    string Id,
    IReadOnlyList<string> ToolUseIds,
    string StartedAt,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    string? Model,
    string? CcSessionId);

/// <summary>
/// A non-transcript (proxy-metered) wire_calls row, for conversation rollup R5 attribution.
/// </summary>
public sealed record ProxyWireRow(
    string Id,
    string StartedAt,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    string? Model,
    string? CcSessionId);

/// <summary>
/// Session identity info needed by the conversation rollup: session_key, cc_session_id (D3 bridge),
/// and conversation_id (transcript stem). All nullable except session_key.
/// </summary>
public sealed record ConversationSessionInfo(
    string SessionKey,
    string? CcSessionId,
    string? ConversationId);

/// <summary>
/// Per-session or remainder token totals from a conversation rollup. session_key="" means remainder.
/// </summary>
public sealed record SessionCostSlice(
    string SessionKey,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    int RowCount);

/// <summary>
/// Output of <see cref="ObservabilityAuditDb.ComputeConversationRollup"/>: per-session slices,
/// a remainder slice (unowned rows), and conversation-level totals.
/// <para>
/// Invariant: TotalInputTokens == sum(SessionSlices[i].InputTokens) + Remainder.InputTokens
/// (and similarly for Output/CacheCreation/CacheRead). This is exact by construction (no estimation).
/// </para>
/// </summary>
public sealed record ConversationRollupData(
    string ScopeConversationId,
    IReadOnlyList<SessionCostSlice> SessionSlices,
    SessionCostSlice Remainder,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheCreationTokens,
    long TotalCacheReadTokens);
