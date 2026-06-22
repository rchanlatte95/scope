namespace Scope.Core.Telemetry.Trace;

using System.Text.Json;
using static Scope.Core.Telemetry.Trace.TraceProjection;

/// <summary>
/// Attributes the canonical per-task wire-metered cost (from <see cref="TraceRollup"/>) down to the
/// individual trace rows (tool-call events and text/turn spans) that produced it, so the trace
/// timeline can show a legible per-row cost whose SUM equals the trace total BY CONSTRUCTION
/// (T-2026-06-20-03, OBS-10).
///
/// <para>
/// <b>Why this reconciles by construction.</b> This primitive NEVER re-prices wire tokens. It takes the
/// already-computed <see cref="RollupBody"/> (the SAME object the Accounting tab / <c>/api/trace/rollup</c>
/// serves) and partitions each <see cref="RollupRow"/>'s cost across the rows that belong to that task
/// (the task = a top-level-span subtree). Because every cost-bearing row is attributed to exactly one
/// task by the rollup, and this primitive distributes each task's single cost number across that task's
/// own rows without remainder, <c>SUM(per-row cost) == SUM(by_task cost) == totals.cost</c> exactly
/// (no rounding divergence: it is the same doubles, only redistributed). The rollup's
/// <see cref="RollupBody.Unattributed"/> remainder carries a null cost and contributes nothing, exactly
/// as it does to the totals.
/// </para>
///
/// <para>
/// <b>Within-task distribution.</b> A task's cost is split across the tool-call events emitted in that
/// task (joined via <c>wire_calls.tool_use_ids</c> -> <c>obs_agent_events.tool_use_id</c>, the structured
/// id the projection surfaces). The split weight is OUTPUT-TOKEN SHARE when recoverable (the
/// <c>output</c> length on the event's <c>event_data_json</c>), else an EVEN split across the task's
/// tool-call events. Cost that is not attributable to any tool-call event (a text-only assistant turn,
/// or a wire row attributed to the task by the header/session fallback with no joinable tool_use on
/// this page) lands on the task's root span row (its agent/turn home), never dropped. A multi-tool wire
/// turn is therefore split across its parallel tools by output share; a single-tool turn puts its full
/// cost on that one tool-call row.
/// </para>
///
/// <para>
/// Pure: no database access, no clock, no randomness. Provenance is carried through from the rollup's
/// 6-value <c>cost_provenance</c> for the owning task; an unknown-model task (the rollup priced it null)
/// surfaces <c>unavailable</c> on its rows and never a fabricated price.
/// </para>
/// </summary>
public static class WireCostAttribution
{
    /// <summary>
    /// The attributed cost for a single trace row: a USD value (null when the owning task had no
    /// resolvable cost), the 6-value cost provenance carried through from the canonical rollup, the
    /// 4-value Money.source for the owning task, and the "prices as of" stamp the rollup applied.
    /// </summary>
    /// <param name="ValueUsd">Attributed USD for this row, or null when the owning task's cost was unavailable.</param>
    /// <param name="CostProvenance">
    /// The 6-value provenance (wire_exact | joined_exact | provider_estimated | scope_estimated |
    /// unmetered | unavailable) of the owning task, carried through unchanged.
    /// </param>
    /// <param name="MoneySource">The 4-value Money.source (wire|exact|estimate|unknown) of the owning task.</param>
    /// <param name="PricingVersion">The "prices as of" stamp the rollup applied to the owning task, or null.</param>
    public readonly record struct AttributedCost(
        // decimal (T-2026-06-20-20 Piece 2): the per-row share. Because the redistribution is decimal
        // and the last share is the exact decimal remainder (total - allocated), SUM(per-row) == total
        // EXACTLY and no share can be negative (allocated <= total for a non-negative total). This is
        // what lets the negative-residual clamp be deleted.
        decimal? ValueUsd,
        string CostProvenance,
        string MoneySource,
        string? PricingVersion);

