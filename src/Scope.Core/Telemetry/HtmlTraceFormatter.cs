namespace Scope.Core.Telemetry;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

/// <summary>
/// Generates self-contained interactive HTML trace viewers from observability data.
/// Produces a single HTML file with inline CSS/JS matching the Scope audit trace design.
/// </summary>
// Public contract preserved (T-2026-06-17 sweep, owner): these public static formatters accept the
// canonical loosely-typed List<Dictionary<string,object?>> row shapes produced by ObservabilityAuditDb
// and are public Scope.Core API; the parameter collection types are preserved.
#pragma warning disable MA0016 // public Scope.Core HTML-trace-formatter API; canonical DB-row parameter shapes preserved
public static class HtmlTraceFormatter
{
    /// <summary>
    /// Produces a self-contained HTML string visualizing a trace as an interactive timeline.
    /// </summary>
    public static string Format(
        List<Dictionary<string, object?>> traceEntries,
        Dictionary<string, object?>? dispatchInfo = null)
    {
        var sb = new StringBuilder(65536);

        var requestEntries = new List<Dictionary<string, object?>>();
        var eventEntries = new List<Dictionary<string, object?>>();
        var spanEntries = new List<Dictionary<string, object?>>();
        foreach (var e in traceEntries)
        {
            var source = Get(e, "_source");
            if (string.Equals(source, "request", StringComparison.Ordinal)) requestEntries.Add(e);
            else if (string.Equals(source, "event", StringComparison.Ordinal)) eventEntries.Add(e);
            else if (string.Equals(source, "span", StringComparison.Ordinal)) spanEntries.Add(e);
        }

        var dispatchId = dispatchInfo != null ? Get(dispatchInfo, "id") : "-";
        var label = dispatchInfo != null ? Get(dispatchInfo, "label") : "-";
        var sessionKey = requestEntries.Count > 0 ? Get(requestEntries[0], "agent_session_key") : "-";
        var primarySurface = requestEntries.Count > 0 ? Get(requestEntries[0], "caller_surface") : "-";
        var (startDt, endDt, durationStr) = ComputeTiming(traceEntries, dispatchInfo);

        // Collect agent names from spans for the filter dropdown
        var agentNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in spanEntries)
        {
            var name = Get(s, "agent_name");
            if (!(string.Equals(name, "-", StringComparison.Ordinal))) agentNames.Add(name);
        }

