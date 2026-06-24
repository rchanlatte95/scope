// =============================================================================
// scope-trace-v1 contract types (frontend mirror).
//
// Authoritative spec: internal design notes
// Server projection: src/Scope.Core/Telemetry/Trace/. This file mirrors the
// SERVED JSON shape (TYPES only, no view logic). It is the single place the SPA
// imports trace contract types from, so contract changes are a one-file swap.
//
// Only the shapes the current SPA consumes (the local-full sessions_list envelope
// and the shared envelope/primitives) are declared here. Additional kinds
// (session_graph, trace, rollup, top_actions, wire) are added as the SPA grows
// (ST-13/14/15); their server shapes already exist and are documented in the spec.
// =============================================================================

/** Generic scope-trace-v1 envelope. Identity placeholders live here once. */
export interface TraceEnvelope<TBody> {
  schema: 'scope-trace-v1';
  kind: string;
  tier: 'local-full' | 'packet';
  generated_at: string;
  generator: { app: string; version: string };
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  machine_id?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  workspace_id?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  org_id?: string | null;
  taxonomy_version: string;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  page?: TracePage | null;
  body: TBody;
}

/** Keyset paging cursor for paged trace endpoints. */
export interface TracePage {
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  cursor?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  next_cursor?: string | null;
  limit: number;
}

/** Tagged monetary block. source is closed enum exact|wire|estimate|unknown. */
export interface Money {
  /** WhenWritingNull: omitted when null; arrives as `undefined`. Use `== null` or `?? null`. */
  value_usd?: number | null;
  source: 'exact' | 'wire' | 'estimate' | 'unknown';
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  pricing_version?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  priced_at?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  model_id?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  provider?: string | null;
  /**
   * 6-value cost provenance tag. Present when the backend emits it (T-2026-06-16-06+).
   * Absent on older wire snapshots; fall back to `source` mapping when null/undefined.
   * WhenWritingNull: omitted when null; arrives as `undefined`.
   */
  cost_provenance?: string | null;
}

/** Tagged token block. input+output is NOT asserted to equal total. */
export interface TokenBlock {
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  input?: number | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  output?: number | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  cache_creation?: number | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  cache_read?: number | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. Use `== null` to guard before ops. */
  total?: number | null;
  source: 'exact' | 'wire' | 'estimate' | 'unknown';
}

/** local-full Session row (sessions carry no prose; identifiers + counts only). */
export interface TraceSession {
  id: string;
  display_id: string;
  /** Host identity: claude-code, codex, cron, or a legacy/unknown host label. */
  host?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  agent_type?: string | null;
  /**
   * The host conversation id (cc_session_id from the ingested hook payload).
   * Groups sessions that share the same Claude Code / Codex conversation across
   * base-launch boundaries (e.g. resume restores the original cc_session_id).
   * Null for idless/legacy sessions or hosts that do not emit a session id.
   * WhenWritingNull: omitted when null; arrives as `undefined`.
   * Added T-2026-06-19-04 (universal-cc backend).
   */
  cc_session_id?: string | null;
  /**
   * The transcript filename stem (conversation_id from the backend).
   * RESUME-STABLE: survives a /resume which assigns a new cc_session_id.
   * The correct grouping identity for conversation-level bucketing.
   * Null for idless/legacy sessions or hosts that do not emit a conversation id.
   * WhenWritingNull: omitted when null; arrives as `undefined`.
   */
  conversation_id?: string | null;
  /**
   * Locally-minted machine identity. Paired with conversation_id for cross-machine
   * correctness: two machines may share the same conversation_id by coincidence.
   * Null for sessions captured before machine_id was available.
   * WhenWritingNull: omitted when null; arrives as `undefined`.
   */
  machine_id?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  agent_subtype?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  first_seen_at?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  last_seen_at?: string | null;
  request_count: number;
  event_count: number;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  parent_session_id?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  depth?: number | null;
  /**
   * True when the session has displayable data (events OR spans OR cost).
   * False for request-only sessions with no agent trace content.
   * Added T-2026-06-15-15. Older servers omit this field; treat absent as true.
   */
  has_data?: boolean;
  /**
   * True when the session has an open dispatch or open span (heuristic for "still running").
   * Active sessions sort to the top of the picker. Older servers omit this; treat absent as false.
   * Added T-2026-06-15-15.
   */
  is_active?: boolean;
  /**
   * ISO-8601 UTC timestamp when the session was soft-deleted, or null when not deleted.
   * WhenWritingNull: null IS emitted (not omitted) per the ST-01/02 contract — use `?? null` not `?? undefined`.
   * Added ST-01 (T-2026-06-15-16).
   */
  deleted_at?: string | null;
  /**
   * Scope-minted durable conversation id (schema v18, ST-09).
   * Machine-aware: minted once per unique (machine_id, stem) binding; stable across /resume and
   * /clear splits sharing the same stem. Two sessions on different machines with the same stem get
   * DIFFERENT scope_conversation_id values.
   * When present (non-null, non-empty), this is the PRIMARY grouping key for buildConversationGroups.
   * Falls back to the (machine_id, conversation_id) composite for pre-v18 rows (null/empty).
   * WhenWritingNull: omitted when null; arrives as `undefined`.
   * Added ST-09 (durable-conversation-identity).
   */
  scope_conversation_id?: string | null;
}

