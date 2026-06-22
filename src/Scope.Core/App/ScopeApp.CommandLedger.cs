namespace Scope.Core.App;

using Microsoft.Data.Sqlite;
using Scope.Core.CostEstimation;
using Scope.Core.Telemetry;
using Scope.Core.Telemetry.Trace;
using Scope.Core.Utilities;
using Serilog;
using System.Globalization;

public static partial class ScopeApp
{
    // =========================================================================
    // Canonical ledger reconciliation (T-2026-06-16-04, Shape A / A1).
    //
    // Both the Cost Ledger (GET /api/command/budget) and the trace Accounting tab
    // (GET /api/trace/rollup) MUST derive cost + tokens from the SAME canonical
    // resolver so they can never diverge. The Accounting tab calls
    // TraceRollup.Compute per session. This helper makes the ledger headline (and
    // the in-scope breakdowns) a SUM of those same per-session rollups over the
    // window, so the ledger total == sum of per-session rollup totals BY
    // CONSTRUCTION (same Compute, same inputs).
    //
    // The old CostAggregator obs_cost_estimates COST path is RETIRED: it no longer
    // feeds ANY ledger dimension. The headline/tokens/by_agent/by_model/by_day were
    // moved to per-session rollups (T-2026-06-16-04/-13); by_tool_type and
    // by_day_tool_type are now derived here too (T-2026-06-20-04, OBS-08) from the
    // SAME per-session population via WireCostAttribution, so EVERY dimension
    // reconciles to the headline by construction and the 2309x obs_cost_estimates
    // model fan-out can never be summed again (those CostAggregator methods are deleted).
    //
    // CORRECTNESS TRAP: window sessions are enumerated by
    // obs_agent_sessions.session_key (what the rollup keys off), NOT
    // obs_requests.agent_session_key (mostly '' and 0 overlap with cron-* keys).
    // =========================================================================

    /// <summary>
    /// Cache-INCLUSIVE token totals (input+output+cache_creation+cache_read), matching the rollup
    /// wire-path total rule (TraceRollup.TaskTokens).
    /// </summary>
    private sealed record LedgerTokenTotals(
        long Input, long Output, long CacheCreation, long CacheRead, long Total);

    // SpendUsd on every Ledger row is decimal (T-2026-06-20-20 Piece 2): summed from the canonical
    // decimal rollup (MoneyAggregate.ValueUsd) / decimal attribution, so every breakdown reconciles to
    // the headline EXACTLY. They flow out to the Budget DTOs (also decimal -> serialized as JSON numbers).

    /// <summary>One canonical by-agent row: label + summed cost + count of contributing sessions.</summary>
    private sealed record LedgerAgentRow(string Label, decimal SpendUsd, int SessionCount);

    /// <summary>One canonical by-model row (single-model sessions only): cache-inclusive token split.</summary>
    private sealed record LedgerModelRow(
        string Model, decimal SpendUsd, LedgerTokenTotals Tokens, int SessionCount);

    /// <summary>One canonical by-day row: UTC calendar day, summed spend, count of contributing sessions.</summary>
    private sealed record LedgerDayRow(string Date, decimal SpendUsd, int SessionCount);

    /// <summary>
    /// One canonical by-tool-type row (T-2026-06-20-04, OBS-08): a <see cref="ToolTaxonomy"/> bucket id
    /// (or the reserved <see cref="ToolTaxonomy.ModelTurn"/> for pure-text turns), the SUM of the
    /// per-tool-call costs <see cref="WireCostAttribution"/> attributed to that bucket across the window,
    /// and the count of contributing per-tool-call (or per-task-root) rows. Derived from the SAME
    /// per-session rollups the headline sums, so SUM(by_tool_type spend incl. model_turn) ==
    /// total_spend_usd by construction (the attribution partitions each session's canonical cost
    /// exactly). RunCount is informational only (a tool-call/turn count, not a session count).
    /// </summary>
    private sealed record LedgerToolTypeRow(string TaxonomyId, decimal SpendUsd, int RunCount);

    /// <summary>
    /// One canonical by-day+tool-type row (T-2026-06-20-04, OBS-08): the same canonical attributed cost
    /// as <see cref="LedgerToolTypeRow"/>, additionally bucketed by the contributing session's UTC start
    /// day (matching the by_day attribution rule), so SUM over this list == total_spend_usd as well.
    /// </summary>
    private sealed record LedgerDayToolTypeRow(string Date, string TaxonomyId, decimal SpendUsd, int RunCount);

