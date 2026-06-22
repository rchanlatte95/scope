namespace Scope.Core.Telemetry.Trace;

using System.Text.Json;
using System.Globalization;

// Public contract preserved (T-2026-06-17 sweep, owner): the public TraceProjection methods return
// concrete List<T>/Dictionary<K,V> shapes consumed by the trace-document builders; keeping the
// concrete return types avoids a public-API signature change.
#pragma warning disable MA0016 // public Scope.Core projection API; concrete return collection types preserved

/// <summary>
/// Projects the loosely-typed row dictionaries returned by <see cref="ObservabilityAuditDb"/> into
/// the typed scope-trace-v1 DTOs. Pure functions over already-fetched rows: no database access,
/// no clock, no randomness. Display ids and aliases derive only from stored columns (sort order),
/// never render order, per design-scope-trace-v1.xml idScheme.
/// </summary>
public static class TraceProjection
{
    // -----------------------------------------------------------------------
    // Sessions
    // -----------------------------------------------------------------------

    /// <summary>
    /// Projects session rows (from GetSessionsWithEventCounts / GetSessionTree) to local-full sessions.
    /// Preserves the query's row order (does not re-sort). Emits <c>HasData</c> and <c>IsActive</c>
    /// from the correlated sub-query columns added in T-2026-06-15-15. When those columns are absent
    /// (e.g. GetSessionTree rows or legacy callers), defaults: HasData=true (conservative; avoids
    /// disabling sessions whose data status is unknown), IsActive=false.
    /// </summary>
    public static List<TraceSession> Sessions(IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var result = new List<TraceSession>(rows.Count);
        foreach (var r in rows)
        {
            var key = Str(r, "session_key") ?? "";
            var eventCount = Long(r, "event_count") ?? 0;
            var spanCount = Long(r, "span_count") ?? 0;
            var hasCost = (Long(r, "has_cost") ?? 0) != 0;
            // has_data column present when from GetSessionsWithEventCounts; absent from GetSessionTree.
            // When absent, fall back to conservative true so sessions from other callers are never
            // silently disabled. When present, compute: event_count>0 OR span_count>0 OR has_cost.
            bool hasData;
            if (r.ContainsKey("has_cost"))
                hasData = eventCount > 0 || spanCount > 0 || hasCost;
            else
                hasData = true;
            var isActive = (Long(r, "is_active") ?? 0) != 0;
            result.Add(new TraceSession(
                Id: key,
                DisplayId: TraceIdCodec.DisplayId(key),
                Host: Str(r, "agent_type"),
                AgentType: Str(r, "agent_type"),
                AgentSubtype: Str(r, "agent_subtype"),
                FirstSeenAt: Str(r, "first_seen_at"),
                LastSeenAt: Str(r, "last_seen_at"),
                RequestCount: Long(r, "request_count") ?? 0,
                EventCount: eventCount,
                ParentSessionId: Str(r, "parent_session_key"),
                Depth: Int(r, "_depth"),
                HasData: hasData,
                IsActive: isActive,
                // deleted_at present from GetSessionsWithEventCounts (always selected); absent (null)
                // from GetSessionTree rows and legacy callers. Surfaced for the trash view.
                DeletedAt: Str(r, "deleted_at"),
                // cc_session_id (T-2026-06-19-04): the host conversation id, present from
                // GetSessionsWithEventCounts when the obs schema is v11+ (EnsureSchema-guarded);
                // absent (null) from GetSessionTree rows, legacy callers, or a pre-v11 fallback read.
                CcSessionId: Str(r, "cc_session_id")));
        }
        return result;
    }

    /// <summary>Projects local-full sessions to the content-free packet tier.</summary>
    public static List<PacketSession> ToPacket(IReadOnlyList<TraceSession> sessions)
    {
        var result = new List<PacketSession>(sessions.Count);
        foreach (var s in sessions)
        {
            var uuidShaped = TraceIdCodec.IsUuidShaped(s.Id);
            result.Add(new PacketSession(
                Id: uuidShaped ? s.Id : null,
                SurrogateId: TraceIdCodec.Surrogate(s.Id),
                DisplayId: s.DisplayId,
                AgentType: s.AgentType,
                AgentSubtype: s.AgentSubtype,
                FirstSeenAt: s.FirstSeenAt,
                LastSeenAt: s.LastSeenAt,
                RequestCount: s.RequestCount,
                EventCount: s.EventCount,
                ParentSessionId: TraceIdCodec.PacketSessionId(s.ParentSessionId),
                Depth: s.Depth));
        }
        return result;
    }