// ---------------------------------------------------------------------------
// Base-launch cost rollup (GET /api/command/base-launches -- ST-06 / ST-07).
//
// Mirrors BudgetBaseLaunchRow / CommandBaseLaunchResponse in CommandContract.cs.
// Each row = one durable CLI launcher base session key with the SUM of its
// per-generation rollup costs and the count of /clear-split generations.
// By construction SUM(by_base.spend_usd) == total_spend_usd.
// ---------------------------------------------------------------------------

/**
 * One base-launch row: a durable CLI launcher BASE session key with the SUM of
 * its per-generation rollup costs and the count of generations a /clear produced.
 * T-2026-06-19-03 Wave 2 / ST-06.
 */
export interface BudgetBaseLaunchRow {
  /** The durable base session key (cli-{uuid7}, no #g suffix). */
  base_session_key: string;
  /** Total spend in USD across all generations of this base. */
  spend_usd: number;
  /** Number of logical generations (1 for a launch with no /clear). */
  generation_count: number;
  /** Cache-inclusive token total over the base's generations. */
  total_tokens: number;
}

/**
 * One conversation-level row from the by_conversation partition.
 * conversation_id == the cc_session_id shared across bases in this conversation
 * (or a base key for idless sessions). INVARIANT (backend-proven):
 * SUM(by_conversation.spend_usd) == SUM(by_base.spend_usd) == total_spend_usd.
 * Added T-2026-06-19-04 (universal-cc backend).
 */
export interface BudgetConversationRow {
  /** The cc_session_id (or base key for idless sessions). */
  conversation_id: string;
  /** Total spend in USD across all bases sharing this conversation id. */
  spend_usd: number;
  /** Number of base launches in this conversation (>1 on resume). */
  launch_count: number;
  /** Number of logical generations across all bases in this conversation. */
  generation_count: number;
  /** Cache-inclusive token total for this conversation. */
  total_tokens: number;
}

/**
 * Root DTO for GET /api/command/base-launches.
 * Always 200. by_base is empty (not null) when no spend exists; total_spend_usd is null
 * when no metered spend exists. SUM(by_base.spend_usd) == total_spend_usd by construction.
 */
export interface CommandBaseLaunchResponse {
  window_days: number;
  from_utc: string;
  to_utc: string;
  /** Null/omitted when no metered spend. Never render as $0.00. WhenWritingNull: absent. */
  total_spend_usd?: number | null;
  /** Per-base rows, descending by spend. Empty array when no data. */
  by_base: BudgetBaseLaunchRow[];
  /**
   * Per-conversation rows (T-2026-06-19-04 universal-cc backend).
   * Groups bases that share the same cc_session_id. Absent on older servers
   * that have not yet shipped the T-2026-06-19-04 backend; treat absent as [].
   * INVARIANT: SUM(by_conversation.spend_usd) == SUM(by_base.spend_usd).
   */
  by_conversation?: BudgetConversationRow[];
}

// ---------------------------------------------------------------------------
// Cron session row (GET /api/trace/cron-sessions — PLAIN JSON ARRAY, not a TraceEnvelope).
// Added ST-02 (T-2026-06-15-16).
// ---------------------------------------------------------------------------

/**
 * A single cron-run row from GET /api/trace/cron-sessions.
 * status in {running, completed, failed, skipped}.
 * session_key is the cron-{uuid7} deep-link target; null when the run has no trace session.
 */
