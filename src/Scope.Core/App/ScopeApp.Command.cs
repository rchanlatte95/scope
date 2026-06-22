namespace Scope.Core.App;

using Microsoft.Data.Sqlite;
using Scope.Core.CostEstimation;
using Scope.Core.Entitlement;
using Scope.Core.Storage;
using Scope.Core.Telemetry;
using Scope.Core.Telemetry.Trace;
using Scope.Core.Utilities;
using Serilog;
using System.Globalization;

public static partial class ScopeApp
{
    /// <summary>
    /// Assembles a content-free <see cref="CommandStatusResponse"/> covering:
    /// workspace identity/freshness, entitlement, uplink, compact 7-day budget aggregate,
    /// security/capture mode, operating counters, and recent runs/sessions summaries.
    ///
    /// Works fully offline / free tier without error. All sections degrade honestly to
    /// "unavailable" / empty when their data source is absent. Never fabricates data.
    /// </summary>
    /// <param name="dbPath">Explicit scope.db path; null to discover from CWD.</param>
    /// <param name="searchAllParents">Walk to filesystem root during discovery.</param>
    /// <param name="entitlementProvider">
    /// Entitlement provider to read cached token state. When null, constructs
    /// <see cref="DefaultEntitlementProvider"/> (reads from %APPDATA%/.scope/entitlement.json).
    /// </param>
    /// <param name="activeRunCount">
    /// Active cron run count injected by the server (it owns the CronBackgroundTaskService).
    /// Defaults to 0 when the caller does not track this.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<ScopeCommandResult> CommandStatusAsync(
        string? dbPath,
        bool searchAllParents,
        IEntitlementProvider? entitlementProvider = null,
        int activeRunCount = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolvedDb = FindDbPath(dbPath, searchAllParents: searchAllParents);
            var scopeDir = resolvedDb != null ? Path.GetDirectoryName(resolvedDb)! : null;
            var telemetryDbPath = scopeDir != null
                ? Path.Combine(scopeDir, "telemetry.db")
                : null;

            var workspace = BuildWorkspaceStatus(resolvedDb, scopeDir);
            var entitlement = BuildEntitlementStatus(entitlementProvider ?? new DefaultEntitlementProvider(), scopeDir);
            var uplink = BuildUplinkStatus(scopeDir);
            var security = BuildSecurityStatus(scopeDir);

            CommandBudgetSummary budget;
            CommandOperatingStatus operating;
            List<CommandRecentRun> recentRuns;
            List<CommandRecentSession> recentSessions;