    // -----------------------------------------------------------------------
    // Trace entries (request | span | event)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Projects a merged GetFullTrace entry list into local-full trace entries, computing
    /// span ordinals and event seq aliases from stored sort order within the resolved span tree.
    /// </summary>
    /// <param name="pricingVersion">
    /// The resolved "prices as of" stamp threaded onto each span's <c>rollup_cost</c> Money block, or
    /// null when no pricing config is configured. A date string is content-free (a timestamp, not
    /// content) so it is safe in both tiers.
    /// </param>
    /// <param name="costByEntryId">
    /// Optional per-entry attributed cost (T-2026-06-20-03, OBS-10) keyed by trace-entry id, produced by
    /// <see cref="WireCostAttribution.Attribute"/> from the SAME merged rows. When supplied, the
    /// matching span/event/request entry's <c>cost</c> field is populated with a <see cref="Money"/>
    /// carrying the attributed value + 6-value provenance, so the timeline rows show a legible cost
    /// whose SUM equals the trace total. When null, no per-row cost is populated (legacy behavior).
    /// </param>
    public static List<TraceEntry> Entries(
        IReadOnlyList<Dictionary<string, object?>> rows,
        string? pricingVersion = null,
        IReadOnlyDictionary<string, WireCostAttribution.AttributedCost>? costByEntryId = null)
    {
        var aliases = ComputeAliases(rows);
        var result = new List<TraceEntry>(rows.Count);
        foreach (var r in rows)
        {
            var source = Str(r, "_source");
            var id = Str(r, "id") ?? "";
            var alias = aliases.GetValueOrDefault(id);
            var cost = costByEntryId != null && costByEntryId.TryGetValue(id, out var c) ? AttributedMoney(c) : null;
            result.Add(source switch
            {
                "request" => RequestEntry(r) with { Cost = cost },
                "span" => SpanEntry(r, alias, pricingVersion) with { Cost = cost },
                "event" => EventEntry(r, alias) with { Cost = cost },
                _ => new TraceEntry { Kind = "unknown", Id = id, DisplayId = TraceIdCodec.DisplayId(id) },
            });
        }
        return result;
    }

    /// <summary>
    /// Builds the per-row <see cref="Money"/> from a <see cref="WireCostAttribution.AttributedCost"/>.
    /// The value may be null (the owning task's cost was unavailable): the row still carries the
    /// provenance so the FE renders the empty-value glyph + provenance chip, never a fabricated $0.00.
    /// </summary>
    private static Money AttributedMoney(WireCostAttribution.AttributedCost c)
        => new(
            ValueUsd: c.ValueUsd,
            Source: c.MoneySource,
            PricingVersion: c.ValueUsd.HasValue ? c.PricingVersion : null,
            PricedAt: null,
            ModelId: null,
            Provider: null,
            CostProvenance: c.CostProvenance);

    /// <summary>Strips local-full content fields, producing the content-free packet entry list.</summary>
    public static List<PacketTraceEntry> ToPacketEntries(IReadOnlyList<TraceEntry> entries)
    {
        var result = new List<PacketTraceEntry>(entries.Count);
        foreach (var e in entries)
        {
            result.Add(new PacketTraceEntry
            {
                Kind = e.Kind,
                Id = e.Id,
                DisplayId = e.DisplayId,
                Alias = e.Alias,
                SessionId = TraceIdCodec.PacketSessionId(e.SessionId),
                DispatchId = e.DispatchId,
                CreatedAt = e.CreatedAt,
                SpanId = e.SpanId,
                ResolvedSpanId = e.ResolvedSpanId,
                ParentSpanId = e.ParentSpanId,
                ResolvedParentSpanId = e.ResolvedParentSpanId,
                AgentType = e.AgentType,
                AgentRef = e.AgentRef,
                StartedAt = e.StartedAt,
                CompletedAt = e.CompletedAt,
                Status = e.Status,
                TimeSource = e.TimeSource,
                Tokens = e.Tokens,
                RollupCost = e.RollupCost,
                Model = e.Model,
                ModelProvenance = e.ModelProvenance,
                EventType = e.EventType,
                Tool = e.Tool,
                ToolUseId = e.ToolUseId,
                DurationMs = e.DurationMs,
                RequestId = e.RequestId,
                Caps = e.Caps,
                ParentRequestId = e.ParentRequestId,
                CallerSurface = e.CallerSurface,
                Operation = e.Operation,
                ExitCode = e.ExitCode,
                TotalElapsedMs = e.TotalElapsedMs,
                Stages = e.Stages,
                Cost = e.Cost,
                TokenEstimate = e.TokenEstimate,
            });
        }
        return result;
    }