export interface CronSession {
  run_id: string;
  /** Nullable: the cron-{uuid7} session key for deep-linking; null when no trace session exists. */
  session_key: string | null;
  /** Short BASE36 session handle (same as TraceSession.display_id); null when no trace session exists. */
  display_id: string | null;
  agent_label: string;
  schedule: string;
  /** Status in {running, completed, failed, skipped}. */
  status: string;
  /** Total cost in USD. Null when unavailable. */
  total_cost_usd: number | null;
  started_at: string;
  /** Null when still running. */
  completed_at: string | null;
  /**
   * Cost provenance: "wire_exact" | "joined_exact" | "provider_estimated" | "scope_estimated"
   * | "unmetered" | "unavailable". Never null; "unavailable" when cost data absent.
   */
  cost_provenance: string;
  /**
   * ISO-8601 UTC timestamp when the cron session was soft-deleted (obs_agent_sessions.deleted_at),
   * or null when not deleted. Added T-2026-06-16-02.
   */
  deleted_at: string | null;
  /**
   * Billing-class discriminator (T-2026-06-19-01 Phase 4). See CostBillingClass.
   * Absent on older backends; treat undefined as "unclassified" (no badge).
   */
  cost_billing_class?: CostBillingClass | null;
}

// ---------------------------------------------------------------------------
// Soft-delete / restore response shapes (ST-01).
// ---------------------------------------------------------------------------

/** Response from DELETE /api/trace/sessions/{key} -> 200. */
export interface DeleteSessionResponse {
  session_key: string;
  /** ISO-8601 UTC timestamp; non-null (the session was just deleted). */
  deleted_at: string;
}

/** Response from POST /api/trace/sessions/{key}/restore -> 200. null is EMITTED. */
export interface RestoreSessionResponse {
  session_key: string;
  /** null means restored (deleted_at cleared). */
  deleted_at: null;
}

/** Body for kind=sessions_list. */
export interface SessionsListBody {
  sessions: TraceSession[];
}

// ---------------------------------------------------------------------------
// Observability limits (additive, T-2026-06-13-33).
//
// Mirrors ObservabilityLimitsDto / SessionsListBodyWithLimits in TraceContract.cs.
// Null int fields mean "unlimited" (paid tier). The `limits` field on
// SessionsListBodyWithLimits is OPTIONAL here so older servers that do not yet
// carry it remain backward-compatible: absent limits -> render no notices, no error.
// ---------------------------------------------------------------------------

/**
 * Free-tier observability limits piggybacked on the sessions_list response.
 * Null numeric fields mean "unlimited" (paid/trial tier).
 * `sessions_capped` is true when the returned list was trimmed by `max_dashboard_sessions`.
 * `retention_window_applied` is true when a retention cutoff filtered the read queries.
 */
export interface ObservabilityLimits {
  /** Tier string, e.g. "free" or "paid". */
  tier: string;
  /** Max days of local trace retained in dashboard view. Null = unlimited. */
  observability_retention_days: number | null;
  /** Max sessions shown in dashboard. Null = unlimited. */
  max_dashboard_sessions: number | null;
  /** Whether audit export and HTML artifacts are allowed for this tier. */
  audit_export_allowed: boolean;
  /** True when the session list was trimmed to max_dashboard_sessions. */
  sessions_capped: boolean;
  /** True when a retention cutoff was applied to the read queries. */
  retention_window_applied: boolean;
}

/**
 * Body for kind=sessions_list extended with the limits signal (T-2026-06-13-33)
 * and pagination total_count (T-2026-06-17-03).
 * `limits` is optional so consumers are backward-compatible with servers that omit it.
 * `total_count` is 0 on older servers that don't emit it; treat as "unknown".
 */
export interface SessionsListBodyWithLimits extends SessionsListBody {
  limits?: ObservabilityLimits;
  /** Analytically correct total record count BEFORE the page slice (T-2026-06-17-03). */
  total_count?: number;
}

/** The sessions_list envelope as served by GET /api/trace/sessions. */
export type SessionsListEnvelope = TraceEnvelope<SessionsListBodyWithLimits>;

// ---------------------------------------------------------------------------
// session_graph (GET /api/trace/graph?session=) -- mirrors GraphNodeDto / GraphEdgeDto.
// ---------------------------------------------------------------------------

/** A graph node: a session or a span pseudo-node (kind="span"). */
export interface GraphNode {
  kind: 'session' | 'span';
  id: string;
  display_id: string;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  alias?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  agent_type?: string | null;
  request_count: number;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  cost?: Money | null;
  depth: number;
  /** local-full only free-text agent name (no packet counterpart). WhenWritingNull: omitted when null. */
  label?: string | null;
}

/** A graph edge with honest provenance. */
export interface GraphEdge {
  parent: string;
  child: string;
  link_source: 'parent_session_key' | 'span_enrichment' | 'parent_span_id' | (string & Record<never, never>);
}

