// =============================================================================
// .scopetrace format spec v1 — versioned, self-contained JSON export schema
// (social-trace-export ST-01, roadmap internal design notes).
//
// This is a SEPARATE, purpose-built artifact format from the live scope-trace-v1
// API contract (Scope.Core.Telemetry.Trace.TraceContract.cs / frontend/src/api/traceApi.ts).
// scope-trace-v1 is a live envelope with local-full content fields (query_text,
// summaries, snippets, labels, error_message, ...). .scopetrace is a portable,
// redacted-by-construction export meant to LEAVE the machine (social sharing,
// the future standalone website viewer, ST-05). It intentionally carries far
// less than scope-trace-v1.
//
// REDACTION IS OMIT-AT-EXPORT (preservedDecisions in the roadmap): risky-tier
// bytes are NEVER written to the file. This module enforces that by
// CONSTRUCTION — no type below has a field that can carry tool input/output,
// file contents, or request/response bodies. There is no "redacted: true" flag
// hiding content client-side; the content is structurally absent from the type
// tree. See `findRiskyTierFieldName` at the bottom for a runtime scanner a
// future exporter (ST-03) or a test can use as a second line of defense against
// an ad-hoc/malformed producer.
//
// FORMAT VALIDATION (decision D2, owner-recommended default applied here):
// version + generator stamp only for v1. No MAC/HMAC — the viewer's trust
// anchor is the branded viewer, not a file signature. A checksum is additive
// future work, not a v1 requirement.
//
// SAFE TIER (roadmap preservedDecisions — the ONLY content this format carries):
//   - session/subject identity (kind, id, display_id, alias, agent type, host)
//   - privileged user submits (T-2026-06-16-19 event class): the human prompt(s)
//     ARE the shareable hook by explicit product decision, unlike ordinary tool
//     content. `text` on ScopeTraceUserSubmit is the one free-text field this
//     format intentionally carries.
//   - token I/O counts, cost + provenance, model + model provenance
//   - timing/duration, status
//   - timeline STRUCTURE: span/event/request types, order, span ids/parenting.
//     No bodies.
//
// RISKY TIER (must NEVER appear in this type tree): tool call input/output,
// file contents, request/response bodies, and every scope-trace-v1 local-full
// free-text field on a timeline entry (label, query_text, input_summary,
// output_summary, input_snippet, output_snippet, parameters_json,
// error_message, message_recipient). `ScopeTraceTimelineEntry` below is
// deliberately narrow; do not widen it without a owner decision that changes
// the roadmap's preservedDecisions.
//
// T-2026-06-16-19 STATUS NOTE: the privileged user-submit event class has since
// landed server-side (TraceEntry.UserSubmitSubtype, 7 members) and in the /trace
// UI (frontend/src/lib/traceKind.ts). ST-01 (this module) models the REAL
// subtype vocabulary rather than a bare placeholder, but the exporter that
// populates ScopeTraceUserSubmit from live data is ST-03 (not built here) — so
// `text` may legitimately be an empty string as a placeholder until ST-03 wires
// real content, or after ST-04 review-before-share redacts it.
// =============================================================================

/** The one supported file-format discriminator. */
export const SCOPE_TRACE_FORMAT = 'scopetrace' as const;
export type ScopeTraceFormat = typeof SCOPE_TRACE_FORMAT;

/** Currently supported schema version. Bump when the body shape changes incompatibly. */
export const SCOPE_TRACE_SCHEMA_VERSION = 1 as const;
export type ScopeTraceSchemaVersion = typeof SCOPE_TRACE_SCHEMA_VERSION;

/** Generator binary identity, mirrors TraceGenerator (app + version), for forensics on schema drift. */
export interface ScopeTraceGenerator {
  app: string;
  version: string;
}

// ---------------------------------------------------------------------------
// Closed enums (safe-tier structural values only)
// ---------------------------------------------------------------------------

/** Operational run/session status. Mirrors the app's RunStatus vocabulary (frontend/src/lib/status.ts). */
export type ScopeTraceStatus =
  | 'ready'
  | 'queued'
  | 'running'
  | 'paused'
  | 'skipped'
  | 'failed'
  | 'blocked'
  | 'complete'
  | 'unknown';

