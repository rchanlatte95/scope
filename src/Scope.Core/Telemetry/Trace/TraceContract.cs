namespace Scope.Core.Telemetry.Trace;

using System.Text.Json.Serialization;

// Public contract preserved (T-2026-06-17 sweep, owner): every type in this file is a
// scope-trace-v1 wire-contract DTO with pinned [JsonPropertyName] fields, source-gen
// serialized via ScopeJsonSerializerContext (CLI/MCP/HTTP response shapes). The concrete
// List<T> collection types are part of the serialized shape and must not change.
#pragma warning disable MA0016 // public scope-trace-v1 contract; concrete collection types are the wire shape

// =============================================================================
// scope-trace-v1 contract DTOs (local-full tier + shared content-free primitives).
//
// Authoritative spec: internal design notes
// These types are a faithful projection of the contract. Field names are pinned
// with [JsonPropertyName] so the wire shape matches the contract exactly.
//
// TWO TIERS:
//   - local-full DTOs (this file; content fields nullable) carry the content
//     fields (summaries, snippets, query text, labels). Served only on localhost /
//     written to local export files.
//   - packet DTOs (TracePacket.cs) are a SEPARATE closed record set that is
//     content-free BY CONSTRUCTION: no free-text field exists to bind to. The CI
//     guard test (PacketContentFreeTests) asserts this against the source-gen
//     metadata. Do not add NEVER-list fields to the packet types.
//
// The local-full tier reuses the packet-safe primitives (Money, TokenBlock,
// Coverage, ToolRef, Stage, TraceSubject, TraceWindow, the Rollup/TopActions/Wire
// shapes) and ADDS content fields. Both tiers share the envelope shape.
// =============================================================================

/// <summary>Literal schema/tier/kind constants for the contract.</summary>
public static class TraceSchema
{
    public const string Schema = "scope-trace-v1";
    public const string TierLocalFull = "local-full";
    public const string TierPacket = "packet";

    public const string KindTrace = "trace";
    public const string KindSessionsList = "sessions_list";
    public const string KindSessionGraph = "session_graph";
    public const string KindRollup = "rollup";
    public const string KindExport = "export";
    public const string KindTopActions = "top_actions";
    public const string KindWire = "wire";
    // ST-08: two-axis rollup kinds.
    public const string KindConversationRollup = "conversation_rollup";
    public const string KindCliSessionRollup = "cli_session_rollup";
}

// ---------------------------------------------------------------------------
// Envelope
// ---------------------------------------------------------------------------

/// <summary>Generator binary identity for forensics on schema drift.</summary>
public sealed record TraceGenerator(
    [property: JsonPropertyName("app")] string App,
    [property: JsonPropertyName("version")] string Version);

/// <summary>Keyset paging cursor for paged trace endpoints. Null for unpaged documents.</summary>
public sealed record TracePage(
    [property: JsonPropertyName("cursor")] string? Cursor,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("limit")] int Limit);

/// <summary>
/// The scope-trace-v1 envelope. Generic over the kind-discriminated body so one shape serves
/// every kind. Identity placeholders live here once, never repeated on nested objects.
/// </summary>
public sealed record TraceEnvelope<TBody>(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("generated_at")] string GeneratedAt,
    [property: JsonPropertyName("generator")] TraceGenerator Generator,
    [property: JsonPropertyName("machine_id")] string? MachineId,
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("org_id")] string? OrgId,
    [property: JsonPropertyName("taxonomy_version")] string TaxonomyVersion,
    [property: JsonPropertyName("page")] TracePage? Page,
    [property: JsonPropertyName("body")] TBody Body);

// ---------------------------------------------------------------------------
// Shared accounting primitives (identical shape in both tiers; content-free)
// ---------------------------------------------------------------------------

/// <summary>
/// Tagged monetary block. source is closed enum exact|wire|estimate|unknown; pricing_version is
/// the "prices as of" stamp (null with source=estimate until wire-metering ST-04, per Q1).
/// </summary>
public sealed record Money(
    // In-memory cost math is System.Decimal (T-2026-06-20-20 Piece 2) so reconciliation sums are
    // EXACT (sum == total, not "to ~9 digits"). decimal is the serialization boundary type:
    // System.Text.Json emits it as a JSON number, so the wire/FE shape is unchanged (FE still
    // receives a number). The only double<->decimal boundary is read-from-SQLite (REAL), converted
    // to decimal at the earliest point so all aggregation downstream is exact.
    [property: JsonPropertyName("value_usd")] decimal? ValueUsd,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("pricing_version")] string? PricingVersion,
    [property: JsonPropertyName("priced_at")] string? PricedAt,
    [property: JsonPropertyName("model_id")] string? ModelId,
    [property: JsonPropertyName("provider")] string? Provider,
    // ADDITIVE (T-2026-06-20-03, OBS-10): the 6-value cost provenance
    // (wire_exact | joined_exact | provider_estimated | scope_estimated | unmetered | unavailable)
    // for this monetary block. ORTHOGONAL to Source (the 4-value primitive source); carried through
    // unchanged from the canonical rollup when a per-row cost is attributed by WireCostAttribution.
    // Appended at the end with a default so existing positional construction and serialized shapes are
    // unaffected (null = no provenance label / legacy block). Mirrors MoneyAggregate.cost_provenance.
    [property: JsonPropertyName("cost_provenance")] string? CostProvenance = null);