/** Body for kind=session_graph. root is the display id of the root session. */
export interface SessionGraphBody {
  root: string;
  nodes: GraphNode[];
  edges: GraphEdge[];
}

/** The session_graph envelope as served by GET /api/trace/graph. */
export type SessionGraphEnvelope = TraceEnvelope<SessionGraphBody>;

// ---------------------------------------------------------------------------
// trace (GET /api/trace/full?session=&limit=&cursor=) -- mirrors TraceEntry / TraceBody.
// ---------------------------------------------------------------------------

/** Resolved tool reference on an event: raw name + taxonomy id + version. */
export interface ToolRef {
  name: string;
  taxonomy_id: string;
  taxonomy_version: string;
}

/** A request stage timing (finite writer vocabulary). */
export interface Stage {
  name: string;
  order: number;
  elapsed_ms: number;
}

/** Subject reference for a trace/rollup body. */
export interface TraceSubject {
  kind: string;
  id: string;
  display_id: string;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  alias?: string | null;
}

/**
 * A single trace entry, discriminated by `kind`. Carries the union of request/span/
 * event fields; fields not applicable to the entry kind are null. Content fields
 * (snippets, summaries, label, query_text, error_message) are local-full only.
 * Mirrors Scope.Core.Telemetry.Trace.TraceEntry exactly.
 *
 * NOTE: All nullable fields are typed `?: T | null` because DefaultIgnoreCondition=WhenWritingNull
 * means null fields are OMITTED from the JSON and arrive as `undefined` (not `null`).
 * Always use `== null` / `!= null` or optional chaining when accessing these fields.
 */
export interface TraceEntry {
  kind: 'request' | 'span' | 'event';
  id: string;
  display_id: string;
  alias?: string | null;
  session_id?: string | null;
  dispatch_id?: string | null;
  created_at?: string | null;

  // span / event linkage
  span_id?: string | null;
  resolved_span_id?: string | null;
  parent_span_id?: string | null;
  resolved_parent_span_id?: string | null;

  // span fields
  agent_type?: string | null;
  agent_ref?: string | null;
  started_at?: string | null;
  completed_at?: string | null;
  status?: string | null;
  time_source?: string | null;
  tokens?: TokenBlock | null;
  rollup_cost?: Money | null;

  // event fields
  event_type?: string | null;
  /**
   * Ratified taxonomy subtype for user_submit events (ST-01/ST-05).
   * One of: initiating_prompt, follow_up, interrupt, tool_approval, tool_denial, redirection, cron_prompt.
   * Null for all other event types and for pre-schema-v10 databases.
   * WhenWritingNull: omitted when null; arrives as `undefined`.
   */
  user_submit_subtype?: string | null;
  /**
   * 0-based turn ordinal for user_submit events (initiating = 0).
   * Null for non-user_submit events and when the capture path could not determine an ordinal.
   * WhenWritingNull: omitted when null; arrives as `undefined`.
   */
  turn_index?: number | null;
  tool?: ToolRef | null;
  tool_use_id?: string | null;
  duration_ms?: number | null;
  request_id?: string | null;
  caps?: string[] | null;
  /** The complete host-field universe for this event's adapter. Present when caps is present; null otherwise. */
  caps_universe?: string[] | null;

  // request fields
  parent_request_id?: string | null;
  caller_surface?: string | null;
  operation?: string | null;
  exit_code?: number | null;
  total_elapsed_ms?: number | null;
  stages?: Stage[] | null;
  cost?: Money | null;
  token_estimate?: TokenBlock | null;

  // local-full content fields (no packet counterpart)
  label?: string | null;
  input_summary?: string | null;
  output_summary?: string | null;
  input_snippet?: string | null;
  output_snippet?: string | null;
  message_recipient?: string | null;
  query_text?: string | null;
  parameters_json?: string | null;
  error_message?: string | null;
}

/** Body for kind=trace. */
export interface TraceBody {
  subject: TraceSubject;
  entries: TraceEntry[];
  /** Analytically correct total record count BEFORE the page slice (T-2026-06-17-03). 0 on older servers. */
  total_count?: number;
}

/** The trace envelope as served by GET /api/trace/full (paged). */
export type TraceEnvelopeFull = TraceEnvelope<TraceBody>;