    /// <summary>
    /// One canonical by-base-launch row (T-2026-06-19-03 Wave 2 / ST-06): the durable launcher BASE
    /// session key, the SUM of its per-generation rollup costs, the count of contributing generations,
    /// and the cache-inclusive token total over those generations. A `/clear` splits one CLI launch
    /// into multiple per-generation sessions; this rolls them back up so per-CLI-launch cost == SUM of
    /// its generations. ADDITIVE: derived from the SAME per-session rollups the headline sums, grouped
    /// by base_session_key, so sum(by_base spend) == total_spend_usd by construction.
    /// </summary>
    private sealed record LedgerBaseRow(
        string BaseSessionKey, decimal SpendUsd, int GenerationCount, long TotalTokens);

    /// <summary>
    /// One canonical by-conversation row (T-2026-06-19-04): the UNIVERSAL cc-grouped view. A conversation
    /// is a host Claude Code / Codex <c>session_id</c> (cc); EVERY in-window session is grouped by its
    /// <c>cc_session_id</c>, and a session genuinely lacking a cc buckets under its base key as its own
    /// singleton conversation. <c>ConversationId = cc ?? baseKey</c>. <c>LaunchCount</c> =
    /// COUNT(DISTINCT base_session_key) in the group; <c>GenerationCount</c> = contributing session rows;
    /// spend/tokens = Σ. Because this partitions the SAME full population <c>by_base</c> does,
    /// <c>SUM(by_conversation.spend) == SUM(by_base.spend) == total</c> for every window (same for tokens).
    /// </summary>
    private sealed record LedgerConversationRow(
        string ConversationId, decimal SpendUsd, int LaunchCount, int GenerationCount, long TotalTokens);

    /// <summary>
    /// The reconciled window aggregate, derived ENTIRELY from per-session TraceRollup totals.
    /// total/tokens/provenance/by_agent/by_model/by_day/by_base/by_conversation reconcile to the
    /// Accounting tab by construction.
    /// </summary>
    private sealed record LedgerRollupAggregate(
        decimal? TotalSpendUsd,
        LedgerTokenTotals Tokens,
        string CostProvenance,
        IReadOnlyList<BudgetProvenanceRow> ProvenanceBreakdown,
        IReadOnlyList<LedgerAgentRow> ByAgent,
        IReadOnlyList<LedgerModelRow> ByModel,
        IReadOnlyList<LedgerDayRow> ByDay,
        IReadOnlyList<LedgerBaseRow> ByBase,
        IReadOnlyList<LedgerConversationRow> ByConversation,
        IReadOnlyList<LedgerToolTypeRow> ByToolType,
        IReadOnlyList<LedgerDayToolTypeRow> ByDayToolType);

    private static LedgerRollupAggregate EmptyLedgerAggregate()
        => new(null, new LedgerTokenTotals(0, 0, 0, 0, 0),
            Cron.Run.CostProvenance.Unavailable, [], [], [], [], [], [], [], []);