/// <summary>
/// Tagged token block. input+output is NOT asserted to equal total (real v5 rows violate it
/// because host totals include cache and child-agent tokens). source is closed enum.
/// </summary>
public sealed record TokenBlock(
    [property: JsonPropertyName("input")] long? Input,
    [property: JsonPropertyName("output")] long? Output,
    [property: JsonPropertyName("cache_creation")] long? CacheCreation,
    [property: JsonPropertyName("cache_read")] long? CacheRead,
    [property: JsonPropertyName("total")] long? Total,
    [property: JsonPropertyName("source")] string Source);

/// <summary>Coverage counts attached to aggregate tagged blocks (known vs unknown constituents).</summary>
public sealed record Coverage(
    [property: JsonPropertyName("known")] int Known,
    [property: JsonPropertyName("unknown")] int Unknown);

/// <summary>Resolved tool reference on an event: raw host name plus taxonomy id and version.</summary>
public sealed record ToolRef(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("taxonomy_id")] string TaxonomyId,
    [property: JsonPropertyName("taxonomy_version")] string TaxonomyVersion);

/// <summary>A request stage timing. Name is from the finite writer vocabulary (content-free).</summary>
public sealed record Stage(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs);

/// <summary>Subject reference shared by trace/rollup bodies: kind + canonical id + display id.</summary>
public sealed record TraceSubject(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_id")] string DisplayId,
    [property: JsonPropertyName("alias")] string? Alias = null);

/// <summary>Window for a rollup subject: wall-clock from/to (UTC).</summary>
public sealed record TraceWindow(
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("to")] string? To);

// ---------------------------------------------------------------------------
// Sessions list (kind=sessions_list)
// ---------------------------------------------------------------------------

/// <summary>
/// local-full Session DTO. Adds no content fields beyond identifiers (sessions carry no prose).
/// <c>HasData</c> = event_count &gt; 0 OR span_count &gt; 0 OR has_cost; false means no displayable
/// trace data (picker renders this session disabled with a "no data" label).
/// <c>IsActive</c> = heuristic: open dispatch or open span (completed_at IS NULL).
/// Added in T-2026-06-15-15 (show-all, disable empties, sort active-first).
/// </summary>
public sealed record TraceSession(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_id")] string DisplayId,
    [property: JsonPropertyName("agent_type")] string? AgentType,
    [property: JsonPropertyName("agent_subtype")] string? AgentSubtype,
    [property: JsonPropertyName("first_seen_at")] string? FirstSeenAt,
    [property: JsonPropertyName("last_seen_at")] string? LastSeenAt,
    [property: JsonPropertyName("request_count")] long RequestCount,
    [property: JsonPropertyName("event_count")] long EventCount,
    [property: JsonPropertyName("parent_session_id")] string? ParentSessionId,
    [property: JsonPropertyName("depth")] int? Depth,
    [property: JsonPropertyName("has_data")] bool HasData,
    [property: JsonPropertyName("is_active")] bool IsActive,
    // Soft-delete flag (T-2026-06-15-16/ST-01). ISO-8601 UTC when the session is soft-deleted,
    // null otherwise. Additive at the end so positional construction elsewhere is unaffected.
    // Default-listed (excludeDeleted) responses always carry null here; the trash view
    // (includeDeleted=true) surfaces the timestamp. Local-full only; no packet counterpart.
    [property: JsonPropertyName("deleted_at")] string? DeletedAt = null,
    [property: JsonPropertyName("host")] string? Host = null,
    // Host Claude Code / Codex session id (cc) for this session (T-2026-06-19-04). Source =
    // obs_agent_sessions.cc_session_id (a v11 column; the reader EnsureSchema-guards it and falls back to
    // omitting it on a pre-v11 db). It is the conversation identity the universal by_conversation grouping
    // partitions on; surfacing it lets the picker label/group per-generation sessions by conversation.
    // null when the host supplied no session id (cron without a captured cc, MCP, host-provided rows).
    [property: JsonPropertyName("cc_session_id")] string? CcSessionId = null,
    // Resume-stable conversation id (schema v16). Source = obs_agent_sessions.conversation_id (the
    // transcript filename stem, continuous across /resume). null when absent (pre-v16 db, non-CC hosts,
    // cron rows). Additive at the end so positional construction elsewhere is unaffected.
    [property: JsonPropertyName("conversation_id")] string? ConversationId = null,
    // Locally-minted stable machine id (schema v16). Source = obs_agent_sessions.machine_id. Enables
    // correct cross-machine merge by distinguishing rows captured on different machines. null on legacy
    // rows and non-capturing hosts. Additive at the end so positional construction elsewhere is unaffected.
    [property: JsonPropertyName("machine_id")] string? MachineId = null);

