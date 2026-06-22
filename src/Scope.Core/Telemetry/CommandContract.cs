namespace Scope.Core.Telemetry;

using System.Text.Json;
using System.Text.Json.Serialization;
using Scope.Core.Utilities;

// Public contract preserved (T-2026-06-17 sweep, owner): every type here is a GET /api/command/*
// response DTO registered in ScopeJsonSerializerContext with pinned [JsonPropertyName] fields.
// The concrete List<T> collection types are part of the serialized CLI/HTTP shape; do not change.
#pragma warning disable MA0016 // public /api/command/* contract; concrete collection types are the serialized shape

// =============================================================================
// GET /api/command/status contract DTO.
//
// One content-free, AOT-safe DTO covering workspace / entitlement / uplink /
// budget (compact 7-day aggregate) / security / operating + recent runs summary.
//
// Design constraints:
//   - snake_case property names pinned with [JsonPropertyName].
//   - Registered in ScopeJsonSerializerContext (no reflection).
//   - Works fully offline / free tier without error (all sections gracefully
//     degrade to honest "unavailable" / null when data is absent).
//   - Content-free: aggregates and states only, never event text.
// =============================================================================

/// <summary>
/// Workspace identity and index freshness.
/// <c>index_state</c> is one of: fresh | stale | missing | unavailable.
/// </summary>
public sealed record CommandWorkspaceStatus(
    [property: JsonPropertyName("index_path")] string? IndexPath,
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("index_state")] string IndexState,
    [property: JsonPropertyName("indexed_at")] string? IndexedAt,
    [property: JsonPropertyName("solution_path")] string? SolutionPath,
    [property: JsonPropertyName("server_reachable")] bool ServerReachable,
    [property: JsonPropertyName("proxy_reachable")] bool? ProxyReachable);

/// <summary>
/// Resolved entitlement state. Null <c>grace_remaining_seconds</c> means not in grace window.
/// <c>status</c> is one of: free | trial | paid | grace | expired | unchecked.
/// </summary>
public sealed record CommandEntitlementStatus(
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("last_refresh_at")] string? LastRefreshAt,
    [property: JsonPropertyName("expires_at")] string? ExpiresAt,
    [property: JsonPropertyName("grace_remaining_seconds")] long? GraceRemainingSeconds);

/// <summary>
/// Uplink state. <c>available</c> is false when the opt-in uplink has never been configured.
/// </summary>
public sealed record CommandUplinkStatus(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("last_success_at")] string? LastSuccessAt);

/// <summary>
/// One row in the budget top-drivers list.
/// </summary>
public sealed record CommandBudgetTopDriver(
    [property: JsonPropertyName("label")] string Label,
    // decimal (T-2026-06-20-20 Piece 2): summed from the canonical decimal ledger aggregate. JSON number.
    [property: JsonPropertyName("spend_usd")] decimal SpendUsd,
    [property: JsonPropertyName("run_count")] int RunCount);

/// <summary>
/// Compact 7-day budget aggregate.
/// <c>cost_provenance</c> is the CostProvenance vocabulary string.
/// <c>top_drivers</c> is by cron agent, descending by spend (default grouping).
/// </summary>
public sealed record CommandBudgetSummary(
    [property: JsonPropertyName("window_days")] int WindowDays,
    // decimal (T-2026-06-20-20 Piece 2): the canonical decimal ledger headline. JSON number.
    [property: JsonPropertyName("total_spend_usd")] decimal? TotalSpendUsd,
    [property: JsonPropertyName("cost_provenance")] string CostProvenance,
    [property: JsonPropertyName("top_drivers")] List<CommandBudgetTopDriver> TopDrivers);

/// <summary>
/// Security / telemetry capture mode.
/// <c>capture_mode</c> is one of: local_only | uplink.
/// <c>payload_capture</c> is true when <c>SCOPE_CAPTURE_PAYLOADS</c> is not "0".
/// </summary>
public sealed record CommandSecurityStatus(
    [property: JsonPropertyName("capture_mode")] string CaptureMode,
    [property: JsonPropertyName("payload_capture")] bool PayloadCapture);

/// <summary>
/// Operational counters. Content-free counts only.
/// </summary>
public sealed record CommandOperatingStatus(
    [property: JsonPropertyName("active_runs")] int ActiveRuns,
    [property: JsonPropertyName("trace_count_24h")] long TraceCount24H,
    [property: JsonPropertyName("warning_count")] int WarningCount);

