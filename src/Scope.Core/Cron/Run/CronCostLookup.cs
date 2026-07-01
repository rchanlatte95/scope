namespace Scope.Core.Cron.Run;

using Microsoft.Data.Sqlite;
using Scope.Core.CostEstimation;
using Scope.Core.Cron.Launch;
using Scope.Core.Telemetry;
using Scope.Core.Telemetry.Trace;
using Scope.Core.Utilities;
using Serilog;
using System.Globalization;

/// <summary>
/// Result of <see cref="CronCostLookup.ResolveDispatchCost"/>. Carries both the USD cost (null when
/// not computable) and a <see cref="CostProvenance"/> value that honestly distinguishes why cost is
/// present or absent. A null <see cref="Cost"/> with <see cref="CostProvenance.JoinedExact"/> or
/// <see cref="CostProvenance.ProviderEstimated"/> is illegal by construction; only the lookup's
/// callers serialize the provenance, so this invariant is enforced there.
/// <para>
/// <see cref="PinnedBreakdown"/> carries the per-component cost breakdown computed at capture time
/// so the rollup can reconstruct it exactly without re-deriving from the live pricing table.
/// Null when cost is not computable or when provenance is Unmetered/Unavailable.
/// </para>
/// </summary>
public sealed record CronCostResult(double? Cost, string Provenance,
    CostBreakdown? PinnedBreakdown = null)
{
    /// <summary>
    /// Wire-joined exact cost result with an optional per-component breakdown.
    /// When the dispatch was priced from a single model via the external pricing table, a
    /// breakdown is built at capture time so the rollup can reconstruct the cost exactly
    /// without re-deriving from the live pricing table. Multi-model dispatches pass null.
    /// </summary>
    public static CronCostResult Exact(double cost, CostBreakdown? breakdown = null) =>
        new(cost, CostProvenance.JoinedExact, breakdown);

    /// <summary>
    /// Estimated cost computed from token counts and the built-in seed pricing table, with a
    /// per-component breakdown stored for price-pinning immutability.
    /// Uses <see cref="CostProvenance.ProviderEstimated"/> because the rates originate from
    /// provider-published list prices (not a Scope heuristic). Wire-exact paths still win
    /// when an external pricing table resolves the model.
    /// </summary>
    public static CronCostResult ProviderEstimated(double cost, CostBreakdown breakdown) =>
        new(cost, CostProvenance.ProviderEstimated, breakdown);

    /// <summary>No wire calls existed to join (legitimately nothing to meter).</summary>
    public static CronCostResult Unmetered() => new(null, CostProvenance.Unmetered, null);

    /// <summary>Telemetry or pricing data is absent; cannot determine cost.</summary>
    public static CronCostResult Unavailable() => new(null, CostProvenance.Unavailable, null);
}