export const SCOPE_TRACE_STATUSES: readonly ScopeTraceStatus[] = [
  'ready',
  'queued',
  'running',
  'paused',
  'skipped',
  'failed',
  'blocked',
  'complete',
  'unknown',
] as const;

/** Cost provenance. Mirrors the app's CostProvenance vocabulary (frontend/src/lib/provenance.ts). */
export type ScopeTraceCostProvenance =
  | 'wire_exact'
  | 'joined_exact'
  | 'provider_estimated'
  | 'scope_estimated'
  | 'unmetered'
  | 'unavailable';

export const SCOPE_TRACE_COST_PROVENANCES: readonly ScopeTraceCostProvenance[] = [
  'wire_exact',
  'joined_exact',
  'provider_estimated',
  'scope_estimated',
  'unmetered',
  'unavailable',
] as const;

/** Model-identity provenance. Mirrors TraceEntry.ModelProvenance's 3-value fallback ladder. */
export type ScopeTraceModelProvenance = 'wire' | 'recorded' | 'unavailable';

export const SCOPE_TRACE_MODEL_PROVENANCES: readonly ScopeTraceModelProvenance[] = [
  'wire',
  'recorded',
  'unavailable',
] as const;

/** Ratified user_submit subtype (T-2026-06-16-19), plus `unknown` for forward compatibility. */
export type ScopeTraceUserSubmitSubtype =
  | 'initiating_prompt'
  | 'follow_up'
  | 'interrupt'
  | 'tool_approval'
  | 'tool_denial'
  | 'redirection'
  | 'cron_prompt'
  | 'unknown';

export const SCOPE_TRACE_USER_SUBMIT_SUBTYPES: readonly ScopeTraceUserSubmitSubtype[] = [
  'initiating_prompt',
  'follow_up',
  'interrupt',
  'tool_approval',
  'tool_denial',
  'redirection',
  'cron_prompt',
  'unknown',
] as const;

/** Timeline entry discriminator. Mirrors scope-trace-v1 TraceEntry.kind. */
export type ScopeTraceTimelineEntryKind = 'span' | 'event' | 'request';

export const SCOPE_TRACE_TIMELINE_ENTRY_KINDS: readonly ScopeTraceTimelineEntryKind[] = [
  'span',
  'event',
  'request',
] as const;

/** The 9-bucket trace tool type. Mirrors frontend/src/lib/traceToolType.ts TraceToolType. */
export type ScopeTraceToolType =
  | 'find'
  | 'pack'
  | 'hook'
  | 'edit'
  | 'model'
  | 'shell'
  | 'external'
  | 'operator'
  | 'system';

export const SCOPE_TRACE_TOOL_TYPES: readonly ScopeTraceToolType[] = [
  'find',
  'pack',
  'hook',
  'edit',
  'model',
  'shell',
  'external',
  'operator',
  'system',
] as const;

// ---------------------------------------------------------------------------
// Narrowing helpers (never throw; safe fallback per codebase convention —
// see toRunStatus/toCostProvenance in status.ts/provenance.ts).
// ---------------------------------------------------------------------------

function makeNarrower<T extends string>(valid: readonly T[], fallback: T): (raw: string | null | undefined) => T {
  const set = new Set<string>(valid);
  return (raw) => {
    if (raw != null && set.has(raw)) return raw as T;
    return fallback;
  };
}

/** Narrows an arbitrary string to ScopeTraceStatus. Unknown/absent values fall back to 'unknown'. */
export const toScopeTraceStatus = makeNarrower(SCOPE_TRACE_STATUSES, 'unknown');

/** Narrows an arbitrary string to ScopeTraceCostProvenance. Unknown/absent values fall back to 'unavailable'. */
export const toScopeTraceCostProvenance = makeNarrower(SCOPE_TRACE_COST_PROVENANCES, 'unavailable');

/** Narrows an arbitrary string to ScopeTraceModelProvenance. Unknown/absent values fall back to 'unavailable'. */
export const toScopeTraceModelProvenance = makeNarrower(SCOPE_TRACE_MODEL_PROVENANCES, 'unavailable');