/// <summary>
/// Compact recent-run summary row. Content-free: label + status + cost + trace session key.
/// <c>session_key</c> is the observability session key for this run (e.g. <c>cron-&lt;uuid7&gt;</c>),
/// used to deep-link to the run's trace view (<c>#/trace?session=KEY</c>). Null when the run
/// has no linked trace session (e.g. skipped runs or rows that pre-date session recording).
/// </summary>
public sealed record CommandRecentRun(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("agent_label")] string AgentLabel,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("started_at")] string StartedAt,
    [property: JsonPropertyName("completed_at")] string? CompletedAt,
    [property: JsonPropertyName("total_cost_usd")] double? TotalCostUsd,
    [property: JsonPropertyName("cost_provenance")] string CostProvenance,
    [property: JsonPropertyName("session_key")] string? SessionKey = null);

/// <summary>
/// Compact recent-session summary row.
/// </summary>
public sealed record CommandRecentSession(
    [property: JsonPropertyName("session_key")] string SessionKey,
    [property: JsonPropertyName("agent_type")] string? AgentType,
    [property: JsonPropertyName("first_seen_at")] string? FirstSeenAt,
    [property: JsonPropertyName("last_seen_at")] string? LastSeenAt,
    [property: JsonPropertyName("request_count")] long RequestCount);

// =============================================================================
// GET /api/command/budget contract DTOs.
//
// Deep, queryable cost-ledger version of the compact CommandBudgetSummary.
// Backs the Cost Ledger Explorer UI (T-05b). Reuses the same telemetry data
// source as CommandBudgetSummary; adds time-series, by-agent (full), by-model,
// and provenance breakdown dimensions.
//
// Cost honesty (binding): never fabricate a $ value. Estimated spend is labeled
// by provenance; unavailable stays null; wire-exact wins when present.
// =============================================================================

/// <summary>
/// One row in the provenance breakdown — how much spend came from each
/// CostProvenance category in the window.
/// </summary>
public sealed record BudgetProvenanceRow(
    [property: JsonPropertyName("provenance")] string Provenance,
    // decimal (T-2026-06-20-20 Piece 2): exact partition of the canonical decimal headline. JSON number.
    [property: JsonPropertyName("spend_usd")] decimal SpendUsd,
    [property: JsonPropertyName("run_count")] int RunCount);

/// <summary>
/// One daily bucket in the time-series.
/// </summary>
public sealed record BudgetDayRow(
    [property: JsonPropertyName("date")] string Date,
    // decimal (T-2026-06-20-20 Piece 2). JSON number.
    [property: JsonPropertyName("spend_usd")] decimal SpendUsd,
    [property: JsonPropertyName("run_count")] int RunCount);

/// <summary>
/// One agent row — full (not top-N), descending by spend.
/// <para>
/// <c>run_count</c> semantics (T-2026-06-16-04): this is the count of CONTRIBUTING SESSIONS grouped
/// under this agent label in the window (a session/contribution basis), NOT the legacy count of
/// obs_cost_estimates rows. The label resolves via cron_runs.session_key -&gt; cron_agents.label;
/// sessions with no cron match keep their raw session key as the label (plain CLI sessions).
/// </para>
/// </summary>
public sealed record BudgetAgentRow(
    [property: JsonPropertyName("label")] string Label,
    // decimal (T-2026-06-20-20 Piece 2). JSON number.
    [property: JsonPropertyName("spend_usd")] decimal SpendUsd,
    [property: JsonPropertyName("run_count")] int RunCount);

/// <summary>
/// One model row — descending by spend. Model id is normalized via ModelIdNormalizer where
/// resolvable; an id that does not resolve keeps its raw value (a distinct bucket, never dropped).
/// A null model_id column is bucketed as the literal "unknown".
/// </summary>
/// <param name="CacheCreationTokens">
/// Cache-creation (cache_write) tokens for this model in the window. Populated from the canonical
/// per-session rollup wire path (cache-INCLUSIVE token rule, T-2026-06-16-04). 0 when the session's
/// token source is exact/unknown (no cache split available) rather than wire.
/// </param>
/// <param name="CacheReadTokens">
/// Cache-read tokens for this model in the window, same canonical source as
/// <paramref name="CacheCreationTokens"/>.
/// </param>
public sealed record BudgetModelRow(
    [property: JsonPropertyName("model")] string Model,
    // decimal (T-2026-06-20-20 Piece 2). JSON number.
    [property: JsonPropertyName("spend_usd")] decimal SpendUsd,
    [property: JsonPropertyName("input_tokens")] long InputTokens,
    [property: JsonPropertyName("output_tokens")] long OutputTokens,
    [property: JsonPropertyName("run_count")] int RunCount,
    [property: JsonPropertyName("cache_creation_tokens")] long CacheCreationTokens = 0,
    [property: JsonPropertyName("cache_read_tokens")] long CacheReadTokens = 0);