    /// <summary>
    /// Computes the per-row attributed-cost map for one merged trace, keyed by trace-entry id.
    /// <paramref name="rows"/> are the <see cref="ObservabilityAuditDb.GetFullTrace"/> merged rows
    /// (already annotated with <c>_parent_span_id</c>, <c>_wire_*</c>, etc.); <paramref name="rollup"/>
    /// is the canonical <see cref="RollupBody"/> for the SAME rows. The two MUST be computed from the
    /// same row set or the partition guarantee does not hold.
    ///
    /// <para>
    /// Only rows that receive an attribution appear in the map. A row absent from the map has no
    /// attributed cost (the projection renders the empty-value glyph + provenance, never $0.00). Never
    /// throws: malformed event JSON or missing columns degrade a row to "no attribution".
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, AttributedCost> Attribute(
        IReadOnlyList<Dictionary<string, object?>> rows,
        RollupBody rollup)
    {
        ArgumentNullException.ThrowIfNull(rollup);
        var result = new Dictionary<string, AttributedCost>(StringComparer.Ordinal);
        if (rows.Count == 0) return result;

        // ---- index span tree: id -> resolved parent, and top-level-ancestor resolution ----
        // Mirror TraceRollup's resolution exactly (write-side parent_span_id wins, else temporal).
        var spanResolvedParent = new Dictionary<string, string?>(StringComparer.Ordinal);
        var spanRows = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!string.Equals(Str(r, "_source"), "span", StringComparison.Ordinal)) continue;
            var id = Str(r, "id");
            if (id == null) continue;
            spanRows[id] = r;
            spanResolvedParent[id] = Str(r, "parent_span_id") ?? Str(r, "_parent_span_id");
        }

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

        // ---- index the rollup's by_task cost by top-level span id ----
        // Subject.Id on a by_task RollupRow is the top-level span id. The cost number here is the
        // canonical, already-priced per-task total we distribute (never re-priced).
        var taskCost = new Dictionary<string, AttributedCost>(StringComparer.Ordinal);
        foreach (var row in rollup.ByTask)
        {
            var topId = row.Subject.Id;
            if (string.IsNullOrEmpty(topId)) continue;
            taskCost[topId] = new AttributedCost(
                row.Cost.ValueUsd,
                row.Cost.CostProvenance ?? Scope.Core.Cron.Run.CostProvenance.Unavailable,
                row.Cost.Source,
                row.Cost.PricingVersion);
        }

        // ---- collect each task's tool-call events with their output-token weight ----
        // An event belongs to the task whose top-level ancestor owns the event's resolved span.
        // Weight is a decimal (T-2026-06-20-20 Piece 2): the split is computed in exact decimal so the
        // per-row shares sum to the task total exactly and no clamp is needed.
        var taskEvents = new Dictionary<string, List<(string Id, decimal Weight)>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!string.Equals(Str(r, "_source"), "event", StringComparison.Ordinal)) continue;
            var id = Str(r, "id");
            if (id == null) continue;

            // Only tool_call events carry a tool_use_id; those are the per-tool-call homes.
            var (toolUseId, outputLen) = ParseEventToolUseAndOutput(Str(r, "event_data_json"));
            if (string.IsNullOrEmpty(toolUseId)) continue;

            var span = Str(r, "_parent_span_id") ?? Str(r, "span_id");
            if (string.IsNullOrEmpty(span) || !spanRows.ContainsKey(span)) continue; // unattached -> stays on residual
            var topId = TopLevelAncestor(span);

            // Output-token share weight: prefer the recoverable output length; default 1 for even split.
            decimal weight = outputLen > 0 ? outputLen : 1m;
            (taskEvents.TryGetValue(topId, out var list) ? list : taskEvents[topId] = []).Add((id, weight));
        }

        // ---- distribute each task's cost over its tool-call events; residual -> root span row ----
        // The top-level span id IS the task's root row (its agent/turn home on the timeline).
        foreach (var (topId, cost) in taskCost)
        {
            if (cost.ValueUsd is not { } total)
            {
                // Unavailable cost: still surface provenance on the task root so the row renders the
                // empty-value glyph + provenance chip (never silently vanishing from the timeline).
                result[topId] = cost;
                continue;
            }

            var events = taskEvents.GetValueOrDefault(topId);
            if (events == null || events.Count == 0)
            {
                // No tool-call events on this page -> the whole task cost is the root span's
                // (text-only turn / header-attributed wire). Reconciles: the task's full cost lands once.
                result[topId] = cost with { ValueUsd = total };
                continue;
            }

            // Weighted split across the task's tool-call events, in EXACT decimal. The LAST event absorbs
            // the residual (total - allocated) so the per-event sum equals the task total to the cent.
            //
            // No negative-residual clamp is needed (T-2026-06-20-20 Piece 2). PROOF: every weight is >= 1
            // (a recovered output length is a positive int; a missing/zero output defaults to 1m), so the
            // last weight w_last >= 1 and the first n-1 weights sum to (weightSum - w_last) <= weightSum - 1.
            // The exact (un-rounded) sum of the first n-1 shares is total*(weightSum - w_last)/weightSum,
            // which is <= total*(weightSum - 1)/weightSum < total. Each decimal share rounds by at most a
            // sub-1e-26 ulp, and there are at most a handful of terms, so the accumulated rounding error is
            // many orders of magnitude smaller than the >= total/weightSum gap to `total`. Therefore
            // allocated < total strictly, so the residual `total - allocated` is strictly positive: no
            // share is ever negative, and the sum is exact by construction. (The prior double clamp existed
            // only because binary FP could leave a ~-1e-14 last residual; decimal eliminates that regime.)
            var weightSum = 0m;
            foreach (var e in events) weightSum += e.Weight;
            if (weightSum <= 0) weightSum = events.Count; // defensive: all-zero weights -> even split

            var shares = new decimal[events.Count];
            var allocated = 0m;
            for (var i = 0; i < events.Count; i++)
            {
                if (i == events.Count - 1)
                {
                    shares[i] = total - allocated; // residual to the last event: exact sum by construction
                }
                else
                {
                    var w = events[i].Weight <= 0 ? (weightSum / events.Count) : events[i].Weight;
                    shares[i] = total * (w / weightSum);
                    allocated += shares[i];
                }
            }

            for (var i = 0; i < events.Count; i++)
                result[events[i].Id] = cost with { ValueUsd = shares[i] };
        }

        return result;
    }

    /// <summary>
    /// Parses an event's <c>event_data_json</c> for its structured <c>tool_use_id</c> and the length of
    /// its <c>output</c> string (the recoverable per-tool output-token proxy used as the split weight).
    /// Returns (null, 0) for non-object JSON, missing fields, or malformed JSON. Never throws.
    /// </summary>
    private static (string? ToolUseId, int OutputLen) ParseEventToolUseAndOutput(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return (null, 0);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, 0);
            var root = doc.RootElement;
            string? toolUseId = root.TryGetProperty("tool_use_id", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            // Output length is a char count (int); the caller turns it into a decimal split weight.
            int outputLen = root.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.String
                ? (o.GetString()?.Length ?? 0)
                : 0;
            return (toolUseId, outputLen);
        }
        catch (JsonException)
        {
            return (null, 0);
        }
    }
}