/// <summary>Body for kind=sessions_list.</summary>
public sealed record SessionsListBody(
    [property: JsonPropertyName("sessions")] List<TraceSession> Sessions);

/// <summary>
/// Response body for the soft-delete and restore session endpoints (T-2026-06-15-16/ST-01).
/// <c>deleted_at</c> is the new ISO-8601 UTC stamp after a soft-delete, or null after a restore.
/// </summary>
public sealed record TraceSessionDeleteResult(
    [property: JsonPropertyName("session_key")] string SessionKey,
    [property: JsonPropertyName("deleted_at")] string? DeletedAt);

// ---------------------------------------------------------------------------
// Session graph (kind=session_graph)
// ---------------------------------------------------------------------------

/// <summary>
/// local-full graph node. The free-text "label" (agent_name) is local-only (NEVER 6); packet
/// consumers render agent_type + alias. Span pseudo-nodes use the bare span canonical id.
/// </summary>
public sealed record GraphNodeDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_id")] string DisplayId,
    [property: JsonPropertyName("alias")] string? Alias,
    [property: JsonPropertyName("agent_type")] string? AgentType,
    [property: JsonPropertyName("request_count")] int RequestCount,
    [property: JsonPropertyName("cost")] Money? Cost,
    [property: JsonPropertyName("depth")] int Depth,
    // local-full only: free-text agent name. Has no packet counterpart.
    [property: JsonPropertyName("label")] string? Label);

/// <summary>Graph edge with honest provenance (link_source).</summary>
public sealed record GraphEdgeDto(
    [property: JsonPropertyName("parent")] string Parent,
    [property: JsonPropertyName("child")] string Child,
    [property: JsonPropertyName("link_source")] string LinkSource);

/// <summary>Body for kind=session_graph.</summary>
public sealed record SessionGraphBody(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("nodes")] List<GraphNodeDto> Nodes,
    [property: JsonPropertyName("edges")] List<GraphEdgeDto> Edges);

// ---------------------------------------------------------------------------
// Trace entries (kind=trace) -- discriminated by "kind": request|span|event
// ---------------------------------------------------------------------------