/// <summary>
/// One tool-type bucket row: spend attributed to a TraceToolType taxonomy bucket.
/// taxonomy_id matches the ToolTaxonomy vocabulary (shell.exec, search.content, file.read, etc.).
/// </summary>
public sealed record BudgetToolTypeRow(
    [property: JsonPropertyName("taxonomy_id")] string TaxonomyId,
    // decimal (T-2026-06-20-20 Piece 2). JSON number.
    [property: JsonPropertyName("spend_usd")] decimal SpendUsd,
    [property: JsonPropertyName("run_count")] int RunCount);

/// <summary>
/// One day+tool-type row for stacked time-series: a single day bucket with a taxonomy_id segment.
/// date is YYYY-MM-DD UTC. taxonomy_id matches the ToolTaxonomy vocabulary.
/// </summary>
public sealed record BudgetDayToolTypeRow(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("taxonomy_id")] string TaxonomyId,
    // decimal (T-2026-06-20-20 Piece 2). JSON number.
    [property: JsonPropertyName("spend_usd")] decimal SpendUsd,
    [property: JsonPropertyName("run_count")] int RunCount);

/// <summary>
/// Root DTO for GET /api/command/budget.
/// Always 200. All arrays are empty (not null) when no data exists.
/// <c>total_spend_usd</c> is null when there is no metered spend in the window.
/// <c>cost_provenance</c> is the aggregate/dominant vocabulary string for the window.
/// <c>by_tool_type</c> groups spend by TraceToolType taxonomy bucket (classified via ToolTaxonomy).
/// <c>by_day_tool_type</c> provides a per-day stacked variant for the time-series stacked bar chart.
/// <para>
/// Headline token totals (T-2026-06-16-04): <c>total_input_tokens</c> / <c>total_output_tokens</c> /
/// <c>total_cache_creation_tokens</c> / <c>total_cache_read_tokens</c> / <c>total_tokens</c> are the
/// cache-INCLUSIVE window totals derived from the SAME canonical per-session rollup the Accounting tab
/// uses (sum of each session's <c>RollupTotals.Tokens</c>). <c>total_tokens</c> ==
/// input+output+cache_creation+cache_read, matching the rollup wire-path total rule. These reconcile
/// with the trace rollup by construction.
/// </para>
/// </summary>
public sealed record CommandBudgetResponse(
    [property: JsonPropertyName("window_days")] int WindowDays,
    [property: JsonPropertyName("from_utc")] string FromUtc,
    [property: JsonPropertyName("to_utc")] string ToUtc,
    // decimal (T-2026-06-20-20 Piece 2): the canonical decimal headline. JSON number.
    [property: JsonPropertyName("total_spend_usd")] decimal? TotalSpendUsd,
    [property: JsonPropertyName("cost_provenance")] string CostProvenance,
    [property: JsonPropertyName("provenance_breakdown")] List<BudgetProvenanceRow> ProvenanceBreakdown,
    [property: JsonPropertyName("by_day")] List<BudgetDayRow> ByDay,
    [property: JsonPropertyName("by_agent")] List<BudgetAgentRow> ByAgent,
    [property: JsonPropertyName("by_model")] List<BudgetModelRow> ByModel,
    [property: JsonPropertyName("by_tool_type")] List<BudgetToolTypeRow> ByToolType,
    [property: JsonPropertyName("by_day_tool_type")] List<BudgetDayToolTypeRow> ByDayToolType,
    [property: JsonPropertyName("total_input_tokens")] long TotalInputTokens = 0,
    [property: JsonPropertyName("total_output_tokens")] long TotalOutputTokens = 0,
    [property: JsonPropertyName("total_cache_creation_tokens")] long TotalCacheCreationTokens = 0,
    [property: JsonPropertyName("total_cache_read_tokens")] long TotalCacheReadTokens = 0,
    [property: JsonPropertyName("total_tokens")] long TotalTokens = 0)
{
    /// <summary>Serializes via the source-generated context (no reflection).</summary>
    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CommandBudgetResponse);
}

/// <summary>
/// One base-launch row (T-2026-06-19-03 Wave 2 / ST-06): a durable CLI launcher BASE session key with
/// the SUM of its per-generation rollup costs and the count of generations a `/clear` produced under it.
/// <para>
/// Reconciliation: this is an ADDITIVE roll-up of the SAME per-session rollups the headline sums,
/// grouped by <c>base_session_key</c> (a NULL stored base falls back to the analytic
/// <c>SessionKeyResolver.SplitEffectiveKey</c> inverse, so a single-generation session is its own base).
/// Therefore <c>SUM(by_base.spend_usd) == total_spend_usd</c> by construction. It does NOT replace the
/// canonical per-generation grain the ledger total and trace Accounting derive from.
/// </para>
/// <c>generation_count</c> is the number of logical generations (1 for a launch with no `/clear`).
/// <c>total_tokens</c> is the cache-inclusive token total over the base's generations.
/// </summary>
public sealed record BudgetBaseLaunchRow(
    [property: JsonPropertyName("base_session_key")] string BaseSessionKey,
    // decimal (T-2026-06-20-20 Piece 2). JSON number.
    [property: JsonPropertyName("spend_usd")] decimal SpendUsd,
    [property: JsonPropertyName("generation_count")] int GenerationCount,
    [property: JsonPropertyName("total_tokens")] long TotalTokens);