    /// <summary>
    /// Computes the canonical window aggregate: enumerates window sessions by
    /// obs_agent_sessions.session_key, computes each session's rollup through the SAME
    /// TraceRollup.Compute the Accounting tab uses (via the pre-fetched-session seam, one window
    /// fetch + one GetFullTrace per session), and sums cost + cache-inclusive tokens + provenance.
    /// Never throws: any failure degrades to an honest empty aggregate.
    /// </summary>
    /// <param name="telConn">Open telemetry.db connection (obs_* tables).</param>
    /// <param name="scopeDir">.scope directory (pricing resolution + cron_runs join in scope.db).</param>
    /// <param name="scopeDbPath">scope.db path for the cron_runs -&gt; cron_agents label join; null skips labels.</param>
    /// <param name="window">The cost window.</param>
    private static LedgerRollupAggregate BuildLedgerRollupAggregate(
        SqliteConnection telConn,
        string? scopeDir,
        string? scopeDbPath,
        CostAggregator.CostWindow window)
    {
        try
        {
            // Ensure obs schema is current on this live connection before reading new columns
            // (telemetry.db is migrated lazily by writers; a reader must self-ensure or risk a
            // missing-column failure -- the lazy-schema reader gap).
            new ObservabilitySchemaManager(telConn).EnsureSchema();

            var auditDb = new ObservabilityAuditDb(telConn);
            if (!auditDb.HasObservabilitySchema())
                return EmptyLedgerAggregate();

            // Resolve pricing once for the whole window so provider_estimated / scope_estimated
            // fallbacks price exactly as the Accounting tab does. Never throws.
            ModelPricingConfig? resolvedPricing = null;
            string? pricesAsOf = null;
            try
            {
                if (scopeDir != null)
                {
                    resolvedPricing = PricingResolver.TryResolve(
                        scopeDir, Environment.GetEnvironmentVariable("SCOPE_PRICING_PATH"));
                    pricesAsOf = resolvedPricing?.PricesAsOf;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Ledger pricing resolution failed (non-fatal): {Message}", ex.Message);
            }

            var identity = scopeDir != null ? TraceIdentity.Resolve(scopeDir) : TraceIdentity.Empty;
            var docs = new TraceDocuments(auditDb, identity, "ledger", pricesAsOf, resolvedPricing);

            var fromStr = window.From.UtcDateTime.ToString("o");
            var toStr = window.To.UtcDateTime.ToString("o");

            // ONE window fetch (eliminates the per-session 100k scan).
            var sessionRows = docs.SessionWindowRows(fromStr, toStr);
            if (sessionRows.Count == 0)
                return EmptyLedgerAggregate();

            // Cron label map: session_key -> cron_agents.label (absorbs T-2026-06-16-05).
            var labelBySession = BuildCronLabelMap(scopeDbPath);

            // decimal accumulators (T-2026-06-20-20 Piece 2): every spend SUM is exact, so the headline
            // and every breakdown reconcile to the cent.
            decimal? totalCost = null;
            long inSum = 0, outSum = 0, cacheCreateSum = 0, cacheReadSum = 0, totalTokSum = 0;

            // Provenance + by_agent + by_model + by_day accumulators.
            var provBuckets = new Dictionary<string, (decimal Spend, int Count)>(StringComparer.Ordinal);
            var agentBuckets = new Dictionary<string, (decimal Spend, int Count)>(StringComparer.Ordinal);
            var modelBuckets = new Dictionary<string, (decimal Spend, long In, long Out, long CCreate, long CRead, long Tot, int Count)>(StringComparer.OrdinalIgnoreCase);
            // by_day buckets keyed by UTC calendar day (yyyy-MM-dd).
            var dayBuckets = new Dictionary<string, (decimal Spend, int Count)>(StringComparer.Ordinal);
            // by_base buckets keyed by the durable launcher BASE key (T-2026-06-19-03 Wave 2 / ST-06):
            // a `/clear` splits one CLI launch into per-generation sessions; this rolls those generations
            // back up to their base so per-CLI-launch cost == SUM of its generations. Token total is
            // cache-inclusive, matching the headline.
            var baseBuckets = new Dictionary<string, (decimal Spend, long Tokens, int Count)>(StringComparer.Ordinal);
            // by_conversation buckets keyed by the host cc session id (T-2026-06-19-04): the UNIVERSAL
            // partition of the SAME full population by_base covers. A session with a cc groups by it (a cli
            // resume's two bases share one cc => one conversation, launch_count=2); a session genuinely
            // lacking a cc buckets under its own base key (its own singleton conversation). Distinct base
            // keys per group drive launch_count; contributing rows drive generation_count. By construction
            // sum(by_conversation.spend) == sum(by_base.spend) == total (both partition the same sessions).
            var conversationBuckets = new Dictionary<string, (decimal Spend, long Tokens, int GenCount, HashSet<string> Bases)>(StringComparer.Ordinal);
            // by_tool_type / by_day_tool_type buckets (T-2026-06-20-04, OBS-08): the CANONICAL per-tool-call
            // cost derivation. For each session WireCostAttribution partitions the canonical per-task cost
            // down to the tool-call rows (joined via wire_calls.tool_use_ids -> obs_agent_events.tool_use_id);
            // each per-row cost buckets by ToolTaxonomy.Resolve(<tool name on that event>), and cost on a
            // pure-text/task-root row (no tool_use) lands in the reserved ToolTaxonomy.ModelTurn bucket. The
            // attribution sums EXACTLY to the session rollup total, so SUM(by_tool_type incl. model_turn)
            // == total_spend_usd by construction. NO obs_cost_estimates is read for these dimensions.
            var toolTypeBuckets = new Dictionary<string, (decimal Spend, int Count)>(StringComparer.Ordinal);
            var dayToolTypeBuckets = new Dictionary<(string Day, string TaxonomyId), (decimal Spend, int Count)>();

            foreach (var row in sessionRows)
            {
                var sessionKey = TraceProjection.Str(row, "session_key");
                if (string.IsNullOrEmpty(sessionKey)) continue;

                // Same Compute the Accounting tab runs, via the pre-fetched-session seam. RollupWithRows
                // returns BOTH the merged trace rows and the canonical rollup over those SAME rows, so the
                // by_tool_type attribution below partitions the rollup's own cost (exact-sum guarantee).
                // since=null here: the window itself (first_seen/last_seen overlap) already bounds
                // the session set; per-session retention is applied by the server-facing single
                // overload, not the ledger batch (which intentionally totals all in-window sessions).
                var rollupAndRows = docs.RollupWithRows(sessionKey, row, since: null);
                var body = rollupAndRows.Rollup;
                var totals = body.Totals;

                // ---- cost (immutable per-session rollup value) ----
                var sessionCost = totals.Cost.ValueUsd;
                if (sessionCost.HasValue)
                    totalCost = (totalCost ?? 0) + sessionCost.Value;

                // ---- tokens (cache-inclusive, matching the rollup wire path) ----
                var t = totals.Tokens;
                inSum += t.Input ?? 0;
                outSum += t.Output ?? 0;
                cacheCreateSum += t.CacheCreation ?? 0;
                cacheReadSum += t.CacheRead ?? 0;
                totalTokSum += t.Total ?? 0;

                // ---- provenance breakdown (session-level provenance, weighted by session cost) ----
                if (sessionCost is > 0)
                {
                    var prov = totals.CostProvenance;
                    var pv = provBuckets.GetValueOrDefault(prov);
                    provBuckets[prov] = (pv.Spend + sessionCost.Value, pv.Count + 1);
                }

                // ---- by_agent (cron label or raw session key fallback) ----
                var label = labelBySession.GetValueOrDefault(sessionKey) ?? sessionKey;
                var av = agentBuckets.GetValueOrDefault(label);
                agentBuckets[label] = (av.Spend + (sessionCost ?? 0), av.Count + 1);

                // ---- by_day (canonical time-series; T-2026-06-16-13) ----
                // Attribute the session's FULL cost to ONE UTC calendar day = date() of first_seen_at
                // (the session START day). This GUARANTEES sum(by_day) == total_spend_usd. A session that
                // straddles UTC midnight is bucketed to its start day only; intra-session multi-day cost
                // splitting stays DEFERRED (a session-level total cannot reproduce a sub-day split). This
                // is a minor attribution nuance, not a reconciliation break.
                var dayKey = SessionUtcDay(row, window);
                if (dayKey != null)
                {
                    var dv = dayBuckets.GetValueOrDefault(dayKey);
                    dayBuckets[dayKey] = (dv.Spend + (sessionCost ?? 0), dv.Count + 1);
                }

                // ---- by_base (base-launch rollup; T-2026-06-19-03 Wave 2 / ST-06) ----
                // Roll the per-generation session cost up to its durable launcher BASE so a `/clear`-split
                // launch reports as ONE per-CLI-launch total. The base is the stored base_session_key
                // column when present; legacy/cron/MCP/host rows (NULL column) fall back to the analytic
                // SplitEffectiveKey inverse (a suffixless key is its own base, generation 0), so every
                // session contributes to exactly one base bucket and sum(by_base) == total_spend_usd.
                var storedBase = TraceProjection.Str(row, "base_session_key");
                var baseKey = string.IsNullOrEmpty(storedBase)
                    ? SessionKeyResolver.SplitEffectiveKey(sessionKey).Base
                    : storedBase;
                var bb = baseBuckets.GetValueOrDefault(baseKey);
                baseBuckets[baseKey] = (
                    bb.Spend + (sessionCost ?? 0),
                    bb.Tokens + (t.Total ?? 0),
                    bb.Count + 1);

                // ---- by_conversation (universal cc-grouping; T-2026-06-19-04; v16: keyed on conversation_id) ----
                // Partition EVERY in-window session by its resume-stable conversation id. conversation_id
                // (schema v16 column) is the transcript stem -- continuous across /resume -- so two
                // generations with DIFFERENT cc_session_id but the SAME transcript file merge into ONE bucket.
                // Fallback chain for legacy / non-CC rows:
                //   1. schema v16: conversation_id column (preferred; transcript-stem based).
                //   2. cc_session_id (schema v11; pre-v16 rows or non-transcript captures).
                //   3. baseKey (cron/MCP without a captured cc; stays a singleton).
                // bucket key = (machine_id, conversationId) composite so cross-machine rows don't merge.
                // machine_id null collapses to empty string for legacy rows (all null machine_id = one
                // local bucket -- they were all captured on this machine).
                // BudgetConversationRow.ConversationId is the conversation component alone (not composite)
                // so the frontend matches by it; the composite key lives ONLY in conversationBuckets here.
                var dbConvId = TraceProjection.Str(row, "conversation_id");
                var dbMachineId = TraceProjection.Str(row, "machine_id");
                var ccId = TraceProjection.Str(row, "cc_session_id");
                var convIdComponent = !string.IsNullOrEmpty(dbConvId) ? dbConvId
                    : !string.IsNullOrEmpty(ccId) ? ccId
                    : baseKey;
                var machineComponent = string.IsNullOrEmpty(dbMachineId) ? "" : dbMachineId;
                var bucketKey = machineComponent + " " + convIdComponent;
                if (!conversationBuckets.TryGetValue(bucketKey, out var cb))
                    cb = (0, 0, 0, new HashSet<string>(StringComparer.Ordinal));
                cb.Bases.Add(baseKey);
                conversationBuckets[bucketKey] = (
                    cb.Spend + (sessionCost ?? 0),
                    cb.Tokens + (t.Total ?? 0),
                    cb.GenCount + 1,
                    cb.Bases);

                // ---- by_model (single-model sessions only) ----
                // The rollup emits a totals.Breakdown ONLY for unambiguous single-model sessions;
                // its ModelId is the canonical model. Mixed-model sessions are DEFERRED (no split).
                var modelId = totals.Breakdown?.ModelId;
                if (!string.IsNullOrEmpty(modelId))
                {
                    var mv = modelBuckets.GetValueOrDefault(modelId);
                    modelBuckets[modelId] = (
                        mv.Spend + (sessionCost ?? 0),
                        mv.In + (t.Input ?? 0),
                        mv.Out + (t.Output ?? 0),
                        mv.CCreate + (t.CacheCreation ?? 0),
                        mv.CRead + (t.CacheRead ?? 0),
                        mv.Tot + (t.Total ?? 0),
                        mv.Count + 1);
                }

                // ---- by_tool_type / by_day_tool_type (canonical per-tool-call cost; T-2026-06-20-04) ----
                // Partition this session's canonical cost down to its tool-call rows + task roots, then
                // bucket each per-row cost by ToolTaxonomy. The attribution sums EXACTLY to the session
                // rollup total (it redistributes that total, never re-prices), so adding every session's
                // buckets keeps SUM(by_tool_type incl. model_turn) == totalCost. A row absent from the
                // attribution map carries no cost and contributes nothing (exactly as it does to the total).
                AttributeSessionToolTypes(
                    rollupAndRows.Rows, body, dayKey, toolTypeBuckets, dayToolTypeBuckets);
            }

            // Per-row spend is RAW (full-precision) here. Rounding happens ONCE, at render, from raw
            // (T-2026-06-20-20 Piece 1): pre-rounding each row to 6dp then summing rounded rows on a
            // surface drifts ~1e-6 from the headline (which rounds the raw window total). Carrying raw
            // rows makes round(sum(raw rows)) == round(raw total) == the headline, so every breakdown
            // reconciles to the headline at the display layer. Only the canonical TotalSpendUsd below is
            // rounded (it IS the headline); the CLI/HTTP double contract is unchanged.
            var provBreakdown = provBuckets
                .Select(kv => new BudgetProvenanceRow(kv.Key, kv.Value.Spend, kv.Value.Count))
                .OrderByDescending(r => r.SpendUsd)
                .ToList();

            var byAgent = agentBuckets
                .Select(kv => new LedgerAgentRow(kv.Key, kv.Value.Spend, kv.Value.Count))
                .OrderByDescending(r => r.SpendUsd)
                .ToList();

            var byModel = modelBuckets
                .Select(kv => new LedgerModelRow(
                    kv.Key,
                    kv.Value.Spend,
                    new LedgerTokenTotals(kv.Value.In, kv.Value.Out, kv.Value.CCreate, kv.Value.CRead, kv.Value.Tot),
                    kv.Value.Count))
                .OrderByDescending(r => r.SpendUsd)
                .ToList();

            // by_base: roll per-generation spend up to the launcher base (T-2026-06-19-03 Wave 2 / ST-06).
            // sum(by_base spend) == totalCost by construction (every session lands in exactly one base
            // bucket). Ordered by spend desc so the picker/launch view shows the costliest launches first.
            var byBase = baseBuckets
                .Select(kv => new LedgerBaseRow(
                    kv.Key, kv.Value.Spend, kv.Value.Count, kv.Value.Tokens))
                .OrderByDescending(r => r.SpendUsd)
                .ToList();

            // by_conversation: the universal cc-grouped partition (T-2026-06-19-04; v16: keyed on
            // (machine_id, conversation_id) composite). launch_count = distinct base keys in the group;
            // generation_count = contributing rows. sum(spend) == totalCost by construction (same full
            // population as by_base, partitioned by conversation instead of base).
            // The composite bucket key is "machineComponent conversationIdComponent"; emit only the
            // conversation component as ConversationId so the frontend API shape is unchanged.
            var byConversation = conversationBuckets
                .Select(kv =>
                {
                    // Extract the conversation component: everything after the first space separator.
                    var spaceIdx = kv.Key.IndexOf(' ', StringComparison.Ordinal);
                    var convIdEmit = spaceIdx >= 0 ? kv.Key[(spaceIdx + 1)..] : kv.Key;
                    return new LedgerConversationRow(
                        convIdEmit, kv.Value.Spend, kv.Value.Bases.Count, kv.Value.GenCount, kv.Value.Tokens);
                })
                .OrderByDescending(r => r.SpendUsd)
                .ToList();

            // by_day: zero-fill EVERY UTC day in the window (continuous chart axis), then overlay the
            // bucketed session spend. sum(spend) over this list == totalCost by construction (each
            // session's full cost lands in exactly one in-window day). run_count = contributing sessions.
            var byDay = BuildZeroFilledByDay(window, dayBuckets);

            // by_tool_type: the canonical attributed-cost partition (T-2026-06-20-04, OBS-08). Each
            // taxonomy bucket (plus the reserved model_turn) carries the SUM of the per-tool-call costs
            // WireCostAttribution distributed from the per-session rollups; the partition is exact, so
            // sum(by_tool_type spend) == totalCost. Ordered by spend desc so the costliest tool types lead.
            var byToolType = toolTypeBuckets
                .Select(kv => new LedgerToolTypeRow(kv.Key, kv.Value.Spend, kv.Value.Count))
                .OrderByDescending(r => r.SpendUsd)
                .ToList();

            // by_day_tool_type: the same canonical partition, additionally split by the contributing
            // session's UTC start day (the by_day attribution rule), so sum(by_day_tool_type) == totalCost
            // too. Ordered by date asc then spend desc for a stable stacked time-series.
            var byDayToolType = dayToolTypeBuckets
                .Select(kv => new LedgerDayToolTypeRow(
                    kv.Key.Day, kv.Key.TaxonomyId, kv.Value.Spend, kv.Value.Count))
                .OrderBy(r => r.Date, StringComparer.Ordinal)
                .ThenByDescending(r => r.SpendUsd)
                .ToList();

            // Window-level dominant provenance: strongest present among contributing sessions.
            var provenance = DominantProvenance(provBuckets.Keys, totalCost);

            return new LedgerRollupAggregate(
                TotalSpendUsd: totalCost is > 0 ? Math.Round(totalCost.Value, 6) : (decimal?)null,
                Tokens: new LedgerTokenTotals(inSum, outSum, cacheCreateSum, cacheReadSum, totalTokSum),
                CostProvenance: provenance,
                ProvenanceBreakdown: provBreakdown,
                ByAgent: byAgent,
                ByModel: byModel,
                ByDay: byDay,
                ByBase: byBase,
                ByConversation: byConversation,
                ByToolType: byToolType,
                ByDayToolType: byDayToolType);
        }
        catch (Exception ex)
        {
            Log.Warning("BuildLedgerRollupAggregate failed (non-fatal): {Message}", ex.Message);
            return EmptyLedgerAggregate();
        }
    }

    /// <summary>
    /// Attributes one session's canonical cost across <see cref="ToolTaxonomy"/> buckets (T-2026-06-20-04,
    /// OBS-08) and folds the result into the window-level by_tool_type / by_day_tool_type accumulators.
    /// <para>
    /// Calls <see cref="WireCostAttribution.Attribute"/> over the session's merged rows + canonical
    /// rollup (the SAME pair the headline summed) to get a per-trace-entry cost map that sums EXACTLY to
    /// the session rollup total. Each cost-bearing trace ENTRY is then bucketed:
    /// <list type="bullet">
    ///   <item>a tool-call EVENT row -> <c>ToolTaxonomy.Resolve(&lt;tool name on event_data_json&gt;)</c>;</item>
    ///   <item>any other attributed row (a task-root span carrying a text-only/header-attributed turn with
    ///   no tool_use) -> the reserved <see cref="ToolTaxonomy.ModelTurn"/> bucket.</item>
    /// </list>
    /// Because the attribution partitions the session's cost without remainder, the sum of the buckets
    /// added here equals the session's contribution to the headline total. The whole session's tool-type
    /// cost is attributed to its single UTC start day (<paramref name="sessionDay"/>) for by_day_tool_type,
    /// matching the by_day attribution rule so both day series reconcile to the headline. Never throws.
    /// </para>
    /// </summary>
    private static void AttributeSessionToolTypes(
        IReadOnlyList<Dictionary<string, object?>> rows,
        Scope.Core.Telemetry.Trace.RollupBody rollup,
        string? sessionDay,
        Dictionary<string, (decimal Spend, int Count)> toolTypeBuckets,
        Dictionary<(string Day, string TaxonomyId), (decimal Spend, int Count)> dayToolTypeBuckets)
    {
        var costByEntryId = WireCostAttribution.Attribute(rows, rollup);
        if (costByEntryId.Count == 0) return;

        // Index event rows by id so an attributed entry can recover its tool name (tool-call events only).
        var toolByEventId = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!string.Equals(TraceProjection.Str(r, "_source"), "event", StringComparison.Ordinal)) continue;
            var id = TraceProjection.Str(r, "id");
            if (id == null) continue;
            toolByEventId[id] = ParseEventToolName(TraceProjection.Str(r, "event_data_json"));
        }