/// <summary>
/// Resolves the exact wire-metered total cost (USD) for a dispatch from telemetry, for the cron
/// run-now runner. Returns null when no exact cost can be computed; it never estimates and never
/// returns a partial/underpriced figure as exact.
///
/// Sourcing ruling (owner, 2026-06-12): wire-exact tokens multiplied by the configured official
/// pricing table counts as EXACT USD. The <c>wire_calls</c> table (written by the metering proxy)
/// is the only provider-grade usage source; it is content-free, carrying per-call token counts but
/// no cost column and no dispatch_id. This lookup attributes those rows to the dispatch and prices
/// them with the same <see cref="PricingResolver"/> path the rest of Scope uses. <c>obs_cost_estimates</c>
/// remains forbidden as a source (it is estimate-labeled).
///
/// Attribution mechanics. A <c>wire_calls</c> row carries <c>tool_use_ids</c> (a JSON array of the
/// provider-issued tool_use ids emitted in that call's assistant turn). The cron child writes
/// <c>obs_agent_events</c> rows carrying both the dispatch id and a <c>tool_use_id</c> in their
/// <c>event_data_json</c>. The dispatch's tool_use_id set (<see cref="ObservabilityAuditDb.GetDispatchToolUseIds"/>)
/// is intersected with each wire row's array; an intersecting wire row's full token block attributes
/// to the dispatch. This mirrors the proven join in <see cref="ObservabilityAuditDb"/>'s
/// <c>AnnotateWireTokens</c> and reuses ITS TWO double-count guards, here scoped to the dispatch total
/// rather than per span:
///  - a global <c>consumedToolUseIds</c> set: a tool_use_id is counted by the FIRST wire row (in
///    GetWireCalls iteration order) whose array contains it, so two DISTINCT wire rows sharing an id
///    contribute that id's tokens ONCE (integrity guard for duplicate/malformed rows);
///  - a per-wire <c>consumedWireIds</c> set: one wire row whose array carries several matching ids
///    is counted ONCE for the dispatch, not once per id.
/// The trace-projection join is reimplemented here (not extracted) because the dispatch rollup needs
/// a flat per-model total, not per-span attribution; trace projection behavior is left untouched. The
/// guards are cross-referenced and covered by parity tests against the same fixtures.
///
/// Pricing-coverage rule (honest absence over fabricated exactness). Tokens are summed into four
/// buckets per attributed model (input, output, cache_creation, cache_read) and each nonzero bucket
/// must have a configured official rate or the whole dispatch resolves to null:
///  - input/output: priced at the model's per-million input/output rates (always present for a
///    priced model);
///  - cache_read: priced at the model's <see cref="ModelPricing.CachedInputUsdPerMillionTokens"/>;
///    if a model carries cache_read tokens but that rate is null, return null;
///  - cache_creation: priced at the model's <see cref="ModelPricing.CacheCreationUsdPerMillionTokens"/>
///    (the official cache-write rate, sourced from LiteLLM <c>cache_creation_input_token_cost</c>) when
///    configured; null only when a model carries cache_creation tokens but that rate is absent. This
///    matters because Claude Code headless runs virtually always emit cache_creation tokens, so refusing
///    to price them would null out nearly every real cron run.
/// If any attributed model id is absent from the pricing table, or no wire rows join, or telemetry /
/// pricing is absent, the result is null.
///
/// Known limitation (CR-M1, accepted). A wire call whose response carries no tool_use_ids (e.g. a
/// final text-only assistant turn) cannot be attributed by this id-intersection join and is excluded,
/// so a per-run cost can UNDERCOUNT. cache_creation tokens are priced when the official cache-write
/// rate is configured and force a null only when those tokens are present without a rate. Stronger
/// attribution (per-call dispatch binding at the proxy) is ST-03b territory. Surfaced in the run-now
/// outcome as a known gap. Only <see cref="TryReadMeteredCost"/> changes when that lands; the runner
/// and the cron_runs contract already carry a nullable cost.
/// </summary>
public sealed class CronCostLookup(string telemetryDbPath)
{
    /// <summary>
    /// Returns the wire-metered cost and its provenance for a dispatch. Never throws: any
    /// telemetry or pricing failure resolves to <see cref="CronCostResult.Unavailable"/> so the
    /// runner records cost and provenance honestly rather than crashing.
    ///
    /// Provenance is one of:
    /// <list type="bullet">
    ///   <item><see cref="CostProvenance.JoinedExact"/> when wire-exact tokens joined and were priced.</item>
    ///   <item><see cref="CostProvenance.Unmetered"/> when no wire calls existed to join (no tool_use_ids
    ///     in the dispatch, no wire_calls table, or zero joinable rows).</item>
    ///   <item><see cref="CostProvenance.Unavailable"/> when telemetry is absent, pricing is absent, or
    ///     a model/rate is missing that prevents an exact computation.</item>
    /// </list>
    /// A null dispatchId always returns <see cref="CronCostResult.Unavailable"/>.
    /// </summary>
    public CronCostResult ResolveDispatchCost(string? dispatchId)
    {
        if (string.IsNullOrWhiteSpace(dispatchId))
            return CronCostResult.Unavailable();

        try
        {
            if (!File.Exists(telemetryDbPath))
                return CronCostResult.Unavailable();

            using var connection = SQLite.OpenDb(telemetryDbPath);
            return TryReadMeteredCost(connection, dispatchId);
        }
        catch (Exception ex)
        {
            Log.Warning("Cron cost lookup failed (non-fatal): {ErrorMessage}", ex.Message);
            return CronCostResult.Unavailable();
        }
    }