// ---------------------------------------------------------------------------
// rollup (GET /api/trace/rollup?session=) -- mirrors the Rollup* DTOs (ST-15).
//
// The accounting rollup is a strict PARTITION: totals == sum(by_task) +
// unattributed, BY CONSTRUCTION (design coherenceRules). Cost and token blocks
// carry a `source` tag (exact|wire|estimate|unknown) and `coverage` counts so the
// SPA labels every number honestly and never shows a silently-wrong total.
// ---------------------------------------------------------------------------

/** Coverage counts on an aggregate block (how many constituents were known vs unknown). */
export interface Coverage {
  known: number;
  unknown: number;
}

// ---------------------------------------------------------------------------
// CostEstimationBasis (T-2026-06-15-02) -- additive field on RollupTotals and
// RollupRow when the cost was estimated rather than exactly metered.
//
// null  => cost is exact-metered or genuinely unavailable (no estimation happened).
// recovered=false => Sonnet-tier default was ASSUMED (no model known); show "assumed".
// recovered=true  => model was recovered from wire data (provider_estimated).
// ---------------------------------------------------------------------------

/**
 * Describes HOW an estimated cost was computed. Present when cost_provenance is
 * "scope_estimated" or "provider_estimated"; null when cost is exactly metered
 * or unavailable. Mirrors CostEstimationBasisDto (snake_case wire shape).
 */
export interface CostEstimationBasis {
  /** The model id used for pricing (e.g. "claude-sonnet-4-6"). */
  assumed_model: string;
  /**
   * ISO-8601 date string for the pricing table version. Null when the server
   * could not determine a prices-as-of date.
   * WhenWritingNull: omitted when null; arrives as `undefined`.
   */
  prices_as_of?: string | null;
  /**
   * true  => model was recovered from wire data (provider_estimated provenance).
   * false => Sonnet-tier default was ASSUMED because no model was known (scope_estimated).
   */
  recovered: boolean;
  /**
   * 3-value estimation tier (T-2026-06-15-04 step 2). Absent on older payloads;
   * fall back to recovered-based logic when undefined.
   *
   * "wire"     => Tier 1: proxy-metered model id from wire data (wire_exact / joined_exact).
   * "recorded" => Tier 2: model was explicitly recorded at capture time (e.g. cron --model arg).
   * "assumed"  => Tier 3: Sonnet-tier default assumed because no model was known.
   *
   * WhenWritingNull: omitted when null; arrives as `undefined`.
   */
  source?: 'wire' | 'recorded' | 'assumed';
}

// ---------------------------------------------------------------------------
// CostBreakdown (T-2026-06-15-11b) -- per-component token x rate = cost detail.
//
// Present on MoneyAggregate and Money when the backend has exact token counts
// and a priced model. NULL for unknown-cost tasks and for aggregate totals when
// multiple models contributed. The exact-token path emits input+output only.
// Mirrors CostBreakdownDto / CostBreakdownComponentDto (snake_case wire shape).
// ---------------------------------------------------------------------------

/** A single priced component of a cost breakdown (input, output, cache_read, cache_write). */
export interface CostBreakdownComponent {
  /** Component kind: matches the token category that was priced. */
  kind: 'input' | 'output' | 'cache_read' | 'cache_write';
  /** Number of tokens of this kind. */
  tokens: number;
  /** Rate in USD per million tokens (e.g. 3.0 means $3.00/1M). */
  rate_usd_per_mtok: number;
  /** Computed cost for this component in USD (tokens / 1_000_000 * rate_usd_per_mtok). */
  cost_usd: number;
}

/**
 * Exact cost breakdown for a single priced model call.
 * NULL for unknown-cost tasks or when aggregate totals span multiple models.
 * Mirrors CostBreakdownDto (snake_case wire shape).
 */
export interface CostBreakdown {
  /** Model id that priced this call (e.g. "claude-haiku-4-5-20251001"). */
  model_id: string;
  /** ISO-8601 date string for the pricing table version (yyyy-MM-dd). */
  prices_as_of: string;
  /**
   * true  => immutable snapshot taken at model-call time.
   * false => derived at query time from current pricing tables.
   */
  pinned: boolean;
  /** Per-kind component lines that sum to total_usd. */
  components: CostBreakdownComponent[];
  /** Sum of all component.cost_usd values; must equal the cost block's value_usd. */
  total_usd: number;
}