        foreach (var (entryId, attributed) in costByEntryId)
        {
            if (attributed.ValueUsd is not { } cost) continue; // unavailable cost contributes nothing

            // A tool-call event row resolves to its taxonomy; anything else (a task-root span = a
            // pure-text/header-attributed turn with no tool_use) is a model turn.
            var taxonomyId = toolByEventId.TryGetValue(entryId, out var tool)
                ? ToolTaxonomy.Resolve(tool)
                : ToolTaxonomy.ModelTurn;

            var tv = toolTypeBuckets.GetValueOrDefault(taxonomyId);
            toolTypeBuckets[taxonomyId] = (tv.Spend + cost, tv.Count + 1);

            if (sessionDay != null)
            {
                var key = (sessionDay, taxonomyId);
                var dv = dayToolTypeBuckets.GetValueOrDefault(key);
                dayToolTypeBuckets[key] = (dv.Spend + cost, dv.Count + 1);
            }
        }
    }

    /// <summary>
    /// Parses an event's <c>event_data_json</c> for its <c>tool</c> name (the raw host tool name the
    /// taxonomy classifies). Returns null for non-object JSON, a missing field, or malformed JSON; never
    /// throws. Mirrors the tool-name extraction <see cref="Scope.Core.Telemetry.Trace.TraceTopActions"/> uses.
    /// </summary>
    private static string? ParseEventToolName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty("tool", out var t)
                && t.ValueKind == System.Text.Json.JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a session's UTC calendar day (yyyy-MM-dd) from its <c>first_seen_at</c> column. Parses
    /// the ISO-8601 timestamp with explicit UTC normalization so the day bucket is stable regardless of
    /// the local machine zone. Returns null when the timestamp is missing or unparseable (the session's
    /// cost is then excluded from by_day; never crashes the aggregate).
    /// </summary>
    private static string? SessionUtcDay(Dictionary<string, object?> row, CostAggregator.CostWindow window)
    {
        var firstSeen = TraceProjection.Str(row, "first_seen_at");
        if (string.IsNullOrEmpty(firstSeen)) return null;
        if (!DateTimeOffset.TryParse(
                firstSeen,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            return null;
        // CLAMP into the window's day range. A session is enumerated by overlap (last_seen_at >= from),
        // so its first_seen_at can predate window.From; without clamping its day key would fall outside
        // the zero-filled axis and its spend would be DROPPED from by_day while still counted in the
        // total -> sum(by_day) != total. Attribute such a session to the window's first day so the
        // reconciliation invariant holds for every enumerated session. (first_seen_at <= window.To is
        // guaranteed by the enumeration query, so the upper clamp is defensive only.)
        var day = parsed.UtcDateTime.Date;
        var from = window.From.UtcDateTime.Date;
        var to = window.To.UtcDateTime.Date;
        if (day < from) day = from;
        if (day > to) day = to;
        return day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the continuous by_day time-series: emits a row for EVERY UTC calendar day in the window
    /// (zero spend / zero count when no session started that day), overlaying the bucketed per-session
    /// spend. Preserves the existing zero-fill-across-window chart contract. sum(SpendUsd) == the window
    /// total by construction because every contributing session lands in exactly one in-window day.
    /// </summary>
    private static List<LedgerDayRow> BuildZeroFilledByDay(
        CostAggregator.CostWindow window,
        Dictionary<string, (decimal Spend, int Count)> dayBuckets)
    {
        var byDay = new List<LedgerDayRow>();
        var cursor = window.From.UtcDateTime.Date;
        var end = window.To.UtcDateTime.Date;
        while (cursor <= end)
        {
            var key = cursor.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var bucket = dayBuckets.GetValueOrDefault(key);
            // RAW per-day spend (round once at render, from raw; T-2026-06-20-20 Piece 1) so
            // round(sum(by_day raw)) == round(raw total) == headline.
            byDay.Add(new LedgerDayRow(key, bucket.Spend, bucket.Count));
            cursor = cursor.AddDays(1);
        }
        return byDay;
    }

    /// <summary>
    /// Builds a session_key -&gt; cron-agent-label map by joining scope.db cron_runs -&gt; cron_agents
    /// (the same join shape as <see cref="BuildRecentRuns"/>). Sessions with no cron run keep their raw
    /// key at the call site. Returns an empty map when scope.db / the cron tables are absent. Never throws.
    /// </summary>
    private static Dictionary<string, string> BuildCronLabelMap(string? scopeDbPath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (scopeDbPath == null || !File.Exists(scopeDbPath)) return map;

        try
        {
            using var conn = SQLite.OpenDb(scopeDbPath);

            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('cron_runs','cron_agents');";
            if (Convert.ToInt32(checkCmd.ExecuteScalar(), CultureInfo.InvariantCulture) < 2) return map;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT r.session_key, a.label
                FROM cron_runs r
                JOIN cron_agents a ON a.id = r.cron_agent_id
                WHERE r.session_key IS NOT NULL AND r.session_key <> '';
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var key = reader.GetString(0);
                var label = reader.IsDBNull(1) ? key : reader.GetString(1);
                // Last write wins is fine: a session_key maps to a single cron run/agent.
                map[key] = label;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("BuildCronLabelMap failed (non-fatal): {Message}", ex.Message);
        }
        return map;
    }

    // Strength order so the window-level provenance reports the strongest source present.
    private static int ProvenanceStrength(string provenance) => provenance switch
    {
        Cron.Run.CostProvenance.WireExact => 5,
        Cron.Run.CostProvenance.JoinedExact => 4,
        Cron.Run.CostProvenance.ProviderEstimated => 3,
        Cron.Run.CostProvenance.ScopeEstimated => 2,
        Cron.Run.CostProvenance.Unmetered => 1,
        _ => 0,
    };

    /// <summary>
    /// Picks the window-level dominant provenance: the strongest provenance present among
    /// contributing (cost-bearing) sessions. Unavailable when no cost was resolved.
    /// </summary>
    private static string DominantProvenance(IEnumerable<string> provenances, decimal? totalCost)
    {
        if (totalCost is not > 0) return Cron.Run.CostProvenance.Unavailable;
        string best = Cron.Run.CostProvenance.Unavailable;
        var bestStrength = -1;
        foreach (var p in provenances)
        {
            var s = ProvenanceStrength(p);
            if (s > bestStrength) { bestStrength = s; best = p; }
        }
        return bestStrength >= 0 ? best : Cron.Run.CostProvenance.Unavailable;
    }
}