            if (telemetryDbPath != null && File.Exists(telemetryDbPath))
            {
                try
                {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
                    await using var telConn = await SQLite.OpenDbAsync(telemetryDbPath, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
                    budget = BuildBudgetSummary(telConn, scopeDir, resolvedDb);
                    operating = BuildOperatingStatus(telConn, activeRunCount);
                    recentRuns = BuildRecentRuns(resolvedDb, telConn);
                    recentSessions = BuildRecentSessions(telConn);
                }
                catch (SqliteException ex)
                {
                    Log.Debug("CommandStatus: telemetry.db unavailable ({Message}); returning empty sections.", ex.Message);
                    budget = UnavailableBudget();
                    operating = new CommandOperatingStatus(activeRunCount, 0, 0);
                    recentRuns = [];
                    recentSessions = [];
                }
            }
            else
            {
                budget = UnavailableBudget();
                operating = new CommandOperatingStatus(activeRunCount, 0, 0);
                recentRuns = [];
                recentSessions = [];
            }

            var response = new CommandStatusResponse(
                GeneratedAt: DateTime.UtcNow.ToString("o"),
                Workspace: workspace,
                Entitlement: entitlement,
                Uplink: uplink,
                Budget: budget,
                Security: security,
                Operating: operating,
                RecentRuns: recentRuns,
                RecentSessions: recentSessions);

            return ScopeCommandResult.Success(response.ToJsonString());
        }
        catch (Exception ex)
        {
            Log.Warning("CommandStatusAsync failed (non-fatal): {Message}", ex.Message);
            // Return an honest minimal response rather than an error.
            var fallback = new CommandStatusResponse(
                GeneratedAt: DateTime.UtcNow.ToString("o"),
                Workspace: new CommandWorkspaceStatus(null, null, "unavailable", null, null, true, null),
                Entitlement: new CommandEntitlementStatus("free", "unchecked", null, null, null),
                Uplink: new CommandUplinkStatus(false, null),
                Budget: UnavailableBudget(),
                Security: BuildSecurityStatus(null),
                Operating: new CommandOperatingStatus(0, 0, 0),
                RecentRuns: [],
                RecentSessions: []);
            return ScopeCommandResult.Success(fallback.ToJsonString());
        }
    }

    // ---- budget ledger ----

    /// <summary>
    /// Assembles the deep cost-ledger <see cref="CommandBudgetResponse"/> for
    /// GET /api/command/budget (T-05a).
    ///
    /// <para>Window clamping: <paramref name="windowDays"/> is clamped to [1, 3650]. The 3650-day
    /// (10-year) ceiling covers the GAAP/IRS financial-retention horizon with headroom; obs_* cost
    /// data is never hard-deleted, so the clamp is a sanity bound, not a data-retention limit.</para>
    ///
    /// <para>Performance: large windows (years) recompute per-session rollups at read time via
    /// <c>BuildLedgerRollupAggregate</c> (O(n_sessions)); the A2 persistence optimization (persist
    /// per-session cost once on write so multi-year queries sum N rows) is deferred.</para>
    ///
    /// <para>Cost honesty: total_spend_usd is null when no metered spend exists in the
    /// window. Estimated spend is labeled by provenance; unavailable stays null; all
    /// arrays are empty (not null) when no data exists.</para>
    ///
    /// <para>Data source: same <c>obs_cost_estimates</c> + <c>obs_requests</c> join used by
    /// <see cref="CommandStatusAsync"/>'s compact budget section. The new method adds
    /// time-series (by_day), full by-agent (not top-5), by-model with token counts and
    /// ModelIdNormalizer canonicalization, and provenance_breakdown dimensions.</para>
    /// </summary>
    /// <param name="dbPath">Explicit scope.db path; null to discover from CWD.</param>
    /// <param name="windowDays">Requested look-back window in days [1..3650]. Default 30.</param>
    /// <param name="fromUtc">
    /// Optional explicit custom-range start (UTC). When BOTH <paramref name="fromUtc"/> and
    /// <paramref name="toUtc"/> are supplied and form a valid range, a
    /// <see cref="CostAggregator.CostWindow.Custom"/> window is used and <paramref name="windowDays"/>
    /// is ignored. An invalid pair (to &lt; from, or an unrecoverable range) falls back to the
    /// <paramref name="windowDays"/> rolling window with a logged warning. The start is clamped up to the
    /// 2020-01-01T00:00:00Z floor and the range is clamped to a 3650-day ceiling, matching the FE picker.
    /// The response carries the effective from_utc/to_utc (post-clamp) so the FE can confirm what was used.
    /// </param>
    /// <param name="toUtc">Optional explicit custom-range end (UTC). See <paramref name="fromUtc"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<ScopeCommandResult> CommandBudgetAsync(
        string? dbPath,
        int windowDays = 30,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        // Clamp window_days to valid range (back-compat path; also the fallback for an invalid custom range).
        windowDays = Math.Max(1, Math.Min(3650, windowDays));

        // Resolve the effective window: a valid custom {from,to} pair wins over window_days; otherwise
        // fall back to the rolling window_days window. ResolveBudgetWindow never throws.
        var window = ResolveBudgetWindow(windowDays, fromUtc, toUtc);

        try
        {
            var resolvedDb = FindDbPath(dbPath, searchAllParents: false);
            var scopeDir = resolvedDb != null ? Path.GetDirectoryName(resolvedDb)! : null;
            var telemetryDbPath = scopeDir != null
                ? Path.Combine(scopeDir, "telemetry.db")
                : null;

            CommandBudgetResponse response;

            if (telemetryDbPath != null && File.Exists(telemetryDbPath))
            {
                try
                {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
                    await using var telConn = await SQLite.OpenDbAsync(telemetryDbPath, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
                    response = BuildBudgetLedger(telConn, scopeDir, resolvedDb, window, windowDays);
                }
                catch (SqliteException ex)
                {
                    Log.Debug("CommandBudget: telemetry.db unavailable ({Message}); returning empty ledger.", ex.Message);
                    response = EmptyBudgetLedger(window, windowDays);
                }
            }
            else
            {
                response = EmptyBudgetLedger(window, windowDays);
            }

            return ScopeCommandResult.Success(response.ToJsonString());
        }
        catch (Exception ex)
        {
            Log.Warning("CommandBudgetAsync failed (non-fatal): {Message}", ex.Message);
            return ScopeCommandResult.Success(EmptyBudgetLedger(window, windowDays).ToJsonString());
        }
    }

    /// <summary>
    /// Assembles the base-launch cost rollup (T-2026-06-19-03 Wave 2 / ST-06) for the session
    /// picker / launch view: per-CLI-launch cost == SUM of its `/clear`-split generations.
    /// <para>
    /// ADDITIVE to the canonical per-generation grain: it sums the SAME per-session
    /// <see cref="TraceRollup"/> totals the headline uses, grouped by <c>base_session_key</c> (with the
    /// analytic <c>SplitEffectiveKey</c> fallback for legacy/cron/MCP/host rows). It does NOT change the
    /// per-generation derivation the Cost Ledger and trace Accounting depend on, so
    /// <c>SUM(by_base.spend_usd) == total_spend_usd == the Ledger headline</c> for the same window by
    /// construction (one canonical source, cross-screen reconciliation rule).
    /// </para>
    /// Always 200; an honest empty rollup (null total, empty by_base) when there is no spend or no
    /// telemetry db. Window semantics + clamping match <see cref="CommandBudgetAsync"/> exactly.
    /// </summary>
    /// <param name="dbPath">Explicit scope.db path; null to discover from CWD.</param>
    /// <param name="windowDays">Requested look-back window in days [1..3650]. Default 30.</param>
    /// <param name="fromUtc">Optional custom-range start (UTC); see <see cref="CommandBudgetAsync"/>.</param>
    /// <param name="toUtc">Optional custom-range end (UTC); see <see cref="CommandBudgetAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<ScopeCommandResult> CommandBaseLaunchCostsAsync(
        string? dbPath,
        int windowDays = 30,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        windowDays = Math.Max(1, Math.Min(3650, windowDays));
        var window = ResolveBudgetWindow(windowDays, fromUtc, toUtc);

        CommandBaseLaunchResponse Empty() => new(
            WindowDays: windowDays,
            FromUtc: window.From.UtcDateTime.ToString("o"),
            ToUtc: window.To.UtcDateTime.ToString("o"),
            TotalSpendUsd: null,
            ByBase: [],
            ByConversation: []);

        try
        {
            var resolvedDb = FindDbPath(dbPath, searchAllParents: false);
            var scopeDir = resolvedDb != null ? Path.GetDirectoryName(resolvedDb)! : null;
            var telemetryDbPath = scopeDir != null ? Path.Combine(scopeDir, "telemetry.db") : null;

            if (telemetryDbPath == null || !File.Exists(telemetryDbPath))
                return ScopeCommandResult.Success(Empty().ToJsonString());

            try
            {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
                await using var telConn = await SQLite.OpenDbAsync(telemetryDbPath, cancellationToken).ConfigureAwait(false);
#pragma warning restore MA0004
                // SAME canonical aggregate the Cost Ledger sums; by_base is its base-grouped view and
                // by_conversation its universal cc-grouped view (T-2026-06-19-04). Both partition the same
                // full population, so SUM(by_conversation) == SUM(by_base) == total_spend_usd by construction.
                var canonical = BuildLedgerRollupAggregate(telConn, scopeDir, resolvedDb, window);
                var byBase = canonical.ByBase
                    .Select(b => new BudgetBaseLaunchRow(b.BaseSessionKey, b.SpendUsd, b.GenerationCount, b.TotalTokens))
                    .ToList();
                var byConversation = canonical.ByConversation
                    .Select(c => new BudgetConversationRow(c.ConversationId, c.SpendUsd, c.LaunchCount, c.GenerationCount, c.TotalTokens))
                    .ToList();
                var response = new CommandBaseLaunchResponse(
                    WindowDays: windowDays,
                    FromUtc: window.From.UtcDateTime.ToString("o"),
                    ToUtc: window.To.UtcDateTime.ToString("o"),
                    TotalSpendUsd: canonical.TotalSpendUsd,
                    ByBase: byBase,
                    ByConversation: byConversation);
                return ScopeCommandResult.Success(response.ToJsonString());
            }
            catch (SqliteException ex)
            {
                Log.Debug("CommandBaseLaunchCosts: telemetry.db unavailable ({Message}); returning empty rollup.", ex.Message);
                return ScopeCommandResult.Success(Empty().ToJsonString());
            }
        }
        catch (Exception ex)
        {
            Log.Warning("CommandBaseLaunchCostsAsync failed (non-fatal): {Message}", ex.Message);
            return ScopeCommandResult.Success(Empty().ToJsonString());
        }
    }

    /// <summary>Floor for any custom-range start, matching the FE DateRangePicker floor (owner decision).</summary>
    private static readonly DateTimeOffset CustomRangeFloorUtc =
        new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Hard ceiling on a custom-range length, matching the window_days ceiling (T-2026-06-16-15).</summary>
    private const int CustomRangeMaxDays = 3650;

    /// <summary>
    /// Resolves the effective <see cref="CostAggregator.CostWindow"/> for the budget ledger. When BOTH
    /// <paramref name="fromUtc"/> and <paramref name="toUtc"/> are supplied AND the pair recovers to a
    /// valid range, returns a <see cref="CostAggregator.CostWindow.Custom"/> window (windowDays ignored);
    /// otherwise returns the rolling <paramref name="windowDays"/> window (back-compat).
    ///
    /// <para>Validation + clamping (analytical, never throws):</para>
    /// <list type="bullet">
    ///   <item>The start is clamped UP to the 2020-01-01T00:00:00Z floor (a start below the floor is honest,
    ///   just bounded, rather than silently discarded).</item>
    ///   <item>A range whose end is strictly before its (post-floor-clamp) start is REJECTED -> fall back
    ///   to windowDays (an inverted range is a caller bug, not a clampable value).</item>
    ///   <item>A range longer than <see cref="CustomRangeMaxDays"/> days is clamped by moving the start
    ///   forward to <c>to - 3650d</c> (preserving the requested end, the more recent / more useful bound).</item>
    /// </list>
    /// </summary>
    private static CostAggregator.CostWindow ResolveBudgetWindow(
        int windowDays, DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        if (fromUtc is { } from && toUtc is { } to)
        {
            // Normalize to UTC so the window math + by_day bucketing are zone-stable regardless of the
            // offset the caller parsed with (backend is ALWAYS UTC).
            var fromU = from.ToUniversalTime();
            var toU = to.ToUniversalTime();

            // Floor clamp: never start a custom range before the picker floor.
            if (fromU < CustomRangeFloorUtc) fromU = CustomRangeFloorUtc;

            // Inverted range is unrecoverable -> honest fall back to window_days.
            if (toU < fromU)
            {
                Log.Warning(
                    "CommandBudget: custom range to_utc {To} precedes from_utc {From}; falling back to window_days={Days}.",
                    toU.ToString("o"), fromU.ToString("o"), windowDays);
                return CostAggregator.CostWindow.Rolling(windowDays);
            }

            // Ceiling clamp: cap the range length, preserving the requested (more recent) end.
            var maxSpan = TimeSpan.FromDays(CustomRangeMaxDays);
            if (toU - fromU > maxSpan)
            {
                var clampedFrom = toU - maxSpan;
                // The clamp must not push the start back below the floor.
                fromU = clampedFrom < CustomRangeFloorUtc ? CustomRangeFloorUtc : clampedFrom;
            }

            return CostAggregator.CostWindow.Custom(fromU, toU);
        }

        // Either bound missing -> back-compat rolling window.
        return CostAggregator.CostWindow.Rolling(windowDays);
    }

    private static CommandBudgetResponse BuildBudgetLedger(
        SqliteConnection telConn,
        string? scopeDir,
        string? scopeDbPath,
        CostAggregator.CostWindow window,
        int windowDays)
    {
        // CANONICAL headline + in-scope breakdowns (T-2026-06-16-04, Shape A1):
        // total_spend / tokens / cost_provenance / provenance_breakdown / by_agent / by_model
        // are a SUM of per-session TraceRollup totals over the window -- the SAME Compute the
        // Accounting tab runs. This makes the ledger total == sum of per-session rollups BY
        // CONSTRUCTION (divergence impossible). The old obs_cost_estimates COST SUM is gone from
        // the headline (live: that table had 0 rows -> the ledger was blank).
        var canonical = BuildLedgerRollupAggregate(telConn, scopeDir, scopeDbPath, window);

        var byAgent = canonical.ByAgent
            .Select(g => new BudgetAgentRow(g.Label, g.SpendUsd, g.SessionCount))
            .ToList();

        // By-model: single-model sessions only (mixed-model split is DEFERRED). Canonicalize the
        // model id and merge rows that collapse to the same canonical id. Cache-inclusive tokens.
        var byModel = canonical.ByModel
            .Select(g =>
            {
                var canonicalModel = ModelIdNormalizer.Normalize(g.Model) ?? g.Model;
                return new BudgetModelRow(
                    canonicalModel, g.SpendUsd, g.Tokens.Input, g.Tokens.Output, g.SessionCount,
                    g.Tokens.CacheCreation, g.Tokens.CacheRead);
            })
            .GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .Select(grp => new BudgetModelRow(
                grp.Key,
                // RAW summed spend (round once at render, from raw; T-2026-06-20-20 Piece 1): the
                // canonical-model merge here re-sums per-model raw rows, so it must NOT pre-round or the
                // by_model SUM would drift from the headline. Display rounds once.
                grp.Sum(r => r.SpendUsd),
                grp.Sum(r => r.InputTokens),
                grp.Sum(r => r.OutputTokens),
                grp.Sum(r => r.RunCount),
                grp.Sum(r => r.CacheCreationTokens),
                grp.Sum(r => r.CacheReadTokens)))
            .OrderByDescending(r => r.SpendUsd)
            .ToList();

        // ---- by_day: CANONICAL time-series (T-2026-06-16-13) ----
        // by_day is now derived from the SAME per-session rollups as the headline (each session's full
        // cost attributed to its UTC start day; zero-filled across the window). This RECONCILES:
        // sum(by_day spend) == total_spend_usd by construction. The legacy CostAggregator.AggregateByDay
        // path read the empty obs_cost_estimates table and rendered an all-zero "Spend over time" chart
        // while the headline was nonzero (the live regression).
        //
        // canonical.ByDay is empty ONLY when the aggregate degraded to empty (no obs schema / no window
        // sessions / failure). In that case fall back to the same zero-filled continuous axis the empty
        // ledger emits, so the chart always gets every day in the window (a contract the FE relies on).
        var byDay = canonical.ByDay.Count > 0
            ? canonical.ByDay.Select(d => new BudgetDayRow(d.Date, d.SpendUsd, d.SessionCount)).ToList()
            : ZeroFilledByDayRows(window);

        // ---- by_tool_type / by_day_tool_type: CANONICAL per-tool-call cost (T-2026-06-20-04, OBS-08) ----
        // RECONCILED. These now derive from the SAME per-session population the headline sums, via
        // WireCostAttribution (each session's canonical cost partitioned down to its tool-call rows + a
        // reserved model_turn bucket for pure-text turns). SUM(by_tool_type spend incl. model_turn) ==
        // total_spend_usd by construction. The old CostAggregator.AggregateByToolType /
        // AggregateByDayAndToolType path SUMMED the obs_cost_estimates projection (a ~2309x model fan-out
        // that over-counted by ~1009x); those methods are DELETED so that projection can never be summed
        // for these dimensions again. Contract unchanged: by_tool_type stays an array of
        // {taxonomy_id, spend_usd, run_count}; only the VALUES are correct now and a model_turn id may appear.
        var byToolType = canonical.ByToolType
            .Select(g => new BudgetToolTypeRow(g.TaxonomyId, g.SpendUsd, g.RunCount))
            .ToList();
        var byDayToolType = canonical.ByDayToolType
            .Select(g => new BudgetDayToolTypeRow(g.Date, g.TaxonomyId, g.SpendUsd, g.RunCount))
            .ToList();

        var tok = canonical.Tokens;

        return new CommandBudgetResponse(
            WindowDays: windowDays,
            FromUtc: window.From.UtcDateTime.ToString("o"),
            ToUtc: window.To.UtcDateTime.ToString("o"),
            TotalSpendUsd: canonical.TotalSpendUsd,
            CostProvenance: canonical.CostProvenance,
            ProvenanceBreakdown: canonical.ProvenanceBreakdown.ToList(),
            ByDay: byDay,
            ByAgent: byAgent,
            ByModel: byModel,
            ByToolType: byToolType,
            ByDayToolType: byDayToolType,
            TotalInputTokens: tok.Input,
            TotalOutputTokens: tok.Output,
            TotalCacheCreationTokens: tok.CacheCreation,
            TotalCacheReadTokens: tok.CacheRead,
            TotalTokens: tok.Total);
    }

    /// <summary>
    /// Builds the zero-filled by_day continuous axis (every UTC day in the window, spend 0 / run_count 0)
    /// used when there is no spend to attribute. Shared by the empty ledger and the empty-aggregate
    /// fallback so the "Spend over time" chart always receives a continuous axis.
    /// </summary>
    private static List<BudgetDayRow> ZeroFilledByDayRows(CostAggregator.CostWindow window)
    {
        var byDay = new List<BudgetDayRow>();
        var cursor = window.From.UtcDateTime.Date;
        var end = window.To.UtcDateTime.Date;
        while (cursor <= end)
        {
            byDay.Add(new BudgetDayRow(cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 0m, 0));
            cursor = cursor.AddDays(1);
        }
        return byDay;
    }

    private static CommandBudgetResponse EmptyBudgetLedger(
        CostAggregator.CostWindow window, int windowDays)
    {
        // Build zero-filled by_day without a telemetry connection.
        var byDay = ZeroFilledByDayRows(window);

        return new CommandBudgetResponse(
            WindowDays: windowDays,
            FromUtc: window.From.UtcDateTime.ToString("o"),
            ToUtc: window.To.UtcDateTime.ToString("o"),
            TotalSpendUsd: null,
            CostProvenance: Cron.Run.CostProvenance.Unavailable,
            ProvenanceBreakdown: [],
            ByDay: byDay,
            ByAgent: [],
            ByModel: [],
            ByToolType: [],
            ByDayToolType: []);
    }

    // ---- workspace ----

    private static CommandWorkspaceStatus BuildWorkspaceStatus(string? dbPath, string? scopeDir)
    {
        var indexState = "missing";
        string? indexedAt = null;
        string? solutionPath = null;

        if (dbPath != null && File.Exists(dbPath))
        {
            try
            {
                using var conn = SQLite.OpenDb(dbPath);
                var db = new IndexDb(conn);
                indexedAt = db.GetMetadata("indexed_at");
                solutionPath = db.GetMetadata("solution_path");
                indexState = indexedAt != null ? "fresh" : "stale";
            }
            catch (Exception)
            {
                indexState = "unavailable";
            }
        }

        var workspaceId = scopeDir != null
            ? TraceIdentity.Resolve(scopeDir).WorkspaceId
            : null;

        // Proxy reachability: check if proxy.json exists and implies port 5135.
        // We don't make an actual HTTP call here (offline-first); we surface presence/absence.
        bool? proxyReachable = null;
        if (scopeDir != null)
        {
            var proxyStatePath = Path.Combine(scopeDir, "proxy.json");
            var proxyState = ProxyState.TryLoad(proxyStatePath);
            proxyReachable = proxyState != null;
        }

        return new CommandWorkspaceStatus(
            IndexPath: dbPath,
            WorkspaceId: workspaceId,
            IndexState: indexState,
            IndexedAt: indexedAt,
            SolutionPath: solutionPath,
            ServerReachable: true, // trivially true -- we are the server
            ProxyReachable: proxyReachable);
    }

    // ---- entitlement ----

    private static CommandEntitlementStatus BuildEntitlementStatus(
        IEntitlementProvider provider, string? scopeDir)
    {
        try
        {
            var state = provider.GetState();
            var statusStr = state.Status switch
            {
                EntitlementStatus.Free => "free",
                EntitlementStatus.Trial => "trial",
                EntitlementStatus.Paid => "paid",
                EntitlementStatus.Grace => "grace",
                EntitlementStatus.Expired => "expired",
                _ => "unchecked",
            };

            // last_refresh_at: read the fetched_at_utc from the cache file if present.
            string? lastRefreshAt = null;
            try
            {
                var cacheJson = File.Exists(EntitlementCache.CacheFilePath())
                    ? File.ReadAllText(EntitlementCache.CacheFilePath())
                    : null;
                if (cacheJson != null)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(cacheJson);
                    if (doc.RootElement.TryGetProperty("fetched_at_utc", out var v))
                        lastRefreshAt = v.GetString();
                }
            }
            catch { /* best effort */ }

            return new CommandEntitlementStatus(
                Tier: state.Tier,
                Status: statusStr,
                LastRefreshAt: lastRefreshAt,
                ExpiresAt: state.ExpiresAt?.ToString("o"),
                GraceRemainingSeconds: state.GraceRemaining.HasValue
                    ? (long)state.GraceRemaining.Value.TotalSeconds
                    : null);
        }
        catch (Exception ex)
        {
            Log.Debug("CommandStatus.BuildEntitlementStatus failed: {Message}", ex.Message);
            return new CommandEntitlementStatus("free", "unchecked", null, null, null);
        }
    }

    // ---- uplink ----

    private static CommandUplinkStatus BuildUplinkStatus(string? scopeDir)
    {
        // Uplink is opt-in and currently minimal. We report it as unavailable
        // unless a future uplink-state file is present in .scope/.
        // The proxy.json presence confirms the metering proxy is (or was) active;
        // it does not confirm content-free scope-trace-v1 uplink.
        return new CommandUplinkStatus(Available: false, LastSuccessAt: null);
    }

    // ---- security ----

    private static CommandSecurityStatus BuildSecurityStatus(string? scopeDir)
    {
        var capturePayloads = !string.Equals(
            Environment.GetEnvironmentVariable("SCOPE_CAPTURE_PAYLOADS"), "0",
            StringComparison.OrdinalIgnoreCase);
        return new CommandSecurityStatus(
            CaptureMode: "local_only",
            PayloadCapture: capturePayloads);
    }

    // ---- budget ----

    private static CommandBudgetSummary BuildBudgetSummary(
        SqliteConnection telConn, string? scopeDir, string? scopeDbPath)
    {
        try
        {
            var window = CostAggregator.CostWindow.Last7Days();

            // CANONICAL (T-2026-06-16-04): the compact summary card uses the SAME per-session rollup
            // sum the deep ledger and the Accounting tab use, so the CommandView budget card and the
            // Cost Ledger headline can never disagree. Top drivers = top-5 by_agent rows.
            var canonical = BuildLedgerRollupAggregate(telConn, scopeDir, scopeDbPath, window);

            var drivers = canonical.ByAgent
                .Take(5)
                .Select(g => new CommandBudgetTopDriver(
                    Label: g.Label,
                    SpendUsd: g.SpendUsd,
                    RunCount: g.SessionCount))
                .ToList();

            return new CommandBudgetSummary(
                WindowDays: window.ApproximateDays,
                TotalSpendUsd: canonical.TotalSpendUsd,
                CostProvenance: canonical.CostProvenance,
                TopDrivers: drivers);
        }
        catch (Exception ex)
        {
            Log.Debug("CommandStatus.BuildBudgetSummary failed: {Message}", ex.Message);
            return UnavailableBudget();
        }
    }

    private static CommandBudgetSummary UnavailableBudget()
        => new CommandBudgetSummary(
            WindowDays: 7,
            TotalSpendUsd: null,
            CostProvenance: Cron.Run.CostProvenance.Unavailable,
            TopDrivers: []);

    // ---- operating ----

    private static CommandOperatingStatus BuildOperatingStatus(SqliteConnection telConn, int activeRuns)
    {
        long traceCount24h = 0;
        try
        {
            var since24h = DateTime.UtcNow.AddHours(-24).ToString("o");
            using var cmd = telConn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) FROM obs_agent_sessions
                WHERE last_seen_at >= $since;
                """;
            cmd.Parameters.AddWithValue("$since", since24h);
            traceCount24h = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        }
        catch (SqliteException) { /* table may not exist */ }

        return new CommandOperatingStatus(
            ActiveRuns: activeRuns,
            TraceCount24H: traceCount24h,
            WarningCount: 0);
    }

    // ---- recent runs ----

    private static List<CommandRecentRun> BuildRecentRuns(string? dbPath, SqliteConnection telConn)
    {
        if (dbPath == null || !File.Exists(dbPath)) return [];

        try
        {
            // Get the 10 most recent cron runs, joined with agent labels, in a single query.
            using var conn = SQLite.OpenDb(dbPath);

            // Check that cron_runs table exists (schema may not yet be provisioned).
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cron_runs';";
            if (Convert.ToInt32(checkCmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return [];

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT r.id, a.label, r.status, r.started_at, r.completed_at,
                       r.total_cost_usd, r.cost_provenance, r.session_key
                FROM cron_runs r
                JOIN cron_agents a ON a.id = r.cron_agent_id
                ORDER BY r.started_at DESC
                LIMIT 10;
                """;

            using var reader = cmd.ExecuteReader();
            var runs = new List<CommandRecentRun>();
            while (reader.Read())
            {
                runs.Add(new CommandRecentRun(
                    RunId: reader.GetString(0),
                    AgentLabel: reader.GetString(1),
                    Status: reader.GetString(2),
                    StartedAt: reader.GetString(3),
                    CompletedAt: reader.IsDBNull(4) ? null : reader.GetString(4),
                    TotalCostUsd: reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    CostProvenance: reader.IsDBNull(6)
                        ? Cron.Run.CostProvenance.Unavailable
                        : reader.GetString(6),
                    SessionKey: reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
            return runs;
        }
        catch (Exception ex)
        {
            Log.Debug("CommandStatus.BuildRecentRuns failed: {Message}", ex.Message);
            return [];
        }
    }

    // ---- recent sessions ----

    private static List<CommandRecentSession> BuildRecentSessions(SqliteConnection telConn)
    {
        try
        {
            var auditDb = new ObservabilityAuditDb(telConn);
            if (!auditDb.HasObservabilitySchema()) return [];

            var rows = auditDb.GetSessionsWithEventCounts(10);
            return rows.Select(r => new CommandRecentSession(
                SessionKey: Get(r, "session_key"),
                AgentType: NullIfMissing(r, "agent_type"),
                FirstSeenAt: NullIfMissing(r, "first_seen_at"),
                LastSeenAt: NullIfMissing(r, "last_seen_at"),
                RequestCount: LongFrom(r, "request_count")))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug("CommandStatus.BuildRecentSessions failed: {Message}", ex.Message);
            return [];
        }
    }

    // ---- local helpers ----

    private static string Get(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static string? NullIfMissing(Dictionary<string, object?> row, string key)
    {
        var v = Get(row, key);
        return v.Length == 0 || string.Equals(v, "-", StringComparison.Ordinal) ? null : v;
    }

    private static long LongFrom(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return 0;
        return v switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            string s when long.TryParse(s, CultureInfo.InvariantCulture, out var p) => p,
            _ => 0,
        };
    }
}