/**
 * Billing-class discriminator (T-2026-06-19-01 Phase 4). Orthogonal to cost_provenance:
 * provenance is data-source QUALITY; billing-class is whether the dollars are an actual charge.
 *
 * "billed"          => a real per-token charge (cron API-key run, or proxy-metered interactive).
 * "shadow_would_be" => a would-be cost at API rates for a subscription session; reference, not an invoice.
 * "unclassified"    => cannot be confidently classified (legacy data, no-cost sessions, unknown prefix).
 *
 * The backend emits this as a snake_case string. Older/legacy rows omit the field entirely
 * (undefined), which the UI MUST treat the same as "unclassified" (no badge, backward-compatible).
 * Tolerate future unknown values by widening the type with an open string union member.
 */
export type CostBillingClass =
  | 'billed'
  | 'shadow_would_be'
  | 'unclassified'
  | (string & Record<never, never>); // forward-compatible: tolerate unknown future values without crashing

/** An aggregate cost block carrying coverage + the "prices as of" stamp (pricing_version). */
export interface MoneyAggregate {
  /** WhenWritingNull: omitted when null; arrives as `undefined`. Use `== null` to guard. */
  value_usd?: number | null;
  source: 'exact' | 'wire' | 'estimate' | 'unknown';
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  pricing_version?: string | null;
  coverage: Coverage;
  /**
   * Per-component cost breakdown. Present when the backend has exact token counts and
   * a priced model for this specific task. NULL for unknown-cost tasks and aggregate
   * totals spanning multiple models.
   * WhenWritingNull: omitted when null; arrives as `undefined`. Always guard with `== null`.
   */
  breakdown?: CostBreakdown | null;
  /** 6-value cost provenance string. Present on by_task rows and totals. WhenWritingNull: absent when null. */
  cost_provenance?: string | null;
  /**
   * Billing-class discriminator (T-2026-06-19-01 Phase 4). See CostBillingClass.
   * WhenWritingNull: omitted when null; arrives as `undefined`. Treat absent as "unclassified" (no badge).
   */
  cost_billing_class?: CostBillingClass | null;
}

/** One row in the cost_provenance_breakdown: a 6-value provenance bucket with its spend and task count. */
export interface ProvenanceBreakdownRow {
  provenance: string;
  spend_usd: number;
  count: number;
}

/** An aggregate token block carrying coverage. */
export interface TokenAggregate {
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  input?: number | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  output?: number | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  cache_creation?: number | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  cache_read?: number | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. Use `== null` to guard before ops. */
  total?: number | null;
  source: 'exact' | 'wire' | 'estimate' | 'unknown';
  coverage: Coverage;
}

/** How much of a window's time is measurable vs zero-duration vs unknown. */
export interface TimeCoverage {
  measured: number;
  zero_duration: number;
  unknown: number;
}

/**
 * Rollup totals. busy_ms_sum is a cpu-style aggregate (sum of per-task busy spans);
 * it MAY exceed wall_ms under concurrency and the field name says so.
 */
export interface RollupTotals {
  cost: MoneyAggregate;
  tokens: TokenAggregate;
  wall_ms: number;
  busy_ms_sum: number;
  time_coverage: TimeCoverage;
  request_count: number;
  event_count: number;
  /**
   * Estimation basis for the total cost. Present when cost was estimated
   * (scope_estimated or provider_estimated); null/absent when cost is exact or unavailable.
   * WhenWritingNull: omitted when null; arrives as `undefined`. Always guard with `== null`.
   */
  cost_estimation_basis?: CostEstimationBasis | null;
  /** Session-level 6-value cost provenance summary. Present when cost is estimated. */
  cost_provenance?: string | null;
  /** Per-provenance breakdown for the two-bucket known/assumed note. */
  cost_provenance_breakdown?: ProvenanceBreakdownRow[] | null;
  /**
   * Session-level billing-class discriminator (T-2026-06-19-01 Phase 4). See CostBillingClass.
   * WhenWritingNull: omitted when null; arrives as `undefined`. Treat absent as "unclassified" (no badge).
   */
  cost_billing_class?: CostBillingClass | null;
}

/** One partition row of a rollup (a top-level span task, or the unattributed remainder). */
export interface RollupRow {
  subject: TraceSubject;
  cost: MoneyAggregate;
  tokens: TokenAggregate;
  busy_ms: number;
  time_source: string;
  request_count: number;
  event_count: number;
  /**
   * Estimation basis for this row's cost. Present when cost was estimated
   * (scope_estimated or provider_estimated); null/absent when cost is exact or unavailable.
   * WhenWritingNull: omitted when null; arrives as `undefined`. Always guard with `== null`.
   */
  cost_estimation_basis?: CostEstimationBasis | null;
}

