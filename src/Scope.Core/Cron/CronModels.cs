using Scope.Core.Utilities;

namespace Scope.Core.Cron;

/// <summary>
/// Supported headless agent harness identifiers for cron definitions.
/// </summary>
public static class CronHarnesses
{
    public const string ClaudeCode = "claude-code";
    public const string Codex = "codex";

    public static bool IsSupported(string harness)
        => string.Equals(harness, ClaudeCode, StringComparison.Ordinal)
           || string.Equals(harness, Codex, StringComparison.Ordinal);
}

/// <summary>
/// Persisted cron-agent definition.
/// </summary>
public sealed record CronAgentDefinition(
    string Id,
    string Label,
    string Schedule,
    string Prompt,
    string RepoPath,
    string Harness,
    string? Model,
    int? TokenBudget,
    bool Enabled,
    string CreatedAt,
    string UpdatedAt,
    bool BlockConcurrentRuns = true,
    string? DeletedAt = null,
    int? MaxRuntimeSeconds = null);

/// <summary>
/// Input for creating a cron-agent definition.
/// </summary>
public sealed record CronCreateRequest(
    string Label,
    string Schedule,
    string Prompt,
    string RepoPath,
    string Harness,
    string? Model,
    int? TokenBudget,
    bool Enabled = true,
    bool BlockConcurrentRuns = true,
    int? MaxRuntimeSeconds = null);

/// <summary>
/// Input for updating (FULL REPLACE) the editable fields of an existing cron-agent definition
/// (T-2026-06-17-05). The agent is identified by its immutable label (the URL/API path key and
/// identity; cron_runs join by cron_agent_id). The label is NOT a field here precisely because it
/// cannot change. Every other definition field is editable. <c>enabled</c> stays on the dedicated
/// enable/disable endpoints, so it is intentionally absent: an update never flips the enabled flag
/// and therefore never affects the free-tier enabled-agent count.
///
/// Mirrors <see cref="CronCreateRequest"/>'s editable-field shape so create and edit share ONE
/// validation path (<c>CronService.ValidateEditableFields</c>) and cannot drift.
/// </summary>
public sealed record CronUpdateRequest(
    string Schedule,
    string Prompt,
    string RepoPath,
    string Harness,
    string? Model,
    int? TokenBudget,
    bool BlockConcurrentRuns = true,
    int? MaxRuntimeSeconds = null);

/// <summary>
/// Partial (FETCH-MERGE) edit of an existing cron-agent definition, used by the NON-destructive
/// CLI and MCP edit adapters (T-2026-06-17). Every field is nullable and means "not provided":
/// the service fetches the existing agent and overlays ONLY the fields the caller explicitly set,
/// then funnels the MERGED full object through the SAME validator/write path as the full-replace
/// <see cref="CronUpdateRequest"/>, so create/edit cannot drift.
/// <para>
/// This exists to fix the destructive PUT-full-replace edit: a CLI/MCP edit that omits an optional
/// flag (e.g. <c>--budget</c>, <c>--max-runtime</c>, <c>--block-concurrent-runs</c>) MUST preserve
/// the agent's current value rather than reset it to a default. The server PUT endpoint and the
/// frontend (which prefills every field) intentionally keep the full-replace contract.
/// </para>
/// <para>
/// <see cref="BlockConcurrentRuns"/> is a nullable bool (tri-state): null = keep the current
/// setting; true/false = set it. The label is the immutable identity key and is therefore not a
/// field here. <c>enabled</c> stays on the enable/disable endpoints and is intentionally absent.
/// </para>
/// </summary>
public sealed record CronUpdatePatch(
    string? Schedule = null,
    string? Prompt = null,
    string? RepoPath = null,
    string? Harness = null,
    string? Model = null,
    int? TokenBudget = null,
    bool? BlockConcurrentRuns = null,
    int? MaxRuntimeSeconds = null);

/// <summary>
/// Normalized, validated editable fields shared by create and update. Produced by the single
/// validator (<c>CronService.ValidateEditableFields</c>) so both code paths apply identical
/// schedule/harness/budget/runtime/repo-path rules and cannot diverge.
/// </summary>
public sealed record CronEditableFields(
    string Schedule,
    string Prompt,
    string RepoPath,
    string Harness,
    string? Model,
    int? TokenBudget,
    bool BlockConcurrentRuns,
    int? MaxRuntimeSeconds);

/// <summary>
/// Persisted execution row for a cron-agent run.
/// <para>
/// <c>MeteringMisconfigured</c> (schema v7): true when metering was expected (proxy and api-key
/// both set at launch) but the final resolved provenance is still <c>unmetered</c> after the F2
/// self-reported fallback. NULL in the database reads back as <c>false</c> for historical rows.
/// Never set for <c>joined_exact</c>, <c>provider_estimated</c>, <c>unavailable</c>, or honest
/// no-proxy <c>unmetered</c> runs.
/// </para>
/// </summary>
public sealed record CronRunRecord(
    string Id,
    string CronAgentId,
    string? DispatchId,
    string? SessionKey,
    string StartedAt,
    string? CompletedAt,
    string Status,
    int? ExitCode,
    double? TotalCostUsd,
    string? SkipReason,
    string? ErrorMessage,
    string? CostProvenance = null,
    bool MeteringMisconfigured = false);

/// <summary>
/// Input for writing a cron-agent run record.
/// </summary>
public sealed record CronRunCreateRequest(
    string CronAgentId,
    string? DispatchId,
    string? SessionKey,
    string Status,
    int? ExitCode = null,
    double? TotalCostUsd = null,
    string? CompletedAt = null,
    string? SkipReason = null,
    string? ErrorMessage = null,
    string? CostProvenance = null);