        // Collect cost data for the summary
        var costData = CollectCostData(requestEntries, eventEntries, spanEntries);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.Append("<title>Scope Audit Trace");
        if (!(string.Equals(dispatchId, "-", StringComparison.Ordinal)))
            sb.Append(" - Dispatch ").Append(Enc(dispatchId.Length > 8 ? dispatchId[..8] : dispatchId));
        sb.AppendLine("</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(Css);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        WriteHeader(sb, dispatchId, label, requestEntries.Count, eventEntries.Count, spanEntries.Count, durationStr);
        WriteSearchBar(sb, agentNames);
        WriteCostSummary(sb, costData);
        WriteParticipants(sb, sessionKey, primarySurface, spanEntries.Count);
        WriteLegend(sb);
        WriteTimeline(sb, traceEntries, dispatchId, label, startDt, endDt, sessionKey);

        sb.AppendLine("<script>");
        sb.AppendLine(Script);
        sb.AppendLine("</script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    /// <summary>
    /// Collects cost breakdown data from trace entries for the summary section.
    /// Returns total cost, per-agent costs, and per-tool-type costs.
    /// </summary>
    internal static CostBreakdown CollectCostData(
        List<Dictionary<string, object?>> requests,
        List<Dictionary<string, object?>> events,
        List<Dictionary<string, object?>> spans)
    {
        var totalCost = 0.0;
        var perAgent = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var perTool = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // Costs from spans (which have _total_cost_usd from EnrichSpanCosts)
        foreach (var s in spans)
        {
            var costStr = Get(s, "_total_cost_usd");
            var parentSpanId = Get(s, "_parent_span_id");
            var dbParentSpanId = Get(s, "parent_span_id");
            var isChild = !(string.Equals(parentSpanId, "-", StringComparison.Ordinal)) || !(string.Equals(dbParentSpanId, "-", StringComparison.Ordinal));

            if (!(string.Equals(costStr, "-", StringComparison.Ordinal)) && double.TryParse(costStr, CultureInfo.InvariantCulture, out var cost) && cost > 0)
            {
                // Only count top-level span costs toward total to avoid double-counting
                if (!isChild)
                    totalCost += cost;

                var agentName = Get(s, "agent_name");
                if (!(string.Equals(agentName, "-", StringComparison.Ordinal)))
                {
                    perAgent.TryGetValue(agentName, out var existing);
                    perAgent[agentName] = existing + cost;
                }
            }
        }

        // Per-tool costs from request operations (when no spans have costs)
        foreach (var r in requests)
        {
            var operation = Get(r, "operation");
            if (!(string.Equals(operation, "-", StringComparison.Ordinal)))
            {
                perTool.TryGetValue(operation, out var existing);
                perTool[operation] = existing + 1; // count, not cost
            }
        }

        // If total cost is 0 from spans, it means no cost data available
        return new CostBreakdown(totalCost, perAgent, perTool, requests.Count);
    }

    internal record CostBreakdown(
        double TotalCost,
        Dictionary<string, double> PerAgent,
        Dictionary<string, double> PerTool,
        int RequestCount);

    /// <summary>
    /// Produces a self-contained HTML string with an interactive session graph at the top and
    /// per-session trace timelines below. Each session node in the graph is clickable and scrolls
    /// to its corresponding trace section. The <paramref name="sessionGraph"/> list contains
    /// session nodes from <see cref="ObservabilityAuditDb.GetSessionGraphData"/>.
    /// The <paramref name="perSessionTraces"/> maps session_key to its trace entries.
    /// </summary>
    public static string FormatWithSessionGraph(
        List<Dictionary<string, object?>> sessionGraph,
        Dictionary<string, List<Dictionary<string, object?>>> perSessionTraces,
        Dictionary<string, object?>? dispatchInfo = null)
    {
        var sb = new StringBuilder(131072);

        // Aggregate stats across all sessions
        var totalRequests = 0;
        var totalEvents = 0;
        var totalSpans = 0;
        foreach (var (_, entries) in perSessionTraces)
        {
            foreach (var e in entries)
            {
                var source = Get(e, "_source");
                if (string.Equals(source, "request", StringComparison.Ordinal)) totalRequests++;
                else if (string.Equals(source, "event", StringComparison.Ordinal)) totalEvents++;
                else if (string.Equals(source, "span", StringComparison.Ordinal)) totalSpans++;
            }
        }

        var dispatchId = dispatchInfo != null ? Get(dispatchInfo, "id") : "-";
        var label = dispatchInfo != null ? Get(dispatchInfo, "label") : "-";
        var allEntries = perSessionTraces.Values.SelectMany(e => e).ToList();
        var (startDt, endDt, durationStr) = ComputeTiming(allEntries, dispatchInfo);
        var rootSession = sessionGraph.Count > 0 ? Get(sessionGraph[0], "session_key") : "-";
        var primarySurface = sessionGraph.Count > 0 ? Get(sessionGraph[0], "agent_type") : "-";

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.Append("<title>Scope Session Graph");
        if (!(string.Equals(dispatchId, "-", StringComparison.Ordinal)))
            sb.Append(" - Dispatch ").Append(Enc(dispatchId.Length > 8 ? dispatchId[..8] : dispatchId));
        sb.AppendLine("</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(Css);
        sb.AppendLine(SessionGraphCss);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        WriteHeader(sb, dispatchId, label, totalRequests, totalEvents, totalSpans, durationStr);

        // Session graph SVG
        WriteSessionGraph(sb, sessionGraph);

        WriteLegend(sb);

        // Per-session traces (single-session starts collapsed)
        var singleSession = sessionGraph.Count == 1;
        foreach (var session in sessionGraph)
        {
            var key = Get(session, "session_key");
            var agentName = Get(session, "agent_name");
            var depth = session.TryGetValue("_depth", out var d) && d is int di ? di : 0;

            if (!perSessionTraces.TryGetValue(key, out var entries))
                entries = [];

            WriteSessionTraceSection(sb, key, agentName, depth, entries, dispatchId, label, startDt, endDt, collapsed: singleSession);
        }

        sb.AppendLine("<script>");
        sb.AppendLine(Script);
        sb.AppendLine(SessionGraphScript);
        sb.AppendLine("</script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    /// <summary>
    /// Computes layout positions for session graph nodes and renders an SVG with edges.
    /// Layout: horizontal tree, root at left, children spread vertically per depth column.
    /// </summary>
    internal static void WriteSessionGraph(StringBuilder sb, List<Dictionary<string, object?>> sessions)
    {
        if (sessions.Count == 0) return;

        const int nodeWidth = 200;
        const int nodeHeight = 72;
        const int columnGap = 100;
        const int rowGap = 24;
        const int paddingX = 40;
        const int paddingY = 40;

        // Build depth-level groups (depth = column from left)
        var byDepth = new Dictionary<int, List<Dictionary<string, object?>>>();
        var maxDepth = 0;
        foreach (var s in sessions)
        {
            var depth = s.TryGetValue("_depth", out var d) && d is int di ? di : 0;
            if (depth > maxDepth) maxDepth = depth;
            if (!byDepth.TryGetValue(depth, out var list))
            {
                list = [];
                byDepth[depth] = list;
            }
            list.Add(s);
        }

        var maxNodesAtLevel = 1;
        foreach (var (_, nodes) in byDepth)
            if (nodes.Count > maxNodesAtLevel) maxNodesAtLevel = nodes.Count;

        var svgWidth = paddingX * 2 + (maxDepth + 1) * nodeWidth + maxDepth * columnGap;
        var svgHeight = Math.Max(paddingY * 2 + maxNodesAtLevel * nodeHeight + (maxNodesAtLevel - 1) * rowGap, 200);

        // Compute x, y positions: depth -> column (left-to-right), index -> row (top-to-bottom)
        var positions = new Dictionary<string, (int X, int Y, int CenterX, int CenterY)>(StringComparer.Ordinal);
        for (var depth = 0; depth <= maxDepth; depth++)
        {
            if (!byDepth.TryGetValue(depth, out var nodesAtLevel)) continue;
            var x = paddingX + depth * (nodeWidth + columnGap);
            var totalHeight = nodesAtLevel.Count * nodeHeight + (nodesAtLevel.Count - 1) * rowGap;
            var startY = (svgHeight - totalHeight) / 2;

            for (var i = 0; i < nodesAtLevel.Count; i++)
            {
                var y = startY + i * (nodeHeight + rowGap);
                var key = Get(nodesAtLevel[i], "session_key");
                positions[key] = (x, y, x + nodeWidth / 2, y + nodeHeight / 2);
            }
        }

        sb.AppendLine("<div class=\"session-graph-container\">");
        sb.AppendLine("  <h2 class=\"session-graph-title\">Session Graph</h2>");
        sb.Append("  <svg class=\"session-graph\" width=\"").Append(svgWidth)
          .Append("\" height=\"").Append(svgHeight).AppendLine("\" viewBox=\"0 0 " + svgWidth + " " + svgHeight + "\">");

        // Draw edges (parent right edge -> child left edge, horizontal Bezier)
        foreach (var session in sessions)
        {
            var key = Get(session, "session_key");
            var parentKey = Get(session, "parent_session_key");
            if (string.Equals(parentKey, "-", StringComparison.Ordinal) || !positions.ContainsKey(key) || !positions.ContainsKey(parentKey)) continue;

            var parentPos = positions[parentKey];
            var childPos = positions[key];

            var parentRight = parentPos.X + nodeWidth;
            var childLeft = childPos.X;
            var midX = (parentRight + childLeft) / 2;

            sb.Append("    <path class=\"session-edge\" d=\"M ")
              .Append(parentRight).Append(' ').Append(parentPos.CenterY)
              .Append(" C ").Append(midX).Append(' ').Append(parentPos.CenterY)
              .Append(' ').Append(midX).Append(' ').Append(childPos.CenterY)
              .Append(' ').Append(childLeft).Append(' ').Append(childPos.CenterY)
              .Append("\" data-parent=\"").Append(Enc(parentKey))
              .Append("\" data-child=\"").Append(Enc(key))
              .AppendLine("\"/>");
        }

        // Draw nodes
        foreach (var session in sessions)
        {
            var key = Get(session, "session_key");
            if (!positions.TryGetValue(key, out var pos)) continue;

            var agentName = Get(session, "agent_name");
            var requestCount = Get(session, "request_count");
            var costStr = Get(session, "total_cost_usd");
            var depth = session.TryGetValue("_depth", out var d) && d is int di ? di : 0;
            double costVal = 0;
            var hasCost = !(string.Equals(costStr, "-", StringComparison.Ordinal)) && double.TryParse(costStr, CultureInfo.InvariantCulture, out costVal) && costVal > 0;

            var displayName = agentName.Length > 22 ? agentName[..19] + "..." : agentName;
            var isRoot = depth == 0;

            sb.Append("    <g class=\"session-node").Append(isRoot ? " session-root" : "")
              .Append("\" data-session=\"").Append(Enc(key))
              .Append("\" onclick=\"selectSession('").Append(EscJs(key)).AppendLine("')\" style=\"cursor:pointer\">");

            sb.Append("      <rect x=\"").Append(pos.X).Append("\" y=\"").Append(pos.Y)
              .Append("\" width=\"").Append(nodeWidth).Append("\" height=\"").Append(nodeHeight)
              .AppendLine("\" rx=\"8\" class=\"session-node-rect\"/>");

            sb.Append("      <text x=\"").Append(pos.CenterX).Append("\" y=\"").Append(pos.Y + 28)
              .Append("\" text-anchor=\"middle\" class=\"session-node-name\">")
              .Append(Enc(displayName)).AppendLine("</text>");

            var metaParts = new List<string> { $"{requestCount} req" };
            if (hasCost) metaParts.Add($"~${costVal:F4}");
            sb.Append("      <text x=\"").Append(pos.CenterX).Append("\" y=\"").Append(pos.Y + 48)
              .Append("\" text-anchor=\"middle\" class=\"session-node-meta\">")
              .Append(Enc(string.Join(" | ", metaParts))).AppendLine("</text>");

            var keyShort = key.Length > 16 ? key[..16] + "..." : key;
            sb.Append("      <text x=\"").Append(pos.CenterX).Append("\" y=\"").Append(pos.Y + 63)
              .Append("\" text-anchor=\"middle\" class=\"session-node-key\">")
              .Append(Enc(keyShort)).AppendLine("</text>");

            sb.AppendLine("    </g>");
        }

        sb.AppendLine("  </svg>");
        sb.AppendLine("</div>");
    }

    private static void WriteSessionTraceSection(StringBuilder sb, string sessionKey, string agentName,
        int depth, List<Dictionary<string, object?>> entries, string dispatchId, string label,
        DateTime? startDt, DateTime? endDt, bool collapsed = false)
    {
        var requestCount = 0;
        var eventCount = 0;
        var spanCount = 0;
        foreach (var e in entries)
        {
            var source = Get(e, "_source");
            if (string.Equals(source, "request", StringComparison.Ordinal)) requestCount++;
            else if (string.Equals(source, "event", StringComparison.Ordinal)) eventCount++;
            else if (string.Equals(source, "span", StringComparison.Ordinal)) spanCount++;
        }

        var depthIndent = depth > 0 ? $"depth-{Math.Min(depth, 3)}" : "";
        var collapsedClass = collapsed ? " collapsed" : "";
        sb.Append("<div class=\"session-section ").Append(depthIndent).Append(collapsedClass)
          .Append("\" id=\"session-").Append(Enc(sessionKey)).AppendLine("\">");

        sb.AppendLine("  <div class=\"session-section-header\" onclick=\"toggleSession(this)\">");
        sb.AppendLine("    <div class=\"session-section-toggle\"></div>");
        sb.Append("    <div class=\"session-section-avatar\">").Append(depth == 0 ? "R" : $"S{depth}").AppendLine("</div>");
        sb.AppendLine("    <div class=\"session-section-info\">");
        sb.Append("      <div class=\"session-section-name\">").Append(Enc(agentName)).AppendLine("</div>");
        sb.Append("      <div class=\"session-section-meta\">");
        sb.Append(requestCount).Append(" requests");
        if (eventCount > 0) sb.Append(" | ").Append(eventCount).Append(" events");
        if (spanCount > 0) sb.Append(" | ").Append(spanCount).Append(" spans");
        sb.AppendLine("</div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");

        if (entries.Count == 0)
        {
            sb.AppendLine("  <div class=\"session-section-body\">");
            sb.AppendLine("    <div style=\"color:var(--text-muted);font-style:italic;padding:12px 0\">No trace entries for this session.</div>");
            sb.AppendLine("  </div>");
        }
        else
        {
            sb.AppendLine("  <div class=\"session-section-body\">");
            var sessionKey2 = entries.Count > 0 ? Get(entries[0], "agent_session_key") : sessionKey;
            WriteTimeline(sb, entries, dispatchId, label, startDt, endDt, sessionKey2);
            sb.AppendLine("  </div>");
        }

        sb.AppendLine("</div>");
    }

    private static void WriteHeader(StringBuilder sb, string dispatchId, string label,
        int requestCount, int eventCount, int spanCount, string durationStr)
    {
        sb.AppendLine("<div class=\"header\">");
        sb.AppendLine("  <h1>Scope Audit Trace</h1>");
        if (!(string.Equals(dispatchId, "-", StringComparison.Ordinal)))
            sb.Append("  <div class=\"dispatch-id\">").Append(Enc(dispatchId)).AppendLine("</div>");
        sb.AppendLine("  <div class=\"header-meta\">");
        if (!(string.Equals(label, "-", StringComparison.Ordinal)))
            sb.Append("    <span>Label: <strong>").Append(Enc(label)).AppendLine("</strong></span>");
        sb.Append("    <span>Scope Requests: <strong>").Append(requestCount).AppendLine("</strong></span>");
        if (eventCount > 0)
            sb.Append("    <span>Tool Calls: <strong>").Append(eventCount).AppendLine("</strong></span>");
        if (spanCount > 0)
            sb.Append("    <span>Agent Spans: <strong>").Append(spanCount).AppendLine("</strong></span>");
        if (!(string.Equals(durationStr, "-", StringComparison.Ordinal)))
            sb.Append("    <span>Duration: <strong>").Append(Enc(durationStr)).AppendLine("</strong></span>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</div>");
    }

    private static void WriteSearchBar(StringBuilder sb, SortedSet<string> agentNames)
    {
        sb.AppendLine("<div class=\"search-bar\" id=\"search-bar\">");
        sb.AppendLine("  <div class=\"search-controls\">");
        sb.AppendLine("    <input type=\"text\" id=\"search-tool\" class=\"search-input\" placeholder=\"Filter by tool name...\" oninput=\"applyFilters()\">");
        sb.AppendLine("    <select id=\"search-agent\" class=\"search-select\" onchange=\"applyFilters()\">");
        sb.AppendLine("      <option value=\"\">All agents</option>");
        foreach (var name in agentNames)
            sb.Append("      <option value=\"").Append(Enc(name)).Append("\">").Append(Enc(name)).AppendLine("</option>");
        sb.AppendLine("    </select>");
        sb.AppendLine("    <button class=\"search-clear\" onclick=\"clearFilters()\">Clear</button>");
        sb.AppendLine("    <span class=\"search-count\" id=\"search-count\"></span>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</div>");
    }

    private static void WriteCostSummary(StringBuilder sb, CostBreakdown costData)
    {
        var hasCosts = costData.TotalCost > 0;
        if (!hasCosts && costData.RequestCount == 0) return;

        sb.AppendLine("<div class=\"cost-summary\" id=\"cost-summary\">");
        sb.AppendLine("  <div class=\"cost-summary-title\">Cost Breakdown</div>");
        sb.AppendLine("  <div class=\"cost-grid\">");

        // Total cost card
        sb.AppendLine("    <div class=\"cost-card\">");
        sb.AppendLine("      <div class=\"cost-card-label\">Total Estimated Cost</div>");
        sb.Append("      <div class=\"cost-card-value\">");
        sb.Append(hasCosts ? $"${costData.TotalCost:F4}" : "N/A");
        sb.AppendLine("</div>");
        sb.AppendLine("    </div>");

        // Request count card
        sb.AppendLine("    <div class=\"cost-card\">");
        sb.AppendLine("      <div class=\"cost-card-label\">Total Requests</div>");
        sb.Append("      <div class=\"cost-card-value\">").Append(costData.RequestCount).AppendLine("</div>");
        sb.AppendLine("    </div>");

        sb.AppendLine("  </div>");

        // Per-agent table (if costs exist)
        if (hasCosts && costData.PerAgent.Count > 0)
        {
            sb.AppendLine("  <div class=\"cost-table-section\">");
            sb.AppendLine("    <div class=\"cost-table-title\">Cost per Agent</div>");
            sb.AppendLine("    <table class=\"cost-table\">");
            sb.AppendLine("      <thead><tr><th>Agent</th><th>Cost</th></tr></thead>");
            sb.AppendLine("      <tbody>");
            foreach (var kv in costData.PerAgent.OrderByDescending(k => k.Value))
            {
                sb.Append("        <tr><td>").Append(Enc(kv.Key)).Append("</td>");
                sb.Append("<td class=\"cost-value\">$").Append(CultureInfo.InvariantCulture, $"{kv.Value:F4}").AppendLine("</td></tr>");
            }
            sb.AppendLine("      </tbody>");
            sb.AppendLine("    </table>");
            sb.AppendLine("  </div>");
        }

        // Per-tool-type table (request counts by operation)
        if (costData.PerTool.Count > 0)
        {
            sb.AppendLine("  <div class=\"cost-table-section\">");
            sb.AppendLine("    <div class=\"cost-table-title\">Requests per Tool Type</div>");
            sb.AppendLine("    <table class=\"cost-table\">");
            sb.AppendLine("      <thead><tr><th>Operation</th><th>Count</th></tr></thead>");
            sb.AppendLine("      <tbody>");
            foreach (var kv in costData.PerTool.OrderByDescending(k => k.Value))
            {
                sb.Append("        <tr><td>").Append(Enc(kv.Key)).Append("</td>");
                sb.Append("<td class=\"cost-value\">").Append(CultureInfo.InvariantCulture, $"{kv.Value:F0}").AppendLine("</td></tr>");
            }
            sb.AppendLine("      </tbody>");
            sb.AppendLine("    </table>");
            sb.AppendLine("  </div>");
        }

        sb.AppendLine("</div>");
    }

    private static void WriteParticipants(StringBuilder sb, string sessionKey, string surface, int spanCount)
    {
        sb.AppendLine("<div class=\"participants\">");
        sb.AppendLine("  <div class=\"participant\">");
        sb.AppendLine("    <div class=\"participant-avatar agent-avatar\">A</div>");
        sb.AppendLine("    <div class=\"participant-info\">");
        sb.AppendLine("      <div class=\"participant-name\" style=\"color:var(--clr-agent)\">Agent</div>");
        sb.Append("      <div class=\"participant-detail\">");
        sb.Append(Enc(!(string.Equals(sessionKey, "-", StringComparison.Ordinal)) ? sessionKey : "unknown"));
        if (!(string.Equals(surface, "-", StringComparison.Ordinal))) sb.Append(" &middot; ").Append(Enc(surface));
        sb.AppendLine("</div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");
        sb.AppendLine("  <div class=\"participant\">");
        sb.AppendLine("    <div class=\"participant-avatar scope-avatar\">S</div>");
        sb.AppendLine("    <div class=\"participant-info\">");
        sb.AppendLine("      <div class=\"participant-name\" style=\"color:var(--clr-scope)\">Scope</div>");
        sb.AppendLine("      <div class=\"participant-detail\">code intelligence engine</div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");
        if (spanCount > 0)
        {
            sb.AppendLine("  <div class=\"participant\">");
            sb.AppendLine("    <div class=\"span-avatar\" style=\"width:32px;height:32px;font-size:13px\">A+</div>");
            sb.AppendLine("    <div class=\"participant-info\">");
            sb.AppendLine("      <div class=\"participant-name\" style=\"color:var(--span-accent)\">Sub-agents</div>");
            sb.Append("      <div class=\"participant-detail\">").Append(spanCount).Append(" agent span");
            if (spanCount > 1) sb.Append('s');
            sb.AppendLine("</div>");
            sb.AppendLine("    </div>");
            sb.AppendLine("  </div>");
        }
        sb.AppendLine("</div>");
    }

    private static void WriteLegend(StringBuilder sb) => sb.AppendLine(LegendHtml);

    private static void WriteTimeline(StringBuilder sb, List<Dictionary<string, object?>> entries,
        string dispatchId, string label, DateTime? startDt, DateTime? endDt, string sessionKey)
    {
        sb.AppendLine("<div class=\"timeline\">");

        WriteStartBookend(sb, dispatchId, label, startDt, sessionKey);

        if (entries.Count == 0)
        {
            sb.AppendLine("  <div class=\"tl-node\">");
            sb.AppendLine("    <div style=\"color:var(--text-muted);font-style:italic;padding:12px 0\">No trace entries found.</div>");
            sb.AppendLine("  </div>");
        }
        else
        {
            var childrenByParent = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
            var topLevel = new List<Dictionary<string, object?>>();

            foreach (var entry in entries)
            {
                var parentId = Get(entry, "_parent_span_id");
                if (string.Equals(parentId, "-", StringComparison.Ordinal))
                {
                    topLevel.Add(entry);
                }
                else
                {
                    if (!childrenByParent.TryGetValue(parentId, out var list))
                    {
                        list = [];
                        childrenByParent[parentId] = list;
                    }
                    list.Add(entry);
                }
            }

            foreach (var entry in topLevel)
                WriteTimelineEntry(sb, entry, entries, childrenByParent, startDt);
        }

        WriteEndBookend(sb, dispatchId, entries.Count, endDt, startDt);

        sb.AppendLine("</div>");
    }

    private static void WriteTimelineEntry(StringBuilder sb, Dictionary<string, object?> entry,
        List<Dictionary<string, object?>> allEntries,
        Dictionary<string, List<Dictionary<string, object?>>> childrenByParent,
        DateTime? traceStart)
    {
        var source = Get(entry, "_source");
        if (string.Equals(source, "span", StringComparison.Ordinal))
            WriteSpanNode(sb, entry, allEntries, childrenByParent, traceStart);
        else if (string.Equals(source, "event", StringComparison.Ordinal))
            WriteEventNode(sb, entry, traceStart);
        else
            WriteRequestNode(sb, entry, traceStart);
    }

    private static void WriteStartBookend(StringBuilder sb, string dispatchId, string label,
        DateTime? startDt, string sessionKey)
    {
        var timeStr = startDt.HasValue ? FormatTimeShort(startDt.Value) : "-";
        sb.AppendLine("  <div class=\"tl-node\" style=\"margin-bottom:20px\">");
        WriteTimelineMarker(sb, startDt, startDt);
        sb.AppendLine("    <div class=\"tl-icon\">");
        sb.AppendLine("      <svg width=\"36\" height=\"36\" viewBox=\"0 0 36 36\"><polygon points=\"18,2 34,18 18,34 2,18\" fill=\"none\" stroke=\"var(--clr-start)\" stroke-width=\"2.5\"/><circle cx=\"18\" cy=\"18\" r=\"5\" fill=\"var(--clr-start)\"/></svg>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"tooltip\">");
        WriteTooltipRow(sb, "Event", !(string.Equals(dispatchId, "-", StringComparison.Ordinal)) ? "Dispatch created" : "Trace start");
        if (!(string.Equals(dispatchId, "-", StringComparison.Ordinal)))
        {
            var idShort = dispatchId.Length > 12 ? $"{dispatchId[..8]}...{dispatchId[^4..]}" : dispatchId;
            WriteTooltipRow(sb, "Dispatch", idShort);
        }
        if (!(string.Equals(label, "-", StringComparison.Ordinal))) WriteTooltipRow(sb, "Label", label);
        if (startDt.HasValue) WriteTooltipRow(sb, "Time", FormatTimeFull(startDt.Value));
        if (!(string.Equals(sessionKey, "-", StringComparison.Ordinal))) WriteTooltipRow(sb, "Agent", sessionKey);
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"bookend bookend-start\">");
        sb.Append("      <span class=\"bookend-label\">").Append(!(string.Equals(dispatchId, "-", StringComparison.Ordinal)) ? "Dispatch Start" : "Trace Start").AppendLine("</span>");
        sb.Append("      <span class=\"bookend-time\">").Append(Enc(timeStr)).AppendLine("</span>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");
    }

    private static void WriteEndBookend(StringBuilder sb, string dispatchId, int totalEntries,
        DateTime? endDt, DateTime? traceStart)
    {
        var timeStr = endDt.HasValue ? FormatTimeShort(endDt.Value) : "-";
        sb.AppendLine("  <div class=\"tl-node\" style=\"margin-bottom:0\">");
        WriteTimelineMarker(sb, endDt, traceStart);
        sb.AppendLine("    <div class=\"tl-icon\">");
        sb.AppendLine("      <svg width=\"36\" height=\"36\" viewBox=\"0 0 36 36\"><rect x=\"6\" y=\"6\" width=\"24\" height=\"24\" rx=\"4\" fill=\"none\" stroke=\"var(--clr-end)\" stroke-width=\"2.5\"/><rect x=\"12\" y=\"12\" width=\"12\" height=\"12\" rx=\"2\" fill=\"var(--clr-end)\"/></svg>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"tooltip\">");
        WriteTooltipRow(sb, "Event", !(string.Equals(dispatchId, "-", StringComparison.Ordinal)) ? "Dispatch end" : "Trace end");
        if (!(string.Equals(dispatchId, "-", StringComparison.Ordinal)))
        {
            var idShort = dispatchId.Length > 12 ? $"{dispatchId[..8]}...{dispatchId[^4..]}" : dispatchId;
            WriteTooltipRow(sb, "Dispatch", idShort);
        }
        if (endDt.HasValue) WriteTooltipRow(sb, "Time", FormatTimeFull(endDt.Value));
        WriteTooltipRow(sb, "Total", $"{totalEntries} entries");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"bookend bookend-end\">");
        sb.Append("      <span class=\"bookend-label\">").Append(!(string.Equals(dispatchId, "-", StringComparison.Ordinal)) ? "Dispatch End" : "Trace End").AppendLine("</span>");
        sb.Append("      <span class=\"bookend-time\">").Append(Enc(timeStr)).AppendLine("</span>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");
    }

    /// <summary>
    /// Writes a timeline elapsed marker showing seconds since trace start.
    /// </summary>
    private static void WriteTimelineMarker(StringBuilder sb, DateTime? entryTime, DateTime? traceStart)
    {
        if (!entryTime.HasValue || !traceStart.HasValue) return;
        var elapsed = entryTime.Value - traceStart.Value;
        var elapsedStr = elapsed.TotalSeconds < 0.05 ? "+0.0s" : $"+{elapsed.TotalSeconds:F1}s";
        sb.Append("    <div class=\"tl-marker\">").Append(elapsedStr).AppendLine("</div>");
    }

    private static void WriteRequestNode(StringBuilder sb, Dictionary<string, object?> entry, DateTime? traceStart)
    {
        var operation = Get(entry, "operation");
        var surface = Get(entry, "caller_surface");
        var status = Get(entry, "status");
        var elapsed = Get(entry, "total_elapsed_ms");
        var createdAt = Get(entry, "created_at");
        var opType = GetOperationType(operation);

        sb.Append("  <div class=\"tl-node\" data-tool=\"").Append(Enc(operation)).AppendLine("\">");

        // Timeline marker
        if (!(string.Equals(createdAt, "-", StringComparison.Ordinal)) && TryParseTime(createdAt, out var entryDt))
            WriteTimelineMarker(sb, entryDt, traceStart);

        sb.AppendLine("    <div class=\"tl-icon\">");
        sb.Append("      ").AppendLine(GetTimelineIconSvg(opType));
        sb.AppendLine("    </div>");

        WriteRequestTooltip(sb, entry, opType);

        var query = Get(entry, "query_text");

        sb.AppendLine("    <div class=\"request-header\">");
        sb.Append("      <span class=\"badge badge-op\" data-op=\"").Append(opType).Append("\">").Append(Enc(operation)).AppendLine("</span>");
        if (!(string.Equals(query, "-", StringComparison.Ordinal)))
            sb.Append("      <span class=\"req-query\">").Append(Enc(query)).AppendLine("</span>");
        sb.Append("      <span class=\"badge badge-secondary\">").Append(Enc(surface)).AppendLine("</span>");
        if (!(string.Equals(elapsed, "-", StringComparison.Ordinal)))
            sb.Append("      <span class=\"badge badge-secondary\">").Append(Enc(elapsed)).AppendLine("ms</span>");
        sb.Append("      <span class=\"badge badge-status\">").Append(Enc(status)).AppendLine("</span>");
        if (!(string.Equals(createdAt, "-", StringComparison.Ordinal)) && TryParseTime(createdAt, out var dt))
            sb.Append("      <span class=\"req-timestamp\">").Append(FormatTimeShort(dt)).AppendLine("</span>");
        sb.AppendLine("    </div>");

        WriteInboundMessage(sb, entry);
        WriteOutboundMessage(sb, entry);

        sb.AppendLine("  </div>");
    }

    private static void WriteEventNode(StringBuilder sb, Dictionary<string, object?> entry, DateTime? traceStart)
    {
        var eventDataJson = Get(entry, "event_data_json");
        var eventType = Get(entry, "event_type");
        var createdAt = Get(entry, "created_at");
        string toolName = "-", input = "", output = "", recipient = "";

        if (!(string.Equals(eventDataJson, "-", StringComparison.Ordinal)))
        {
            try
            {
                using var doc = JsonDocument.Parse(eventDataJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("tool", out var t)) toolName = t.GetString() ?? "-";
                if (root.TryGetProperty("input", out var i)) input = i.GetString() ?? "";
                if (root.TryGetProperty("output", out var o)) output = o.GetString() ?? "";
                if (root.TryGetProperty("recipient", out var r)) recipient = r.GetString() ?? "";
            }
            catch { /* best effort */ }
        }

        var isMessage = string.Equals(eventType, "agent_message", StringComparison.Ordinal);
        // agent_message -> model bucket (per taxonomy-collapse: agent.message -> model)
        var category = isMessage ? "model" : GetEventToolCategory(toolName);
        var (title, detail) = isMessage
            ? GetMessageSummary(recipient, input, output)
            : GetEventSummary(toolName, category, input, output);

        sb.Append("  <div class=\"tl-node\" data-tool=\"").Append(Enc(toolName)).AppendLine("\">");

        // Timeline marker
        if (!(string.Equals(createdAt, "-", StringComparison.Ordinal)) && TryParseTime(createdAt, out var entryDt))
            WriteTimelineMarker(sb, entryDt, traceStart);

        sb.AppendLine("    <div class=\"tl-icon\">");
        sb.Append("      ").AppendLine(GetTimelineIconSvg(category));
        sb.AppendLine("    </div>");

        if (isMessage)
            WriteMessageTooltip(sb, recipient, createdAt, input, output);
        else
            WriteEventTooltip(sb, toolName, category, createdAt, input, output);

        sb.AppendLine("    <div class=\"request-header\">");
        sb.Append("      <span class=\"badge badge-op\" data-op=\"").Append(category)
          .Append("\">").Append(Enc(isMessage ? "SendMessage" : toolName)).AppendLine("</span>");
        if (isMessage && recipient.Length > 0)
            sb.Append("      <span class=\"badge badge-secondary\">").Append(Enc(recipient)).AppendLine("</span>");
        if (!(string.Equals(createdAt, "-", StringComparison.Ordinal)) && TryParseTime(createdAt, out var dt))
            sb.Append("      <span class=\"req-timestamp\">").Append(FormatTimeShort(dt)).AppendLine("</span>");
        sb.AppendLine("    </div>");

        sb.AppendLine("    <div class=\"event-card\">");
        if (title.Length > 0)
            sb.Append("      <div class=\"event-title\">").Append(Enc(title)).AppendLine("</div>");
        if (detail.Length > 0)
            sb.Append("      <div class=\"event-detail\">").Append(Enc(detail)).AppendLine("</div>");

        var inputLabel = isMessage ? "Sent" : "Input";
        var outputLabel = isMessage ? "Received" : "Output";

        if (input.Length > 0 || output.Length > 0)
        {
            sb.AppendLine("      <button class=\"toggle-btn\" onclick=\"toggleEventPayload(this)\">Show details</button>");
            sb.AppendLine("      <div class=\"event-payload\" style=\"display:none\">");

            if (input.Length > 0)
            {
                sb.AppendLine("        <div class=\"event-payload-section\">");
                sb.Append("          <div class=\"event-payload-label\">").Append(inputLabel).AppendLine("</div>");
                WriteEventInputPre(sb, input);
                sb.AppendLine("        </div>");
            }

            if (output.Length > 0)
            {
                sb.AppendLine("        <div class=\"event-payload-section\">");
                sb.Append("          <div class=\"event-payload-label\">").Append(outputLabel).AppendLine("</div>");
                sb.Append("          <pre>").Append(Enc(output)).AppendLine("</pre>");
                sb.AppendLine("        </div>");
            }

            sb.AppendLine("      </div>");
        }

        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");
    }

    private static void WriteSpanNode(StringBuilder sb, Dictionary<string, object?> entry,
        List<Dictionary<string, object?>> allEntries,
        Dictionary<string, List<Dictionary<string, object?>>> childrenByParent,
        DateTime? traceStart)
    {
        var id = Get(entry, "id");
        var agentType = Get(entry, "agent_type");
        var agentName = Get(entry, "agent_name");
        var status = Get(entry, "status");
        var startedAt = Get(entry, "started_at");
        var completedAt = Get(entry, "completed_at");
        var inputSummary = Get(entry, "input_summary");
        var outputSummary = Get(entry, "output_summary");
        var parentSpanId = Get(entry, "_parent_span_id");
        var dbParentSpanId = Get(entry, "parent_span_id");

        var idShort = id.Length > 10 ? id[..10] : id;
        var durationStr = ComputeSpanDuration(startedAt, completedAt);
        var hasChildren = childrenByParent.ContainsKey(id);
        var isChild = !(string.Equals(parentSpanId, "-", StringComparison.Ordinal)) || !(string.Equals(dbParentSpanId, "-", StringComparison.Ordinal));

        // Determine avatar label (A1, A2, etc. based on position among spans)
        var spanIndex = 0;
        foreach (var e in allEntries)
        {
            if (string.Equals(Get(e, "_source"), "span", StringComparison.Ordinal))
            {
                if (string.Equals(Get(e, "id"), id, StringComparison.Ordinal)) break;
                spanIndex++;
            }
        }
        var avatarLabel = isChild ? $"A{spanIndex + 1}" : "A";

        var childCount = hasChildren && childrenByParent.TryGetValue(id, out var childList) ? childList.Count : 0;

        sb.Append("  <div class=\"tl-node\" data-tool=\"span\" data-agent=\"").Append(Enc(agentName)).AppendLine("\">");

        // Timeline marker
        if (!(string.Equals(startedAt, "-", StringComparison.Ordinal)) && TryParseTime(startedAt, out var spanDt))
            WriteTimelineMarker(sb, spanDt, traceStart);

        sb.AppendLine("    <div class=\"tl-icon\">");
        sb.Append("      ").AppendLine(SpanIconSvg);
        sb.AppendLine("    </div>");

        // Tooltip
        sb.AppendLine("    <div class=\"tooltip\">");
        WriteTooltipRow(sb, "Span", idShort);
        WriteTooltipRow(sb, "Type", agentType);
        WriteTooltipRow(sb, "Agent", agentName);
        if (!(string.Equals(startedAt, "-", StringComparison.Ordinal)) && TryParseTime(startedAt, out var sdt))
            WriteTooltipRow(sb, "Started", FormatTimePrecise(sdt));
        if (durationStr.Length > 0)
            WriteTooltipRow(sb, "Duration", durationStr);
        WriteTooltipRow(sb, "Status", status);
        if (isChild)
        {
            var pId = !(string.Equals(parentSpanId, "-", StringComparison.Ordinal)) ? parentSpanId : dbParentSpanId;
            WriteTooltipRow(sb, "Parent", pId.Length > 10 ? pId[..10] + "..." : pId);
        }
        if (childCount > 0)
            WriteTooltipRow(sb, "Children", $"{childCount} entries");
        var costStr = Get(entry, "_total_cost_usd");
        double costVal = 0;
        var hasCost = !(string.Equals(costStr, "-", StringComparison.Ordinal)) && double.TryParse(costStr, CultureInfo.InvariantCulture, out costVal) && costVal > 0;
        if (hasCost)
            WriteTooltipRow(sb, "Cost", $"${costVal:F4}");
        var tokensStr = Get(entry, "_total_tokens");
        long tokensVal = 0;
        var hasTokens = !(string.Equals(tokensStr, "-", StringComparison.Ordinal)) && long.TryParse(tokensStr, CultureInfo.InvariantCulture, out tokensVal) && tokensVal > 0;
        if (hasTokens)
            WriteTooltipRow(sb, "Tokens", tokensVal.ToString("N0", CultureInfo.InvariantCulture) + " (exact)");
        sb.AppendLine("    </div>");

        // Span card
        sb.AppendLine("    <div class=\"agent-span\">");
        sb.AppendLine("      <div class=\"span-header\" onclick=\"toggleSpanContent(this)\">");
        sb.Append("        <div class=\"span-avatar\">").Append(Enc(avatarLabel)).AppendLine("</div>");
        sb.Append("        <span class=\"span-type\">").Append(Enc(agentType)).AppendLine("</span>");
        sb.Append("        <span class=\"span-name\">").Append(Enc(agentName.Length > 60 ? agentName[..57] + "..." : agentName)).AppendLine("</span>");
        if (durationStr.Length > 0)
            sb.Append("        <span class=\"span-timing\">").Append(Enc(durationStr)).AppendLine("</span>");
        if (hasCost)
            sb.Append("        <span class=\"span-cost\">~$").Append(CultureInfo.InvariantCulture, $"{costVal:F4}").AppendLine("</span>");
        if (hasTokens)
            sb.Append("        <span class=\"span-tokens\">").Append(tokensVal.ToString("N0", CultureInfo.InvariantCulture)).AppendLine(" tok</span>");
        if (childCount > 0)
            sb.Append("        <span class=\"span-child-count\">").Append(childCount).Append(" inner").AppendLine("</span>");
        sb.Append("        <span class=\"span-status\">").Append(Enc(status)).AppendLine("</span>");
        sb.AppendLine("      </div>");

        // Expandable content: I/O + child entries
        var hasIO = (!(string.Equals(inputSummary, "-", StringComparison.Ordinal)) && inputSummary.Length > 0) || (!(string.Equals(outputSummary, "-", StringComparison.Ordinal)) && outputSummary.Length > 0);
        if (hasIO || hasChildren)
        {
            sb.AppendLine("      <div class=\"span-content\">");
            if (hasIO)
            {
                sb.AppendLine("        <div class=\"span-io\">");
                if (!(string.Equals(inputSummary, "-", StringComparison.Ordinal)) && inputSummary.Length > 0)
                {
                    sb.AppendLine("          <div class=\"event-payload-section\">");
                    sb.AppendLine("            <div class=\"event-payload-label\">Agent Input</div>");
                    WriteEventInputPre(sb, inputSummary);
                    sb.AppendLine("          </div>");
                }
                if (!(string.Equals(outputSummary, "-", StringComparison.Ordinal)) && outputSummary.Length > 0)
                {
                    sb.AppendLine("          <div class=\"event-payload-section\">");
                    sb.AppendLine("            <div class=\"event-payload-label\">Agent Output</div>");
                    sb.Append("            <pre>").Append(Enc(outputSummary.Length > 500 ? outputSummary[..497] + "..." : outputSummary)).AppendLine("</pre>");
                    sb.AppendLine("          </div>");
                }
                sb.AppendLine("        </div>");
            }

            if (hasChildren)
            {
                sb.AppendLine("        <div class=\"span-children\">");
                foreach (var child in childrenByParent[id])
                    WriteTimelineEntry(sb, child, allEntries, childrenByParent, traceStart);
                sb.AppendLine("        </div>");
            }
            sb.AppendLine("      </div>");
        }

        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");
    }

    private static string ComputeSpanDuration(string startedAt, string completedAt)
    {
        if (string.Equals(startedAt, "-", StringComparison.Ordinal) || string.Equals(completedAt, "-", StringComparison.Ordinal)) return "";
        if (!TryParseTime(startedAt, out var start) || !TryParseTime(completedAt, out var end)) return "";
        var elapsed = end - start;
        if (elapsed.TotalSeconds < 1) return $"{elapsed.TotalMilliseconds:F0}ms";
        if (elapsed.TotalMinutes < 1) return $"{elapsed.TotalSeconds:F1}s";
        return $"{elapsed.TotalMinutes:F1}m";
    }

    private static void WriteEventTooltip(StringBuilder sb, string toolName, string category,
        string createdAt, string input, string output)
    {
        sb.AppendLine("    <div class=\"tooltip\">");
        WriteTooltipRow(sb, "Tool", toolName);
        WriteTooltipRow(sb, "Category", category);
        if (!(string.Equals(createdAt, "-", StringComparison.Ordinal)) && TryParseTime(createdAt, out var dt))
            WriteTooltipRow(sb, "Time", FormatTimePrecise(dt));
        if (input.Length > 0)
            WriteTooltipRow(sb, "Input", input.Length > 60 ? $"{input[..57]}..." : input);
        if (output.Length > 0)
            WriteTooltipRow(sb, "Output", output.Length > 60 ? $"{output.Length} chars" : output);
        sb.AppendLine("    </div>");
    }

    private static void WriteMessageTooltip(StringBuilder sb, string recipient,
        string createdAt, string input, string output)
    {
        sb.AppendLine("    <div class=\"tooltip\">");
        WriteTooltipRow(sb, "Type", "Agent Message");
        if (recipient.Length > 0)
            WriteTooltipRow(sb, "Recipient", recipient.Length > 60 ? $"{recipient[..57]}..." : recipient);
        if (!(string.Equals(createdAt, "-", StringComparison.Ordinal)) && TryParseTime(createdAt, out var dt))
            WriteTooltipRow(sb, "Time", FormatTimePrecise(dt));
        if (input.Length > 0)
            WriteTooltipRow(sb, "Sent", input.Length > 60 ? $"{input[..57]}..." : input);
        if (output.Length > 0)
            WriteTooltipRow(sb, "Received", output.Length > 60 ? $"{output.Length} chars" : output);
        sb.AppendLine("    </div>");
    }

    private static (string title, string detail) GetMessageSummary(
        string recipient, string input, string output)
    {
        var title = recipient.Length > 0 ? $"Message to {recipient}" : "Agent message";
        var detail = "";
        if (input.Length > 0)
        {
            var preview = input.Length > 80 ? input[..77] + "..." : input;
            detail = preview;
        }
        return (title, detail);
    }

    private static void WriteEventInputPre(StringBuilder sb, string input)
    {
        try
        {
            using var doc = JsonDocument.Parse(input);
            sb.Append("          <pre>").Append(HighlightJson(input)).AppendLine("</pre>");
        }
        catch
        {
            sb.Append("          <pre>").Append(Enc(input)).AppendLine("</pre>");
        }
    }

    // Taxonomy-collapse (per resolved decisions trace-tool-buckets + taxonomy-collapse):
    //   search/grep -> find   (search.content+search.files -> find)
    //   file.read / glob / fs.list -> system
    //   file.write / edit -> edit
    //   shell -> shell
    //   agent_message -> model  (handled separately via isMessage branch)
    //   unknown/generic tool -> system  (task.manage+fallback -> system)
    private static string GetEventToolCategory(string toolName) => toolName.ToLowerInvariant() switch
    {
        "read" => "system",
        "glob" => "system",
        "write" => "edit",
        "edit" => "edit",
        "notebookedit" => "edit",
        "grep" => "find",
        "bash" or "powershell" => "shell",
        _ => "system"
    };

    private static (string title, string detail) GetEventSummary(
        string toolName, string category, string input, string output)
    {
        try
        {
            return category switch
            {
                // "system" covers former "file" category (Read/Glob -> system bucket per taxonomy-collapse)
                "system" => toolName.ToLowerInvariant() is "read" or "glob"
                    ? GetFileSummary(toolName, input)
                    : GetGenericSummary(toolName, input),
                "edit" => GetEditSummary(toolName, input),
                // "find" covers former "search" category (Grep -> find bucket per taxonomy-collapse)
                "find" => GetSearchSummary(input, output),
                "shell" => GetShellSummary(input, output),
                _ => GetGenericSummary(toolName, input)
            };
        }
        catch { return (toolName, ""); }
    }

    private static (string title, string detail) GetFileSummary(string toolName, string input)
    {
        if (input.Length == 0) return ("file operation", "");
        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;

            if (toolName.Equals("Read", StringComparison.OrdinalIgnoreCase))
            {
                var path = root.TryGetProperty("file_path", out var fp) ? fp.GetString() ?? "" : "";
                var fileName = path.Length > 0 ? Path.GetFileName(path) : "";
                var parts = new List<string>();
                if (root.TryGetProperty("offset", out var off)) parts.Add($"from line {off}");
                if (root.TryGetProperty("limit", out var lim)) parts.Add($"{lim} lines");
                return (fileName.Length > 0 ? fileName : path, string.Join(" · ", parts));
            }

            if (toolName.Equals("Glob", StringComparison.OrdinalIgnoreCase))
            {
                var pattern = root.TryGetProperty("pattern", out var p) ? p.GetString() ?? "" : "";
                var path = root.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
                return (pattern.Length > 0 ? pattern : "glob", path.Length > 0 ? $"in {Path.GetFileName(path)}" : "");
            }

            return ("file operation", "");
        }
        catch { return ("file operation", ""); }
    }

    private static (string title, string detail) GetEditSummary(string toolName, string input)
    {
        if (input.Length == 0) return ("edit", "");
        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            var path = root.TryGetProperty("file_path", out var fp) ? fp.GetString() ?? "" : "";
            var fileName = path.Length > 0 ? Path.GetFileName(path) : "";

            if (toolName.Equals("Edit", StringComparison.OrdinalIgnoreCase))
            {
                var old = root.TryGetProperty("old_string", out var os) ? os.GetString() ?? "" : "";
                var newStr = root.TryGetProperty("new_string", out var ns) ? ns.GetString() ?? "" : "";
                var oldLines = old.Length > 0 ? old.Split('\n').Length : 0;
                var newLines = newStr.Length > 0 ? newStr.Split('\n').Length : 0;
                var detail = oldLines > 0 || newLines > 0 ? $"{oldLines} lines → {newLines} lines" : "";
                return (fileName.Length > 0 ? fileName : "edit", detail);
            }

            return (fileName.Length > 0 ? fileName : "write", "file write");
        }
        catch { return ("edit", ""); }
    }

    private static (string title, string detail) GetSearchSummary(string input, string output)
    {
        if (input.Length == 0) return ("search", "");
        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            var pattern = root.TryGetProperty("pattern", out var p) ? p.GetString() ?? "" : "";
            var parts = new List<string>();
            if (root.TryGetProperty("path", out var pp) && pp.GetString() is { Length: > 0 } pathVal)
                parts.Add(Path.GetFileName(pathVal));
            if (root.TryGetProperty("type", out var tp) && tp.GetString() is { Length: > 0 } typeVal)
                parts.Add(typeVal);
            if (root.TryGetProperty("glob", out var gp) && gp.GetString() is { Length: > 0 } globVal)
                parts.Add(globVal);

            if (output.Length > 0)
            {
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                parts.Add($"{lines.Length} result lines");
            }

            var title = pattern.Length > 0 ? $"\"{pattern}\"" : "search";
            return (title, string.Join(" · ", parts));
        }
        catch { return ("search", ""); }
    }