    private static TraceEntry RequestEntry(Dictionary<string, object?> r)
    {
        var id = Str(r, "id") ?? "";
        return new TraceEntry
        {
            Kind = "request",
            Id = id,
            DisplayId = TraceIdCodec.DisplayId(id),
            SessionId = Str(r, "agent_session_key"),
            DispatchId = Str(r, "dispatch_id"),
            CreatedAt = Str(r, "created_at"),
            ResolvedSpanId = Str(r, "_parent_span_id"),
            ParentRequestId = Str(r, "parent_request_id"),
            CallerSurface = Str(r, "caller_surface"),
            Operation = Str(r, "operation"),
            Status = Str(r, "status"),
            ExitCode = Int(r, "exit_code"),
            TotalElapsedMs = Long(r, "total_elapsed_ms"),
            CompletedAt = Str(r, "completed_at"),
            // local-full content
            QueryText = Str(r, "query_text"),
            ParametersJson = Str(r, "parameters_json"),
            ErrorMessage = Str(r, "error_message"),
        };
    }

    private static TraceEntry SpanEntry(Dictionary<string, object?> r, string? alias, string? pricingVersion)
    {
        var id = Str(r, "id") ?? "";
        var started = Str(r, "started_at");
        var completed = Str(r, "completed_at");
        var tokens = SpanTokens(r);
        var rollupCost = Double(r, "_total_cost_usd");
        return new TraceEntry
        {
            Kind = "span",
            Id = id,
            DisplayId = TraceIdCodec.DisplayId(id),
            Alias = alias,
            SessionId = Str(r, "session_key"),
            DispatchId = Str(r, "dispatch_id"),
            CreatedAt = started,
            ParentSpanId = Str(r, "parent_span_id"),
            ResolvedParentSpanId = Str(r, "_parent_span_id") ?? Str(r, "parent_span_id"),
            AgentType = Str(r, "agent_type"),
            AgentRef = Str(r, "agent_ref"),
            StartedAt = started,
            CompletedAt = completed,
            Status = Str(r, "status"),
            TimeSource = TimeSourceOf(started, completed),
            Tokens = tokens,
            // DB boundary (T-2026-06-20-20 Piece 2): _total_cost_usd is a SQLite REAL (double); convert
            // to decimal for the Money.value_usd field (serialized as a JSON number, shape unchanged).
            RollupCost = rollupCost is > 0
                ? new Money((decimal)rollupCost.Value, "estimate", PricingVersion: pricingVersion, PricedAt: null, ModelId: null, Provider: null)
                : null,
            // Resolved model + provenance (OBS-11): stamped onto the row by ObservabilityAuditDb's
            // ResolveSpanModels (wire-recovered for inherited dispatches, else recorded, else unavailable).
            // Null model with provenance=unavailable when genuinely unknown -- never a fabricated default.
            Model = Str(r, "_resolved_model"),
            ModelProvenance = Str(r, "_resolved_model_provenance"),
            // local-full content
            Label = Str(r, "agent_name"),
            InputSummary = Str(r, "input_summary"),
            OutputSummary = Str(r, "output_summary"),
        };
    }