/// <summary>
/// A single trace entry. Discriminated by Kind. Carries the union of request/span/event fields;
/// fields not applicable to the entry kind are null. Content fields (query_text, summaries,
/// snippets, label, error_message) are local-full only -- omitted entirely from the packet DTO.
/// </summary>
public sealed record TraceEntry
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("display_id")] public required string DisplayId { get; init; }
    [JsonPropertyName("alias")] public string? Alias { get; init; }
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    [JsonPropertyName("dispatch_id")] public string? DispatchId { get; init; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; init; }

    // span / event linkage
    [JsonPropertyName("span_id")] public string? SpanId { get; init; }
    [JsonPropertyName("resolved_span_id")] public string? ResolvedSpanId { get; init; }
    [JsonPropertyName("parent_span_id")] public string? ParentSpanId { get; init; }
    [JsonPropertyName("resolved_parent_span_id")] public string? ResolvedParentSpanId { get; init; }

    // span fields
    [JsonPropertyName("agent_type")] public string? AgentType { get; init; }
    [JsonPropertyName("agent_ref")] public string? AgentRef { get; init; }
    [JsonPropertyName("started_at")] public string? StartedAt { get; init; }
    [JsonPropertyName("completed_at")] public string? CompletedAt { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("time_source")] public string? TimeSource { get; init; }
    [JsonPropertyName("tokens")] public TokenBlock? Tokens { get; init; }
    [JsonPropertyName("rollup_cost")] public Money? RollupCost { get; init; }
    /// <summary>
    /// The TRUE model attributed to this span (OBS-11), resolved by the explicit fallback ladder in
    /// <c>model_provenance</c>: Tier-1 the proxy-metered wire model joined to this span, else Tier-2 the
    /// recorded/requested model (cron --model or a hook's tool_input.model), else null. Null means the
    /// model is genuinely unknown (inherited dispatch with no joinable wire row) -- never a fabricated
    /// default. Lets the FE show the per-sub-agent model even on inherited-model dispatches.
    /// </summary>
    [JsonPropertyName("model")] public string? Model { get; init; }
    /// <summary>
    /// Provenance of <c>model</c> (OBS-11), the explicit, non-fabricating fallback ladder:
    /// <c>wire</c> (Tier-1, proxy-metered truth) | <c>recorded</c> (Tier-2, the requested model Scope
    /// captured) | <c>unavailable</c> (no model resolvable, and never invented). This is the origin of the
    /// model IDENTITY, not its price: an observed-but-unpriceable model still reads <c>wire</c>/<c>recorded</c>
    /// here, while its PRICE separately degrades to unavailable (ModelIdNormalizer.Lookup returns null, so a
    /// price is never fabricated for an unknown model). Null only on legacy/pre-OBS-11 rows.
    /// </summary>
    [JsonPropertyName("model_provenance")] public string? ModelProvenance { get; init; }

    // event fields
    [JsonPropertyName("event_type")] public string? EventType { get; init; }
    /// <summary>
    /// Ratified taxonomy subtype for user_submit events (ST-01/ST-05). One of the seven members:
    /// initiating_prompt, follow_up, interrupt, tool_approval, tool_denial, redirection, cron_prompt.
    /// Null for all other event types and for pre-v10 databases that predate the column.
    /// </summary>
    [JsonPropertyName("user_submit_subtype")] public string? UserSubmitSubtype { get; init; }
    /// <summary>
    /// 0-based ordinal of this user_submit within its session (initiating_prompt = 0).
    /// Null for non-user_submit events and when the capture path could not determine an ordinal.
    /// </summary>
    [JsonPropertyName("turn_index")] public int? TurnIndex { get; init; }
    [JsonPropertyName("tool")] public ToolRef? Tool { get; init; }
    [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; init; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; init; }
    [JsonPropertyName("request_id")] public string? RequestId { get; init; }
    [JsonPropertyName("caps")] public List<string>? Caps { get; init; }
    /// <summary>
    /// The complete host-field universe for this event's adapter (e.g. all 8 Claude Code fields).
    /// Present when caps is present; null otherwise. Enables the frontend to show N/TOTAL coverage
    /// and list which fields are missing without re-deriving host shape outside the adapter.
    /// </summary>
    [JsonPropertyName("caps_universe")] public List<string>? CapsUniverse { get; init; }

    // request fields
    [JsonPropertyName("parent_request_id")] public string? ParentRequestId { get; init; }
    [JsonPropertyName("caller_surface")] public string? CallerSurface { get; init; }
    [JsonPropertyName("operation")] public string? Operation { get; init; }
    [JsonPropertyName("exit_code")] public int? ExitCode { get; init; }
    [JsonPropertyName("total_elapsed_ms")] public long? TotalElapsedMs { get; init; }
    [JsonPropertyName("stages")] public List<Stage>? Stages { get; init; }
    [JsonPropertyName("cost")] public Money? Cost { get; init; }
    [JsonPropertyName("token_estimate")] public TokenBlock? TokenEstimate { get; init; }

    // ----- local-full content fields (NO packet counterpart) -----
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("input_summary")] public string? InputSummary { get; init; }
    [JsonPropertyName("output_summary")] public string? OutputSummary { get; init; }
    [JsonPropertyName("input_snippet")] public string? InputSnippet { get; init; }
    [JsonPropertyName("output_snippet")] public string? OutputSnippet { get; init; }
    [JsonPropertyName("message_recipient")] public string? MessageRecipient { get; init; }
    [JsonPropertyName("query_text")] public string? QueryText { get; init; }
    [JsonPropertyName("parameters_json")] public string? ParametersJson { get; init; }
    [JsonPropertyName("error_message")] public string? ErrorMessage { get; init; }
}

/// <summary>
/// Body for kind=trace.
/// total_count (T-2026-06-17-03) is the analytically correct count of entries for this session
/// matching the active retention filter, BEFORE any page/limit slice. The FE uses this to compute
/// page counts for the event-card timeline pagination. Never approximate; always a DB count.
/// </summary>
public sealed record TraceBody(
    [property: JsonPropertyName("subject")] TraceSubject Subject,
    [property: JsonPropertyName("entries")] List<TraceEntry> Entries,
    [property: JsonPropertyName("total_count")] long TotalCount = 0);

// ---------------------------------------------------------------------------
// Rollup (kind=rollup) -- content-free in both tiers (no prose fields)
// ---------------------------------------------------------------------------

