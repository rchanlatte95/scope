namespace Scope.Core.Telemetry.Trace;

using System.Text.Json;
using Scope.Core.CostEstimation;
using Scope.Core.Utilities;
using static Scope.Core.Telemetry.Trace.TraceProjection;
using System.Globalization;

/// <summary>
/// Computes the scope-trace-v1 accounting rollup (kind=rollup) as a strict partition over a merged
/// trace (the GetFullTrace rows for one session, already annotated with _parent_span_id,
/// _total_cost_usd, _total_tokens/_token_source by ObservabilityAuditDb). Pure: no db access.
///
/// Coherence is by construction (design coherenceRules):
///  - Every cost-, token-, and time-bearing trace row is attributed to exactly one by_task row
///    (its top-level span ancestor) or to the mandatory unattributed remainder. Totals are the
///    SUM of the rows, never independently computed, so totals == sum(by_task) + unattributed.
///  - Token dedup: within each resolved span tree only the TOPMOST span carrying exact tokens
///    contributes its block to sums; deeper exact-token spans are excluded (prevents double count).
///  - Time accounting is dual: wall_ms is the subject window; busy_ms_sum is the cpu-style sum of
///    per-task busy_ms. Zero-duration hook spans contribute busy_ms=0 with time_source=zero_duration.
/// </summary>
public static class TraceRollup
{
    /// <summary>
    /// Builds the rollup body for a session from its merged trace rows plus the session window.
    /// <paramref name="requestCount"/> and <paramref name="eventCount"/> are the session-level totals
    /// (kept separate from the attributed counts, which are informational on each row).
    /// </summary>
    /// <param name="pricingVersion">
    /// The resolved "prices as of" stamp (<see cref="Scope.Core.CostEstimation.ModelPricingConfig.PricesAsOf"/>),
    /// or null when no pricing config is configured. Stamped onto cost blocks that carry an actual
    /// value (source != "unknown"); unknown cost (value null) leaves pricing_version null because no
    /// price was applied. A date string is content-free (a timestamp, not content), so it is safe in
    /// both tiers.
    /// </param>
    /// <param name="estimationPricing">
    /// Optional external pricing config used as a higher-priority override for estimation (local
    /// pricing.json or LiteLLM cache). When null or when the model is absent from it, the built-in
    /// seed table (<see cref="Scope.Core.CostEstimation.SeedPricingTable"/>) is tried via
    /// <see cref="Scope.Core.CostEstimation.ModelIdNormalizer"/>. Only used for
    /// <c>provider_estimated</c> fallback when a task has wire tokens but no cost from
    /// <c>obs_cost_estimates</c>. Exact costs from <c>obs_cost_estimates</c> always win.
    /// </param>
    public static RollupBody Compute(
        TraceSubject subject,
        string? windowFrom,
        string? windowTo,
        IReadOnlyList<Dictionary<string, object?>> rows,
        long requestCount,
        long eventCount,
        string? pricingVersion = null,
        Scope.Core.CostEstimation.ModelPricingConfig? estimationPricing = null,
        string? defaultEstimationModel = null)
    {
        var aliases = ComputeAliases(rows);

        // ---- index spans ----
        // span id -> resolved parent span id (write-side parent_span_id wins; else temporal _parent_span_id)
        var spanResolvedParent = new Dictionary<string, string?>(StringComparer.Ordinal);
        var spanRows = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!(string.Equals(Str(r, "_source"), "span", StringComparison.Ordinal))) continue;
            var id = Str(r, "id");
            if (id == null) continue;
            spanRows[id] = r;
            var parent = Str(r, "parent_span_id") ?? Str(r, "_parent_span_id");
            spanResolvedParent[id] = parent;
        }

        // top-level ancestor of any span: walk resolved parents until the parent is not a known span.
        string TopLevelAncestor(string spanId)
        {
            var current = spanId;
            var guard = 0;
            while (guard++ < 1024)
            {
                var parent = spanResolvedParent.GetValueOrDefault(current);
                if (parent == null || !spanRows.ContainsKey(parent)) return current;
                current = parent;
            }
            return current;
        }

        // ---- request costs grouped by their span (top-level ancestor) or unattributed ----
        // _total_cost_usd is a per-span subtree rollup on span rows; for partition coherence we
        // attribute the RAW per-request cost to the request's resolved span instead, so the
        // by_task token/cost/time sums and the totals are computed from the same constituent rows.
        // (Request cost source: obs_cost_estimates is local-only "estimate"; we cannot read the
        // per-request value from the merged rows here without a separate query, so cost uses the
        // span-level _total_cost_usd attributed to the top-level span. See coherence note below.)

        // ---- exact tokens: topmost span per resolved tree ----
        // A span's exact tokens count ONLY if no ancestor span in its resolved chain also carries
        // exact tokens (topmost wins). Map: spanId -> its TokenBlock when it is the topmost exact.
        var exactTokenSpans = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, r) in spanRows)
        {
            if (string.Equals(Str(r, "_token_source"), "exact", StringComparison.Ordinal)) exactTokenSpans.Add(id);
        }
        bool IsTopmostExact(string spanId)
        {
            if (!exactTokenSpans.Contains(spanId)) return false;
            var current = spanResolvedParent.GetValueOrDefault(spanId);
            var guard = 0;
            while (current != null && spanRows.ContainsKey(current) && guard++ < 1024)
            {
                if (exactTokenSpans.Contains(current)) return false; // an ancestor is exact -> not topmost
                current = spanResolvedParent.GetValueOrDefault(current);
            }
            return true;
        }

        // ---- accumulate per top-level span ----
        var topLevelOrder = new List<string>();
        var acc = new Dictionary<string, TaskAcc>(StringComparer.Ordinal);

        TaskAcc AccFor(string topId)
        {
            if (!acc.TryGetValue(topId, out var a))
            {
                a = new TaskAcc();
                acc[topId] = a;
                topLevelOrder.Add(topId);
            }
            return a;
        }

        foreach (var (id, r) in spanRows)
        {
            var top = TopLevelAncestor(id);
            var a = AccFor(top);

            // time: each span contributes its own busy_ms to its top-level task's busy sum.
            var started = Str(r, "started_at");
            var completed = Str(r, "completed_at");
            var ts = TimeSourceOf(started, completed);
            var busy = DurationMs(started, completed);
            a.BusyMs += busy;
            if (string.Equals(ts, "measured", StringComparison.Ordinal)) a.MeasuredCount++;
            else if (string.Equals(ts, "zero_duration", StringComparison.Ordinal)) a.ZeroDurationCount++;
            else a.UnknownTimeCount++;

            // tokens: wire-sourced exact usage (ST-03) and host-completion exact usage are kept in
            // SEPARATE accumulators so the per-task precedence pick (wire > exact > unknown) happens
            // once, after the subtree is fully walked. This prevents double counting when a task has
            // BOTH a host Agent-completion usage AND wire calls for its tool_use_ids: exactly one
            // source is chosen per task, never summed.
            var hasWire = (Str(r, "_wire_tokens") ?? "").Length > 0 && Long(r, "_wire_total_tokens").HasValue;
            var topmostExact = IsTopmostExact(id);

            // wire: every span carrying DIRECT wire tokens contributes them to its task's wire subtree
            // sum (wire dedup is at the join: each wire call counted once per span by AnnotateWireTokens).
            if (hasWire)
            {
                a.HasWireTokens = true;
                a.WireInput += Long(r, "_wire_input_tokens") ?? 0;
                a.WireOutput += Long(r, "_wire_output_tokens") ?? 0;
                a.WireCacheCreation += Long(r, "_wire_cache_creation_tokens") ?? 0;
                a.WireCacheRead += Long(r, "_wire_cache_read_tokens") ?? 0;
                a.WireTotal += Long(r, "_wire_total_tokens") ?? 0;
                // Track the model(s) seen on wire spans for provider_estimated cost fallback.
                var wireModel = Str(r, "_wire_model");
                if (wireModel != null)
                    (a.WireModels ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(wireModel);
            }

            // exact: only topmost-exact spans contribute (dedup by construction).
            if (topmostExact)
            {
                a.HasExactTokens = true;
                a.TokenInput += Long(r, "_input_tokens") ?? Long(r, "input_tokens") ?? 0;
                a.TokenOutput += Long(r, "_output_tokens") ?? Long(r, "output_tokens") ?? 0;
                a.TokenTotal += Long(r, "_total_tokens") ?? Long(r, "total_tokens") ?? 0;
            }

            // recorded model (Tier 2, schema v9): the model the work was REQUESTED with (cron
            // --model, or a hook's tool_input.model on a dispatch). Tracked per task so the
            // un-metered exact-token case can be priced from the recorded model instead of the
            // assumed default. Collected from every span in the subtree; the recorded tier prices
            // only when exactly ONE distinct recorded model resolves to a rate (honest, not invented).
            var recordedModel = Str(r, "model");
            if (recordedModel != null)
                (a.RecordedModels ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(recordedModel);

            // coverage: a span is "known" if it contributes any chosen-source tokens, else "unknown".
            if (hasWire || topmostExact) a.TokenKnown++;
            else a.TokenUnknown++;

            // cost: attribute the span's own DIRECT cost contribution. _total_cost_usd is a subtree
            // rollup placed on the TOP-LEVEL span by EnrichSpanCosts; to avoid double counting we
            // read it only on the top-level span itself.
            // Also capture pinned-cost columns (schema v6) from the top-level span: when present,
            // the rollup uses the immutable stored snapshot instead of re-deriving.
            if (string.Equals(id, top, StringComparison.Ordinal))
            {
                // DB boundary (T-2026-06-20-20 Piece 2): _total_cost_usd is a SQLite REAL (double).
                // Convert to decimal at this earliest read point so ALL downstream accumulation /
                // redistribution is exact decimal arithmetic. The DB representation is unchanged.
                var cost = Double(r, "_total_cost_usd");
                if (cost is > 0) { a.CostUsd += (decimal)cost.Value; a.CostKnown++; }
                else a.CostUnknown++;

                // Pinned-cost snapshot (schema v6): present only on cron-materialized root spans.
                // Same DB boundary: pinned_cost_usd is a REAL; convert to decimal here.
                var pinnedCost = Double(r, "pinned_cost_usd");
                if (pinnedCost is > 0)
                {
                    a.PinnedCostUsd = (decimal)pinnedCost.Value;
                    a.PinnedModelId = Str(r, "pinned_model_id");
                    a.PinnedPricesAsOf = Str(r, "pinned_prices_as_of");
                    a.PinnedBreakdownJson = Str(r, "pinned_breakdown_json");
                }
            }
        }

        // ---- unattributed: cost/token/time-bearing rows with no span home ----
        // Requests whose resolved span is absent (no _parent_span_id pointing at a known span).
        // Their cost is already folded into span _total_cost_usd when they DO have a span parent;
        // requests without a span parent are the unattributed remainder.
        var un = new TaskAcc();
        foreach (var r in rows)
        {
            if (!(string.Equals(Str(r, "_source"), "request", StringComparison.Ordinal))) continue;
            var parentSpan = Str(r, "_parent_span_id");
            if (parentSpan != null && spanRows.ContainsKey(parentSpan)) continue; // folded into a span task
            // This request is unattributed. We do not have its per-request cost in the merged rows
            // (only span-level rollups), so it contributes to the unknown coverage honestly.
            un.CostUnknown++;
        }

        // ---- build rows ----
        var byTask = new List<RollupRow>(topLevelOrder.Count);
        foreach (var topId in topLevelOrder)
        {
            var a = acc[topId];
            var spanRow = spanRows[topId];
            var subj = new TraceSubject(
                Kind: "span",
                Id: topId,
                DisplayId: TraceIdCodec.DisplayId(topId),
                Alias: aliases.GetValueOrDefault(topId));

            // Attempt provider_estimated cost fallback when: no exact obs_cost_estimates cost
            // is present BUT wire tokens with a known model are available. If that also fails,
            // attempt scope_estimated fallback from exact tokens using Sonnet-tier default.
            // Exact (obs_cost_estimates) always wins; estimation is the fallback only.
            var (taskCostValue, taskCostSource, taskCostPricingVersion, taskCostBasis, taskBreakdown, taskProvenance) =
                ResolveTaskCost(a, pricingVersion, estimationPricing, defaultEstimationModel);

            // Coverage: the "known" count includes the fallback-estimated cost when it resolved;
            // the "unknown" count is the span tally (not the obs_cost_estimates coverage for the
            // row-level, since a fallback cost fills one unknown slot).
            var fallbackCostResolved = taskCostValue.HasValue && a.CostKnown == 0;
            var rowCostKnown = a.CostKnown + (fallbackCostResolved ? 1 : 0);
            var rowCostUnknown = Math.Max(0, a.CostUnknown - (fallbackCostResolved ? 1 : 0));

            byTask.Add(new RollupRow(
                Subject: subj,
                Cost: new MoneyAggregate(
                    taskCostValue,
                    taskCostSource,
                    PricingVersion: taskCostPricingVersion,
                    new Coverage(rowCostKnown, rowCostUnknown),
                    Breakdown: taskBreakdown,
                    CostProvenance: taskProvenance),
                Tokens: TaskTokens(a),
                BusyMs: a.BusyMs,
                TimeSource: RowTimeSource(a),
                RequestCount: 0,
                EventCount: 0,
                CostEstimationBasis: taskCostBasis,
                Breakdown: taskBreakdown));
        }

        var unattributed = new RollupRow(
            Subject: new TraceSubject("other", "", "0000000"),
            Cost: new MoneyAggregate(null, "unknown", null, new Coverage(0, un.CostUnknown)),
            Tokens: new TokenAggregate(null, null, null, null, null, "unknown", new Coverage(0, 0)),
            BusyMs: 0,
            TimeSource: "unknown",
            RequestCount: 0,
            EventCount: 0);

        // ---- totals: SUM the chosen-source by_task blocks (by construction) ----
        // Each task reports exactly one token block (its precedence pick: wire > exact > unknown).
        // Totals sum those same blocks plus the unattributed remainder, so
        // totals == sum(by_task) + unattributed holds regardless of the source mix.
        long busySum = unattributed.BusyMs;
        long? tokenTotalSum = null, tokenInSum = null, tokenOutSum = null, tokenCacheCreationSum = null, tokenCacheReadSum = null;
        int tokKnown = 0, tokUnknown = 0;
        // decimal (T-2026-06-20-20 Piece 2): the totals cost SUM is exact, so sum(by_task) == totals.
        decimal? costSum = null;
        int costKnown = 0, costUnknown = un.CostUnknown;
        int measured = 0, zero = 0, unknownTime = 0;
        var anyWireTokens = false;
        var anyExactTokens = false;
        var costSourceStrength = SourceStrength("unknown");
        var anyProviderEstimated = false;
        var anyScopeEstimated = false;
        CostEstimationBasis? sessionBasis = null;

        foreach (var a in acc.Values)
        {
            busySum += a.BusyMs;
            measured += a.MeasuredCount;
            zero += a.ZeroDurationCount;
            unknownTime += a.UnknownTimeCount;

            var t = TaskTokens(a);
            if (string.Equals(t.Source, "wire", StringComparison.Ordinal)) anyWireTokens = true;
            else if (string.Equals(t.Source, "exact", StringComparison.Ordinal)) anyExactTokens = true;
            if (t.Total.HasValue)
            {
                tokenTotalSum = (tokenTotalSum ?? 0) + (t.Total ?? 0);
                tokenInSum = (tokenInSum ?? 0) + (t.Input ?? 0);
                tokenOutSum = (tokenOutSum ?? 0) + (t.Output ?? 0);
                // Sum cache components when present; null task components contribute 0 to the aggregate.
                if (t.CacheCreation.HasValue)
                    tokenCacheCreationSum = (tokenCacheCreationSum ?? 0) + t.CacheCreation.Value;
                if (t.CacheRead.HasValue)
                    tokenCacheReadSum = (tokenCacheReadSum ?? 0) + t.CacheRead.Value;
            }
            tokKnown += a.TokenKnown;
            tokUnknown += a.TokenUnknown;

            if (a.CostKnown > 0)
            {
                costSum = (costSum ?? 0) + a.CostUsd;
                costKnown += a.CostKnown;
                costSourceStrength = Math.Max(costSourceStrength, SourceStrength("estimate"));
            }
            else
            {
                // Check if this task qualifies for provider_estimated or scope_estimated fallback.
                // a.CostKnown == 0 here (we are in the else branch), so ResolveTaskCost will
                // try the estimation path; if it returns a non-null value, it came from estimation.
                var (estCost, _, _, estBasis, _, _) = ResolveTaskCost(a, pricingVersion, estimationPricing, defaultEstimationModel);
                if (estCost.HasValue)
                {
                    costSum = (costSum ?? 0) + estCost.Value;
                    costKnown++;
                    costSourceStrength = Math.Max(costSourceStrength, SourceStrength("estimate"));
                    // Track which estimation path was used for the session-level cost_provenance.
                    // recovered=true means wire model was used (provider_estimated);
                    // recovered=false means Sonnet default was assumed (scope_estimated).
                    if (estBasis is { Recovered: true }) anyProviderEstimated = true;
                    else anyScopeEstimated = true;
                    // Use the first resolved basis as the session-level basis (represents the
                    // dominant assumption; multi-task sessions may mix, but one basis per session).
                    sessionBasis ??= estBasis;
                }
            }
            costUnknown += a.CostUnknown;
        }

        // Aggregate token source: wire if any task is wire-sourced, else exact if any is exact, else
        // unknown. wire and exact are mixable across tasks (each is provider-grade exact usage); the
        // aggregate label reports the strongest/most-specific source present (wire is the most specific).
        var tokenSource = anyWireTokens ? "wire" : anyExactTokens ? "exact" : "unknown";

        // Map the aggregate cost-source strength to the 6-value CostProvenance vocabulary.
        // Money.source (4-value: wire|exact|estimate|unknown) stays untouched on cost primitives;
        // CostProvenance is the additive session/run-level summary field.
        // When the only costs are from the provider_estimated fallback, use ProviderEstimated.
        // When exact-token scope_estimated fallback was used (Sonnet default assumed), use ScopeEstimated.
        // When there is a mix of exact (obs_cost_estimates) and estimated, use ScopeEstimated
        // (a conservative label for mixed provenance). Wire/exact still win when present.
        var hasAnyObsExact = acc.Values.Any(a => a.CostKnown > 0);
        var costProvenanceStr = StrengthName(costSourceStrength) switch
        {
            "wire" => Scope.Core.Cron.Run.CostProvenance.WireExact,
            "exact" => Scope.Core.Cron.Run.CostProvenance.JoinedExact,
            "estimate" => anyScopeEstimated || hasAnyObsExact
                            ? Scope.Core.Cron.Run.CostProvenance.ScopeEstimated
                            : anyProviderEstimated && costKnown > 0
                                ? Scope.Core.Cron.Run.CostProvenance.ProviderEstimated
                                : Scope.Core.Cron.Run.CostProvenance.ScopeEstimated,
            _ => Scope.Core.Cron.Run.CostProvenance.Unavailable,
        };

        // When costs came from the seed estimation fallback (no external pricingVersion), use the
        // seed's AsOfDate as the pricing_version stamp so the FE can show "prices as of 2026-06-14".
        var anyEstimated = anyProviderEstimated || anyScopeEstimated;
        var effectivePricingVersion = pricingVersion
            ?? (anyEstimated && costKnown > 0 ? Scope.Core.CostEstimation.SeedPricingTable.AsOfDate : null);

        // Session-level basis: null when no estimation was used (exact metered or unavailable).
        var totalsBasis = costKnown > 0 ? sessionBasis : null;

        // ---- aggregate breakdown for totals (single-model sessions only) ----
        // Collect non-null breakdowns from by_task rows. Emit an aggregated breakdown on totals
        // ONLY when the session is unambiguous: all contributing breakdowns share the same
        // ModelId AND PricesAsOf, and every priced by_task row (cost.value_usd > 0) has a
        // breakdown. If any condition fails (mixed models, priced task missing a breakdown),
        // leave totalsBreakdown null -- honest, not invented.
        CostBreakdown? totalsBreakdown = null;
        {
            var contributingBreakdowns = byTask
                .Where(r => r.Cost.Breakdown is not null)
                .Select(r => r.Cost.Breakdown!)
                .ToList();

            if (contributingBreakdowns.Count > 0)
            {
                // All contributing breakdowns must share the same model and prices_as_of.
                var firstModel = contributingBreakdowns[0].ModelId;
                var firstPricesAsOf = contributingBreakdowns[0].PricesAsOf;
                var allSameModel = contributingBreakdowns.All(b =>
                    string.Equals(b.ModelId, firstModel, StringComparison.Ordinal) &&
                    string.Equals(b.PricesAsOf, firstPricesAsOf, StringComparison.Ordinal));

                // Every priced by_task row must have a breakdown (no priced task is missing one).
                var allPricedHaveBreakdown = byTask.All(r =>
                    r.Cost.ValueUsd is not > 0 || r.Cost.Breakdown is not null);

                if (allSameModel && allPricedHaveBreakdown)
                {
                    // Sum components by kind, preserving canonical order.
                    var kindOrder = new[] { "input", "output", "cache_read", "cache_write" };
                    var summedComponents = new List<CostBreakdownComponent>();
                    foreach (var kind in kindOrder)
                    {
                        var kindComponents = contributingBreakdowns
                            .SelectMany(b => b.Components.Where(c => string.Equals(c.Kind, kind, StringComparison.Ordinal)))
                            .ToList();
                        if (kindComponents.Count == 0) continue;

                        // Rate is the same for a given (model, kind); take from first occurrence.
                        var rate = kindComponents[0].RateUsdPerMtok;
                        var totalTokens = kindComponents.Sum(c => c.Tokens);
                        var totalCostUsd = kindComponents.Sum(c => c.CostUsd);
                        summedComponents.Add(new CostBreakdownComponent(kind, totalTokens, rate, totalCostUsd));
                    }

                    // pinned=true only if every contributing breakdown is pinned.
                    var allPinned = contributingBreakdowns.All(b => b.Pinned);

                    totalsBreakdown = new CostBreakdown(
                        ModelId: firstModel,
                        PricesAsOf: firstPricesAsOf,
                        Pinned: allPinned,
                        Components: summedComponents,
                        TotalUsd: costSum ?? 0m);
                }
            }
        }

        // ---- cost_provenance_breakdown: sum by_task rows by 6-value provenance ----
        // Only tasks with a non-null cost value are included (null-cost tasks contribute no spend).
        var provenanceBreakdown = byTask
            .Where(r => r.Cost.ValueUsd.HasValue && r.Cost.CostProvenance is not null)
            .GroupBy(r => r.Cost.CostProvenance!, StringComparer.Ordinal)
            .Select(g => new ProvenanceBreakdownRow(g.Key, g.Sum(r => r.Cost.ValueUsd ?? 0m), g.Count()))
            .OrderBy(r => r.Provenance, StringComparer.Ordinal)
            .ToList();
        List<ProvenanceBreakdownRow>? costProvenanceBreakdown = provenanceBreakdown.Count > 0 ? provenanceBreakdown : null;

        // ---- billing-class (T-2026-06-19-01 Phase 3b): orthogonal to cost_provenance ----
        // Derived from the session AUTH MODE (the session-key prefix: cron- runs with an API key, cli-
        // is interactive) crossed with the resolved provenance (wire-sourced spend on an interactive
        // session means it went through the proxy with a key -> a real charge). Conservative: anything
        // we cannot classify stays Unclassified, so legacy/non-session subjects carry no new label.
        // subject.Id is the session key when subject.Kind == "session"; for any other subject kind we
        // pass the id through and CostBillingClass.Derive returns Unclassified on an unknown prefix.
        // tokenSource ("wire" when any task is wire-sourced) is the auth-mode signal that distinguishes
        // a metered (billed) interactive session from a subscription (shadow) one.
        var billingClass = CostBillingClass.Derive(subject.Id, costProvenanceStr, tokenSource);

        var totals = new RollupTotals(
            // The totals cost carries the "prices as of" stamp when any constituent cost value was
            // applied (costKnown > 0); a totals block with no known cost (value null) leaves it null.
            Cost: new MoneyAggregate(costSum, StrengthName(costSourceStrength),
                costKnown > 0 ? effectivePricingVersion : null, new Coverage(costKnown, costUnknown),
                Breakdown: totalsBreakdown,
                CostBillingClass: billingClass),
            Tokens: new TokenAggregate(tokenInSum, tokenOutSum, tokenCacheCreationSum, tokenCacheReadSum, tokenTotalSum,
                tokenSource, new Coverage(tokKnown, tokUnknown)),
            WallMs: WallMs(windowFrom, windowTo),
            BusyMsSum: busySum,
            TimeCoverage: new TimeCoverage(measured, zero, unknownTime),
            RequestCount: requestCount,
            EventCount: eventCount,
            CostProvenance: costProvenanceStr,
            CostEstimationBasis: totalsBasis,
            Breakdown: totalsBreakdown,
            CostProvenanceBreakdown: costProvenanceBreakdown,
            CostBillingClass: billingClass);

        return new RollupBody(subject, new TraceWindow(windowFrom, windowTo), totals, byTask, unattributed);
    }

    private static string RowTimeSource(TaskAcc a)
    {
        if (a.MeasuredCount > 0) return "measured";
        if (a.ZeroDurationCount > 0) return "zero_duration";
        return "unknown";
    }

    /// <summary>
    /// Picks the single token block reported for a task, applying the source precedence
    /// wire &gt; exact &gt; unknown. wire wins when the task's resolved-span subtree carries
    /// wire-joined tokens (ST-03); the host Agent-completion exact block is then suppressed for the
    /// SAME task (never summed with wire) so the same work is counted once. Coverage counts (the
    /// known/unknown span tallies) are independent of which source is chosen.
    /// </summary>
    private static TokenAggregate TaskTokens(TaskAcc a)
    {
        if (a.HasWireTokens)
            // Cache is included in wire totals (owner-approved: token total includes cache so tokens
            // reconcile with the cost breakdown which already prices cache_read + cache_write).
            return new TokenAggregate(a.WireInput, a.WireOutput, a.WireCacheCreation, a.WireCacheRead,
                a.WireInput + a.WireOutput + a.WireCacheCreation + a.WireCacheRead, "wire",
                new Coverage(a.TokenKnown, a.TokenUnknown));
        if (a.HasExactTokens)
            return new TokenAggregate(a.TokenInput, a.TokenOutput, null, null, a.TokenTotal, "exact",
                new Coverage(a.TokenKnown, a.TokenUnknown));
        return new TokenAggregate(null, null, null, null, null, "unknown",
            new Coverage(0, a.TokenUnknown));
    }

    // The canonical fallback model for scope_estimated cost when the caller supplies no assumedModel
    // and no wire/recorded model is known. "recovered=false" in the basis signals that this is an
    // assumption, not a recovered value.
    private const string FallbackEstimationModel = "claude-sonnet-4-6";

    /// <summary>
    /// Resolves the (costValue, moneySource, pricingVersion, estimationBasis) quad for a task row.
    /// The <c>moneySource</c> is the 4-value Money.source (estimate|unknown) used on the
    /// <see cref="MoneyAggregate"/> primitive; the caller tracks whether the cost came from
    /// <c>provider_estimated</c> or <c>scope_estimated</c> separately for the session-level
    /// <c>cost_provenance</c> field.
    ///
    /// Priority:
    /// <list type="number">
    ///   <item>Exact cost from obs_cost_estimates (<c>a.CostKnown &gt; 0</c>) -> "estimate" money
    ///         source, <paramref name="pricingVersion"/> stamp, null basis.</item>
    ///   <item>Tier 1 -- provider-estimated fallback from wire tokens + single resolved model ->
    ///         "estimate" money source, seed version stamp, basis.source=wire (recovered=true). The
    ///         caller uses the return value to set <c>anyProviderEstimated = true</c>.</item>
    ///   <item>Tier 2 -- recorded-model fallback from exact tokens + a single resolved RECORDED model
    ///         (cron --model / hook tool_input.model) -> "estimate", seed version stamp,
    ///         basis.source=recorded (recovered=true), provenance provider_estimated.</item>
    ///   <item>Tier 3 -- scope-estimated fallback from exact tokens (no wire, no recorded, no obs
    ///         cost) -> "estimate", seed version stamp, basis.source=assumed (recovered=false; the
    ///         Sonnet/peg default). The caller uses the return value to set <c>anyScopeEstimated = true</c>.</item>
    ///   <item>Unknown -> (null, "unknown", null, null).</item>
    /// </list>
    /// Returns a non-null Value ONLY when a real (positive) cost was computed. Zero cost is
    /// treated as unknown (no price applied) to avoid silently suppressing the fallback for empty
    /// token buckets (which should not occur in practice but must not fabricate a $0 estimate).
    /// </summary>
    private static (decimal? Value, string MoneySource, string? PricingVersionStamp, CostEstimationBasis? Basis, CostBreakdown? Breakdown, string TaskProvenance) ResolveTaskCost(
        TaskAcc a,
        string? pricingVersion,
        Scope.Core.CostEstimation.ModelPricingConfig? estimationPricing,
        string? defaultEstimationModel)
    {
        // Priority 0: pinned-cost snapshot stored on the span at capture time (schema v6).
        // When present, the cost is immutable against future pricing table changes: return it
        // directly with pinned=true and DO NOT re-derive. The breakdown is deserialized from
        // the stored JSON so it matches the original capture exactly.
        if (a.PinnedCostUsd.HasValue && !string.IsNullOrEmpty(a.PinnedModelId) && !string.IsNullOrEmpty(a.PinnedPricesAsOf))
        {
            List<CostBreakdownComponent>? pinnedComponents = null;
            if (!string.IsNullOrEmpty(a.PinnedBreakdownJson))
            {
                try
                {
                    pinnedComponents = JsonSerializer.Deserialize(
                        a.PinnedBreakdownJson,
                        ScopeJsonSerializerContext.Default.ListCostBreakdownComponent);
                }
                catch (JsonException) { /* malformed stored JSON: treat as no breakdown */ }
            }
            var pinnedBreakdown = pinnedComponents is { Count: > 0 }
                ? new CostBreakdown(a.PinnedModelId, a.PinnedPricesAsOf, Pinned: true, pinnedComponents, TotalUsd: a.PinnedCostUsd.Value)
                : null;
            // Pinned snapshot derives from a metered cron run (wire/exact at capture), so its
            // attribution source is wire (recovered=true preserved via the derived flag).
            var pinnedBasis = new CostEstimationBasis(a.PinnedModelId, a.PinnedPricesAsOf, CostEstimationSource.Wire);
            return (a.PinnedCostUsd.Value, "estimate", a.PinnedPricesAsOf, pinnedBasis, pinnedBreakdown, Scope.Core.Cron.Run.CostProvenance.ProviderEstimated);
        }

        if (a.CostKnown > 0)
            return (a.CostUsd, "estimate", pricingVersion, null, null, Scope.Core.Cron.Run.CostProvenance.ScopeEstimated);

        // Provider-estimated fallback: requires wire tokens AND exactly one distinct model.
        if (a.HasWireTokens && a.WireModels is { Count: 1 })
        {
            string model;
            using (var e = a.WireModels.GetEnumerator()) { e.MoveNext(); model = e.Current; }

            var rate = Scope.Core.CostEstimation.ModelIdNormalizer.Lookup(model, estimationPricing);
            if (rate is not null)
            {
                // decimal rates straight from ModelPricing (no double round-trip). Absent cache rate -> 0m.
                var inRate = rate.InputUsdPerMillionTokens;
                var outRate = rate.OutputUsdPerMillionTokens;
                var cachedInRate = rate.CachedInputUsdPerMillionTokens ?? 0m;
                var cacheCreateRate = rate.CacheCreationUsdPerMillionTokens ?? 0m;

                // Canonical bucket arithmetic (decimal, T-2026-06-20-20 Piece 2). Cache terms are added
                // only when the bucket is positive AND the rate is present (the helper's guards); decimal
                // addition is exact and order-independent.
                var cost = Scope.Core.CostEstimation.CostPricing.ComputeBucketCost(
                    a.WireInput, a.WireOutput, a.WireCacheCreation, a.WireCacheRead, rate);

                if (cost > 0)
                {
                    // Return "estimate" for MoneyAggregate.Source (4-value); caller tracks
                    // provider_estimated for the 6-value rollup cost_provenance field.
                    // Semantic date: strip the time component from a fetch timestamp such as
                    // "2026-06-15T18:57:06.17Z" so prices_as_of is always yyyy-MM-dd.
                    var versionStamp = Scope.Core.CostEstimation.SeedPricingTable.ToSemanticDate(estimationPricing?.PricesAsOf);
                    var basis = new CostEstimationBasis(rate.Id, versionStamp, CostEstimationSource.Wire);

                    // Compute per-component breakdown (wire path: 4 components always emitted).
                    var components = new List<CostBreakdownComponent>
                    {
                        new("input",       a.WireInput,         inRate,          Scope.Core.CostEstimation.CostPricing.ComputeComponentCost(a.WireInput, inRate)),
                        new("output",      a.WireOutput,        outRate,         Scope.Core.CostEstimation.CostPricing.ComputeComponentCost(a.WireOutput, outRate)),
                        new("cache_read",  a.WireCacheRead,     cachedInRate,    Scope.Core.CostEstimation.CostPricing.ComputeComponentCost(a.WireCacheRead, cachedInRate)),
                        new("cache_write", a.WireCacheCreation, cacheCreateRate, Scope.Core.CostEstimation.CostPricing.ComputeComponentCost(a.WireCacheCreation, cacheCreateRate)),
                    };
                    var breakdown = new CostBreakdown(rate.Id, versionStamp, Pinned: false, components, TotalUsd: cost);

                    return (cost, "estimate", versionStamp, basis, breakdown, Scope.Core.Cron.Run.CostProvenance.ProviderEstimated);
                }
            }
        }

        // Recorded-model fallback (Tier 2, T-2026-06-15-04): exact tokens with no wire metering and
        // no obs cost, BUT the task's span carries a RECORDED/REQUESTED model (cron --model, or a
        // hook's tool_input.model). Price the exact tokens from that recorded model. This sits
        // BETWEEN the wire path (Tier 1, above) and the assumed-default path (Tier 3, below): wire
        // truth still wins; the assumed default is the last resort. Requires exactly ONE distinct
        // recorded model that resolves to a rate (honest -- never invent a model for a mixed task).
        if (a.HasExactTokens && a.TokenTotal > 0 && a.RecordedModels is { Count: 1 })
        {
            string recorded;
            using (var e = a.RecordedModels.GetEnumerator()) { e.MoveNext(); recorded = e.Current; }

            // The stored recorded model may be a bare harness ALIAS (e.g. a cron agent's
            // definition.Model = "haiku"), which ModelIdNormalizer does not resolve. Expand the alias
            // to its current full id first so an un-metered cron run prices/labels honestly as RECORDED
            // (its model IS known) instead of falling through to the ASSUMED default. A non-alias value
            // (already-full id) returns null and we keep the raw value. The hook path resolves aliases
            // at write time; this keeps the read path robust for any writer.
            var resolvedRecorded = Scope.Core.CostEstimation.ModelAliasMap.ResolveAliasToCurrentId(recorded) ?? recorded;

            var recordedRate = Scope.Core.CostEstimation.ModelIdNormalizer.Lookup(resolvedRecorded, estimationPricing);
            if (recordedRate is not null)
            {
                var inRate = recordedRate.InputUsdPerMillionTokens;
                var outRate = recordedRate.OutputUsdPerMillionTokens;

                // Canonical bucket arithmetic (decimal), input+output only (no cache on the recorded
                // tier): cache buckets are passed as 0 so the helper's cache terms contribute nothing.
                var cost = Scope.Core.CostEstimation.CostPricing.ComputeBucketCost(
                    a.TokenInput, a.TokenOutput, 0, 0, recordedRate);
                if (cost > 0)
                {
                    var versionStamp = Scope.Core.CostEstimation.SeedPricingTable.ToSemanticDate(estimationPricing?.PricesAsOf);
                    // Source = recorded (Tier 2). recovered=true via the derived flag (a real model
                    // was recovered from the requested model, not assumed). Provenance is
                    // provider_estimated: priced from a published rate, model is honest per session.
                    var basis = new CostEstimationBasis(recordedRate.Id, versionStamp, CostEstimationSource.Recorded);

                    var components = new List<CostBreakdownComponent>
                    {
                        new("input",  a.TokenInput,  inRate,  Scope.Core.CostEstimation.CostPricing.ComputeComponentCost(a.TokenInput, inRate)),
                        new("output", a.TokenOutput, outRate, Scope.Core.CostEstimation.CostPricing.ComputeComponentCost(a.TokenOutput, outRate)),
                    };
                    var breakdown = new CostBreakdown(recordedRate.Id, versionStamp, Pinned: false, components, TotalUsd: cost);

                    return (cost, "estimate", versionStamp, basis, breakdown, Scope.Core.Cron.Run.CostProvenance.ProviderEstimated);
                }
            }
        }

        // Scope-estimated fallback: exact tokens with no wire/obs cost. Apply the caller-supplied
        // default estimation model (user peg), or fall back to the hard-coded Sonnet default when
        // none is supplied or it does not resolve in the pricing table.
        // NEVER return null/unknown when any token data exists -- the defect this fixes.
        if (a.HasExactTokens && a.TokenTotal > 0)
        {
            // Resolve: caller-supplied peg first, then hard-coded Sonnet fallback.
            var pegModel = string.IsNullOrWhiteSpace(defaultEstimationModel) ? null : defaultEstimationModel;
            var resolvedModel = FallbackEstimationModel;
            var pegRate = pegModel != null
                ? Scope.Core.CostEstimation.ModelIdNormalizer.Lookup(pegModel, estimationPricing)
                : null;
            Scope.Core.CostEstimation.ModelPricing? defaultRate;
            if (pegRate is not null)
            {
                defaultRate = pegRate;
                resolvedModel = pegModel!;
            }
            else
            {
                defaultRate = Scope.Core.CostEstimation.ModelIdNormalizer.Lookup(FallbackEstimationModel, estimationPricing);
            }

            if (defaultRate is not null)
            {
                var inRate = defaultRate.InputUsdPerMillionTokens;
                var outRate = defaultRate.OutputUsdPerMillionTokens;

                // Canonical bucket arithmetic (decimal), input+output only (no cache on the scope-
                // estimated tier): cache buckets are passed as 0 so the helper's cache terms add nothing.
                var cost = Scope.Core.CostEstimation.CostPricing.ComputeBucketCost(
                    a.TokenInput, a.TokenOutput, 0, 0, defaultRate);
                if (cost > 0)
                {
                    // Semantic date: strip the time component from a fetch timestamp.
                    var versionStamp = Scope.Core.CostEstimation.SeedPricingTable.ToSemanticDate(estimationPricing?.PricesAsOf);
                    var basis = new CostEstimationBasis(resolvedModel, versionStamp, CostEstimationSource.Assumed);

                    // Compute per-component breakdown (scope-estimated path: input + output only, no cache).
                    var components = new List<CostBreakdownComponent>
                    {
                        new("input",  a.TokenInput,  inRate,  Scope.Core.CostEstimation.CostPricing.ComputeComponentCost(a.TokenInput, inRate)),
                        new("output", a.TokenOutput, outRate, Scope.Core.CostEstimation.CostPricing.ComputeComponentCost(a.TokenOutput, outRate)),
                    };
                    var breakdown = new CostBreakdown(resolvedModel, versionStamp, Pinned: false, components, TotalUsd: cost);

                    return (cost, "estimate", versionStamp, basis, breakdown, Scope.Core.Cron.Run.CostProvenance.ScopeEstimated);
                }
            }
        }

        return (null, "unknown", null, null, null, Scope.Core.Cron.Run.CostProvenance.Unavailable);
    }

    private sealed class TaskAcc
    {
        public long BusyMs;
        public int MeasuredCount, ZeroDurationCount, UnknownTimeCount;
        public bool HasExactTokens;
        public long TokenInput, TokenOutput, TokenTotal;
        public bool HasWireTokens;
        public long WireInput, WireOutput, WireCacheCreation, WireCacheRead, WireTotal;
        /// <summary>
        /// Wire model(s) seen for this task's spans. Populated from _wire_model annotations written
        /// by AnnotateWireTokens. Used to drive provider_estimated cost fallback when no
        /// obs_cost_estimates cost is available for this task.
        /// </summary>
        public HashSet<string>? WireModels;
        /// <summary>
        /// Recorded/requested model(s) seen on this task's spans (obs_agent_spans.model, schema v9).
        /// Tier-2 cost source: used to price the un-metered exact-token case when no wire model is
        /// present. Prices only when exactly one distinct recorded model resolves to a rate.
        /// </summary>
        public HashSet<string>? RecordedModels;
        public int TokenKnown, TokenUnknown;
        // decimal (T-2026-06-20-20 Piece 2): exact in-memory cost accumulation.
        public decimal CostUsd;
        public int CostKnown, CostUnknown;

        // Pinned-cost snapshot (schema v6): captured from obs_agent_spans pinned_* columns.
        // Non-null only on cron root spans that had their cost pinned at finalize time.
        // When set, ResolveTaskCost uses this snapshot and emits pinned=true (immutable).
        public decimal? PinnedCostUsd;
        public string? PinnedModelId;
        public string? PinnedPricesAsOf;
        public string? PinnedBreakdownJson;
    }

    // Strength order for aggregate source labeling: wire == exact > estimate > unknown.
    private static int SourceStrength(string source) => source switch
    {
        "wire" => 3,
        "exact" => 3,
        "estimate" => 2,
        _ => 1,
    };

    private static string StrengthName(int strength) => strength switch
    {
        3 => "exact",
        2 => "estimate",
        _ => "unknown",
    };

    internal static long DurationMs(string? started, string? completed)
    {
        if (string.IsNullOrEmpty(started) || string.IsNullOrEmpty(completed)) return 0;
        if (!DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var s) || !DateTimeOffset.TryParse(completed, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var e)) return 0;
        var ms = (long)(e - s).TotalMilliseconds;
        return ms > 0 ? ms : 0;
    }

    internal static long WallMs(string? from, string? to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return 0;
        if (!DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var f) || !DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) return 0;
        var ms = (long)(t - f).TotalMilliseconds;
        return ms > 0 ? ms : 0;
    }
}