/** Body for kind=rollup. totals == sum(by_task) + unattributed, by construction. */
export interface RollupBody {
  subject: TraceSubject;
  window: { from?: string | null; to?: string | null };
  totals: RollupTotals;
  by_task: RollupRow[];
  unattributed: RollupRow;
}

/** The rollup envelope as served by GET /api/trace/rollup. */
export type RollupEnvelope = TraceEnvelope<RollupBody>;

// ---------------------------------------------------------------------------
// top_actions (GET /api/trace/top-actions?session=) -- item 10. Mirrors TopAction* DTOs.
// ---------------------------------------------------------------------------

/** One aggregated action row: a taxonomy category with count + total time. */
export interface TopActionRow {
  taxonomy_id: string;
  count: number;
  total_ms: number;
}

/** Body for kind=top_actions: the same rows ranked two ways (item 10). */
export interface TopActionsBody {
  taxonomy_version: string;
  by_count: TopActionRow[];
  by_time: TopActionRow[];
}

/** The top-actions envelope as served by GET /api/trace/top-actions. */
export type TopActionsEnvelope = TraceEnvelope<TopActionsBody>;

// ---------------------------------------------------------------------------
// Fetchers
// ---------------------------------------------------------------------------
//
// In dev, Vite proxies /api to Scope.Server on localhost:5130 (vite.config.ts). In
// production the SPA is served by Scope.Server itself, so the relative URL hits the
// same origin. All endpoints default to the local-full tier server-side.
//
// T-2026-06-14-13 (Lane F1): routes through the shared apiFetch client.
// Public function signatures are UNCHANGED so TraceView compiles without modification.

import { apiFetch } from './apiClient';

function fetchEnvelope<T>(path: string): Promise<T> {
  return apiFetch<T>(path);
}

/**
 * Fetches the sessions_list envelope.
 *
 * @param from - Optional ISO-8601 UTC start bound (overlap filter). Default: server-side last 24 h.
 * @param to - Optional ISO-8601 UTC end bound. Default: server-side "now".
 * @param includeDeleted - When true, includes soft-deleted sessions (deleted_at set). Default: false.
 * @param page - 1-based page number for server-side pagination (T-2026-06-17-03). Omit for legacy behavior.
 * @param pageSize - Records per page (10|25|50|100). Omit for legacy behavior.
 */
export function fetchTraceSessions(
  from?: string | null,
  to?: string | null,
  includeDeleted?: boolean,
  page?: number,
  pageSize?: number,
): Promise<SessionsListEnvelope> {
  const params = new URLSearchParams();
  if (from != null && from !== '') params.set('from', from);
  if (to != null && to !== '') params.set('to', to);
  if (includeDeleted === true) params.set('includeDeleted', 'true');
  if (page != null && page > 0) params.set('page', String(page));
  if (pageSize != null && pageSize > 0) params.set('pageSize', String(pageSize));
  // Legacy callers that don't pass page/pageSize get the server's default limit behavior.
  const qs = params.toString();
  return fetchEnvelope<SessionsListEnvelope>(`/api/trace/sessions${qs ? `?${qs}` : ''}`);
}

/**
 * Fetches the cron-sessions array.
 * NOTE: Returns a PLAIN JSON ARRAY (not a TraceEnvelope). Do NOT use fetchEnvelope.
 *
 * @param from - Optional ISO-8601 UTC start bound.
 * @param to - Optional ISO-8601 UTC end bound.
 * @param includeDeleted - When true, includes soft-deleted cron sessions. Default: false.
 */
export function fetchCronSessions(
  from?: string | null,
  to?: string | null,
  includeDeleted?: boolean,
): Promise<CronSession[]> {
  const params = new URLSearchParams();
  if (from != null && from !== '') params.set('from', from);
  if (to != null && to !== '') params.set('to', to);
  if (includeDeleted === true) params.set('includeDeleted', 'true');
  const qs = params.toString();
  return apiFetch<CronSession[]>(`/api/trace/cron-sessions${qs ? `?${qs}` : ''}`);
}

/**
 * Soft-deletes a session. Returns the session_key and the deleted_at timestamp.
 * URL-encodes the key.
 */
export function deleteSession(sessionKey: string): Promise<DeleteSessionResponse> {
  return apiFetch<DeleteSessionResponse>(
    `/api/trace/sessions/${encodeURIComponent(sessionKey)}`,
    { method: 'DELETE' },
  );
}

/**
 * Restores a soft-deleted session. Returns the session_key and deleted_at: null.
 * URL-encodes the key.
 */