    private static (string title, string detail) GetShellSummary(string input, string output)
    {
        if (input.Length == 0) return ("shell command", "");
        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            var command = root.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            var displayCmd = command.Length > 80 ? command[..77] + "..." : command;

            var detail = "";
            if (output.Length > 0)
            {
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                {
                    var lastLine = lines[^1].Trim();
                    detail = lastLine.Length > 0 && lastLine.Length < 100
                        ? lastLine : $"{lines.Length} lines of output";
                }
            }

            return (displayCmd.Length > 0 ? displayCmd : "shell command", detail);
        }
        catch { return ("shell command", ""); }
    }

    private static (string title, string detail) GetGenericSummary(string toolName, string input)
    {
        if (input.Length == 0) return (toolName, "");
        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            var parts = new List<string>();
            foreach (var prop in root.EnumerateObject())
            {
                if (parts.Count >= 3) break;
                var val = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? "" : prop.Value.ToString();
                if (val.Length > 40) val = val[..37] + "...";
                parts.Add($"{prop.Name}: {val}");
            }
            return (toolName, string.Join(" · ", parts));
        }
        catch { return (toolName, ""); }
    }

    private static void WriteInboundMessage(StringBuilder sb, Dictionary<string, object?> entry)
    {
        var inPayload = Get(entry, "inbound_payload");
        var inType = Get(entry, "inbound_content_type");
        var inTokens = Get(entry, "inbound_token_count");
        var query = Get(entry, "query_text");
        var paramsJson = Get(entry, "parameters_json");
        var operation = Get(entry, "operation");

        if (!(string.Equals(inPayload, "-", StringComparison.Ordinal)))
        {
            sb.AppendLine("    <div class=\"msg msg-inbound msg-collapsed\">");
            var meta = !(string.Equals(inType, "-", StringComparison.Ordinal)) ? inType : "request";
            if (!(string.Equals(inTokens, "-", StringComparison.Ordinal))) meta += $" &middot; ~{inTokens} tokens";
            WriteMsgHeader(sb, "A", "Agent", "Scope", meta, clickable: true);
            sb.AppendLine("      <div class=\"msg-body\" style=\"display:none\">");
            if (!(string.Equals(inType, "-", StringComparison.Ordinal)) && inType.Contains("json", StringComparison.OrdinalIgnoreCase))
                sb.Append("<pre>").Append(HighlightJson(inPayload)).AppendLine("</pre>");
            else
                sb.Append("<pre>").Append(Enc(inPayload)).AppendLine("</pre>");
            sb.AppendLine("      </div>");
            sb.AppendLine("    </div>");
        }
        else if (!(string.Equals(paramsJson, "-", StringComparison.Ordinal)))
        {
            sb.AppendLine("    <div class=\"msg msg-inbound msg-collapsed\">");
            WriteMsgHeader(sb, "A", "Agent", "Scope", "json", clickable: true);
            sb.AppendLine("      <div class=\"msg-body\" style=\"display:none\">");
            sb.Append("<pre>").Append(HighlightJson(paramsJson)).AppendLine("</pre>");
            sb.AppendLine("      </div>");
            sb.AppendLine("    </div>");
        }
        else
        {
            sb.AppendLine("    <div class=\"msg msg-inbound\">");
            var desc = !(string.Equals(query, "-", StringComparison.Ordinal)) ? $"{operation} check" : $"{operation} request";
            WriteMsgHeader(sb, "A", "Agent", "Scope", desc);
            sb.AppendLine("      <div class=\"msg-body\" style=\"color:var(--text-muted);font-style:italic;padding-top:8px\">");
            if (!(string.Equals(query, "-", StringComparison.Ordinal)))
                sb.Append("        query: ").AppendLine(Enc(query));
            else
                sb.Append("        Request ").Append(Enc(operation)).AppendLine(" (no parameters)");
            sb.AppendLine("      </div>");
            sb.AppendLine("    </div>");
        }
    }

    private static void WriteOutboundMessage(StringBuilder sb, Dictionary<string, object?> entry)
    {
        var outPayload = Get(entry, "outbound_payload");
        var outType = Get(entry, "outbound_content_type");
        var outTokens = Get(entry, "outbound_token_count");
        var operation = Get(entry, "operation");

        if (string.Equals(outPayload, "-", StringComparison.Ordinal) || outPayload.Length == 0)
        {
            sb.AppendLine("    <div class=\"msg msg-disabled\">");
            WriteMsgHeader(sb, "S", "Scope", "Agent", "no payload recorded");
            sb.AppendLine("      <div class=\"msg-body\" style=\"padding-top:8px\">");
            sb.AppendLine("        No payload was recorded for this request.");
            sb.AppendLine("      </div>");
            sb.AppendLine("    </div>");
            return;
        }

        sb.AppendLine("    <div class=\"msg msg-outbound msg-collapsed\">");
        var meta = !(string.Equals(outType, "-", StringComparison.Ordinal)) ? outType : "response";
        if (!(string.Equals(outTokens, "-", StringComparison.Ordinal))) meta += $" &middot; ~{outTokens} tokens";
        WriteMsgHeader(sb, "S", "Scope", "Agent", meta, clickable: true);
        sb.AppendLine("      <div class=\"msg-body\" style=\"display:none\">");

        if (operation.Equals("find", StringComparison.OrdinalIgnoreCase))
            WriteFindPayload(sb, outPayload);
        else if (operation.Equals("pack", StringComparison.OrdinalIgnoreCase))
            WritePackPayload(sb, outPayload);
        else if (outType != null && outType.Contains("json", StringComparison.OrdinalIgnoreCase))
            sb.Append("<pre>").Append(HighlightJson(outPayload)).AppendLine("</pre>");
        else
            sb.Append("<pre>").Append(Enc(outPayload)).AppendLine("</pre>");

        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");
    }

    private static void WriteFindPayload(StringBuilder sb, string payload)
    {
        var lines = payload.Split('\n');
        string? summaryLine = null;
        var contentStart = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length > 0 && (trimmed.Contains("results", StringComparison.Ordinal) || trimmed.Contains("candidates", StringComparison.Ordinal) || trimmed.Contains("budget", StringComparison.Ordinal)))
            {
                summaryLine = trimmed;
                contentStart = i + 1;
                break;
            }
        }

        if (summaryLine != null)
            sb.Append("        <div class=\"find-summary\">").Append(Enc(summaryLine)).AppendLine("</div>");

        var content = string.Join('\n', lines.AsSpan(contentStart).ToArray()).Trim();
        if (content.Length > 0)
        {
            sb.AppendLine("        <div class=\"result-item\">");
            sb.AppendLine("          <div class=\"result-header\" onclick=\"toggleResult(this)\">");
            sb.AppendLine("            <span><span class=\"result-file\">Find results</span></span>");
            sb.AppendLine("          </div>");
            sb.AppendLine("          <div class=\"result-body\">");
            sb.Append("<pre>").Append(Enc(content)).AppendLine("</pre>");
            sb.AppendLine("          </div>");
            sb.AppendLine("        </div>");
        }
    }

    private static void WritePackPayload(StringBuilder sb, string payload)
    {
        var lines = payload.Split('\n');
        string? headerLine = null;
        var contentStart = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("@scope", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("target=", StringComparison.Ordinal) || trimmed.Contains("budget=", StringComparison.Ordinal))
            {
                headerLine = trimmed;
                contentStart = i + 1;
            }
            break;
        }

        if (headerLine != null)
            sb.Append("        <div class=\"pack-header\">").Append(Enc(headerLine)).AppendLine("</div>");

        var content = string.Join('\n', lines.AsSpan(contentStart).ToArray()).Trim();
        if (content.Length > 0)
        {
            sb.AppendLine("        <button class=\"toggle-btn\" onclick=\"togglePack(this)\">Show full context pack</button>");
            sb.AppendLine("        <div class=\"pack-content\" style=\"display:none\">");
            sb.Append("<pre>").Append(Enc(content)).AppendLine("</pre>");
            sb.AppendLine("        </div>");
        }
        else
        {
            sb.Append("<pre>").Append(Enc(payload)).AppendLine("</pre>");
        }
    }

    private static void WriteMsgHeader(StringBuilder sb, string avatar, string sender,
        string receiver, string meta, bool clickable = false)
    {
        if (clickable)
            sb.AppendLine("      <div class=\"msg-header msg-header-toggle\" onclick=\"toggleMsgBody(this)\">");
        else
            sb.AppendLine("      <div class=\"msg-header\">");
        sb.Append("        <div class=\"msg-avatar\">").Append(avatar).AppendLine("</div>");
        sb.Append("        <span class=\"msg-sender\">").Append(Enc(sender)).AppendLine("</span>");
        sb.AppendLine("        <span class=\"msg-arrow\">&rarr;</span>");
        sb.Append("        <span class=\"msg-receiver\">").Append(Enc(receiver)).AppendLine("</span>");
        sb.Append("        <span class=\"msg-meta\">").Append(meta).AppendLine("</span>");
        if (clickable)
            sb.AppendLine("        <span class=\"msg-toggle-icon\">&#x25B6;</span>");
        sb.AppendLine("      </div>");
    }

    private static void WriteRequestTooltip(StringBuilder sb, Dictionary<string, object?> entry, string opType)
    {
        var operation = Get(entry, "operation");
        var surface = Get(entry, "caller_surface");
        var query = Get(entry, "query_text");
        var status = Get(entry, "status");
        var elapsed = Get(entry, "total_elapsed_ms");
        var createdAt = Get(entry, "created_at");
        var inTokens = Get(entry, "inbound_token_count");
        var outTokens = Get(entry, "outbound_token_count");
        var outPayload = Get(entry, "outbound_payload");

        sb.AppendLine("    <div class=\"tooltip\">");
        WriteTooltipRow(sb, "Operation", operation);
        WriteTooltipRow(sb, "Surface", surface);
        if (!(string.Equals(query, "-", StringComparison.Ordinal)))
        {
            var queryLabel = operation.Equals("pack", StringComparison.OrdinalIgnoreCase) ? "Symbol" : "Query";
            WriteTooltipRow(sb, queryLabel, query);
        }
        if (!(string.Equals(status, "-", StringComparison.Ordinal)))
        {
            var color = string.Equals(status, "completed", StringComparison.Ordinal) ? "var(--clr-scope)" : "#f85149";
            sb.Append("      <div class=\"tooltip-row\"><span class=\"tooltip-label\">Status</span><span class=\"tooltip-value\" style=\"color:")
              .Append(color).Append("\">").Append(Enc(status)).AppendLine("</span></div>");
        }
        if (!(string.Equals(elapsed, "-", StringComparison.Ordinal)))
            WriteTooltipRow(sb, "Elapsed", $"{elapsed}ms");
        if (!(string.Equals(createdAt, "-", StringComparison.Ordinal)) && TryParseTime(createdAt, out var dt))
            WriteTooltipRow(sb, "Time", FormatTimePrecise(dt));
        if (!(string.Equals(inTokens, "-", StringComparison.Ordinal)) || !(string.Equals(outTokens, "-", StringComparison.Ordinal)))
        {
            var parts = new List<string>();
            if (!(string.Equals(inTokens, "-", StringComparison.Ordinal))) parts.Add($"~{inTokens} in");
            if (!(string.Equals(outTokens, "-", StringComparison.Ordinal))) parts.Add($"~{outTokens} out");
            WriteTooltipRow(sb, "Tokens", string.Join(" / ", parts));
        }
        if (string.Equals(outPayload, "-", StringComparison.Ordinal))
        {
            WriteTooltipRow(sb, "Payloads", "capture disabled", "var(--text-muted)");
        }
        sb.AppendLine("    </div>");
    }

    private static void WriteTooltipRow(StringBuilder sb, string label, string value, string? color = null)
    {
        sb.Append("      <div class=\"tooltip-row\"><span class=\"tooltip-label\">")
          .Append(Enc(label)).Append("</span><span class=\"tooltip-value\"");
        if (color != null) sb.Append(" style=\"color:").Append(color).Append('"');
        sb.Append('>').Append(Enc(value)).AppendLine("</span></div>");
    }

    // Taxonomy-collapse for server-side request operations:
    //   status -> system  (status/info events -> system per taxonomy-collapse)
    //   find -> find      (keep)
    //   pack -> pack      (keep; server export retains pack as it knows the specific op)
    //   hook -> hook      (keep; server export retains hook)
    //   _ -> external     (MCP and other external calls -> external per taxonomy-collapse)
    private static string GetOperationType(string operation) => operation.ToLowerInvariant() switch
    {
        "status" => "system",
        "find" => "find",
        "pack" => "pack",
        "hook" => "hook",
        _ => "external"
    };

    // Icon SVGs reference CSS custom properties so the inlined dual-theme token block drives them.
    // Bucket mapping (per taxonomy-collapse + trace-tool-buckets decisions):
    //   system  (was: status, file, tool) -> --scope-trace-tool-system  #FFB000 light / #FFB000 dark
    //   find    (was: find, search/grep)  -> --scope-trace-tool-find    #618dff light / #648FFF dark
    //   pack    (unchanged)               -> --scope-trace-tool-pack    #fa5f00 light / #FE6100 dark
    //   hook    (unchanged)               -> --scope-trace-tool-hook    #DC267F both
    //   edit    (was: edit, file.write)   -> --scope-trace-tool-edit    #39a648 light / #3FB950 dark
    //   model   (was: message)            -> --scope-trace-tool-model   #249fa8 light / #56D4DD dark
    //   shell   (unchanged)               -> --scope-trace-tool-shell   #A371F7 both
    //   external (was: mcp)              -> --scope-trace-tool-external #785EF0 both
    //   operator (new, no legacy source) -> --scope-trace-tool-operator #1f936d light / #2eb88a dark
    private static string GetTimelineIconSvg(string opType) => opType switch
    {
        // system: covers former status, file, tool categories
        "system" => "<svg width=\"36\" height=\"36\" viewBox=\"0 0 36 36\"><circle cx=\"18\" cy=\"18\" r=\"14\" fill=\"var(--scope-trace-tool-system-bg)\" stroke=\"var(--scope-trace-tool-system)\" stroke-width=\"2.5\"/><text x=\"18\" y=\"23\" text-anchor=\"middle\" fill=\"var(--scope-trace-tool-system)\" font-size=\"16\" font-weight=\"700\" font-family=\"-apple-system, sans-serif\">i</text></svg>",
        "find" => "<svg width=\"36\" height=\"36\" viewBox=\"0 0 36 36\"><circle cx=\"15\" cy=\"15\" r=\"9\" fill=\"var(--scope-trace-tool-find-bg)\" stroke=\"var(--scope-trace-tool-find)\" stroke-width=\"2.5\"/><line x1=\"22\" y1=\"22\" x2=\"31\" y2=\"31\" stroke=\"var(--scope-trace-tool-find)\" stroke-width=\"2.5\" stroke-linecap=\"round\"/></svg>",
        "pack" => "<svg width=\"36\" height=\"36\" viewBox=\"0 0 36 36\"><rect x=\"5\" y=\"9\" width=\"26\" height=\"20\" rx=\"3\" fill=\"var(--scope-trace-tool-pack-bg)\" stroke=\"var(--scope-trace-tool-pack)\" stroke-width=\"2.5\"/><line x1=\"5\" y1=\"16\" x2=\"31\" y2=\"16\" stroke=\"var(--scope-trace-tool-pack)\" stroke-width=\"2\"/><line x1=\"13\" y1=\"9\" x2=\"13\" y2=\"3\" stroke=\"var(--scope-trace-tool-pack)\" stroke-width=\"2.5\" stroke-linecap=\"round\"/><line x1=\"23\" y1=\"9\" x2=\"23\" y2=\"3\" stroke=\"var(--scope-trace-tool-pack)\" stroke-width=\"2.5\" stroke-linecap=\"round\"/></svg>",
        "hook" => "<svg width=\"36\" height=\"36\" viewBox=\"0 0 36 36\"><polygon points=\"18,3 27,9 30,20 18,33 6,20 9,9\" fill=\"var(--scope-trace-tool-hook-bg)\" stroke=\"var(--scope-trace-tool-hook)\" stroke-width=\"2.5\"/><circle cx=\"18\" cy=\"18\" r=\"4\" fill=\"var(--scope-trace-tool-hook)\"/></svg>",
        "edit" => "<svg width=\"36\" height=\"36\" viewBox=\"0 0 36 36\"><path d=\"M22 6l8 8-16 16H6v-8z\" fill=\"var(--scope-trace-tool-edit-bg)\" stroke=\"var(--scope-trace-tool-edit)\" stroke-width=\"2\" stroke-linejoin=\"round\"/><line x1=\"18\" y1=\"10\" x2=\"26\" y2=\"18\" stroke=\"var(--scope-trace-tool-edit)\" stroke-width=\"2\"/></svg>",
        "model" => "<svg width=\"36\" height=\"36\" viewBox=\"0 0 36 36\"><rect x=\"4\" y=\"8\" width=\"28\" height=\"20\" rx=\"3\" fill=\"var(--scope-trace-tool-model-bg)\" stroke=\"var(--scope-trace-tool-model)\" stroke-width=\"2\"/><polyline points=\"4,10 18,20 32,10\" fill=\"none\" stroke=\"var(--scope-trace-tool-model)\" stroke-width=\"2\" stroke-linejoin=\"round\"/></svg>",
        "shell" => "<svg width=\"36\" height=\"36\" viewBox=\"0 0 36 36\"><rect x=\"4\" y=\"6\" width=\"28\" height=\"24\" rx=\"3\" fill=\"var(--scope-trace-tool-shell-bg)\" stroke=\"var(--scope-trace-tool-shell)\" stroke-width=\"2\"/><polyline points=\"10,16 15,20 10,24\" fill=\"none\" stroke=\"var(--scope-trace-tool-shell)\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/><line x1=\"18\" y1=\"24\" x2=\"26\" y2=\"24\" stroke=\"var(--scope-trace-tool-shell)\" stroke-width=\"2\" stroke-linecap=\"round\"/></svg>",
        // external: covers former mcp category
        "external" => "<svg width=\"36\" height=\"36\" viewBox=\"0 0 36 36\"><polygon points=\"18,3 33,18 18,33 3,18\" fill=\"var(--scope-trace-tool-external-bg)\" stroke=\"var(--scope-trace-tool-external)\" stroke-width=\"2.5\"/></svg>",
        "operator" => "<svg width=\"36\" height=\"36\" viewBox=\"0 0 36 36\"><circle cx=\"18\" cy=\"18\" r=\"14\" fill=\"var(--scope-trace-tool-operator-bg)\" stroke=\"var(--scope-trace-tool-operator)\" stroke-width=\"2.5\"/><text x=\"18\" y=\"23\" text-anchor=\"middle\" fill=\"var(--scope-trace-tool-operator)\" font-size=\"16\" font-weight=\"700\" font-family=\"-apple-system, sans-serif\">?</text></svg>",
        _ => "<svg width=\"36\" height=\"36\" viewBox=\"0 0 36 36\"><polygon points=\"18,3 33,18 18,33 3,18\" fill=\"var(--scope-trace-tool-external-bg)\" stroke=\"var(--scope-trace-tool-external)\" stroke-width=\"2.5\"/></svg>"
    };

    private static string HighlightJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            WriteJsonElement(sb, doc.RootElement, 0);
            return sb.ToString();
        }
        catch
        {
            return Enc(json);
        }
    }

    private static void WriteJsonElement(StringBuilder sb, JsonElement el, int indent)
    {
        var pad = new string(' ', indent * 2);
        var inner = new string(' ', (indent + 1) * 2);
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var props = new List<JsonProperty>();
                foreach (var p in el.EnumerateObject()) props.Add(p);
                for (var i = 0; i < props.Count; i++)
                {
                    sb.AppendLine();
                    sb.Append(inner);
                    sb.Append("<span class=\"json-key\">\"").Append(Enc(props[i].Name)).Append("\"</span>: ");
                    WriteJsonElement(sb, props[i].Value, indent + 1);
                    if (i < props.Count - 1) sb.Append(',');
                }
                if (props.Count > 0) { sb.AppendLine(); sb.Append(pad); }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var items = new List<JsonElement>();
                foreach (var item in el.EnumerateArray()) items.Add(item);
                for (var i = 0; i < items.Count; i++)
                {
                    sb.AppendLine();
                    sb.Append(inner);
                    WriteJsonElement(sb, items[i], indent + 1);
                    if (i < items.Count - 1) sb.Append(',');
                }
                if (items.Count > 0) { sb.AppendLine(); sb.Append(pad); }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                sb.Append("<span class=\"json-str\">\"").Append(Enc(el.GetString() ?? "")).Append("\"</span>");
                break;
            case JsonValueKind.Number:
                sb.Append("<span class=\"json-num\">").Append(el.GetRawText()).Append("</span>");
                break;
            case JsonValueKind.True or JsonValueKind.False:
                sb.Append("<span class=\"json-bool\">").Append(el.GetRawText()).Append("</span>");
                break;
            case JsonValueKind.Null:
                sb.Append("<span class=\"json-bool\">null</span>");
                break;
        }
    }

    private static (DateTime? start, DateTime? end, string duration) ComputeTiming(
        List<Dictionary<string, object?>> entries, Dictionary<string, object?>? dispatchInfo)
    {
        DateTime? start = ParseTime(dispatchInfo, "created_at");
        DateTime? end = ParseTime(dispatchInfo, "completed_at");

        foreach (var e in entries)
        {
            var created = ParseTime(e, "created_at");
            var completed = ParseTime(e, "completed_at");
            if (created.HasValue && (!start.HasValue || created < start)) start = created;
            if (completed.HasValue && (!end.HasValue || completed > end)) end = completed;
            else if (created.HasValue && (!end.HasValue || created > end)) end = created;
        }

        var dur = start.HasValue && end.HasValue ? FormatDuration(end.Value - start.Value) : "-";
        return (start, end, dur);
    }

    private static DateTime? ParseTime(Dictionary<string, object?>? dict, string key)
    {
        if (dict == null) return null;
        var s = Get(dict, key);
        return TryParseTime(s, out var dt) ? dt : null;
    }

    private static bool TryParseTime(string s, out DateTime dt)
    {
        dt = default;
        return !(string.Equals(s, "-", StringComparison.Ordinal)) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt);
    }

    private static string FormatTimeShort(DateTime dt) => $"{dt:HH:mm:ss} UTC";
    private static string FormatTimePrecise(DateTime dt) => $"{dt:HH:mm:ss.fff} UTC";
    private static string FormatTimeFull(DateTime dt) => $"{dt:yyyy-MM-dd HH:mm:ss} UTC";

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1) return $"{ts.TotalMilliseconds:F0}ms";
        if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:F1}s";
        if (ts.TotalMinutes < 60) return $"{ts.TotalMinutes:F1}m";
        return $"{ts.TotalHours:F1}h";
    }

    private static string Get(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var val) && val != null ? val.ToString() ?? "-" : "-";

    private static string Enc(string text) => WebUtility.HtmlEncode(text);

    /// <summary>Escapes a string for embedding in a JS single-quoted string literal.</summary>
    private static string EscJs(string text) => text.Replace("\\", "\\\\").Replace("'", "\\'");

    // Span icon uses structural span token (not a TraceToolType bucket; spans represent agent scopes).
    // Raw hex #f0883e is the span accent color (consistent with sub-agent branding, not a tool bucket).
    private const string SpanIconSvg = """<svg width="36" height="36" viewBox="0 0 36 36"><rect x="4" y="6" width="28" height="24" rx="4" fill="var(--span-bg)" stroke="var(--span-accent)" stroke-width="2"/><rect x="8" y="10" width="20" height="16" rx="3" fill="none" stroke="var(--span-accent)" stroke-width="1.5" stroke-dasharray="4 2"/><circle cx="18" cy="18" r="4" fill="var(--span-accent)"/></svg>""";

    private const string Css = """
/*
 * Scope Audit Trace — self-contained export stylesheet
 *
 * Tool-type visualization uses --scope-trace-tool-* tokens (9 TraceToolType buckets).
 * FROZEN hex values from FE-0b (authoritative, do not change without updating the SPA):
 *   find     light #618dff  dark #648FFF
 *   pack     light #fa5f00  dark #FE6100
 *   hook     light #DC267F  dark #DC267F  (both themes)
 *   edit     light #39a648  dark #3FB950
 *   model    light #249fa8  dark #56D4DD
 *   shell    light #A371F7  dark #A371F7  (both themes)
 *   external light #785EF0  dark #785EF0  (both themes)
 *   operator light #1f936d  dark #2eb88a
 *   system   light #c28600  dark #FFB000
 *
 * Taxonomy-collapse applied (per resolved decisions trace-tool-buckets + taxonomy-collapse):
 *   status -> system | file(read/glob) -> system | tool(generic) -> system
 *   search/grep -> find | mcp -> external | message(agent_message) -> model
 *   find / pack / hook / edit / shell keep their bucket
 *   operator: no human-in-the-loop events exist in this export today; bucket is reserved.
 *
 * Dual-theme: :root sets dark-theme values (dark-first document with no explicit data-theme);
 *   [data-theme=light] and @media (prefers-color-scheme: light) provide light overrides.
 *   The result is: dark in dark-preferring browsers, light in light-preferring browsers,
 *   and user can force a theme via data-theme attribute on <html>.
 */

/* ── Dark theme (default / system dark) ─────────────────────────────────────── */
:root, [data-theme=dark] {
  --bg: #0d1117;
  --surface: #161b22;
  --surface-raised: #1c2129;
  --border: #30363d;
  --text: #e6edf3;
  --text-muted: #8b949e;
  --code-bg: #0d1117;

  /* TraceToolType bucket tokens — FROZEN FE-0b dark hex */
  --scope-trace-tool-find:     #648FFF;
  --scope-trace-tool-pack:     #FE6100;
  --scope-trace-tool-hook:     #DC267F;
  --scope-trace-tool-edit:     #3FB950;
  --scope-trace-tool-model:    #56D4DD;
  --scope-trace-tool-shell:    #A371F7;
  --scope-trace-tool-external: #785EF0;
  --scope-trace-tool-operator: #2eb88a;
  --scope-trace-tool-system:   #FFB000;

  /* Semi-transparent backgrounds for icon fills (18% alpha) */
  --scope-trace-tool-find-bg:     #648FFF18;
  --scope-trace-tool-pack-bg:     #FE610018;
  --scope-trace-tool-hook-bg:     #DC267F18;
  --scope-trace-tool-edit-bg:     #3FB95018;
  --scope-trace-tool-model-bg:    #56D4DD18;
  --scope-trace-tool-shell-bg:    #A371F718;
  --scope-trace-tool-external-bg: #785EF018;
  --scope-trace-tool-operator-bg: #2eb88a18;
  --scope-trace-tool-system-bg:   #FFB00018;

  /* Structural / participant chrome tokens (not tool-type colors) */
  --clr-start: #3FB950;
  --clr-end: #8b949e;
  --clr-agent: #648FFF;
  --clr-scope: #3FB950;
  --clr-agent-bg: #648fff0a;
  --clr-scope-bg: #3fb9500a;
  --clr-agent-border: #648fff55;
  --clr-scope-border: #3fb95055;

  /* Agent-span structural token (sub-agent branding; not a TraceToolType bucket) */
  --span-accent: #f0883e;
  --span-bg: #f0883e0a;
  --span-border: #f0883e55;
}

/* ── Light theme ─────────────────────────────────────────────────────────────── */
/* Applied via explicit data-theme attribute or via system preference.             */
[data-theme=light] {
  --bg: #ffffff;
  --surface: #f6f8fa;
  --surface-raised: #eaeef2;
  --border: #d0d7de;
  --text: #1f2328;
  --text-muted: #656d76;
  --code-bg: #f6f8fa;
  --scope-trace-tool-find:     #618dff;
  --scope-trace-tool-pack:     #fa5f00;
  --scope-trace-tool-hook:     #DC267F;
  --scope-trace-tool-edit:     #39a648;
  --scope-trace-tool-model:    #249fa8;
  --scope-trace-tool-shell:    #A371F7;
  --scope-trace-tool-external: #785EF0;
  --scope-trace-tool-operator: #1f936d;
  --scope-trace-tool-system:   #c28600;
  --scope-trace-tool-find-bg:     #618dff18;
  --scope-trace-tool-pack-bg:     #fa5f0018;
  --scope-trace-tool-hook-bg:     #DC267F18;
  --scope-trace-tool-edit-bg:     #39a64818;
  --scope-trace-tool-model-bg:    #249fa818;
  --scope-trace-tool-shell-bg:    #A371F718;
  --scope-trace-tool-external-bg: #785EF018;
  --scope-trace-tool-operator-bg: #1f936d18;
  --scope-trace-tool-system-bg:   #c2860018;
  --clr-start: #39a648;
  --clr-end: #656d76;
  --clr-agent: #618dff;
  --clr-scope: #39a648;
  --clr-agent-bg: #618dff0a;
  --clr-scope-bg: #39a6480a;
  --clr-agent-border: #618dff55;
  --clr-scope-border: #39a64855;
  --span-accent: #e07826;
  --span-bg: #e078260a;
  --span-border: #e0782655;
}
@media (prefers-color-scheme: light) {
  :root:not([data-theme=dark]) {
  --bg: #ffffff;
  --surface: #f6f8fa;
  --surface-raised: #eaeef2;
  --border: #d0d7de;
  --text: #1f2328;
  --text-muted: #656d76;
  --code-bg: #f6f8fa;
  /* TraceToolType bucket tokens — FROZEN FE-0b light hex */
  --scope-trace-tool-find:     #618dff;
  --scope-trace-tool-pack:     #fa5f00;
  --scope-trace-tool-hook:     #DC267F;
  --scope-trace-tool-edit:     #39a648;
  --scope-trace-tool-model:    #249fa8;
  --scope-trace-tool-shell:    #A371F7;
  --scope-trace-tool-external: #785EF0;
  --scope-trace-tool-operator: #1f936d;
  --scope-trace-tool-system:   #c28600;
  --scope-trace-tool-find-bg:     #618dff18;
  --scope-trace-tool-pack-bg:     #fa5f0018;
  --scope-trace-tool-hook-bg:     #DC267F18;
  --scope-trace-tool-edit-bg:     #39a64818;
  --scope-trace-tool-model-bg:    #249fa818;
  --scope-trace-tool-shell-bg:    #A371F718;
  --scope-trace-tool-external-bg: #785EF018;
  --scope-trace-tool-operator-bg: #1f936d18;
  --scope-trace-tool-system-bg:   #c2860018;
  --clr-start: #39a648;
  --clr-end: #656d76;
  --clr-agent: #618dff;
  --clr-scope: #39a648;
  --clr-agent-bg: #618dff0a;
  --clr-scope-bg: #39a6480a;
  --clr-agent-border: #618dff55;
  --clr-scope-border: #39a64855;
  --span-accent: #e07826;
  --span-bg: #e078260a;
  --span-border: #e0782655;
  }
}
* { margin: 0; padding: 0; box-sizing: border-box; }
body {
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif;
  background: var(--bg);
  color: var(--text);
  line-height: 1.6;
  padding: 32px 24px;
  max-width: 1020px;
  margin: 0 auto;
}
.header {
  border-bottom: 1px solid var(--border);
  padding-bottom: 20px;
  margin-bottom: 12px;
}
.header h1 { font-size: 20px; font-weight: 600; }
.dispatch-id {
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  color: var(--text-muted); font-size: 13px; margin-top: 4px;
}
.header-meta {
  display: flex; gap: 20px; margin-top: 10px;
  font-size: 13px; color: var(--text-muted); flex-wrap: wrap;
}
.header-meta strong { color: var(--text); }
.search-bar {
  position: sticky; top: 0; z-index: 50;
  background: var(--surface); border: 1px solid var(--border);
  border-radius: 8px; padding: 10px 14px; margin-bottom: 12px;
}
.search-controls {
  display: flex; gap: 8px; align-items: center; flex-wrap: wrap;
}
.search-input {
  flex: 1; min-width: 180px;
  background: var(--bg); border: 1px solid var(--border);
  border-radius: 4px; padding: 5px 10px;
  color: var(--text); font-size: 12px;
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
}
.search-input:focus { outline: none; border-color: var(--scope-trace-tool-find); }
.search-select {
  background: var(--bg); border: 1px solid var(--border);
  border-radius: 4px; padding: 5px 10px;
  color: var(--text); font-size: 12px;
}
.search-select:focus { outline: none; border-color: var(--scope-trace-tool-find); }
.search-clear {
  background: none; border: 1px solid var(--border);
  color: var(--text-muted); padding: 5px 12px;
  border-radius: 4px; font-size: 11px; cursor: pointer;
}
.search-clear:hover { border-color: var(--scope-trace-tool-find); color: var(--scope-trace-tool-find); }
.search-count {
  font-size: 11px; color: var(--text-muted);
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  margin-left: 4px;
}
.cost-summary {
  background: var(--surface); border: 1px solid var(--border);
  border-radius: 8px; padding: 14px 18px; margin-bottom: 12px;
}
.cost-summary-title {
  font-size: 13px; font-weight: 600; margin-bottom: 10px;
  color: var(--text);
}
.cost-grid {
  display: flex; gap: 12px; margin-bottom: 10px; flex-wrap: wrap;
}
.cost-card {
  background: var(--bg); border: 1px solid var(--border);
  border-radius: 6px; padding: 10px 14px; min-width: 140px;
}
.cost-card-label {
  font-size: 10px; text-transform: uppercase; letter-spacing: 0.8px;
  color: var(--text-muted); font-weight: 600;
}
.cost-card-value {
  font-size: 18px; font-weight: 600; margin-top: 2px;
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  color: #58a6ff;
}
.cost-table-section { margin-top: 8px; }
.cost-table-title {
  font-size: 11px; color: var(--text-muted); margin-bottom: 4px;
  font-weight: 600;
}
.cost-table {
  width: 100%; border-collapse: collapse; font-size: 12px;
}
.cost-table th {
  text-align: left; padding: 4px 8px;
  border-bottom: 1px solid var(--border);
  color: var(--text-muted); font-weight: 600;
  font-size: 10px; text-transform: uppercase; letter-spacing: 0.5px;
}
.cost-table td {
  padding: 3px 8px; border-bottom: 1px solid #21262d;
}
.cost-value {
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  color: #58a6ff;
}
.tl-node.filter-dimmed { opacity: 0.25; }
.legend {
  padding: 14px 18px; margin-bottom: 28px;
  background: var(--surface); border: 1px solid var(--border);
  border-radius: 8px; font-size: 12px;
}
.legend-section {
  display: flex; gap: 18px; flex-wrap: wrap; align-items: center;
}
.legend-section + .legend-section {
  margin-top: 8px; padding-top: 8px;
  border-top: 1px solid var(--border);
}
.legend-title {
  font-size: 10px; text-transform: uppercase; letter-spacing: 0.8px;
  color: var(--text-muted); font-weight: 600; min-width: 80px;
}
.legend-item {
  display: flex; align-items: center; gap: 6px;
  color: var(--text-muted);
}
.legend-icon {
  width: 20px; height: 20px;
  display: flex; align-items: center; justify-content: center;
  flex-shrink: 0;
}
.participants {
  display: flex; gap: 32px; margin-bottom: 24px;
  padding: 14px 18px;
  background: var(--surface); border: 1px solid var(--border);
  border-radius: 8px;
}
.participant {
  display: flex; align-items: center; gap: 10px;
}
.participant-avatar {
  width: 32px; height: 32px; border-radius: 50%;
  display: flex; align-items: center; justify-content: center;
  font-weight: 700; font-size: 13px; flex-shrink: 0;
}
.participant-avatar.agent-avatar {
  background: var(--clr-agent-bg); border: 2px solid var(--clr-agent);
  color: var(--clr-agent);
}
.participant-avatar.scope-avatar {
  background: var(--clr-scope-bg); border: 2px solid var(--clr-scope);
  color: var(--clr-scope);
}
.participant-info { font-size: 13px; }
.participant-name { font-weight: 600; }
.participant-detail { font-size: 11px; color: var(--text-muted); }
.timeline { position: relative; padding-left: 44px; }
.timeline::before {
  content: '';
  position: absolute;
  left: 17px; top: 0; bottom: 0;
  width: 2px;
  background: var(--border);
}
.tl-node { position: relative; margin-bottom: 28px; }
.tl-icon {
  position: absolute;
  left: -44px; top: 4px;
  width: 36px; height: 36px;
  display: flex; align-items: center; justify-content: center;
  z-index: 2; cursor: default;
}
.tl-marker {
  position: absolute;
  left: -92px; top: 10px;
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  font-size: 10px; color: var(--text-muted);
  text-align: right; width: 44px;
  opacity: 0.7;
}
.tooltip {
  display: none;
  position: absolute;
  left: 42px; top: -4px;
  background: #1c2129; border: 1px solid var(--border);
  border-radius: 6px; padding: 10px 14px;
  font-size: 12px; line-height: 1.7;
  white-space: nowrap; z-index: 100;
  box-shadow: 0 4px 16px rgba(0,0,0,0.5);
  pointer-events: none;
}
.tl-node:hover > .tooltip { display: block; }
.tooltip-row { display: flex; gap: 8px; }
.tooltip-label { color: var(--text-muted); min-width: 72px; }
.tooltip-value {
  color: var(--text);
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
}
.bookend {
  display: flex; align-items: center; gap: 12px;
  font-size: 13px; font-weight: 600; min-height: 36px;
}
.bookend-start { color: var(--clr-start); }
.bookend-end   { color: var(--clr-end); }
.bookend-label {
  text-transform: uppercase; font-size: 11px; letter-spacing: 1px;
}
.bookend-time {
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  font-size: 11px; font-weight: 400; color: var(--text-muted);
}
.request-header {
  display: flex; align-items: center; gap: 8px;
  flex-wrap: wrap; margin-bottom: 14px; min-height: 36px;
}
.badge {
  display: inline-flex; align-items: center;
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  font-size: 12px; padding: 2px 10px; border-radius: 12px;
  font-weight: 500;
}
/* Badge-op: 9 TraceToolType buckets (taxonomy-collapse applied).
   Old data-op values status/file/tool/search/mcp/message are now
   system/system/system/find/external/model respectively. */
.badge-op { color: #fff; }
.badge-op[data-op="find"]     { background: var(--scope-trace-tool-find); }
.badge-op[data-op="pack"]     { background: var(--scope-trace-tool-pack); }
.badge-op[data-op="hook"]     { background: var(--scope-trace-tool-hook); }
.badge-op[data-op="edit"]     { background: var(--scope-trace-tool-edit); }
.badge-op[data-op="model"]    { background: var(--scope-trace-tool-model); color: #000; }
.badge-op[data-op="shell"]    { background: var(--scope-trace-tool-shell); }
.badge-op[data-op="external"] { background: var(--scope-trace-tool-external); }
.badge-op[data-op="operator"] { background: var(--scope-trace-tool-operator); }
.badge-op[data-op="system"]   { background: var(--scope-trace-tool-system); color: #000; }
.badge-secondary {
  background: transparent; border: 1px solid var(--border);
  color: var(--text-muted);
}
.badge-status {
  background: #1a3a2a; color: #3fb950; border: 1px solid #23843655;
}
.req-query {
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  font-size: 13px; color: var(--text); font-weight: 500;
}
.req-timestamp {
  margin-left: auto;
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  font-size: 11px; color: var(--text-muted);
}
.msg {
  border-radius: 8px;
  padding: 0;
  margin-bottom: 10px;
  font-size: 13px;
  overflow: hidden;
}
.msg-header {
  display: flex; align-items: center; gap: 8px;
  padding: 8px 14px;
  font-size: 12px; font-weight: 600;
}
.msg-header-toggle { cursor: pointer; user-select: none; }
.msg-header-toggle:hover { background: rgba(255,255,255,0.03); }
.msg-toggle-icon {
  margin-left: auto; font-size: 9px; color: var(--text-muted);
  transition: transform 0.15s;
}
.msg-expanded .msg-toggle-icon { transform: rotate(90deg); }
.msg-avatar {
  width: 22px; height: 22px; border-radius: 50%;
  display: flex; align-items: center; justify-content: center;
  font-size: 10px; font-weight: 700; flex-shrink: 0;
}
.msg-sender { font-weight: 600; }
.msg-arrow {
  color: var(--text-muted); font-size: 11px;
  margin: 0 2px;
}
.msg-receiver { font-weight: 600; }
.msg-meta {
  font-size: 11px; color: var(--text-muted); font-weight: 400;
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
}
.msg-body { padding: 0 14px 12px 14px; }
.msg-inbound {
  background: var(--clr-agent-bg);
  border: 1px solid var(--clr-agent-border);
  border-left: 3px solid var(--clr-agent);
}
.msg-inbound .msg-header {
  border-bottom: 1px solid var(--clr-agent-border);
}
.msg-inbound .msg-avatar {
  background: var(--clr-agent-bg); border: 1.5px solid var(--clr-agent);
  color: var(--clr-agent);
}
.msg-inbound .msg-sender { color: var(--clr-agent); }
.msg-inbound .msg-receiver { color: var(--clr-scope); }
.msg-outbound {
  background: var(--clr-scope-bg);
  border: 1px solid var(--clr-scope-border);
  border-left: 3px solid var(--clr-scope);
}
.msg-outbound .msg-header {
  border-bottom: 1px solid var(--clr-scope-border);
}
.msg-outbound .msg-avatar {
  background: var(--clr-scope-bg); border: 1.5px solid var(--clr-scope);
  color: var(--clr-scope);
}
.msg-outbound .msg-sender { color: var(--clr-scope); }
.msg-outbound .msg-receiver { color: var(--clr-agent); }
.msg-disabled {
  background: var(--surface);
  border: 1px dashed var(--border);
  border-left: 3px dashed var(--border);
}
.msg-disabled .msg-header {
  border-bottom: 1px dashed var(--border);
}
.msg-disabled .msg-avatar {
  background: #21262d; border: 1.5px solid var(--text-muted);
  color: var(--text-muted);
}
.msg-disabled .msg-sender,
.msg-disabled .msg-receiver { color: var(--text-muted); }
.msg-disabled .msg-body {
  color: var(--text-muted); font-style: italic;
}
pre {
  background: var(--code-bg);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 12px;
  overflow-x: auto;
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  font-size: 12px; line-height: 1.5; color: var(--text);
  margin-top: 6px;
}
.json-key  { color: var(--scope-trace-tool-find); }
.json-str  { color: #a5d6ff; }
.json-num  { color: #79c0ff; }
.json-bool { color: var(--scope-trace-tool-system); }
.find-summary {
  font-size: 12px; color: var(--text-muted);
  padding: 6px 10px; background: #21262d;
  border-radius: 4px; margin-bottom: 10px;
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
}
.result-item {
  border: 1px solid var(--border);
  border-radius: 6px; margin-bottom: 6px; overflow: hidden;
}
.result-header {
  display: flex; align-items: center; justify-content: space-between;
  padding: 8px 12px; background: #21262d;
  font-size: 12px; cursor: pointer; user-select: none;
}
.result-header:hover { background: var(--surface-raised); }
.result-header::after {
  content: '\25B6'; font-size: 9px; color: var(--text-muted);
  transition: transform 0.15s;
}
.result-header.open::after { transform: rotate(90deg); }
.result-file {
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  color: var(--scope-trace-tool-find);
}
.result-meta {
  color: var(--text-muted); margin-left: 12px; white-space: nowrap;
}
.result-body { display: none; }
.result-body.open { display: block; }
.result-body pre {
  margin: 0; border: none; border-radius: 0;
  border-top: 1px solid var(--border);
}
.pack-header {
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  font-size: 12px; color: var(--scope-trace-tool-pack); margin-bottom: 8px;
}
.toggle-btn {
  background: none; border: 1px solid var(--border);
  color: var(--text-muted); padding: 4px 12px;
  border-radius: 4px; font-size: 11px; cursor: pointer;
  transition: all 0.15s;
}
.toggle-btn:hover { border-color: var(--scope-trace-tool-find); color: var(--scope-trace-tool-find); }
.pack-content { margin-top: 8px; }
.event-card { margin-top: 4px; }
.event-title {
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  font-size: 13px; color: var(--text); font-weight: 500;
}
.event-detail {
  font-size: 12px; color: var(--text-muted); margin-top: 2px;
}
.event-payload { margin-top: 8px; }
.event-payload-section { margin-bottom: 8px; }
.event-payload-label {
  font-size: 10px; text-transform: uppercase; letter-spacing: 0.8px;
  color: var(--text-muted); margin-bottom: 4px; font-weight: 600;
}
.agent-span {
  margin: 8px 0 16px 0;
  border: 1px solid var(--span-border);
  border-left: 3px solid var(--span-accent);
  border-radius: 8px;
  background: var(--span-bg);
}
.span-header {
  display: flex; align-items: center; gap: 8px;
  padding: 10px 14px;
  cursor: pointer; user-select: none;
  font-size: 13px;
}
.span-header:hover { background: var(--span-bg); }
.span-header::after {
  content: '\25BC'; font-size: 9px; color: var(--text-muted);
  margin-left: auto; transition: transform 0.15s;
}
.span-header.collapsed::after { transform: rotate(-90deg); }
.span-avatar {
  width: 24px; height: 24px; border-radius: 50%;
  display: flex; align-items: center; justify-content: center;
  font-size: 10px; font-weight: 700; flex-shrink: 0;
  background: var(--span-bg); border: 1.5px solid var(--span-accent);
  color: var(--span-accent);
}
.span-name {
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  font-weight: 600; color: var(--span-accent);
}
.span-type {
  font-size: 11px; color: var(--text-muted);
  text-transform: uppercase; letter-spacing: 0.5px;
}
.span-timing {
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  font-size: 11px; color: var(--text-muted);
}
.span-status {
  font-size: 11px; padding: 1px 8px; border-radius: 10px;
  background: #1a3a2a; color: #3fb950; border: 1px solid #23843655;
}
.span-cost {
  font-size: 11px; padding: 1px 8px; border-radius: 10px;
  background: #1a2a3a; color: #58a6ff; border: 1px solid #264a7055;
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
}
.span-tokens {
  font-size: 11px; padding: 1px 8px; border-radius: 10px;
  background: #2a1a3a; color: #d2a8ff; border: 1px solid #4a267055;
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
}
.span-child-count {
  font-size: 11px; color: var(--text-muted);
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
}
.span-content {
  padding: 4px 14px 14px 14px;
  border-top: 1px solid var(--span-border);
}
.span-content .tl-node { margin-bottom: 20px; }
.span-io { margin-top: 8px; }
.span-children {
  margin-top: 10px;
  padding-left: 44px;
  position: relative;
}
.span-children::before {
  content: '';
  position: absolute;
  left: 17px; top: 0; bottom: 0;
  width: 2px;
  background: var(--span-border);
}
.span-children .tl-node { margin-bottom: 12px; }
.span-children .agent-span { margin: 4px 0 8px 0; }
""";

    // Legend SVGs reference CSS custom properties (both tool-type and structural tokens).
    // No raw hex in SVG attributes -- all colors driven by the dual-theme token block above.
    private const string LegendHtml = """
<div class="legend">
  <div class="legend-section">
    <span class="legend-title">Timeline</span>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><polygon points="9,1 17,9 9,17 1,9" fill="none" stroke="var(--clr-start)" stroke-width="2"/><circle cx="9" cy="9" r="2.5" fill="var(--clr-start)"/></svg></div>
      <span>Start</span>
    </div>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><rect x="3" y="3" width="12" height="12" rx="2" fill="none" stroke="var(--clr-end)" stroke-width="2"/><rect x="6" y="6" width="6" height="6" rx="1" fill="var(--clr-end)"/></svg></div>
      <span>End</span>
    </div>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><circle cx="9" cy="9" r="7" fill="none" stroke="var(--scope-trace-tool-system)" stroke-width="2"/><text x="9" y="13" text-anchor="middle" fill="var(--scope-trace-tool-system)" font-size="11" font-weight="700" font-family="sans-serif">i</text></svg></div>
      <span>system</span>
    </div>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><circle cx="8" cy="8" r="5" fill="none" stroke="var(--scope-trace-tool-find)" stroke-width="2"/><line x1="12" y1="12" x2="16" y2="16" stroke="var(--scope-trace-tool-find)" stroke-width="2" stroke-linecap="round"/></svg></div>
      <span>find</span>
    </div>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><rect x="2" y="4" width="14" height="11" rx="2" fill="none" stroke="var(--scope-trace-tool-pack)" stroke-width="2"/><line x1="2" y1="8" x2="16" y2="8" stroke="var(--scope-trace-tool-pack)" stroke-width="1.5"/><line x1="6" y1="4" x2="6" y2="1" stroke="var(--scope-trace-tool-pack)" stroke-width="2" stroke-linecap="round"/><line x1="12" y1="4" x2="12" y2="1" stroke="var(--scope-trace-tool-pack)" stroke-width="2" stroke-linecap="round"/></svg></div>
      <span>pack</span>
    </div>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><polygon points="9,1 17,9 9,17 1,9" fill="none" stroke="var(--scope-trace-tool-external)" stroke-width="2"/></svg></div>
      <span>external</span>
    </div>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><rect x="2" y="3" width="14" height="12" rx="2" fill="none" stroke="var(--span-accent)" stroke-width="1.5"/><rect x="4" y="5" width="10" height="8" rx="1.5" fill="none" stroke="var(--span-accent)" stroke-width="1" stroke-dasharray="2 1"/><circle cx="9" cy="9" r="2" fill="var(--span-accent)"/></svg></div>
      <span>Sub-agent</span>
    </div>
  </div>
  <div class="legend-section">
    <span class="legend-title">Agent Tools</span>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><path d="M5 2h5l4 4v9a1 1 0 01-1 1H5a1 1 0 01-1-2V3a1 1 0 011-1z" fill="none" stroke="var(--scope-trace-tool-system)" stroke-width="1.5"/><polyline points="10,2 10,6 14,6" fill="none" stroke="var(--scope-trace-tool-system)" stroke-width="1.5"/></svg></div>
      <span>Read / Glob (system)</span>
    </div>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><path d="M11 3l4 4-8 8H3v-4z" fill="none" stroke="var(--scope-trace-tool-edit)" stroke-width="1.5" stroke-linejoin="round"/></svg></div>
      <span>Edit / Write (edit)</span>
    </div>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><circle cx="8" cy="8" r="5" fill="none" stroke="var(--scope-trace-tool-find)" stroke-width="1.5"/><line x1="12" y1="12" x2="16" y2="16" stroke="var(--scope-trace-tool-find)" stroke-width="1.5" stroke-linecap="round"/><line x1="5" y1="7" x2="11" y2="7" stroke="var(--scope-trace-tool-find)" stroke-width="1" stroke-linecap="round"/><line x1="5" y1="9" x2="9" y2="9" stroke="var(--scope-trace-tool-find)" stroke-width="1" stroke-linecap="round"/></svg></div>
      <span>Grep (find)</span>
    </div>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><rect x="2" y="3" width="14" height="12" rx="2" fill="none" stroke="var(--scope-trace-tool-shell)" stroke-width="1.5"/><polyline points="5,8 7.5,10 5,12" fill="none" stroke="var(--scope-trace-tool-shell)" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/><line x1="9" y1="12" x2="13" y2="12" stroke="var(--scope-trace-tool-shell)" stroke-width="1.5" stroke-linecap="round"/></svg></div>
      <span>Bash / Shell (shell)</span>
    </div>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><path d="M10 2 L8 8 L12 8 L7 16 L9 10 L5 10 Z" fill="none" stroke="var(--scope-trace-tool-system)" stroke-width="1.5" stroke-linejoin="round"/></svg></div>
      <span>Other tool (system)</span>
    </div>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><rect x="2" y="4" width="14" height="10" rx="2" fill="none" stroke="var(--scope-trace-tool-model)" stroke-width="1.5"/><polyline points="2,5 9,10 16,5" fill="none" stroke="var(--scope-trace-tool-model)" stroke-width="1.5" stroke-linejoin="round"/></svg></div>
      <span>SendMessage (model)</span>
    </div>
    <div class="legend-item">
      <div class="legend-icon"><svg width="18" height="18" viewBox="0 0 18 18"><polygon points="9,1 16,5 16,13 9,17 2,13 2,5" fill="none" stroke="var(--scope-trace-tool-hook)" stroke-width="1.5"/><circle cx="9" cy="9" r="2" fill="var(--scope-trace-tool-hook)"/></svg></div>
      <span>Hook (hook)</span>
    </div>
  </div>
  <div class="legend-section">
    <span class="legend-title">Messages</span>
    <div class="legend-item">
      <div class="legend-icon"><div style="width:16px;height:16px;border-radius:50%;background:var(--clr-agent-bg);border:1.5px solid var(--clr-agent);display:flex;align-items:center;justify-content:center;font-size:8px;font-weight:700;color:var(--clr-agent)">A</div></div>
      <span>Agent sends request</span>
    </div>
    <div class="legend-item">
      <div class="legend-icon"><div style="width:16px;height:16px;border-radius:50%;background:var(--clr-scope-bg);border:1.5px solid var(--clr-scope);display:flex;align-items:center;justify-content:center;font-size:8px;font-weight:700;color:var(--clr-scope)">S</div></div>
      <span>Scope sends response</span>
    </div>
  </div>
</div>
""";

    private const string Script = """
function toggleResult(header) {
  header.classList.toggle('open');
  header.nextElementSibling.classList.toggle('open');
}
function togglePack(btn) {
  var content = btn.nextElementSibling;
  var showing = content.style.display !== 'none';
  content.style.display = showing ? 'none' : 'block';
  btn.textContent = showing ? 'Show full context pack' : 'Hide full context pack';
}
function toggleEventPayload(btn) {
  var content = btn.nextElementSibling;
  var showing = content.style.display !== 'none';
  content.style.display = showing ? 'none' : 'block';
  btn.textContent = showing ? 'Show details' : 'Hide details';
}
function toggleSpanContent(header) {
  header.classList.toggle('collapsed');
  var content = header.nextElementSibling;
  if (content && content.classList.contains('span-content')) {
    content.style.display = content.style.display === 'none' ? 'block' : 'none';
  }
}
function toggleMsgBody(header) {
  var msg = header.parentElement;
  var body = header.nextElementSibling;
  if (!body) return;
  var isHidden = body.style.display === 'none';
  body.style.display = isHidden ? 'block' : 'none';
  if (isHidden) { msg.classList.add('msg-expanded'); msg.classList.remove('msg-collapsed'); }
  else { msg.classList.remove('msg-expanded'); msg.classList.add('msg-collapsed'); }
}
function applyFilters() {
  var toolFilter = (document.getElementById('search-tool').value || '').toLowerCase();
  var agentFilter = document.getElementById('search-agent').value || '';
  var nodes = document.querySelectorAll('.timeline .tl-node[data-tool]');
  var total = 0;
  var matched = 0;
  for (var i = 0; i < nodes.length; i++) {
    var node = nodes[i];
    var tool = (node.getAttribute('data-tool') || '').toLowerCase();
    var agent = node.getAttribute('data-agent') || '';
    var toolMatch = !toolFilter || tool.indexOf(toolFilter) !== -1;
    var agentMatch = !agentFilter || agent === agentFilter;
    if (toolMatch && agentMatch) {
      node.classList.remove('filter-dimmed');
      matched++;
    } else {
      node.classList.add('filter-dimmed');
    }
    total++;
  }
  // Un-dim span containers if any nested child matches
  var spans = document.querySelectorAll('.timeline .tl-node[data-tool="span"]');
  for (var j = 0; j < spans.length; j++) {
    var span = spans[j];
    var children = span.querySelectorAll('.span-children .tl-node[data-tool]');
    var anyChildVisible = false;
    for (var k = 0; k < children.length; k++) {
      if (!children[k].classList.contains('filter-dimmed')) {
        anyChildVisible = true;
        break;
      }
    }
    if (anyChildVisible) {
      span.classList.remove('filter-dimmed');
    }
  }
  var countEl = document.getElementById('search-count');
  if (toolFilter || agentFilter) {
    countEl.textContent = 'Showing ' + matched + ' of ' + total + ' entries';
  } else {
    countEl.textContent = '';
  }
}
function clearFilters() {
  document.getElementById('search-tool').value = '';
  document.getElementById('search-agent').value = '';
  applyFilters();
}
""";

    private const string SessionGraphCss = """
.session-graph-container {
  margin-bottom: 28px;
  padding: 16px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 8px;
  overflow-x: auto;
}
.session-graph-title {
  font-size: 16px; font-weight: 600; margin-bottom: 12px;
  color: var(--text);
}
.session-graph { display: block; margin: 0 auto; }
.session-edge {
  fill: none; stroke: var(--border); stroke-width: 2;
  transition: stroke 0.2s, stroke-width 0.2s;
}
.session-edge.highlight {
  stroke: var(--span-accent); stroke-width: 3;
}
.session-node-rect {
  fill: var(--surface-raised); stroke: var(--border); stroke-width: 2;
  transition: stroke 0.2s, fill 0.2s;
}
.session-node:hover .session-node-rect,
.session-node.selected .session-node-rect {
  stroke: var(--span-accent); fill: var(--span-bg);
}
.session-root .session-node-rect {
  stroke: var(--clr-agent); stroke-width: 2.5;
}
.session-root:hover .session-node-rect,
.session-root.selected .session-node-rect {
  stroke: var(--clr-agent); fill: var(--clr-agent-bg);
}
.session-node-name {
  fill: var(--text); font-size: 13px; font-weight: 600;
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
}
.session-node-meta {
  fill: var(--text-muted); font-size: 11px;
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif;
}
.session-node-key {
  fill: var(--text-muted); font-size: 9px;
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
  opacity: 0.6;
}
.session-section {
  margin-bottom: 24px;
  border: 1px solid var(--border);
  border-radius: 8px;
  overflow: hidden;
}
.session-section.depth-1 { margin-left: 24px; }
.session-section.depth-2 { margin-left: 48px; }
.session-section.depth-3 { margin-left: 72px; }
.session-section-header {
  display: flex; align-items: center; gap: 12px;
  padding: 12px 16px;
  background: var(--surface);
  border-bottom: 1px solid var(--border);
  cursor: pointer;
}
.session-section-header:hover { background: var(--surface-raised); }
.session-section-avatar {
  width: 32px; height: 32px; border-radius: 50%;
  display: flex; align-items: center; justify-content: center;
  font-weight: 700; font-size: 12px; flex-shrink: 0;
  background: var(--span-bg); border: 2px solid var(--span-accent);
  color: var(--span-accent);
}
.session-section.depth- .session-section-avatar,
.session-section:first-of-type .session-section-avatar {
  background: var(--clr-agent-bg); border-color: var(--clr-agent);
  color: var(--clr-agent);
}
.session-section-name {
  font-size: 14px; font-weight: 600; color: var(--text);
  font-family: 'SF Mono', SFMono-Regular, Consolas, monospace;
}
.session-section-meta {
  font-size: 12px; color: var(--text-muted);
}
.session-section-toggle {
  width: 0; height: 0;
  border-left: 6px solid var(--text-muted);
  border-top: 5px solid transparent;
  border-bottom: 5px solid transparent;
  transition: transform 0.15s ease;
  flex-shrink: 0;
}
.session-section:not(.collapsed) .session-section-toggle {
  transform: rotate(90deg);
}
.session-section.collapsed .session-section-body { display: none; }
.session-section-body { padding: 16px; }
.session-section.highlight {
  border-color: var(--span-accent);
  box-shadow: 0 0 0 2px var(--span-bg);
}
""";

    private const string SessionGraphScript = """
function toggleSession(header) {
  var section = header.closest('.session-section');
  if (section) section.classList.toggle('collapsed');
}
function selectSession(sessionKey) {
  // Remove previous selection
  document.querySelectorAll('.session-node.selected').forEach(function(n) {
    n.classList.remove('selected');
  });
  document.querySelectorAll('.session-edge.highlight').forEach(function(e) {
    e.classList.remove('highlight');
  });
  document.querySelectorAll('.session-section.highlight').forEach(function(s) {
    s.classList.remove('highlight');
  });

  // Highlight clicked node
  var node = document.querySelector('.session-node[data-session="' + sessionKey + '"]');
  if (node) node.classList.add('selected');

  // Highlight connected edges
  document.querySelectorAll('.session-edge').forEach(function(e) {
    if (e.getAttribute('data-parent') === sessionKey || e.getAttribute('data-child') === sessionKey) {
      e.classList.add('highlight');
    }
  });

  // Scroll to, expand, and highlight session section
  var section = document.getElementById('session-' + sessionKey);
  if (section) {
    section.classList.remove('collapsed');
    section.classList.add('highlight');
    section.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }
}
""";
}

#pragma warning restore MA0016
