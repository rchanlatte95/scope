namespace Scope.Core.Telemetry.Trace;

using static Scope.Core.Telemetry.Trace.TraceProjection;

/// <summary>
/// Projects the session graph rows produced by ObservabilityAuditDb (GetSessionGraphData enriched
/// by EnrichGraphWithSpans, which adds "span:{id}" pseudo-nodes) into the scope-trace-v1
/// session_graph body. The internal "span:{id}" key prefix is stripped: span nodes carry the bare
/// span canonical id and kind="span"; session nodes carry their session key and kind="session".
/// Edges are derived from each node's parent_session_key with honest link_source provenance.
/// </summary>
public static class TraceGraphProjection
{
    private const string SpanPrefix = "span:";

    /// <summary>Builds the local-full session_graph body from the enriched graph rows.</summary>
    /// <param name="pricingVersion">
    /// The resolved "prices as of" stamp threaded onto each node's cost Money block, or null when no
    /// pricing config is configured. A date string is content-free (a timestamp, not content).
    /// </param>
    public static SessionGraphBody Build(string rootSessionKey, IReadOnlyList<Dictionary<string, object?>> graphRows, string? pricingVersion = null)
    {
        var nodes = new List<GraphNodeDto>(graphRows.Count);
        var edges = new List<GraphEdgeDto>();

        foreach (var row in graphRows)
        {
            var rawKey = Str(row, "session_key") ?? "";
            var isSpan = rawKey.StartsWith(SpanPrefix, StringComparison.Ordinal);
            var canonicalId = isSpan ? rawKey[SpanPrefix.Length..] : rawKey;
            var kind = isSpan ? "span" : "session";

            // DB boundary (T-2026-06-20-20 Piece 2): total_cost_usd is a SQLite REAL (double); convert
            // to decimal for the Money.value_usd field (serialized as a JSON number, shape unchanged).
            var cost = Double(row, "total_cost_usd");
            var node = new GraphNodeDto(
                Kind: kind,
                Id: canonicalId,
                DisplayId: TraceIdCodec.DisplayId(canonicalId),
                Alias: null, // ordinal aliases require the full ordered trace; null in the graph body
                AgentType: Str(row, "agent_type"),
                RequestCount: Int(row, "request_count") ?? 0,
                Cost: cost is > 0
                    ? new Money((decimal)cost.Value, "estimate", PricingVersion: pricingVersion, PricedAt: null, ModelId: null, Provider: null)
                    : null,
                Depth: Int(row, "_depth") ?? 0,
                Label: Str(row, "agent_name"));
            nodes.Add(node);

            var parentRaw = Str(row, "parent_session_key");
            if (!string.IsNullOrEmpty(parentRaw) && !(string.Equals(parentRaw, "-", StringComparison.Ordinal)))
            {
                var parentIsSpan = parentRaw.StartsWith(SpanPrefix, StringComparison.Ordinal);
                var parentId = parentIsSpan ? parentRaw[SpanPrefix.Length..] : parentRaw;
                // Provenance: a span pseudo-parent comes from EnrichGraphWithSpans; otherwise the
                // stored parent_session_key linkage.
                var linkSource = (isSpan || parentIsSpan) ? "span_enrichment" : "parent_session_key";
                edges.Add(new GraphEdgeDto(parentId, canonicalId, linkSource));
            }
        }

        return new SessionGraphBody(TraceIdCodec.DisplayId(rootSessionKey), nodes, edges);
    }

    /// <summary>Projects a local-full session_graph body to the content-free packet tier (drops node labels).</summary>
    public static PacketSessionGraphBody ToPacket(SessionGraphBody body)
    {
        var nodes = new List<PacketGraphNode>(body.Nodes.Count);
        foreach (var n in body.Nodes)
        {
            // Span ids are UUID-shaped; session ids may be raw keys -> map to packet identifier.
            var packetId = string.Equals(n.Kind, "span", StringComparison.Ordinal) ? n.Id : (TraceIdCodec.PacketSessionId(n.Id) ?? n.Id);
            nodes.Add(new PacketGraphNode(
                Kind: n.Kind,
                Id: packetId,
                DisplayId: n.DisplayId,
                Alias: n.Alias,
                AgentType: n.AgentType,
                RequestCount: n.RequestCount,
                Cost: n.Cost,
                Depth: n.Depth));
        }

        var edges = new List<GraphEdgeDto>(body.Edges.Count);
        foreach (var e in body.Edges)
        {
            var parent = TraceIdCodec.IsUuidShaped(e.Parent) ? e.Parent : (TraceIdCodec.PacketSessionId(e.Parent) ?? e.Parent);
            var child = TraceIdCodec.IsUuidShaped(e.Child) ? e.Child : (TraceIdCodec.PacketSessionId(e.Child) ?? e.Child);
            edges.Add(new GraphEdgeDto(parent, child, e.LinkSource));
        }

        return new PacketSessionGraphBody(body.Root, nodes, edges);
    }
}