/** Narrows an arbitrary string to ScopeTraceUserSubmitSubtype. Unknown/absent values fall back to 'unknown'. */
export const toScopeTraceUserSubmitSubtype = makeNarrower(SCOPE_TRACE_USER_SUBMIT_SUBTYPES, 'unknown');

// ---------------------------------------------------------------------------
// Body shapes
// ---------------------------------------------------------------------------

/** Session/subject identity. No free-text label field (that is scope-trace-v1 local-full only). */
export interface ScopeTraceSubject {
  kind: string;
  id: string;
  display_id: string;
  alias?: string | null;
  agent_type?: string | null;
  agent_subtype?: string | null;
  host?: string | null;
}

/** Tagged token block. Mirrors TokenBlock (Scope.Core.Telemetry.Trace.TraceContract.cs), content-free. */
export interface ScopeTraceTokens {
  input: number | null;
  output: number | null;
  cache_creation: number | null;
  cache_read: number | null;
  total: number | null;
}

/** Tagged monetary block. Mirrors Money's provenance-carrying fields, content-free. */
export interface ScopeTraceCost {
  value_usd: number | null;
  provenance: ScopeTraceCostProvenance;
  pricing_version?: string | null;
  model_id?: string | null;
  provider?: string | null;
}

/** Model identity + how it was determined. See ScopeTraceModelProvenance. */
export interface ScopeTraceModel {
  model_id: string | null;
  provenance: ScopeTraceModelProvenance;
}

/** Timing/duration for the session. */
export interface ScopeTraceTiming {
  started_at: string | null;
  completed_at: string | null;
  duration_ms: number | null;
}

/**
 * A privileged human-submitted input (T-2026-06-16-19). `text` is the ONE
 * free-text field this whole format carries, by explicit product decision
 * (the human prompt is the shareable hook). May be an empty string as a
 * PLACEHOLDER when the exporter has not populated it yet, or after
 * review-before-share redaction (ST-04).
 */
export interface ScopeTraceUserSubmit {
  id: string;
  subtype: ScopeTraceUserSubmitSubtype;
  turn_index: number | null;
  created_at: string | null;
  text: string;
}

/**
 * Timeline entry: STRUCTURE ONLY. Deliberately excludes every scope-trace-v1
 * local-full content field (label, query_text, input_summary, output_summary,
 * input_snippet, output_snippet, parameters_json, error_message,
 * message_recipient) and any tool input/output. Adding a free-text field back
 * here is a regression against the safe-tier contract — do not do it without
 * a owner decision that changes the roadmap's preservedDecisions.
 */
export interface ScopeTraceTimelineEntry {
  id: string;
  kind: ScopeTraceTimelineEntryKind;
  order: number;
  span_id?: string | null;
  parent_span_id?: string | null;
  tool_type?: ScopeTraceToolType | null;
  status?: ScopeTraceStatus | null;
  started_at?: string | null;
  completed_at?: string | null;
  duration_ms?: number | null;
}

/** The full safe-tier body of one exported session. */
export interface ScopeTraceBody {
  subject: ScopeTraceSubject;
  status: ScopeTraceStatus;
  timing: ScopeTraceTiming;
  model: ScopeTraceModel;
  tokens: ScopeTraceTokens;
  cost: ScopeTraceCost;
  user_submits: ScopeTraceUserSubmit[];
  timeline: ScopeTraceTimelineEntry[];
}

/**
 * The .scopetrace v1 envelope. `format` + `schema_version` + `generator` are
 * the validation stamp (decision D2): no MAC/HMAC in v1.
 */
export interface ScopeTraceEnvelope {
  format: ScopeTraceFormat;
  schema_version: ScopeTraceSchemaVersion;
  generated_at: string;
  generator: ScopeTraceGenerator;
  body: ScopeTraceBody;
}

// ---------------------------------------------------------------------------
// Risky-tier runtime scanner (defense in depth; the type tree above already
// makes these structurally absent for anything built through the types).
// A future exporter (ST-03) or a test can use this to prove real output bytes
// carry none of these keys, recursively, anywhere in the document.
// ---------------------------------------------------------------------------