/// <summary>
/// Valid cron-run status values persisted in cron_runs.
/// </summary>
public static class CronRunStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";

    /// <summary>
    /// Terminal status (cron schema v6) for a run stopped by the runaway guard because it reached
    /// its enforced token budget OR its wall-clock cutoff. Distinct from <see cref="Failed"/> so the
    /// outcome is auditably "stopped by Scope on purpose" rather than "the harness errored". The
    /// specific reason (budget vs wall-clock) is carried in <c>error_message</c>; the partial
    /// cost/tokens spent up to the cut are recorded honestly on the row.
    /// </summary>
    public const string BudgetExhausted = "budget_exhausted";

    public static bool IsValid(string status)
        => status.IsAnyOf(Running, Completed, Failed, Skipped, BudgetExhausted);
}

/// <summary>
/// Runaway-guard policy constants (T-2026-06-17-06). Two independent guards stop a cron run that
/// would otherwise burn unbounded resources, neither of which depends on agent cooperation:
/// <list type="bullet">
///   <item><b>Token budget</b> (the proxy refuses NEW API calls at the budget; the supervisor
///   force-kills at budget + a small grace if the harness ignores the refusal or spins);</item>
///   <item><b>Wall-clock</b> (the supervisor force-kills a run that exceeds its max-runtime, which
///   catches a local infinite loop that burns NO API tokens and so is invisible to the proxy).</item>
/// </list>
/// </summary>
public static class CronRunawayGuard
{
    /// <summary>
    /// Fraction of the token budget added as a grace margin for the HARD force-kill threshold so the
    /// cooperative terminal proxy-refusal (which fires at exactly the budget) and the harness's own
    /// clean exit can happen first. The kill only fires when a harness ignored the refusal or is
    /// spinning past it. Effective kill threshold = budget + max(budget * this, <see cref="MinGraceTokens"/>).
    /// </summary>
    public const double HardKillGraceFraction = 0.05;

    /// <summary>Absolute floor for the grace margin so a tiny budget still leaves room before the kill.</summary>
    public const long MinGraceTokens = 10_000;

    /// <summary>
    /// Global default wall-clock cutoff (seconds) applied to a run whose agent did not set a
    /// per-agent <see cref="CronAgentDefinition.MaxRuntimeSeconds"/>. 30 minutes is generous for a
    /// headless cron review while still bounding a runaway local loop.
    /// </summary>
    public const int DefaultMaxRuntimeSeconds = 30 * 60;

    /// <summary>How often the supervisor re-reads cumulative tokens and checks the wall-clock.</summary>
    public static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Computes the hard force-kill token threshold for a budget: the budget plus a grace margin
    /// (the larger of the fractional and absolute floors). Returns null when no budget is set
    /// (an unbudgeted run has no token-based kill threshold; the wall-clock still applies).
    /// </summary>
    public static long? HardKillThreshold(int? tokenBudget)
    {
        if (tokenBudget is not int budget || budget <= 0)
            return null;
        var grace = Math.Max((long)(budget * HardKillGraceFraction), MinGraceTokens);
        return budget + grace;
    }

    /// <summary>
    /// Resolves the effective wall-clock cutoff in seconds for an agent: its per-agent override when
    /// set and positive, otherwise the global <see cref="DefaultMaxRuntimeSeconds"/>.
    /// </summary>
    public static int EffectiveMaxRuntimeSeconds(int? perAgentMaxRuntimeSeconds)
        => perAgentMaxRuntimeSeconds is int s and > 0 ? s : DefaultMaxRuntimeSeconds;
}

/// <summary>
/// Small adapter-friendly result wrapper for cron service operations.
/// </summary>
public sealed record CronResult<T>(T? Value, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>
/// Non-generic factory for <see cref="CronResult{T}"/> to avoid static members on a generic type
/// (CA1000). Use <c>CronResult.Ok(value)</c> and <c>CronResult.Fail&lt;T&gt;(error)</c>.
/// </summary>
public static class CronResult
{
    /// <summary>Creates a successful result wrapping <paramref name="value"/>.</summary>
    public static CronResult<T> Ok<T>(T value) => new(value, null);

    /// <summary>Creates a failed result with the given <paramref name="error"/> message.</summary>
    public static CronResult<T> Fail<T>(string error) => new(default, error);
}

/// <summary>
/// Lightweight projection returned by the single group-by latest-run query in
/// <see cref="CronRepository.GetLatestRunPerAgent"/>. One row per agent id; null fields
/// indicate the agent has never run.
/// </summary>
public sealed record CronAgentLastRunSummary(
    string CronAgentId,
    string? Status,
    string? StartedAt,
    double? TotalCostUsd,
    string? CostProvenance);

/// <summary>
/// Cross-agent cron run row for the /trace page Cron tab (T-2026-06-15-16/ST-02). One row per
/// cron_run, joined to its cron_agent for the human label and schedule. Keyed by session_key so the
/// frontend can deep-link into the trace/accounting detail for the cron session. All fields come from
/// scope.db (cron_runs JOIN cron_agents); read-only.
/// </summary>
public sealed record CronSessionRow(
    string RunId,
    string? SessionKey,
    string AgentLabel,
    string Schedule,
    string Status,
    double? TotalCostUsd,
    string StartedAt,
    string? CompletedAt,
    string? CostProvenance);