export function restoreSession(sessionKey: string): Promise<RestoreSessionResponse> {
  return apiFetch<RestoreSessionResponse>(
    `/api/trace/sessions/${encodeURIComponent(sessionKey)}/restore`,
    { method: 'POST' },
  );
}

/** Fetches the session_graph envelope rooted at the given session key. */
export function fetchTraceGraph(sessionKey: string): Promise<SessionGraphEnvelope> {
  return fetchEnvelope<SessionGraphEnvelope>(`/api/trace/graph?session=${encodeURIComponent(sessionKey)}`);
}

/**
 * Fetches one page of the full trace for a session.
 *
 * Supports two pagination modes:
 * - Page-based (T-2026-06-17-03): pass `page` and `pageSize` for server-side pagination.
 *   The response body carries `total_count` for the Pagination component.
 * - Cursor-based (legacy): pass `cursor` for the keyset cursor from a prior page.
 *   Omit `page`/`pageSize` for cursor-based mode.
 *
 * @param sessionKey - The session key to fetch the trace for.
 * @param limit - Max records per page (used in cursor-based mode). Default: 300.
 * @param cursor - Opaque next_cursor from a prior page (keyset over created_at,id). Null for first page.
 * @param page - 1-based page number for page-based mode. Omit for cursor-based mode.
 * @param pageSize - Records per page for page-based mode (10|25|50|100). Omit for cursor-based mode.
 */
export function fetchTraceFull(
  sessionKey: string,
  limit = 300,
  cursor: string | null = null,
  page?: number,
  pageSize?: number,
): Promise<TraceEnvelopeFull> {
  const params = new URLSearchParams({ session: sessionKey });
  if (page != null && page > 0 && pageSize != null && pageSize > 0) {
    // Page-based mode: server uses page/pageSize, ignores limit/cursor.
    params.set('page', String(page));
    params.set('pageSize', String(pageSize));
  } else {
    // Cursor-based legacy mode.
    params.set('limit', String(limit));
    if (cursor != null && cursor !== '') params.set('cursor', cursor);
  }
  return fetchEnvelope<TraceEnvelopeFull>(`/api/trace/full?${params.toString()}`);
}

/**
 * Fetches the accounting rollup envelope (header totals + by_task partition) for a session.
 *
 * @param assumedModel - Optional model id to use as the cost estimation peg for scope_estimated
 *   tasks (no recorded model). When omitted the server uses its default (claude-sonnet-4-6).
 *   Invalid/unrecognized values are silently ignored server-side (falls back to Sonnet default).
 */
export function fetchTraceRollup(sessionKey: string, assumedModel?: string): Promise<RollupEnvelope> {
  const params = new URLSearchParams({ session: sessionKey });
  if (assumedModel != null && assumedModel !== '') params.set('assumedModel', assumedModel);
  return fetchEnvelope<RollupEnvelope>(`/api/trace/rollup?${params.toString()}`);
}

/** Fetches the top-actions envelope (ranked by count and by time, item 10) for a session. */
export function fetchTraceTopActions(sessionKey: string, limit = 20): Promise<TopActionsEnvelope> {
  const params = new URLSearchParams({ session: sessionKey, limit: String(limit) });
  return fetchEnvelope<TopActionsEnvelope>(`/api/trace/top-actions?${params.toString()}`);
}

/**
 * Fetches the base-launch cost rollup (GET /api/command/base-launches).
 * Groups per-CLI-launch total cost == SUM of its /clear-split generations.
 * Two modes:
 *   1. Window days: pass windowDays; from/to are null/omitted.
 *   2. Explicit range: pass from and to as ISO-8601 UTC strings.
 * Always 200; returns an empty by_base array when no spend exists.
 * T-2026-06-19-03 Wave 2 / ST-07.
 *
 * @param windowDays - Number of days to look back (1..365). Ignored when from+to provided.
 * @param from - Optional ISO-8601 UTC start bound.
 * @param to - Optional ISO-8601 UTC end bound.
 */
export function fetchBaseLaunches(
  windowDays = 30,
  from?: string | null,
  to?: string | null,
): Promise<CommandBaseLaunchResponse> {
  if (from != null && from !== '' && to != null && to !== '') {
    const params = new URLSearchParams({ from_utc: from, to_utc: to });
    return apiFetch<CommandBaseLaunchResponse>(`/api/command/base-launches?${params.toString()}`);
  }
  const clamped = Math.min(365, Math.max(1, Math.round(windowDays)));
  return apiFetch<CommandBaseLaunchResponse>(`/api/command/base-launches?window_days=${String(clamped)}`);
}
