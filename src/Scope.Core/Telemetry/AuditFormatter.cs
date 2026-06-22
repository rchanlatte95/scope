namespace Scope.Core.Telemetry;

using System.Text.Json;
using Scope.Core.Utilities;
using System.Globalization;

// Public contract preserved (T-2026-06-17 sweep, owner): these public static formatters accept the
// canonical loosely-typed List<Dictionary<string,object?>> row shape produced by ObservabilityAuditDb
// and are public Scope.Core API behind the `scope audit` CLI. Parameter collection types preserved.
#pragma warning disable MA0016 // public Scope.Core audit-formatter API; canonical DB-row parameter shapes preserved

/// <summary>
/// Formats audit query results as markdown tables or indented JSON.
/// Used by the <c>scope audit</c> CLI command.
/// </summary>
public static class AuditFormatter
{

    public static string FormatRequests(List<Dictionary<string, object?>> rows, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(rows, ScopeJsonSerializerContext.Default.ListDictionaryStringObject);

        if (rows.IsNullOrEmpty)
            return "No requests found.";

        using var w = new StringWriter();
        w.WriteLine("# Recent Requests");
        w.WriteLine();
        foreach (var r in rows)
        {
            w.WriteLine($"  {Get(r, "id")}");
            w.WriteLine($"    surface: {Get(r, "caller_surface")}  operation: {Get(r, "operation")}  status: {Get(r, "status")}");
            w.WriteLine($"    query: {Get(r, "query_text")}");
            w.WriteLine($"    elapsed: {Get(r, "total_elapsed_ms")}ms  exit_code: {Get(r, "exit_code")}");
            w.WriteLine($"    agent: {Get(r, "agent_session_key")}  parent: {Get(r, "parent_request_id")}");
            w.WriteLine($"    created: {Get(r, "created_at")}  completed: {Get(r, "completed_at")}");
            w.WriteLine();
        }
        return w.ToString().TrimEnd();
    }