    /// <summary>
    /// Returns the dispatch's CUMULATIVE total wire tokens (input + output + cache_creation + cache_read)
    /// attributed via the same tool_use_id intersection join (and the same two double-count guards) the
    /// cost lookup uses, summed across models. Used by the supervisor's runaway-guard monitor to decide
    /// the hard force-kill: it reads this live (the proxy writes wire_calls as calls complete) and
    /// kills the harness when it reaches budget + grace. Never throws: returns 0 when telemetry is
    /// absent, no wire calls join, or any read fails (an absent/locked db must never crash the monitor).
    /// A null or blank dispatchId returns 0.
    /// </summary>
    public long ResolveDispatchTokens(string? dispatchId)
    {
        if (string.IsNullOrWhiteSpace(dispatchId))
            return 0;

        try
        {
            if (!File.Exists(telemetryDbPath))
                return 0;

            using var connection = SQLite.OpenDb(telemetryDbPath);
            var auditDb = new ObservabilityAuditDb(connection);
            if (!auditDb.HasWireCallsTable())
                return 0;

            var dispatchToolUseIds = auditDb.GetDispatchToolUseIds(dispatchId!);
            if (dispatchToolUseIds.Count == 0)
                return 0;

            List<Dictionary<string, object?>> wireRows;
            try { wireRows = auditDb.GetWireCalls(100000); }
            catch (SqliteException) { return 0; }

            long total = 0;
            // Same two guards as TryReadMeteredCost: count each tool_use_id once (by the first wire row
            // carrying it) and each wire row once for the dispatch even if several of its ids match.
            var consumedToolUseIds = new HashSet<string>(StringComparer.Ordinal);
            var consumedWireIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var w in wireRows)
            {
                var wireId = Str(w, "id");
                var ids = ParseWireToolUseIds(Str(w, "tool_use_ids"));
                if (ids.Count == 0) continue;

                foreach (var tuid in ids)
                {
                    if (!dispatchToolUseIds.Contains(tuid)) continue;
                    if (!consumedToolUseIds.Add(tuid)) continue;
                    if (!consumedWireIds.Add(wireId)) continue;

                    total += LongCol(w, "input_tokens")
                             + LongCol(w, "output_tokens")
                             + LongCol(w, "cache_creation_tokens")
                             + LongCol(w, "cache_read_tokens");
                }
            }

            return total;
        }
        catch (Exception ex)
        {
            Log.Warning("Cron dispatch token read failed (non-fatal): {ErrorMessage}", ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Attempts to estimate cost from self-reported span tokens when no wire-exact data is available.
    /// This is the F2 fallback for Codex subscription/OAuth runs: Codex's own event stream reports
    /// token usage in <c>usage</c> blocks (parsed by <see cref="CodexEventStreamParser"/>). Scope
    /// prices those tokens via <see cref="ModelIdNormalizer"/> and the seed/external pricing table,
    /// yielding <see cref="CostProvenance.ProviderEstimated"/> provenance.
    ///
    /// Token mapping (OpenAI-shaped usage, disjoint-bucket rules consistent with A3):
    /// <list type="bullet">
    ///   <item>cache_read = min(CachedInputTokens, InputTokens) (clamped; F4 over-count guard)</item>
    ///   <item>net_input = max(0, InputTokens - cache_read) (disjoint: prompt minus what was cached)</item>
    ///   <item>output = OutputTokens (reasoning tokens are included in output; no separate bucket)</item>
    ///   <item>cache_creation = 0 (Codex/OpenAI does not report a cache-write fee)</item>
    /// </list>
    ///
    /// Returns <see cref="CronCostResult.Unavailable"/> when:
    /// <list type="bullet">
    ///   <item><paramref name="modelId"/> is null/empty or unmappable by <see cref="ModelIdNormalizer"/>.</item>
    ///   <item><paramref name="usage"/> is null or all token buckets are zero (no real tokens to price).</item>
    /// </list>
    /// Never throws; pricing or lookup failures are logged and degrade to Unavailable.
    /// </summary>
    /// <param name="modelId">The model id used for this run (e.g. from <see cref="HarnessLaunchRequest.Model"/>).</param>
    /// <param name="usage">Self-reported token counts parsed from the harness event stream.</param>
    public CronCostResult TryComputeSelfReportedEstimate(string? modelId, HarnessUsage? usage)
    {
        if (usage is null) return CronCostResult.Unavailable();
        if (string.IsNullOrWhiteSpace(modelId)) return CronCostResult.Unavailable();

        try
        {
            var inputRaw = usage.InputTokens ?? 0L;
            var cachedRaw = usage.CachedInputTokens ?? 0L;

            // Disjoint-bucket rules (F4 clamp: cached > input is bad data, clamp to input).
            var cacheRead = Math.Min(cachedRaw, inputRaw);
            var netInput = Math.Max(0L, inputRaw - cacheRead);
            var output = usage.OutputTokens ?? 0L;
            // Codex/OpenAI does not report a cache-write bucket; cache_creation is always zero.
            const long cacheCreation = 0L;

            // Guard: all buckets are zero -- nothing real to price, honest absence beats zero estimate.
            if (netInput == 0L && output == 0L && cacheRead == 0L)
                return CronCostResult.Unavailable();

            var externalPricing = PricingResolver.TryResolve(
                ResolveScopeDir(),
                Environment.GetEnvironmentVariable("SCOPE_PRICING_PATH"));

            var rate = ModelIdNormalizer.Lookup(modelId, externalPricing);
            if (rate is null)
            {
                Log.Warning(
                    "Cron self-reported estimate: model {Model} could not be mapped to any pricing entry; cost is unavailable.",
                    modelId);
                return CronCostResult.Unavailable();
            }

            var inRate = rate.InputUsdPerMillionTokens;
            var outRate = rate.OutputUsdPerMillionTokens;
            var cachedInRate = rate.CachedInputUsdPerMillionTokens ?? 0m;
            var cacheCreateRate = rate.CacheCreationUsdPerMillionTokens ?? 0m;

            var inputCost = CostEstimation.CostPricing.ComputeComponentCost(netInput, inRate);
            var outputCost = CostEstimation.CostPricing.ComputeComponentCost(output, outRate);
            // Cache-read: priced when the seed entry has a rate; absent rate yields 0 cost (not Unavailable).
            // All OpenAI seed entries carry a cached-input rate (F5 compliance), so this is non-zero for
            // all priceable Codex models that report cached tokens.
            var cacheRdCost = (cacheRead > 0L && rate.CachedInputUsdPerMillionTokens.HasValue)
                ? CostEstimation.CostPricing.ComputeComponentCost(cacheRead, cachedInRate) : 0m;
            // cache_creation = 0 for Codex; cost is zero.
            const decimal cacheWrCost = 0m;

            var totalCost = inputCost + outputCost + cacheRdCost + cacheWrCost;

            var pricesAsOf = CostEstimation.SeedPricingTable.ToSemanticDate(externalPricing?.PricesAsOf);

            var components = new List<CostBreakdownComponent>
            {
                new("input",       netInput,      inRate,          inputCost),
                new("output",      output,        outRate,         outputCost),
                new("cache_read",  cacheRead,     cachedInRate,    cacheRdCost),
                new("cache_write", cacheCreation, cacheCreateRate, cacheWrCost),
            };
            var breakdown = new CostBreakdown(rate.Id, pricesAsOf, Pinned: true, components, TotalUsd: totalCost);

            // CronCostResult.Cost stays a double (persisted to cron_runs REAL + double DTOs); decimal is the
            // exact source for the breakdown.
            return CronCostResult.ProviderEstimated((double)totalCost, breakdown);
        }
        catch (Exception ex)
        {
            Log.Warning("Cron self-reported cost estimate failed (non-fatal): {ErrorMessage}", ex.Message);
            return CronCostResult.Unavailable();
        }
    }

    /// <summary>
    /// Computes the dispatch's wire-attributed cost by joining wire_calls rows via the tool_use_id
    /// intersection join, summing per-model token buckets under the two double-count guards, and
    /// pricing them. Pricing priority:
    /// <list type="number">
    ///   <item>External pricing table (local pricing.json or LiteLLM cache) -> <see cref="CronCostResult.Exact"/>.</item>
    ///   <item>Built-in seed table via <see cref="ModelIdNormalizer"/> -> <see cref="CronCostResult.ProviderEstimated"/>.</item>
    ///   <item>No rate found for any attributed model -> <see cref="CronCostResult.Unavailable"/>.</item>
    /// </list>
    ///
    /// Other provenance values:
    /// <list type="bullet">
    ///   <item><see cref="CronCostResult.Unmetered"/> when no wire calls exist to join (no wire_calls
    ///     table, no tool_use_ids in the dispatch, or no wire rows join the dispatch's ids).
    ///     This means no API calls were attributed, not that data was broken.</item>
    ///   <item><see cref="CronCostResult.Unavailable"/> when telemetry is absent or a model cannot
    ///     be resolved by any pricing source.</item>
    /// </list>
    /// See the class remarks for the full ruling, mechanics, and known limitation. Never throws.
    /// </summary>
    private CronCostResult TryReadMeteredCost(SqliteConnection connection, string dispatchId)
    {
        var auditDb = new ObservabilityAuditDb(connection);
        if (!auditDb.HasWireCallsTable())
            // No wire_calls table = the metering proxy was never active for this run.
            // There were no wire calls to meter, so this is unmetered (not broken telemetry).
            return CronCostResult.Unmetered();

        // The dispatch's provider-issued tool_use_id set is the join key against wire arrays.
        var dispatchToolUseIds = auditDb.GetDispatchToolUseIds(dispatchId);
        if (dispatchToolUseIds.Count == 0)
            // No tool_use_ids recorded for this dispatch means no API calls were observed.
            // This is legitimately unmetered (the agent made no LLM calls or none were captured).
            return CronCostResult.Unmetered();

        List<Dictionary<string, object?>> wireRows;
        try { wireRows = auditDb.GetWireCalls(100000); }
        catch (SqliteException) { return CronCostResult.Unavailable(); }
        if (wireRows.Count == 0)
            // Wire table exists but is empty; this is also unmetered (no API calls in the proxy).
            return CronCostResult.Unmetered();

        // Per-model token accumulators, attributed under the same two guards AnnotateWireTokens uses:
        //  - consumedToolUseIds: count each id once (by the first wire row that carries it);
        //  - consumedWireIds: count each wire row once for the dispatch even if several of its ids match.
        var perModel = new Dictionary<string, TokenBuckets>(StringComparer.OrdinalIgnoreCase);
        var consumedToolUseIds = new HashSet<string>(StringComparer.Ordinal);
        var consumedWireIds = new HashSet<string>(StringComparer.Ordinal);
        var attributedAny = false;

        foreach (var w in wireRows)
        {
            var wireId = Str(w, "id");
            var ids = ParseWireToolUseIds(Str(w, "tool_use_ids"));
            if (ids.Count == 0) continue;

            foreach (var tuid in ids)
            {
                if (!dispatchToolUseIds.Contains(tuid)) continue;
                if (!consumedToolUseIds.Add(tuid)) continue;       // id already counted by an earlier wire row
                if (!consumedWireIds.Add(wireId)) continue;         // this wire row already counted for the dispatch

                var model = Str(w, "model");
                if (string.IsNullOrEmpty(model) || string.Equals(model, "-", StringComparison.Ordinal))
                {
                    // A priced figure requires a model id to look up; an attributed row without one
                    // cannot be priced exactly, so the whole dispatch is unavailable (not unmetered:
                    // we DID see a wire call, we just cannot price it).
                    Log.Warning(
                        "Cron cost lookup: wire row {WireId} attributed to dispatch carries no model id; cost is not exact.",
                        wireId);
                    return CronCostResult.Unavailable();
                }

                if (!perModel.TryGetValue(model, out var buckets))
                {
                    buckets = new TokenBuckets();
                    perModel[model] = buckets;
                }
                buckets.Input += LongCol(w, "input_tokens");
                buckets.Output += LongCol(w, "output_tokens");
                buckets.CacheCreation += LongCol(w, "cache_creation_tokens");
                buckets.CacheRead += LongCol(w, "cache_read_tokens");
                attributedAny = true;
            }
        }

        if (!attributedAny)
            // Wire rows exist but none join to this dispatch's tool_use_ids.
            // The dispatch had no API calls attributable to it via the id-intersection join.
            return CronCostResult.Unmetered();

        // Try wire-exact pricing first (operator-supplied pricing.json or LiteLLM cache).
        var externalPricing = PricingResolver.TryResolve(ResolveScopeDir(), Environment.GetEnvironmentVariable("SCOPE_PRICING_PATH"));

        // Attempt an exact computation from the external pricing table.
        var exactResult = TryComputeExactCost(perModel, externalPricing);
        if (exactResult is not null)
            return exactResult;

        // Wire-exact failed (no external pricing table, or a model/rate is missing).
        // Fall back to estimation using the built-in seed table via ModelIdNormalizer.
        // This yields provider_estimated provenance, never joined_exact.
        return TryComputeEstimatedCost(perModel, externalPricing);
    }

    /// <summary>
    /// Attempts to compute an exact wire-priced cost from the external pricing table.
    /// Returns <see cref="CronCostResult.Exact"/> when all models are priced and all required
    /// bucket rates are present. Returns null when the external table is absent, a model is missing,
    /// or a bucket rate is absent -- in all null cases the caller tries estimation instead.
    /// </summary>
    private static CronCostResult? TryComputeExactCost(
        Dictionary<string, TokenBuckets> perModel,
        ModelPricingConfig? externalPricing)
    {
        if (externalPricing is null)
            return null;

        var pricingById = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in externalPricing.Models)
            pricingById[m.Id] = m;

        decimal total = 0m;
        foreach (var (model, buckets) in perModel)
        {
            if (!pricingById.TryGetValue(model, out var rate))
            {
                Log.Debug(
                    "Cron cost lookup: model {Model} has no exact pricing entry; will try estimation fallback.",
                    model);
                return null;
            }

            if (buckets.CacheCreation > 0 && rate.CacheCreationUsdPerMillionTokens is null)
            {
                Log.Debug(
                    "Cron cost lookup: model {Model} has cache_creation tokens but no cache-creation rate; will try estimation fallback.",
                    model);
                return null;
            }

            if (buckets.CacheRead > 0 && rate.CachedInputUsdPerMillionTokens is null)
            {
                Log.Debug(
                    "Cron cost lookup: model {Model} has cache_read tokens but no cache rate; will try estimation fallback.",
                    model);
                return null;
            }

            // Canonical bucket arithmetic (decimal). The early returns above guarantee a non-null
            // cache rate whenever the corresponding bucket is positive, so the helper's HasValue
            // guard is always satisfied here -- behavior is identical to the prior !.Value form.
            total += CostPricing.ComputeBucketCost(
                buckets.Input, buckets.Output, buckets.CacheCreation, buckets.CacheRead, rate);
        }

        // Single-model dispatch: build a pinned breakdown so the rollup can reconstruct the
        // exact cost without re-deriving from the live pricing table (immutability guarantee).
        // Multi-model dispatches: no single model_id to attribute, so no breakdown.
        CostBreakdown? breakdown = null;
        if (perModel.Count == 1)
        {
            using var e = perModel.GetEnumerator();
            e.MoveNext();
            var (singleModel, singleBuckets) = e.Current;
            pricingById.TryGetValue(singleModel, out var singleRate);
            if (singleRate is not null)
            {
                // decimal rates + component costs (T-2026-06-20-20 Piece 2): the CostBreakdown DTO carries
                // decimal rate/cost/total now. Absent cache rate -> 0m.
                var inRate = singleRate.InputUsdPerMillionTokens;
                var outRate = singleRate.OutputUsdPerMillionTokens;
                var crdRate = singleRate.CachedInputUsdPerMillionTokens ?? 0m;
                var cwrRate = singleRate.CacheCreationUsdPerMillionTokens ?? 0m;

                var pricesAsOf = CostEstimation.SeedPricingTable.ToSemanticDate(externalPricing.PricesAsOf);

                var components = new List<CostBreakdownComponent>
                {
                    new("input",       singleBuckets.Input,         inRate,  CostEstimation.CostPricing.ComputeComponentCost(singleBuckets.Input, inRate)),
                    new("output",      singleBuckets.Output,        outRate, CostEstimation.CostPricing.ComputeComponentCost(singleBuckets.Output, outRate)),
                    new("cache_read",  singleBuckets.CacheRead,     crdRate, CostEstimation.CostPricing.ComputeComponentCost(singleBuckets.CacheRead, crdRate)),
                    new("cache_write", singleBuckets.CacheCreation, cwrRate, CostEstimation.CostPricing.ComputeComponentCost(singleBuckets.CacheCreation, cwrRate)),
                };
                breakdown = new CostBreakdown(singleRate.Id, pricesAsOf, Pinned: true, components, TotalUsd: total);
            }
        }

        // CronCostResult.Cost stays a double: it is persisted to cron_runs.total_cost_usd (SQLite REAL)
        // and surfaced via the cron/recent-run DTOs (double). The decimal total is the exact source.
        return CronCostResult.Exact((double)total, breakdown);
    }

    /// <summary>
    /// Attempts to estimate cost from the built-in seed pricing table when wire-exact pricing is
    /// unavailable. Uses <see cref="ModelIdNormalizer"/> to map each observed model id to a seed
    /// entry. Returns <see cref="CronCostResult.ProviderEstimated"/> when all models are mappable,
    /// with a per-component <see cref="CostBreakdown"/> stored for price-pinning immutability.
    /// Returns <see cref="CronCostResult.Unavailable"/> when any model cannot be mapped (an unknown
    /// model must never silently receive a fabricated price). Cache-creation tokens are priced when
    /// the seed entry carries a rate; if the bucket is nonzero but the rate is absent, the estimate
    /// is computed without the cache-creation component (seed entries without cache-write rates are
    /// OpenAI models, where the platform absorbs the write cost -- not an error).
    /// </summary>
    private static CronCostResult TryComputeEstimatedCost(
        Dictionary<string, TokenBuckets> perModel,
        ModelPricingConfig? externalPricing)
    {
        // Single-model only: build one breakdown that is pinned at capture time.
        // Multi-model sessions produce Unavailable so no fabricated blended rate is emitted.
        if (perModel.Count != 1)
        {
            // More than one model in this dispatch means we cannot produce a single-model
            // pinned breakdown. Compute the total without a breakdown, or return Unavailable
            // when any model is unmapped (same policy as before).
            decimal multiTotal = 0m;
            foreach (var (model, buckets) in perModel)
            {
                var rate = ModelIdNormalizer.Lookup(model, externalPricing);
                if (rate is null)
                {
                    Log.Warning(
                        "Cron cost lookup: model {Model} could not be mapped to any seed pricing entry; cost is unavailable.",
                        model);
                    return CronCostResult.Unavailable();
                }
                // Canonical bucket arithmetic (decimal). The helper sums the cache terms in the
                // (cache_creation, cache_read) order; decimal addition is exact and order-independent
                // for token-cost magnitudes, so the total is value-identical to the prior inline form.
                multiTotal += CostPricing.ComputeBucketCost(
                    buckets.Input, buckets.Output, buckets.CacheCreation, buckets.CacheRead, rate);
            }
            // No breakdown for multi-model dispatches (cannot attribute a single model_id).
            return new CronCostResult((double)multiTotal, CostProvenance.ProviderEstimated, null);
        }

        // Single model: compute breakdown components and pin them.
        using var e = perModel.GetEnumerator();
        e.MoveNext();
        var (singleModel, singleBuckets) = e.Current;

        var singleRate = ModelIdNormalizer.Lookup(singleModel, externalPricing);
        if (singleRate is null)
        {
            Log.Warning(
                "Cron cost lookup: model {Model} could not be mapped to any seed pricing entry; cost is unavailable.",
                singleModel);
            return CronCostResult.Unavailable();
        }

        // decimal rates + component costs (T-2026-06-20-20 Piece 2): the CostBreakdown DTO is decimal.
        var inRate = singleRate.InputUsdPerMillionTokens;
        var outRate = singleRate.OutputUsdPerMillionTokens;
        var cachedInRate = singleRate.CachedInputUsdPerMillionTokens ?? 0m;
        var cacheCreateRate = singleRate.CacheCreationUsdPerMillionTokens ?? 0m;

        var inputCost = CostEstimation.CostPricing.ComputeComponentCost(singleBuckets.Input, inRate);
        var outputCost = CostEstimation.CostPricing.ComputeComponentCost(singleBuckets.Output, outRate);
        var cacheRdCost = (singleBuckets.CacheRead > 0 && singleRate.CachedInputUsdPerMillionTokens.HasValue)
            ? CostEstimation.CostPricing.ComputeComponentCost(singleBuckets.CacheRead, cachedInRate) : 0m;
        var cacheWrCost = (singleBuckets.CacheCreation > 0 && singleRate.CacheCreationUsdPerMillionTokens.HasValue)
            ? CostEstimation.CostPricing.ComputeComponentCost(singleBuckets.CacheCreation, cacheCreateRate) : 0m;

        var totalCost = inputCost + outputCost + cacheRdCost + cacheWrCost;

        // prices_as_of: semantic date (yyyy-MM-dd), never a runtime timestamp.
        // ToSemanticDate strips the time component from a fetch timestamp such as
        // "2026-06-15T18:57:06.17Z" and returns the date-only "2026-06-15".
        var pricesAsOf = CostEstimation.SeedPricingTable.ToSemanticDate(externalPricing?.PricesAsOf);

        var components = new List<CostBreakdownComponent>
        {
            new("input",       singleBuckets.Input,         inRate,          inputCost),
            new("output",      singleBuckets.Output,        outRate,         outputCost),
            new("cache_read",  singleBuckets.CacheRead,     cachedInRate,    cacheRdCost),
            new("cache_write", singleBuckets.CacheCreation, cacheCreateRate, cacheWrCost),
        };
        var breakdown = new CostBreakdown(singleRate.Id, pricesAsOf, Pinned: true, components, TotalUsd: totalCost);

        // CronCostResult.Cost stays a double (persisted to cron_runs REAL + double DTOs); decimal is the
        // exact source for the breakdown.
        return CronCostResult.ProviderEstimated((double)totalCost, breakdown);
    }

    /// <summary>
    /// Resolves the <c>.scope</c> directory pricing lives in from the telemetry db path
    /// (<c>.scope/telemetry.db</c>). Falls back to the current directory if no parent is available.
    /// </summary>
    private string ResolveScopeDir()
        => Path.GetDirectoryName(Path.GetFullPath(telemetryDbPath)) ?? Directory.GetCurrentDirectory();

    private sealed class TokenBuckets
    {
        public long Input, Output, CacheCreation, CacheRead;
    }

    private static string Str(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static long LongCol(Dictionary<string, object?> row, string key)
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

    private static List<string> ParseWireToolUseIds(string raw)
    {
        if (string.IsNullOrEmpty(raw) || string.Equals(raw, "-", StringComparison.Ordinal)) return [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
            var result = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                    result.Add(el.GetString()!);
            return result;
        }
        catch (System.Text.Json.JsonException) { return []; }
    }
}