/// <summary>
/// One conversation row (T-2026-06-19-04): the UNIVERSAL cc-grouped view of the launch population. A
/// "conversation" is a host Claude Code / Codex <c>session_id</c> (cc); EVERY launch has exactly one.
/// <para>
/// <c>conversation_id</c> is the host cc session id when known, else the session's durable BASE key (a
/// session genuinely lacking a cc buckets under its base as its own singleton conversation, so the
/// partition covers the SAME full population <c>by_base</c> covers). Therefore
/// <c>SUM(by_conversation.spend_usd) == SUM(by_base.spend_usd) == total_spend_usd</c> by construction,
/// for EVERY window (cli-only, cron-only, mixed). Same for <c>total_tokens</c>.
/// </para>
/// <c>launch_count</c> is <c>COUNT(DISTINCT base_session_key)</c> in the cc group (a cli resume reuses one
/// cc across two launcher bases -> one conversation, launch_count == 2; a cron run is its own
/// single-launch conversation, launch_count == 1). <c>generation_count</c> is the number of contributing
/// per-generation session rows in the group. <c>total_tokens</c> is the cache-inclusive token sum.
/// </summary>
public sealed record BudgetConversationRow(
    [property: JsonPropertyName("conversation_id")] string ConversationId,
    // decimal (T-2026-06-20-20 Piece 2). JSON number.
    [property: JsonPropertyName("spend_usd")] decimal SpendUsd,
    [property: JsonPropertyName("launch_count")] int LaunchCount,
    [property: JsonPropertyName("generation_count")] int GenerationCount,
    [property: JsonPropertyName("total_tokens")] long TotalTokens);

/// <summary>
/// Root DTO for the base-launch cost rollup (T-2026-06-19-03 Wave 2 / ST-06), surfaced to the session
/// picker / launch view so per-CLI-launch cost == SUM of its `/clear`-split generations. Always 200.
/// <c>by_base</c> is empty (not null) when no spend exists in the window; <c>total_spend_usd</c> is null
/// when there is no metered spend. By construction <c>SUM(by_base.spend_usd) == total_spend_usd</c>.
/// <para>
/// <c>by_conversation</c> (T-2026-06-19-04) is the UNIVERSAL cc-grouped view of the SAME full launch
/// population, partitioned by host <c>cc_session_id</c> (idless sessions bucket under their base key).
/// Because both partitions cover the same sessions, <c>SUM(by_conversation.spend_usd) ==
/// SUM(by_base.spend_usd) == total_spend_usd</c> holds for EVERY window. Empty (not null) when no spend.
/// </para>
/// </summary>
public sealed record CommandBaseLaunchResponse(
    [property: JsonPropertyName("window_days")] int WindowDays,
    [property: JsonPropertyName("from_utc")] string FromUtc,
    [property: JsonPropertyName("to_utc")] string ToUtc,
    // decimal (T-2026-06-20-20 Piece 2). JSON number.
    [property: JsonPropertyName("total_spend_usd")] decimal? TotalSpendUsd,
    [property: JsonPropertyName("by_base")] List<BudgetBaseLaunchRow> ByBase,
    [property: JsonPropertyName("by_conversation")] List<BudgetConversationRow> ByConversation)
{
    /// <summary>Serializes via the source-generated context (no reflection).</summary>
    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CommandBaseLaunchResponse);
}

/// <summary>
/// Root DTO for GET /api/command/status.
/// All sections are present on every response; null/empty sections reflect honestly-unavailable
/// data rather than a server error.
/// </summary>
public sealed record CommandStatusResponse(
    [property: JsonPropertyName("generated_at")] string GeneratedAt,
    [property: JsonPropertyName("workspace")] CommandWorkspaceStatus Workspace,
    [property: JsonPropertyName("entitlement")] CommandEntitlementStatus Entitlement,
    [property: JsonPropertyName("uplink")] CommandUplinkStatus Uplink,
    [property: JsonPropertyName("budget")] CommandBudgetSummary Budget,
    [property: JsonPropertyName("security")] CommandSecurityStatus Security,
    [property: JsonPropertyName("operating")] CommandOperatingStatus Operating,
    [property: JsonPropertyName("recent_runs")] List<CommandRecentRun> RecentRuns,
    [property: JsonPropertyName("recent_sessions")] List<CommandRecentSession> RecentSessions)
{
    /// <summary>Serializes via the source-generated context (no reflection).</summary>
    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CommandStatusResponse);
}

#pragma warning restore MA0016