/// <summary>An aggregate cost block carrying coverage counts.</summary>
public sealed record MoneyAggregate(
    // decimal (T-2026-06-20-20 Piece 2): in-memory aggregation is exact, so sum(by_task) == totals
    // holds to the cent. Serializes as a JSON number (wire/FE shape unchanged).
    [property: JsonPropertyName("value_usd")] decimal? ValueUsd,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("pricing_version")] string? PricingVersion,
    [property: JsonPropertyName("coverage")] Coverage Coverage,
    [property: JsonPropertyName("breakdown")] CostBreakdown? Breakdown = null,
    [property: JsonPropertyName("cost_provenance")] string? CostProvenance = null,
    // Billing-class discriminator (T-2026-06-19-01 Phase 3b). ORTHOGONAL to cost_provenance:
    // provenance = data-source quality; billing-class = whether the dollars are an ACTUAL per-token
    // charge (billed) vs a subscription would-be figure (shadow_would_be) vs unclassifiable.
    // See CostBillingClass. Additive at the end (default null) so existing positional construction
    // and legacy serialized shapes are unaffected; null means "no billing-class label" (legacy).
    [property: JsonPropertyName("cost_billing_class")] string? CostBillingClass = null);

/// <summary>An aggregate token block carrying coverage counts.</summary>
public sealed record TokenAggregate(
    [property: JsonPropertyName("input")] long? Input,
    [property: JsonPropertyName("output")] long? Output,
    [property: JsonPropertyName("cache_creation")] long? CacheCreation,
    [property: JsonPropertyName("cache_read")] long? CacheRead,
    [property: JsonPropertyName("total")] long? Total,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("coverage")] Coverage Coverage);

/// <summary>Time coverage counts for a rollup window (how much is measurable).</summary>
public sealed record TimeCoverage(
    [property: JsonPropertyName("measured")] int Measured,
    [property: JsonPropertyName("zero_duration")] int ZeroDuration,
    [property: JsonPropertyName("unknown")] int Unknown);

/// <summary>Closed enum of model-attribution sources behind a cost estimate (T-2026-06-15-04).</summary>
public static class CostEstimationSource
{
    /// <summary>Tier 1: the model came from proxy-captured wire metering (source of truth).</summary>
    public const string Wire = "wire";

    /// <summary>Tier 2: the model was RECORDED/REQUESTED for the work (cron --model, or a hook's
    /// tool_input.model on an Agent dispatch). True per-session, but priced from a rate table, not metered.</summary>
    public const string Recorded = "recorded";

    /// <summary>Tier 3: no model was known; a default (Sonnet-tier peg) was ASSUMED.</summary>
    public const string Assumed = "assumed";
}

/// <summary>
/// Estimation basis attached to a rollup when cost was computed via exact-token assumption or
/// wire/recorded-token model lookup (not wire-exact or obs_cost_estimates). Emitted only when an
/// estimate was produced; null when cost is genuinely unavailable or when exact metering was available.
/// <para>
/// <c>assumed_model</c> is the model id used for pricing (the resolved wire model, the recorded
/// requested model, or the Sonnet-tier default when no model was available). <c>prices_as_of</c> is
/// the pricing table snapshot date. <c>source</c> (T-2026-06-15-04, APPROVED scope-trace-v1 contract
/// change) is the 3-value attribution tier: <c>wire</c> (Tier 1, proxy-metered model), <c>recorded</c>
/// (Tier 2, the requested model captured by Scope), or <c>assumed</c> (Tier 3, default peg). It lets
/// the FE distinguish "wire-measured" vs "from requested model" vs "assumed default".
/// </para>
/// <para>
/// <c>recovered</c> (the prior boolean field) is RETAINED additively and DERIVED from <c>source</c>
/// as <c>source != "assumed"</c> so existing consumers keep working: a wire OR recorded model is
/// "recovered" (provenance leans provider_estimated), an assumed default is not (scope_estimated).
/// </para>
/// </summary>
public sealed record CostEstimationBasis(
    [property: JsonPropertyName("assumed_model")] string AssumedModel,
    [property: JsonPropertyName("prices_as_of")] string? PricesAsOf,
    [property: JsonPropertyName("source")] string Source)
{
    /// <summary>
    /// Backward-compatible derived flag: true when the model came from wire OR recorded data (any
    /// non-assumed source), false when the default was assumed. Serialized as <c>recovered</c> so the
    /// existing scope-trace-v1 field stays present (additive; no field removed).
    /// </summary>
    [JsonPropertyName("recovered")]
    public bool Recovered => !string.Equals(Source, CostEstimationSource.Assumed, StringComparison.Ordinal);
}

