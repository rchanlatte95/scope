namespace Scope.Core.Telemetry.Trace;

using System.Text.Json.Serialization;

// Public contract preserved (T-2026-06-17 sweep, owner): scope-trace-v1 PACKET-tier wire DTOs
// with pinned [JsonPropertyName] fields. Content-free-by-construction uplink shape guarded by
// PacketContentFreeTests; concrete List<T> collection types are part of the serialized shape.
#pragma warning disable MA0016 // public scope-trace-v1 packet contract; concrete collection types are the wire shape

// =============================================================================
// scope-trace-v1 PACKET TIER DTOs.
//
// THE ONLY SHAPE THAT MAY LEAVE THE MACHINE (opt-in uplink ST-17, not built yet).
// Content-free BY CONSTRUCTION: this is a SEPARATE closed record set with no
// free-text field to bind to. Every string-typed field is one of: a fixed enum,
// a Scope/provider opaque id (UUIDv7 hex, toolu_*, agent_ref hex, model id), a
// hash hex string, a finite-vocabulary tool name, or an ISO-8601 timestamp.
//
// DO NOT add any NEVER-list field (design-scope-trace-v1.xml neverList): no
// label, agent_name, query_text, input/output summaries or snippets,
// error_message, parameters_json, event_data_json, file paths, command lines.
//
// Enforcement: PacketContentFreeTests walks the source-gen metadata of every
// type reachable from these roots and asserts no NEVER-name property exists, and
// a round-trip test asserts every string value matches the identifier grammar.
//
// Reuses content-free shared primitives from TraceContract.cs (Money, TokenBlock,
// Coverage, ToolRef, Stage, TraceSubject, TraceWindow, MoneyAggregate,
// TokenAggregate, TimeCoverage, RollupTotals, RollupRow, RollupBody,
// TopActionRow, TopActionsBody, GraphEdgeDto, WireBody, WireCallDto,
// TraceGenerator, TracePage, TraceEnvelope<T>).
// =============================================================================

/// <summary>Packet Session: identifiers, enums, counts, timestamps only. No label.</summary>
public sealed record PacketSession(
    // Raw key only when UUID-shaped (else null; surrogate carries identity, NEVER 11).
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("surrogate_id")] string? SurrogateId,
    [property: JsonPropertyName("display_id")] string DisplayId,
    [property: JsonPropertyName("agent_type")] string? AgentType,
    [property: JsonPropertyName("agent_subtype")] string? AgentSubtype,
    [property: JsonPropertyName("first_seen_at")] string? FirstSeenAt,
    [property: JsonPropertyName("last_seen_at")] string? LastSeenAt,
    [property: JsonPropertyName("request_count")] long RequestCount,
    [property: JsonPropertyName("event_count")] long EventCount,
    [property: JsonPropertyName("parent_session_id")] string? ParentSessionId,
    [property: JsonPropertyName("depth")] int? Depth);

/// <summary>Packet sessions_list body.</summary>
public sealed record PacketSessionsListBody(
    [property: JsonPropertyName("sessions")] List<PacketSession> Sessions);

/// <summary>Packet graph node: NO label field (NEVER 6). agent_type + alias identify the node.</summary>
public sealed record PacketGraphNode(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_id")] string DisplayId,
    [property: JsonPropertyName("alias")] string? Alias,
    [property: JsonPropertyName("agent_type")] string? AgentType,
    [property: JsonPropertyName("request_count")] int RequestCount,
    [property: JsonPropertyName("cost")] Money? Cost,
    [property: JsonPropertyName("depth")] int Depth);

/// <summary>Packet session_graph body.</summary>
public sealed record PacketSessionGraphBody(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("nodes")] List<PacketGraphNode> Nodes,
    [property: JsonPropertyName("edges")] List<GraphEdgeDto> Edges);

/// <summary>
/// Packet trace entry. Discriminated by Kind. NO content fields exist on this type:
/// no label, no summaries, no snippets, no query_text, no parameters_json, no error_message,
/// no message_recipient. Only enumerated identifier/enum/count/timestamp fields.
/// </summary>
public sealed record PacketTraceEntry
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("display_id")] public required string DisplayId { get; init; }
    [JsonPropertyName("alias")] public string? Alias { get; init; }
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    [JsonPropertyName("dispatch_id")] public string? DispatchId { get; init; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; init; }

    [JsonPropertyName("span_id")] public string? SpanId { get; init; }
    [JsonPropertyName("resolved_span_id")] public string? ResolvedSpanId { get; init; }
    [JsonPropertyName("parent_span_id")] public string? ParentSpanId { get; init; }
    [JsonPropertyName("resolved_parent_span_id")] public string? ResolvedParentSpanId { get; init; }

    [JsonPropertyName("agent_type")] public string? AgentType { get; init; }
    [JsonPropertyName("agent_ref")] public string? AgentRef { get; init; }
    [JsonPropertyName("started_at")] public string? StartedAt { get; init; }
    [JsonPropertyName("completed_at")] public string? CompletedAt { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("time_source")] public string? TimeSource { get; init; }
    [JsonPropertyName("tokens")] public TokenBlock? Tokens { get; init; }
    [JsonPropertyName("rollup_cost")] public Money? RollupCost { get; init; }
    // Resolved model + provenance (OBS-11). Content-free by construction: a provider model id and a
    // closed provenance enum (wire|recorded|unavailable), no prose. Safe in the packet (uplink) tier.
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("model_provenance")] public string? ModelProvenance { get; init; }

    [JsonPropertyName("event_type")] public string? EventType { get; init; }
    [JsonPropertyName("tool")] public ToolRef? Tool { get; init; }
    [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; init; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; init; }
    [JsonPropertyName("request_id")] public string? RequestId { get; init; }
    [JsonPropertyName("caps")] public List<string>? Caps { get; init; }

    [JsonPropertyName("parent_request_id")] public string? ParentRequestId { get; init; }
    [JsonPropertyName("caller_surface")] public string? CallerSurface { get; init; }
    [JsonPropertyName("operation")] public string? Operation { get; init; }
    [JsonPropertyName("exit_code")] public int? ExitCode { get; init; }
    [JsonPropertyName("total_elapsed_ms")] public long? TotalElapsedMs { get; init; }
    [JsonPropertyName("stages")] public List<Stage>? Stages { get; init; }
    [JsonPropertyName("cost")] public Money? Cost { get; init; }
    [JsonPropertyName("token_estimate")] public TokenBlock? TokenEstimate { get; init; }
}

/// <summary>Packet trace body.</summary>
public sealed record PacketTraceBody(
    [property: JsonPropertyName("subject")] TraceSubject Subject,
    [property: JsonPropertyName("entries")] List<PacketTraceEntry> Entries);

// Rollup, TopActions, and Wire bodies are already content-free; the packet tier
// reuses RollupBody / TopActionsBody / WireBody verbatim. They are listed in the
// packet root set in PacketContentFreeTests so the CI guard walks them.

#pragma warning restore MA0016