/**
 * Field names that must NEVER appear anywhere in a .scopetrace document.
 * Covers both the scope-trace-v1 local-full content fields and generic
 * risky-tier names (tool I/O, payload/file/request/response bodies).
 * Deliberately NOT widened to `readonly string[]` so `RiskyTierFieldName`
 * below stays a literal-string union usable in the compile-time guard.
 */
export const RISKY_TIER_FIELD_NAMES = [
  'toolInput',
  'tool_input',
  'toolOutput',
  'tool_output',
  'payloadBody',
  'payload_body',
  'fileContent',
  'file_content',
  'file_contents',
  'requestBody',
  'request_body',
  'responseBody',
  'response_body',
  'queryText',
  'query_text',
  'inputSummary',
  'input_summary',
  'outputSummary',
  'output_summary',
  'inputSnippet',
  'input_snippet',
  'outputSnippet',
  'output_snippet',
  'parametersJson',
  'parameters_json',
  'errorMessage',
  'error_message',
  'messageRecipient',
  'message_recipient',
  'label',
] as const;

const RISKY_TIER_FIELD_NAME_SET = new Set<string>(RISKY_TIER_FIELD_NAMES);

/** The literal-string union of every risky-tier field name. */
export type RiskyTierFieldName = (typeof RISKY_TIER_FIELD_NAMES)[number];

/**
 * Recursively scans a parsed JSON value for any key in RISKY_TIER_FIELD_NAMES.
 * Returns the first offending key found, or null when the document is clean.
 * Never throws (JSON.parse output is always acyclic; recursion is bounded by
 * document depth).
 */
export function findRiskyTierFieldName(value: unknown): string | null {
  if (value === null || typeof value !== 'object') return null;
  if (Array.isArray(value)) {
    for (const item of value) {
      const found = findRiskyTierFieldName(item);
      if (found !== null) return found;
    }
    return null;
  }
  for (const [key, nested] of Object.entries(value as Record<string, unknown>)) {
    if (RISKY_TIER_FIELD_NAME_SET.has(key)) return key;
    const found = findRiskyTierFieldName(nested);
    if (found !== null) return found;
  }
  return null;
}

// ---------------------------------------------------------------------------
// Compile-time structural-absence proof.
//
// IsRiskyFree<T> resolves to the literal type `true` when none of T's own
// keys are a risky-tier field name, and to `false` otherwise. The guard
// object below assigns the literal `true` to a property typed `IsRiskyFree<T>`
// for every safe-tier interface in this file. If a future edit ever adds a
// risky-tier field name (e.g. `label`, `query_text`, `tool_input`) to one of
// these interfaces, the corresponding property's type collapses to `false`
// and assigning `true` to it fails `tsc` — this file (and therefore
// `npm run check`) stops compiling. This is the "structurally impossible by
// construction" guarantee made mechanically checkable, not just documented.
// ---------------------------------------------------------------------------

type IsRiskyFree<T> = Extract<keyof T, RiskyTierFieldName> extends never ? true : false;

/**
 * Exported so this module's guarantee is checked wherever the module is
 * type-checked (build/typecheck), and so `noUnusedLocals` never flags it.
 * Not intended for runtime use.
 */
export const SCOPE_TRACE_RISKY_TIER_TYPE_GUARD: {
  generator: IsRiskyFree<ScopeTraceGenerator>;
  subject: IsRiskyFree<ScopeTraceSubject>;
  tokens: IsRiskyFree<ScopeTraceTokens>;
  cost: IsRiskyFree<ScopeTraceCost>;
  model: IsRiskyFree<ScopeTraceModel>;
  timing: IsRiskyFree<ScopeTraceTiming>;
  userSubmit: IsRiskyFree<ScopeTraceUserSubmit>;
  timelineEntry: IsRiskyFree<ScopeTraceTimelineEntry>;
  body: IsRiskyFree<ScopeTraceBody>;
  envelope: IsRiskyFree<ScopeTraceEnvelope>;
} = {
  generator: true,
  subject: true,
  tokens: true,
  cost: true,
  model: true,
  timing: true,
  userSubmit: true,
  timelineEntry: true,
  body: true,
  envelope: true,
};