    private static TraceEntry EventEntry(Dictionary<string, object?> r, string? alias)
    {
        var id = Str(r, "id") ?? "";
        var eventType = Str(r, "event_type");
        var isUserSubmit = string.Equals(eventType, "user_submit", StringComparison.Ordinal);

        // For user_submit events, event_data_json holds the raw content verbatim (not a JSON
        // object), so ParseEventData must be skipped to avoid a JsonException.
        var ed = isUserSubmit ? default : ParseEventData(r);
        var rawTool = ed.Tool;
        var caps = ed.Caps;

        // InputSnippet for user_submit: use event_data_json as the prompt content.
        // Trim to 500 chars for the card snippet; full content stays in the DB.
        string? userSubmitSnippet = null;
        if (isUserSubmit)
        {
            var raw = Str(r, "event_data_json");
            if (!string.IsNullOrEmpty(raw))
                userSubmitSnippet = raw.Length <= 500 ? raw : raw[..500] + "…";
        }

        return new TraceEntry
        {
            Kind = "event",
            Id = id,
            DisplayId = TraceIdCodec.DisplayId(id),
            Alias = alias,
            SessionId = Str(r, "session_key"),
            DispatchId = ed.DispatchId,
            CreatedAt = Str(r, "created_at"),
            SpanId = Str(r, "span_id"),
            ResolvedSpanId = Str(r, "_parent_span_id") ?? Str(r, "span_id"),
            EventType = eventType,
            // Tool-call status (schema v14, OBS-12): the heuristic outcome (success/failed) the adapter
            // derived from tool_response, surfaced on the event row. NULL for non-tool_call events and
            // pre-v14 databases that predate the column. Reuses the shared TraceEntry.Status field (spans
            // carry their lifecycle status there; events carry their tool-call outcome).
            Status = Str(r, "status"),
            // user_submit_subtype and turn_index: from schema-v10 columns (ST-01 / ST-05).
            UserSubmitSubtype = isUserSubmit ? Str(r, "event_subtype") : null,
            TurnIndex = isUserSubmit ? Int(r, "turn_index") : null,
            Tool = rawTool != null
                ? new ToolRef(rawTool, ToolTaxonomy.Resolve(rawTool), ToolTaxonomy.Version)
                : null,
            ToolUseId = ed.ToolUseId,
            DurationMs = ed.DurationMs,
            AgentRef = ed.AgentId,
            RequestId = Str(r, "request_id"),
            Caps = caps,
            CapsUniverse = ed.CapsUniverse,
            // local-full content
            InputSnippet = isUserSubmit ? userSubmitSnippet : ed.Input,
            OutputSnippet = ed.Output,
            MessageRecipient = ed.MessageRecipient,
        };
    }

    // -----------------------------------------------------------------------
    // Aliases: {ordinal} for spans, {ordinal}.{seq} for events. Computed from
    // stored sort order (started_at,id) / (created_at,id), per session/span.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Computes the alias for every span and event id in the trace. Spans get a 2-based ordinal
    /// (i+2) within their session ordered by (started_at, id), reserving ordinal 1 for the root
    /// agent. Events get "{ordinal}.{seq}" where seq is 1-based within the resolved span ordered
    /// by (created_at, id); events with no resolved span (unattached / root-agent events) use
    /// ordinal 1 ("1.N"). NO ordinal 0 is assigned anywhere.
    /// </summary>
    public static Dictionary<string, string> ComputeAliases(IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);

