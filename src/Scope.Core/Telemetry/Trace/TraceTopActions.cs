namespace Scope.Core.Telemetry.Trace;

using static Scope.Core.Telemetry.Trace.TraceProjection;

/// <summary>
/// Computes the top-actions body (kind=top_actions, roadmap item 10) from the tool_call/agent_message
/// events of a session. Aggregates event count and total duration_ms by taxonomy category, then ranks
/// the same rows two ways: by count and by total time. Content-free: only taxonomy ids and numbers.
/// </summary>
public static class TraceTopActions
{
    /// <summary>
    /// Builds the top-actions body from merged trace rows (only _source=="event" rows are used).
    /// <paramref name="limit"/> caps each ranking; non-positive means no cap.
    /// </summary>
    public static TopActionsBody Compute(IReadOnlyList<Dictionary<string, object?>> rows, int limit)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var times = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var r in rows)
        {
            if (!(string.Equals(Str(r, "_source"), "event", StringComparison.Ordinal))) continue;
            var ed = ParseEvent(r);
            var taxId = ToolTaxonomy.Resolve(ed.Tool);
            counts[taxId] = counts.GetValueOrDefault(taxId) + 1;
            times[taxId] = times.GetValueOrDefault(taxId) + (ed.DurationMs ?? 0);
        }

        var allIds = new HashSet<string>(counts.Keys, StringComparer.Ordinal);
        var rowsList = new List<TopActionRow>(allIds.Count);
        foreach (var id in allIds)
            rowsList.Add(new TopActionRow(id, counts.GetValueOrDefault(id), times.GetValueOrDefault(id)));

        var byCount = rowsList
            .OrderByDescending(x => x.Count).ThenByDescending(x => x.TotalMs).ThenBy(x => x.TaxonomyId, StringComparer.Ordinal)
            .ToList();
        var byTime = rowsList
            .OrderByDescending(x => x.TotalMs).ThenByDescending(x => x.Count).ThenBy(x => x.TaxonomyId, StringComparer.Ordinal)
            .ToList();

        if (limit > 0)
        {
            if (byCount.Count > limit) byCount = byCount.GetRange(0, limit);
            if (byTime.Count > limit) byTime = byTime.GetRange(0, limit);
        }

        return new TopActionsBody(ToolTaxonomy.Version, byCount, byTime);
    }

    private readonly record struct EventInfo(string? Tool, long? DurationMs);

    private static EventInfo ParseEvent(Dictionary<string, object?> r)
    {
        var raw = Str(r, "event_data_json");
        if (string.IsNullOrEmpty(raw)) return default;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string? tool = root.TryGetProperty("tool", out var tv) && tv.ValueKind == System.Text.Json.JsonValueKind.String
                ? tv.GetString() : null;
            long? dur = root.TryGetProperty("duration_ms", out var dv)
                && dv.ValueKind == System.Text.Json.JsonValueKind.Number && dv.TryGetInt64(out var d) ? d : null;
            return new EventInfo(tool, dur);
        }
        catch (System.Text.Json.JsonException)
        {
            return default;
        }
    }
}