    public static string FormatStages(List<Dictionary<string, object?>> rows, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(rows, ScopeJsonSerializerContext.Default.ListDictionaryStringObject);

        if (rows.IsNullOrEmpty)
            return "No stages found for this request.";

        using var w = new StringWriter();
        w.WriteLine("# Request Stages");
        w.WriteLine();
        w.WriteLine($"  {"#",-4} {"Stage",-24} {"Elapsed (ms)",-14} {"Metadata"}");
        w.WriteLine($"  {"─",4} {"─",24} {"─",14} {"─"}");
        foreach (var r in rows)
        {
            var order = Get(r, "stage_order");
            var name = Get(r, "stage_name");
            var elapsed = Get(r, "elapsed_ms");
            var meta = Get(r, "metadata_json");
            w.WriteLine($"  {order,-4} {name,-24} {elapsed,-14} {meta}");
        }
        return w.ToString().TrimEnd();
    }

    public static string FormatPacking(List<Dictionary<string, object?>> rows, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(rows, ScopeJsonSerializerContext.Default.ListDictionaryStringObject);

        if (rows.IsNullOrEmpty)
            return "No packing metrics found.";

        using var w = new StringWriter();
        w.WriteLine("# Packing Metrics");
        w.WriteLine();
        foreach (var r in rows)
        {
            w.WriteLine($"  {Get(r, "target_fqn")}  (request: {Get(r, "request_id")})");
            w.WriteLine($"    budget: {Get(r, "budget_requested")} requested, {Get(r, "budget_used")} used, {Get(r, "budget_remaining")} remaining");
            w.WriteLine($"    symbols: {Get(r, "symbols_considered")} considered, {Get(r, "symbols_included")} included, {Get(r, "symbols_rejected")} rejected");
            w.WriteLine($"    representation: {Get(r, "full_body_ct")} full, {Get(r, "signature_only_ct")} sig-only, {Get(r, "compression_ct")} compressed, {Get(r, "call_site_ct")} call-site");
            w.WriteLine($"    dedup: {Get(r, "dedup_removed_ct")} removed  free: {Get(r, "free_symbol_ct")}  test-capped: {Get(r, "test_callers_capped")}");
            w.WriteLine($"    tokens: target={Get(r, "target_token_count")} context={Get(r, "context_token_count")}");
            w.WriteLine($"    created: {Get(r, "created_at")}");
            w.WriteLine();
        }
        return w.ToString().TrimEnd();
    }

    public static string FormatAgents(List<Dictionary<string, object?>> rows, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(rows, ScopeJsonSerializerContext.Default.ListDictionaryStringObject);

        if (rows.IsNullOrEmpty)
            return "No agent sessions found.";

        // Build parent -> children lookup for tree rendering
        var allKeys = new HashSet<string>(StringComparer.Ordinal);
        var childrenOf = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
        var roots = new List<Dictionary<string, object?>>();

        foreach (var r in rows)
        {
            var key = Get(r, "session_key");
            if (!(string.Equals(key, "-", StringComparison.Ordinal))) allKeys.Add(key);
        }

        foreach (var r in rows)
        {
            var parentKey = Get(r, "parent_session_key");
            if (string.Equals(parentKey, "-", StringComparison.Ordinal) || !allKeys.Contains(parentKey))
            {
                roots.Add(r);
            }
            else
            {
                if (!childrenOf.TryGetValue(parentKey, out var list))
                {
                    list = [];
                    childrenOf[parentKey] = list;
                }
                list.Add(r);
            }
        }

        using var w = new StringWriter();
        w.WriteLine("# Agent Sessions");
        w.WriteLine();

        foreach (var root in roots)
            WriteSessionNode(w, root, childrenOf, indentLevel: 0);

        return w.ToString().TrimEnd();
    }

    private static void WriteSessionNode(
        StringWriter w,
        Dictionary<string, object?> session,
        Dictionary<string, List<Dictionary<string, object?>>> childrenOf,
        int indentLevel)
    {
        var indent = new string(' ', 2 + indentLevel * 2);
        var detailIndent = indent + "  ";

        var key = Get(session, "session_key");
        var parentKey = Get(session, "parent_session_key");

        w.WriteLine($"{indent}{key}");
        var parentLine = !(string.Equals(parentKey, "-", StringComparison.Ordinal)) ? $"  parent: ...{(parentKey.Length > 12 ? parentKey[^12..] : parentKey)}" : "";
        w.WriteLine($"{detailIndent}type: {Get(session, "agent_type")}/{Get(session, "agent_subtype")}  requests: {Get(session, "request_count")}{parentLine}");
        w.WriteLine($"{detailIndent}first: {Get(session, "first_seen_at")}  last: {Get(session, "last_seen_at")}");

        if (!(string.Equals(key, "-", StringComparison.Ordinal)) && childrenOf.TryGetValue(key, out var children))
        {
            foreach (var child in children)
                WriteSessionNode(w, child, childrenOf, indentLevel + 1);
        }

        // Only add blank line for root-level entries
        if (indentLevel == 0)
            w.WriteLine();
    }

    public static string FormatCosts(List<Dictionary<string, object?>> rows, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(rows, ScopeJsonSerializerContext.Default.ListDictionaryStringObject);

        if (rows.IsNullOrEmpty)
            return "No cost estimates found.";

        using var w = new StringWriter();
        w.WriteLine("# Cost Estimates");
        w.WriteLine();
        foreach (var r in rows)
        {
            w.WriteLine($"  {Get(r, "model_id")} ({Get(r, "provider")})  request: {Get(r, "request_id")}");
            w.WriteLine($"    input: {Get(r, "input_tokens")} tokens (${Get(r, "input_cost_usd")})  output: {Get(r, "estimated_output_tokens")} tokens (${Get(r, "output_cost_usd")})");
            w.WriteLine($"    total: ${Get(r, "total_cost_usd")}  created: {Get(r, "created_at")}");
            w.WriteLine();
        }
        return w.ToString().TrimEnd();
    }

    public static string FormatPayloads(List<Dictionary<string, object?>> rows, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(rows, ScopeJsonSerializerContext.Default.ListDictionaryStringObject);

        if (rows.IsNullOrEmpty)
            return "No payloads found.";

        using var w = new StringWriter();
        w.WriteLine("# Payloads");
        w.WriteLine();
        foreach (var r in rows)
        {
            w.WriteLine($"  [{Get(r, "direction")}] {Get(r, "content_type")}  request: {Get(r, "request_id")}");
            w.WriteLine($"    tokens: {Get(r, "token_count")}  created: {Get(r, "created_at")}");
            var preview = Get(r, "payload_preview");
            if (preview.IsInhabited)
                w.WriteLine($"    preview: {preview}");
            w.WriteLine();
        }
        return w.ToString().TrimEnd();
    }

    public static string FormatSummary(Dictionary<string, object?> summary, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(summary, ScopeJsonSerializerContext.Default.DictionaryStringObject);

        using var w = new StringWriter();
        w.WriteLine("# Observability Summary");
        w.WriteLine();
        w.WriteLine($"  Total requests:  {GetVal(summary, "total_requests")}");
        w.WriteLine($"  Avg elapsed:     {GetVal(summary, "avg_elapsed_ms")} ms");
        w.WriteLine($"  Total cost:      ${GetVal(summary, "total_cost_usd")}");
        w.WriteLine($"  Earliest:        {GetVal(summary, "earliest_request")}");
        w.WriteLine($"  Latest:          {GetVal(summary, "latest_request")}");
        w.WriteLine();

        if (summary.TryGetValue("by_surface", out var bySurface) && bySurface is Dictionary<string, long> surfaces && surfaces.IsInhabited)
        {
            w.WriteLine("  By surface:");
            foreach (var kv in surfaces)
                w.WriteLine($"    {kv.Key}: {kv.Value}");
            w.WriteLine();
        }

        if (summary.TryGetValue("by_operation", out var byOp) && byOp is Dictionary<string, long> ops && ops.IsInhabited)
        {
            w.WriteLine("  By operation:");
            foreach (var kv in ops)
                w.WriteLine($"    {kv.Key}: {kv.Value}");
        }

        return w.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats trace entries in a conversation-style view showing inbound/outbound payloads.
    /// Supports both "request" entries (obs_requests) and "event" entries (obs_agent_events tool_call).
    /// Entries annotated with "_parent_span_id" are rendered indented inside their parent span.
    /// </summary>
    public static string FormatTrace(List<Dictionary<string, object?>> rows, string format, bool fullPayloads = false)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(rows, ScopeJsonSerializerContext.Default.ListDictionaryStringObject);

        if (rows.IsNullOrEmpty)
            return "No trace entries found.";

        var childrenByParent = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
        var topLevel = new List<Dictionary<string, object?>>();

        foreach (var r in rows)
        {
            var parentId = Get(r, "_parent_span_id");
            if (string.Equals(parentId, "-", StringComparison.Ordinal))
            {
                topLevel.Add(r);
            }
            else
            {
                if (!childrenByParent.TryGetValue(parentId, out var list))
                {
                    list = [];
                    childrenByParent[parentId] = list;
                }
                list.Add(r);
            }
        }

        using var w = new StringWriter();
        w.WriteLine("# Trace");
        w.WriteLine();
        foreach (var r in topLevel)
            WriteTraceEntry(w, r, childrenByParent, fullPayloads, depth: 0);

        return w.ToString().TrimEnd();
    }

    private static void WriteTraceEntry(StringWriter w, Dictionary<string, object?> r,
        Dictionary<string, List<Dictionary<string, object?>>> childrenByParent,
        bool fullPayloads, int depth)
    {
        var indent = new string(' ', depth * 2);
        var source = Get(r, "_source");

        if (string.Equals(source, "span", StringComparison.Ordinal))
        {
            FormatSpanEntry(w, r, indent);
            var spanId = Get(r, "id");
            if (childrenByParent.TryGetValue(spanId, out var children))
            {
                foreach (var child in children)
                    WriteTraceEntry(w, child, childrenByParent, fullPayloads, depth + 1);
            }
            w.WriteLine();
            return;
        }

        if (string.Equals(source, "event", StringComparison.Ordinal))
        {
            var eventType = Get(r, "event_type");
            if (string.Equals(eventType, "agent_message", StringComparison.Ordinal))
                FormatAgentMessageEntry(w, r, indent);
            else
                FormatToolCallEntry(w, r, indent);
            return;
        }

        FormatRequestEntry(w, r, fullPayloads, indent);
    }

    private static void FormatSpanEntry(StringWriter w, Dictionary<string, object?> r, string indent)
    {
        var agentName = Get(r, "agent_name");
        var agentType = Get(r, "agent_type");
        var startedAt = Get(r, "started_at");
        var timeOnly = startedAt;
        if (DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            timeOnly = parsed.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        var costStr = "";
        var cost = Get(r, "_total_cost_usd");
        if (!(string.Equals(cost, "-", StringComparison.Ordinal)) && double.TryParse(cost, CultureInfo.InvariantCulture, out var costVal) && costVal > 0)
            costStr = $" ~${costVal:F4}";

        w.WriteLine($"{indent}─── SPAN: \"{agentName}\" ({agentType}) ── started: {timeOnly}{costStr} ───");
    }

    private static void FormatToolCallEntry(StringWriter w, Dictionary<string, object?> r, string indent)
    {
        var eventDataJson = Get(r, "event_data_json");
        var createdAt = Get(r, "created_at");
        string toolName = "-", input = "-", output = "-";

        if (!(string.Equals(eventDataJson, "-", StringComparison.Ordinal)))
        {
            try
            {
                using var doc = JsonDocument.Parse(eventDataJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("tool", out var t)) toolName = t.GetString() ?? "-";
                if (root.TryGetProperty("input", out var i)) input = i.GetString() ?? "-";
                if (root.TryGetProperty("output", out var o)) output = o.GetString() ?? "-";
            }
            catch { /* best effort */ }
        }

        w.WriteLine($"{indent}--- [tool_call] {toolName} | {createdAt} ---");
        if (!(string.Equals(input, "-", StringComparison.Ordinal))) w.WriteLine($"{indent}  input: {input}");
        if (!(string.Equals(output, "-", StringComparison.Ordinal))) w.WriteLine($"{indent}  output: {output}");
        w.WriteLine();
    }

    private static void FormatAgentMessageEntry(StringWriter w, Dictionary<string, object?> r, string indent)
    {
        var eventDataJson = Get(r, "event_data_json");
        var createdAt = Get(r, "created_at");
        string toolName = "SendMessage", recipient = "-", input = "-", output = "-";

        if (!(string.Equals(eventDataJson, "-", StringComparison.Ordinal)))
        {
            try
            {
                using var doc = JsonDocument.Parse(eventDataJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("tool", out var t)) toolName = t.GetString() ?? "SendMessage";
                if (root.TryGetProperty("recipient", out var recv)) recipient = recv.GetString() ?? "-";
                if (root.TryGetProperty("input", out var i)) input = i.GetString() ?? "-";
                if (root.TryGetProperty("output", out var o)) output = o.GetString() ?? "-";
            }
            catch { /* best effort */ }
        }

        var recipientLabel = !(string.Equals(recipient, "-", StringComparison.Ordinal)) ? $" -> {recipient}" : "";
        w.WriteLine($"{indent}--- [message] {toolName}{recipientLabel} | {createdAt} ---");
        if (!(string.Equals(input, "-", StringComparison.Ordinal))) w.WriteLine($"{indent}  sent: {input}");
        if (!(string.Equals(output, "-", StringComparison.Ordinal))) w.WriteLine($"{indent}  received: {output}");
        w.WriteLine();
    }

    private static void FormatRequestEntry(StringWriter w, Dictionary<string, object?> r, bool fullPayloads, string indent)
    {
        var id = Get(r, "id");
        var idShort = id.Length > 8 ? id[..8] : id;
        w.WriteLine($"{indent}--- {idShort} | {Get(r, "caller_surface")} | {Get(r, "operation")} | {Get(r, "created_at")} | {Get(r, "total_elapsed_ms")}ms | {Get(r, "status")} ---");

        var inPayload = Get(r, "inbound_payload");
        var inType = Get(r, "inbound_content_type");
        var inTokens = Get(r, "inbound_token_count");
        if (!(string.Equals(inPayload, "-", StringComparison.Ordinal)))
        {
            w.WriteLine($"{indent}  >> INBOUND ({inType}, ~{inTokens} tokens)");
            w.WriteLine($"{indent}  {inPayload}");
            if (!fullPayloads && inPayload.Length >= 500)
                w.WriteLine($"{indent}  [truncated at 500 chars, use --full for complete payload]");
        }
        else
        {
            var query = Get(r, "query_text");
            var parms = Get(r, "parameters_json");
            if (!(string.Equals(query, "-", StringComparison.Ordinal)) || !(string.Equals(parms, "-", StringComparison.Ordinal)))
            {
                w.WriteLine($"{indent}  >> INBOUND (metadata)");
                if (!(string.Equals(query, "-", StringComparison.Ordinal))) w.WriteLine($"{indent}  query: {query}");
                if (!(string.Equals(parms, "-", StringComparison.Ordinal))) w.WriteLine($"{indent}  params: {parms}");
            }
        }

        var outPayload = Get(r, "outbound_payload");
        var outType = Get(r, "outbound_content_type");
        var outTokens = Get(r, "outbound_token_count");
        if (!(string.Equals(outPayload, "-", StringComparison.Ordinal)))
        {
            w.WriteLine($"{indent}  << OUTBOUND ({outType}, ~{outTokens} tokens)");
            w.WriteLine($"{indent}  {outPayload}");
            if (!fullPayloads && outPayload.Length >= 500)
                w.WriteLine($"{indent}  [truncated at 500 chars, use --full for complete payload]");
        }
        else
        {
            w.WriteLine($"{indent}  << OUTBOUND [no payload recorded]");
        }

        w.WriteLine();
    }

    /// <summary>
    /// Formats agent session runs with aggregate info.
    /// </summary>
    public static string FormatRuns(List<Dictionary<string, object?>> rows, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(rows, ScopeJsonSerializerContext.Default.ListDictionaryStringObject);
        if (rows.IsNullOrEmpty) return "No runs found.";
        using var w = new StringWriter();
        w.WriteLine("# Recent Runs");
        w.WriteLine();
        foreach (var r in rows)
        {
            var key = Get(r, "session_key");
            var keyShort = key.Length > 12 ? key[^12..] : key;
            var parentKey = Get(r, "parent_session_key");
            var parentSuffix = !(string.Equals(parentKey, "-", StringComparison.Ordinal)) ? $"  parent: ...{(parentKey.Length > 12 ? parentKey[^12..] : parentKey)}" : "";
            w.WriteLine($"  ...{keyShort}  {Get(r, "agent_type")}/{Get(r, "agent_subtype")}{parentSuffix}");
            w.WriteLine($"    requests: {Get(r, "request_count")}  ops: {Get(r, "operations")}");
            w.WriteLine($"    first: {Get(r, "first_seen_at")}  last: {Get(r, "last_seen_at")}");
            w.WriteLine();
        }
        return w.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats dispatch entries with request counts.
    /// </summary>
    public static string FormatDispatches(List<Dictionary<string, object?>> rows, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(rows, ScopeJsonSerializerContext.Default.ListDictionaryStringObject);
        if (rows.IsNullOrEmpty) return "No dispatches found.";
        using var w = new StringWriter();
        w.WriteLine("# Dispatches");
        w.WriteLine();
        foreach (var r in rows)
        {
            var id = Get(r, "id");
            var idShort = id.Length > 8 ? id[..8] : id;
            w.WriteLine($"  {idShort}  \"{Get(r, "label")}\"  session: {Get(r, "session_key")}");
            w.WriteLine($"    requests: {Get(r, "request_count")}  created: {Get(r, "created_at")}  completed: {Get(r, "completed_at")}");
            w.WriteLine();
        }
        return w.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats agent span entries as a tree view showing parent-child nesting.
    /// Spans with null parent_span_id are roots; children are indented under their parents.
    /// </summary>
    public static string FormatAgentSpans(List<Dictionary<string, object?>> rows, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(rows, ScopeJsonSerializerContext.Default.ListDictionaryStringObject);

        if (rows.IsNullOrEmpty)
            return "No agent spans found.";

        // Build lookup: parent_span_id -> children, and collect all span ids
        var spanIds = new HashSet<string>(StringComparer.Ordinal);
        var childrenByParent = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
        var roots = new List<Dictionary<string, object?>>();

        foreach (var r in rows)
        {
            var id = Get(r, "id");
            if (!(string.Equals(id, "-", StringComparison.Ordinal))) spanIds.Add(id);
        }

        foreach (var r in rows)
        {
            var parentId = Get(r, "parent_span_id");
            if (string.Equals(parentId, "-", StringComparison.Ordinal) || !spanIds.Contains(parentId))
            {
                roots.Add(r);
            }
            else
            {
                if (!childrenByParent.TryGetValue(parentId, out var list))
                {
                    list = [];
                    childrenByParent[parentId] = list;
                }
                list.Add(r);
            }
        }

        using var w = new StringWriter();
        w.WriteLine("# Agent Spans");
        w.WriteLine();

        foreach (var root in roots)
            WriteSpanNode(w, root, childrenByParent, indentLevel: 0);

        return w.ToString().TrimEnd();
    }

    private static void WriteSpanNode(
        StringWriter w,
        Dictionary<string, object?> span,
        Dictionary<string, List<Dictionary<string, object?>>> childrenByParent,
        int indentLevel)
    {
        var indent = new string(' ', 2 + indentLevel * 2);
        var detailIndent = indent + "  ";

        var id = Get(span, "id");
        var idShort = id.Length > 10 ? id[..10] : id;
        var agentType = Get(span, "agent_type");
        var agentName = Get(span, "agent_name");

        w.WriteLine($"{indent}{idShort}... {agentType} \"{agentName}\"");

        var startedAt = Get(span, "started_at");
        var completedAt = Get(span, "completed_at");
        var status = Get(span, "status");

        if (!(string.Equals(completedAt, "-", StringComparison.Ordinal)) && !(string.Equals(startedAt, "-", StringComparison.Ordinal)))
        {
            var durationText = FormatElapsed(startedAt, completedAt);
            w.WriteLine($"{detailIndent}started: {startedAt}  completed: {durationText}  status: {status}");
        }
        else
        {
            w.WriteLine($"{detailIndent}started: {startedAt}  status: {status}");
        }

        // Recurse into children
        if (!(string.Equals(id, "-", StringComparison.Ordinal)) && childrenByParent.TryGetValue(id, out var children))
        {
            foreach (var child in children)
                WriteSpanNode(w, child, childrenByParent, indentLevel + 1);
        }
    }

    private static string FormatElapsed(string startedAt, string completedAt)
    {
        if (DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var start) && DateTimeOffset.TryParse(completedAt, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var end))
        {
            var elapsed = end - start;
            return elapsed.TotalSeconds >= 1.0
                ? $"{elapsed.TotalSeconds:F1}s"
                : $"{elapsed.TotalMilliseconds:F0}ms";
        }
        return completedAt;
    }

    /// <summary>
    /// Formats export data as JSON. Always uses JSON format regardless of the format argument.
    /// </summary>
    public static string FormatExport(Dictionary<string, object?> exportData, string format)
    {
        return JsonSerializer.Serialize(exportData, ScopeJsonSerializerContext.Default.DictionaryStringObject);
    }

    /// <summary>
    /// Formats a session tree (from GetSessionTree) as an indented hierarchy.
    /// Each row has a _depth field indicating its level in the tree.
    /// </summary>
    public static string FormatSessionTree(List<Dictionary<string, object?>> rows, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(rows, ScopeJsonSerializerContext.Default.ListDictionaryStringObject);

        if (rows.IsNullOrEmpty)
            return "No sessions found in tree.";

        using var w = new StringWriter();
        w.WriteLine("# Session Tree");
        w.WriteLine();

        foreach (var r in rows)
        {
            var depthVal = r.TryGetValue("_depth", out var dv) ? dv : 0;
            var depth = depthVal is int d ? d : 0;
            var indent = new string(' ', 2 + depth * 2);
            var key = Get(r, "session_key");
            var keyShort = key.Length > 16 ? $"...{key[^12..]}" : key;

            w.WriteLine($"{indent}{keyShort}  {Get(r, "agent_type")}/{Get(r, "agent_subtype")}");
            w.WriteLine($"{indent}  requests: {Get(r, "request_count")}  first: {Get(r, "first_seen_at")}  last: {Get(r, "last_seen_at")}");
        }

        return w.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats a session rollup (from GetSessionRollup) showing aggregated counts and costs.
    /// </summary>
    public static string FormatSessionRollup(Dictionary<string, object?> rollup, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(rollup, ScopeJsonSerializerContext.Default.DictionaryStringObject);

        if (rollup.ContainsKey("error"))
            return Get(rollup, "error");

        using var w = new StringWriter();
        w.WriteLine("# Session Rollup");
        w.WriteLine();
        w.WriteLine($"  Session:          {Get(rollup, "session_key")}");
        w.WriteLine($"  Sessions in tree: {GetVal(rollup, "session_count")}");
        w.WriteLine($"  Total requests:   {GetVal(rollup, "total_request_count")}");
        w.WriteLine($"  Total cost:       ${GetVal(rollup, "total_cost_usd")}");

        if (rollup.TryGetValue("children", out var childrenObj) && childrenObj is List<string> children && children.Count > 0)
        {
            w.WriteLine($"  Direct children:  {children.Count}");
            foreach (var child in children)
            {
                var childShort = child.Length > 16 ? $"...{child[^12..]}" : child;
                w.WriteLine($"    {childShort}");
            }
        }

        return w.ToString().TrimEnd();
    }

    private static string Get(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var val) && val != null ? val.ToString() ?? "-" : "-";

    private static string GetVal(Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var val) && val != null ? val.ToString() ?? "-" : "-";
}

#pragma warning restore MA0016