/// <summary>
/// One component of a cost breakdown: a token bucket (kind), its count, the per-MTok rate applied,
/// and the resulting cost. kind is a closed enum: input|output|cache_read|cache_write.
/// </summary>
public sealed record CostBreakdownComponent(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("tokens")] long Tokens,
    // rate + cost are decimal (T-2026-06-20-20 Piece 2): ModelPricing rates are decimal at source,
    // and component costs sum exactly into the breakdown total. Serialized as JSON numbers (shape
    // unchanged). The exception is a pinned breakdown deserialized from stored JSON, whose component
    // costs were written as numbers and round-trip through decimal losslessly.
    [property: JsonPropertyName("rate_usd_per_mtok")] decimal RateUsdPerMtok,
    [property: JsonPropertyName("cost_usd")] decimal CostUsd);

/// <summary>
/// Per-component cost breakdown attached to a Money or MoneyAggregate when cost was computed
/// via estimation (provider_estimated or scope_estimated). Null on exact/wire costs.
/// pinned=true means the snapshot was stored at capture and is immutable against future price changes.
/// pinned=false means cost was re-derived live from the current pricing table (legacy/unpinned).
/// prices_as_of is the SEMANTIC effective date of the rates used, never a runtime timestamp.
/// </summary>
public sealed record CostBreakdown(
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("prices_as_of")] string PricesAsOf,
    [property: JsonPropertyName("pinned")] bool Pinned,
    [property: JsonPropertyName("components")] List<CostBreakdownComponent> Components,
    // decimal (T-2026-06-20-20 Piece 2): equals the sum of its components exactly. JSON number.
    [property: JsonPropertyName("total_usd")] decimal TotalUsd);

/// <summary>One row in the session-level cost_provenance_breakdown: a 6-value provenance bucket with its spend and task count.</summary>
public sealed record ProvenanceBreakdownRow(
    [property: JsonPropertyName("provenance")] string Provenance,
    // decimal (T-2026-06-20-20 Piece 2): summed from MoneyAggregate.ValueUsd exactly. JSON number.
    [property: JsonPropertyName("spend_usd")] decimal SpendUsd,
    [property: JsonPropertyName("count")] int Count);

/// <summary>
/// Rollup totals. busy_ms_sum is cpu-style (MAY exceed wall_ms under concurrency).
/// <para>
/// <c>cost_provenance</c> is the additive 6-value session/run-level field
/// (wire_exact | joined_exact | provider_estimated | scope_estimated | unmetered | unavailable).
/// It does NOT replace <see cref="MoneyAggregate.Source"/> (4-value: exact|wire|estimate|unknown)
/// on the cost primitive. The two coexist: Money.source stays at trace-primitive level;
/// cost_provenance is the run/session-level summary. Mapping from MoneyAggregate.Source:
/// "wire" -> wire_exact, "exact" -> joined_exact, "estimate" -> scope_estimated,
/// "unknown" -> unavailable.
/// </para>
/// </summary>
public sealed record RollupTotals(
    [property: JsonPropertyName("cost")] MoneyAggregate Cost,
    [property: JsonPropertyName("tokens")] TokenAggregate Tokens,
    [property: JsonPropertyName("wall_ms")] long WallMs,
    [property: JsonPropertyName("busy_ms_sum")] long BusyMsSum,
    [property: JsonPropertyName("time_coverage")] TimeCoverage TimeCoverage,
    [property: JsonPropertyName("request_count")] long RequestCount,
    [property: JsonPropertyName("event_count")] long EventCount,
    [property: JsonPropertyName("cost_provenance")] string CostProvenance = "unavailable",
    [property: JsonPropertyName("cost_estimation_basis")] CostEstimationBasis? CostEstimationBasis = null,
    [property: JsonPropertyName("breakdown")] CostBreakdown? Breakdown = null,
    [property: JsonPropertyName("cost_provenance_breakdown")] List<ProvenanceBreakdownRow>? CostProvenanceBreakdown = null,
    // Session-level billing-class (T-2026-06-19-01 Phase 3b). ORTHOGONAL to cost_provenance above:
    // provenance is data-source quality; this says whether the session's dollars are an ACTUAL
    // per-token charge (billed: cron API-key run, or interactive that went through the proxy with a
    // key) vs a subscription would-be figure (shadow_would_be) vs unclassifiable. Defaults to
    // CostBillingClass.Unclassified so existing data shows no new label and stays backward-compatible.
    [property: JsonPropertyName("cost_billing_class")] string CostBillingClass = "unclassified");

/// <summary>One partition row of a rollup (per top-level span, or the unattributed remainder).</summary>
public sealed record RollupRow(
    [property: JsonPropertyName("subject")] TraceSubject Subject,
    [property: JsonPropertyName("cost")] MoneyAggregate Cost,
    [property: JsonPropertyName("tokens")] TokenAggregate Tokens,
    [property: JsonPropertyName("busy_ms")] long BusyMs,
    [property: JsonPropertyName("time_source")] string TimeSource,
    [property: JsonPropertyName("request_count")] long RequestCount,
    [property: JsonPropertyName("event_count")] long EventCount,
    [property: JsonPropertyName("cost_estimation_basis")] CostEstimationBasis? CostEstimationBasis = null,
    [property: JsonPropertyName("breakdown")] CostBreakdown? Breakdown = null);