        // Span ordinals: group spans by session, sort by (started_at, id), assign 2-based
        // (i+2) so ordinal 1 is reserved for the root/unattached-event group.
        var spansBySession = new Dictionary<string, List<(string Id, string Sort)>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!(string.Equals(Str(r, "_source"), "span", StringComparison.Ordinal))) continue;
            var id = Str(r, "id");
            if (id == null) continue;
            var session = Str(r, "session_key") ?? "";
            var sort = (Str(r, "started_at") ?? "") + "|" + id;
            (spansBySession.TryGetValue(session, out var list) ? list : spansBySession[session] = []).Add((id, sort));
        }

        var spanOrdinal = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var list in spansBySession.Values)
        {
            list.Sort((a, b) => string.CompareOrdinal(a.Sort, b.Sort));
            for (var i = 0; i < list.Count; i++)
            {
                spanOrdinal[list[i].Id] = i + 2;
                aliases[list[i].Id] = (i + 2).ToString(CultureInfo.InvariantCulture);
            }
        }

        // Event seqs: group events by (session, resolvedSpanId), sort by (created_at, id).
        var eventsByGroup = new Dictionary<(string Session, string Span), List<(string Id, string Sort)>>();
        foreach (var r in rows)
        {
            if (!(string.Equals(Str(r, "_source"), "event", StringComparison.Ordinal))) continue;
            var id = Str(r, "id");
            if (id == null) continue;
            var session = Str(r, "session_key") ?? "";
            var resolvedSpan = Str(r, "_parent_span_id") ?? Str(r, "span_id") ?? "";
            var sort = (Str(r, "created_at") ?? "") + "|" + id;
            var key = (session, resolvedSpan);
            (eventsByGroup.TryGetValue(key, out var list) ? list : eventsByGroup[key] = []).Add((id, sort));
        }

        foreach (var ((_, span), list) in eventsByGroup)
        {
            list.Sort((a, b) => string.CompareOrdinal(a.Sort, b.Sort));
            // Unattached (root-agent) events use ordinal 1; events under a span use the span's ordinal.
            var ordinal = span.Length > 0 && spanOrdinal.TryGetValue(span, out var o) ? o : 1;
            for (var i = 0; i < list.Count; i++)
                aliases[list[i].Id] = $"{ordinal}.{i + 1}";
        }

        return aliases;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    internal static string? TimeSourceOf(string? started, string? completed)
    {
        if (string.IsNullOrEmpty(started) || string.IsNullOrEmpty(completed)) return "unknown";
        return string.Equals(started, completed, StringComparison.Ordinal) ? "zero_duration" : "measured";
    }

    private static TokenBlock? SpanTokens(Dictionary<string, object?> r)
    {
        // Source precedence wire > exact (wire-metering ST-03). Wire tokens come from the join of
        // wire_calls.tool_use_ids to this span's descendant events (AnnotateWireTokens); they are the
        // span's DIRECT wire tokens. When a span has both wire and host-completion exact usage, wire
        // wins and exact is suppressed for that span -- the same work is reported once, never summed.
        var hasWire = (Str(r, "_wire_tokens") ?? "").Length > 0 && Long(r, "_wire_total_tokens").HasValue;
        if (hasWire)
        {
            return new TokenBlock(
                Input: Long(r, "_wire_input_tokens"),
                Output: Long(r, "_wire_output_tokens"),
                CacheCreation: Long(r, "_wire_cache_creation_tokens"),
                CacheRead: Long(r, "_wire_cache_read_tokens"),
                Total: Long(r, "_wire_total_tokens"),
                Source: "wire");
        }

        // Exact host-reported tokens are present only when AnnotateExactSpanTokens ran and the
        // span has v5 token columns. _token_source=="exact" guards it.
        var source = Str(r, "_token_source");
        if (!(string.Equals(source, "exact", StringComparison.Ordinal))) return null;
        var total = Long(r, "_total_tokens") ?? Long(r, "total_tokens");
        return new TokenBlock(
            Input: Long(r, "_input_tokens") ?? Long(r, "input_tokens"),
            Output: Long(r, "_output_tokens") ?? Long(r, "output_tokens"),
            CacheCreation: null,
            CacheRead: null,
            Total: total,
            Source: "exact");
    }

    private readonly record struct EventData(
        string? Tool, string? Input, string? Output, string? DispatchId,
        string? ToolUseId, long? DurationMs, string? AgentId,
        List<string>? Caps, List<string>? CapsUniverse, string? MessageRecipient);

    private static EventData ParseEventData(Dictionary<string, object?> r)
    {
        var raw = Str(r, "event_data_json");
        if (string.IsNullOrEmpty(raw)) return default;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string? S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? L(string name)
            {
                if (!root.TryGetProperty(name, out var v)) return null;
                return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
            }
            var capsRaw = S("caps");
            var caps = string.IsNullOrEmpty(capsRaw)
                ? null
                : capsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var capsUniverseRaw = S("caps_universe");
            var capsUniverse = string.IsNullOrEmpty(capsUniverseRaw)
                ? null
                : capsUniverseRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            return new EventData(
                Tool: S("tool"),
                Input: S("input"),
                Output: S("output"),
                DispatchId: S("dispatch_id"),
                ToolUseId: S("tool_use_id"),
                DurationMs: L("duration_ms"),
                AgentId: S("agent_id"),
                Caps: caps,
                CapsUniverse: capsUniverse,
                MessageRecipient: S("recipient"));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    internal static string? Str(Dictionary<string, object?> r, string key)
        => r.TryGetValue(key, out var v) ? v?.ToString() : null;

    internal static long? Long(Dictionary<string, object?> r, string key)
    {
        if (!r.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            string s when long.TryParse(s, CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };
    }

    internal static int? Int(Dictionary<string, object?> r, string key)
    {
        var l = Long(r, key);
        return l.HasValue ? (int)l.Value : null;
    }

    internal static double? Double(Dictionary<string, object?> r, string key)
    {
        if (!r.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            double d => d,
            long l => l,
            int i => i,
            string s when double.TryParse(s, CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };
    }
}

#pragma warning restore MA0016