/// <summary>Body for kind=rollup. totals == sum(by_task) + unattributed by construction.</summary>
public sealed record RollupBody(
    [property: JsonPropertyName("subject")] TraceSubject Subject,
    [property: JsonPropertyName("window")] TraceWindow Window,
    [property: JsonPropertyName("totals")] RollupTotals Totals,
    [property: JsonPropertyName("by_task")] List<RollupRow> ByTask,
    [property: JsonPropertyName("unattributed")] RollupRow Unattributed);

// ---------------------------------------------------------------------------
// Top actions (kind=top_actions) -- item 10. Content-free in both tiers.
// ---------------------------------------------------------------------------

/// <summary>An aggregated action row: a taxonomy category with count and total time.</summary>
public sealed record TopActionRow(
    [property: JsonPropertyName("taxonomy_id")] string TaxonomyId,
    [property: JsonPropertyName("count")] long Count,
    [property: JsonPropertyName("total_ms")] long TotalMs);

/// <summary>Body for kind=top_actions: the same rows ranked two ways (item 10).</summary>
public sealed record TopActionsBody(
    [property: JsonPropertyName("taxonomy_version")] string TaxonomyVersion,
    [property: JsonPropertyName("by_count")] List<TopActionRow> ByCount,
    [property: JsonPropertyName("by_time")] List<TopActionRow> ByTime);

// ---------------------------------------------------------------------------
// Wire calls (kind=wire) -- content-free in both tiers (provider ids + hashes only)
// ---------------------------------------------------------------------------

/// <summary>
/// One wire call projected from the merged wire_calls table (wire-metering ST-01). Content-free
/// by construction: endpoint is the route template (token stripped at storage, NEVER 8), model is
/// a provider model id, hashes are hex strings. source on the token block is always "wire".
/// </summary>
public sealed record WireCallDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_id")] string DisplayId,
    [property: JsonPropertyName("started_at")] string StartedAt,
    [property: JsonPropertyName("ttft_ms")] long? TtftMs,
    [property: JsonPropertyName("total_ms")] long? TotalMs,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("message_id")] string? MessageId,
    [property: JsonPropertyName("stop_reason")] string? StopReason,
    [property: JsonPropertyName("tokens")] TokenBlock Tokens,
    [property: JsonPropertyName("tool_use_ids")] List<string> ToolUseIds,
    [property: JsonPropertyName("system_hash")] string? SystemHash,
    [property: JsonPropertyName("first_user_hash")] string? FirstUserHash,
    [property: JsonPropertyName("message_count")] int? MessageCount,
    [property: JsonPropertyName("request_bytes")] long? RequestBytes,
    [property: JsonPropertyName("response_bytes")] long? ResponseBytes,
    // Row provenance (T-2026-06-22-01, Piece 5). Honest source tag from wire_calls.source (schema v3):
    // "proxy" for proxy-metered rows, "transcript" for turns reconstructed from the Claude Code session
    // transcript. Additive at the end with a default so existing positional construction and serialized
    // shapes are unaffected; legacy/pre-v3 rows that lacked the column read "proxy" (the schema DEFAULT).
    // Content-free: a closed provenance enum, never free text.
    [property: JsonPropertyName("source")] string Source = "proxy");

/// <summary>
/// Body for kind=wire. <c>available</c> is false (with an empty <c>calls</c> list) when the
/// wire_calls table does not exist yet or carries no rows: an honest empty wire section, never
/// fabricated data (design Q3, wire-metering ST-01 not yet exercised).
/// </summary>
public sealed record WireBody(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("calls")] List<WireCallDto> Calls);

// ---------------------------------------------------------------------------
// Observability limits (additive, T-2026-06-13-33)
// ---------------------------------------------------------------------------

/// <summary>
/// Observability tier limits included in the sessions_list response so the frontend (T-34) can
/// render honest, non-destructive notices without a separate API round-trip.
///
/// Null int fields mean "unlimited" (paid tier). <c>sessions_capped</c> is true when the
/// returned session list was trimmed by the cap. <c>retention_window_applied</c> is true when a
/// 7-day (or custom) cutoff was injected into the read queries for this request.
/// </summary>
public sealed record ObservabilityLimitsDto(
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("observability_retention_days")] int? ObservabilityRetentionDays,
    [property: JsonPropertyName("max_dashboard_sessions")] int? MaxDashboardSessions,
    [property: JsonPropertyName("audit_export_allowed")] bool AuditExportAllowed,
    [property: JsonPropertyName("sessions_capped")] bool SessionsCapped,
    [property: JsonPropertyName("retention_window_applied")] bool RetentionWindowApplied);

/// <summary>
/// Body for kind=sessions_list, extended with the limits signal (T-2026-06-13-33) and pagination
/// total_count (T-2026-06-17-03). total_count is the analytically correct count of sessions
/// matching the active filter (from/to/includeDeleted/retention) BEFORE any page-size slice.
/// The FE uses total_count / pageSize to compute page count; never approximate this.
/// </summary>
public sealed record SessionsListBodyWithLimits(
    [property: JsonPropertyName("sessions")] List<TraceSession> Sessions,
    [property: JsonPropertyName("limits")] ObservabilityLimitsDto Limits,
    [property: JsonPropertyName("total_count")] long TotalCount = 0);

// ---------------------------------------------------------------------------
// Conversation rollup (kind=conversation_rollup) -- ST-08, additive.
// Token-exact per-session + remainder partition; cost reported as unknown
// (no per-row obs_cost_estimates join at this layer).
// ---------------------------------------------------------------------------

/// <summary>
/// One session's token slice from a conversation rollup, or the remainder bucket
/// (session_key="" for remainder). Token counts are exact from wire_calls rows.
/// <para>
/// <c>value_usd</c> is the priced cost for this slice (ST-08): for a session slice it is the session's
/// own per-session RollupBody cost (reconciling with the per-session trace screen by construction); for
/// the remainder it is the per-row-by-model priced cost of the unowned wire rows. Null when no price
/// could be applied (unknown model / no pricing). The conversation invariant holds at the cost level:
/// conversation total value_usd == sum(session value_usd) + remainder value_usd.
/// </para>
/// </summary>
public sealed record ConversationSessionSlice(
    [property: JsonPropertyName("session_key")] string SessionKey,
    [property: JsonPropertyName("input_tokens")] long InputTokens,
    [property: JsonPropertyName("output_tokens")] long OutputTokens,
    [property: JsonPropertyName("cache_creation_tokens")] long CacheCreationTokens,
    [property: JsonPropertyName("cache_read_tokens")] long CacheReadTokens,
    [property: JsonPropertyName("total_tokens")] long TotalTokens,
    [property: JsonPropertyName("row_count")] int RowCount,
    [property: JsonPropertyName("value_usd")] decimal? ValueUsd);

/// <summary>
/// Body for kind=conversation_rollup (ST-08). Sessions + remainder + totals.
/// Invariant by construction: total_tokens == sum(session.total_tokens) + remainder.total_tokens,
/// and total_value_usd == sum(session.value_usd) + remainder.value_usd (exact decimal).
/// </summary>
public sealed record ConversationRollupBody(
    [property: JsonPropertyName("scope_conversation_id")] string ScopeConversationId,
    [property: JsonPropertyName("sessions")] List<ConversationSessionSlice> Sessions,
    [property: JsonPropertyName("remainder")] ConversationSessionSlice Remainder,
    [property: JsonPropertyName("total_input_tokens")] long TotalInputTokens,
    [property: JsonPropertyName("total_output_tokens")] long TotalOutputTokens,
    [property: JsonPropertyName("total_tokens")] long TotalTokens,
    [property: JsonPropertyName("total_value_usd")] decimal? TotalValueUsd);

/// <summary>
/// One base-session row in a CLI-session rollup: total tokens and cost across all generation-suffixed
/// sessions sharing this base_session_key. Token counts are exact from wire_calls rows; <c>value_usd</c>
/// is the sum of each session's per-session RollupBody cost (ST-08).
/// </summary>
public sealed record CliSessionRollupRow(
    [property: JsonPropertyName("base_session_key")] string BaseSessionKey,
    [property: JsonPropertyName("session_count")] int SessionCount,
    [property: JsonPropertyName("input_tokens")] long InputTokens,
    [property: JsonPropertyName("output_tokens")] long OutputTokens,
    [property: JsonPropertyName("cache_creation_tokens")] long CacheCreationTokens,
    [property: JsonPropertyName("cache_read_tokens")] long CacheReadTokens,
    [property: JsonPropertyName("total_tokens")] long TotalTokens,
    [property: JsonPropertyName("token_source")] string TokenSource,
    [property: JsonPropertyName("value_usd")] decimal? ValueUsd);

/// <summary>
/// Body for kind=cli_session_rollup (ST-08). Per-base-session cost summary.
/// </summary>
public sealed record CliSessionRollupBody(
    [property: JsonPropertyName("rows")] List<CliSessionRollupRow> Rows);

#pragma warning restore MA0016
