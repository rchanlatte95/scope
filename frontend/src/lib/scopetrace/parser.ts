// =============================================================================
// .scopetrace v1 parser/validator (social-trace-export ST-01).
//
// parseScopeTrace turns an untrusted JSON string (or already-parsed value) into
// a typed ScopeTraceEnvelope. It NEVER trusts the input by spreading it: every
// field on the returned envelope is copied over individually, one known-safe
// key at a time. This is "parse, don't validate" applied to the safety
// requirement — even if a producer's JSON carries extra/risky keys the type
// does not declare, parseScopeTrace's output cannot carry them, because the
// output is built field-by-field from the ScopeTraceEnvelope shape only.
//
// Envelope-level required fields (format, schema_version, generated_at,
// generator, body) are validated strictly: missing -> missing_field, wrong
// format -> invalid_field, unsupported schema_version -> unsupported_version.
// Nested body enum values use the never-throw narrowing helpers from types.ts
// (unrecognized values fall back to a safe default rather than failing the
// whole parse), matching the rest of the codebase's status/provenance
// narrowing convention.
// =============================================================================

import {
  SCOPE_TRACE_FORMAT,
  SCOPE_TRACE_SCHEMA_VERSION,
  toScopeTraceCostProvenance,
  toScopeTraceModelProvenance,
  toScopeTraceStatus,
  toScopeTraceUserSubmitSubtype,
  SCOPE_TRACE_TIMELINE_ENTRY_KINDS,
  SCOPE_TRACE_TOOL_TYPES,
  type ScopeTraceBody,
  type ScopeTraceCost,
  type ScopeTraceEnvelope,
  type ScopeTraceGenerator,
  type ScopeTraceModel,
  type ScopeTraceSubject,
  type ScopeTraceTimelineEntry,
  type ScopeTraceTimelineEntryKind,
  type ScopeTraceTiming,
  type ScopeTraceToolType,
  type ScopeTraceTokens,
  type ScopeTraceUserSubmit,
} from './types';

/** A structured, discriminated parse failure. `field` uses dotted paths (e.g. "body.subject.id"). */
export type ScopeTraceParseError =
  | { code: 'invalid_json'; message: string }
  | { code: 'not_an_object'; field: string; message: string }
  | { code: 'missing_field'; field: string; message: string }
  | { code: 'invalid_field'; field: string; message: string }
  | { code: 'unsupported_version'; field: string; found: unknown; supported: number; message: string };

export type ScopeTraceParseResult =
  | { ok: true; value: ScopeTraceEnvelope }
  | { ok: false; error: ScopeTraceParseError };

// ---------------------------------------------------------------------------
// Small unknown-value readers. None throw; each returns null on mismatch so
// callers decide whether absence/mismatch is fatal for that field.
// ---------------------------------------------------------------------------

function asRecord(value: unknown): Record<string, unknown> | null {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return null;
  return value as Record<string, unknown>;
}

function asString(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}

function asStringOrNull(value: unknown): string | null {
  if (value === undefined) return null;
  return asString(value);
}

function asNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function asNumberOrNull(value: unknown): number | null {
  if (value === undefined) return null;
  return asNumber(value);
}

function asArray(value: unknown): unknown[] | null {
  return Array.isArray(value) ? value : null;
}

// ---------------------------------------------------------------------------
// Nested body parsers. These are lenient on VALUE (narrow with safe fallback)
// but the caller decides which nested objects are load-bearing enough to
// require presence.
// ---------------------------------------------------------------------------

function parseGenerator(raw: unknown): ScopeTraceGenerator | null {
  const rec = asRecord(raw);
  if (rec === null) return null;
  const app = asString(rec.app);
  const version = asString(rec.version);
  if (app === null || version === null) return null;
  return { app, version };
}

function parseSubject(raw: Record<string, unknown>): ScopeTraceSubject | null {
  const kind = asString(raw.kind);
  const id = asString(raw.id);
  const displayId = asString(raw.display_id);
  if (kind === null || id === null || displayId === null) return null;
  const subject: ScopeTraceSubject = { kind, id, display_id: displayId };
  const alias = asStringOrNull(raw.alias);
  if (alias !== null) subject.alias = alias;
  const agentType = asStringOrNull(raw.agent_type);
  if (agentType !== null) subject.agent_type = agentType;
  const agentSubtype = asStringOrNull(raw.agent_subtype);
  if (agentSubtype !== null) subject.agent_subtype = agentSubtype;
  const host = asStringOrNull(raw.host);
  if (host !== null) subject.host = host;
  return subject;
}

function parseTokens(raw: unknown): ScopeTraceTokens {
  const rec = asRecord(raw) ?? {};
  return {
    input: asNumberOrNull(rec.input),
    output: asNumberOrNull(rec.output),
    cache_creation: asNumberOrNull(rec.cache_creation),
    cache_read: asNumberOrNull(rec.cache_read),
    total: asNumberOrNull(rec.total),
  };
}

function parseCost(raw: unknown): ScopeTraceCost {
  const rec = asRecord(raw) ?? {};
  const cost: ScopeTraceCost = {
    value_usd: asNumberOrNull(rec.value_usd),
    provenance: toScopeTraceCostProvenance(asString(rec.provenance)),
  };
  const pricingVersion = asStringOrNull(rec.pricing_version);
  if (pricingVersion !== null) cost.pricing_version = pricingVersion;
  const modelId = asStringOrNull(rec.model_id);
  if (modelId !== null) cost.model_id = modelId;
  const provider = asStringOrNull(rec.provider);
  if (provider !== null) cost.provider = provider;
  return cost;
}

function parseModel(raw: unknown): ScopeTraceModel {
  const rec = asRecord(raw) ?? {};
  return {
    model_id: asStringOrNull(rec.model_id),
    provenance: toScopeTraceModelProvenance(asString(rec.provenance)),
  };
}

function parseTiming(raw: unknown): ScopeTraceTiming {
  const rec = asRecord(raw) ?? {};
  return {
    started_at: asStringOrNull(rec.started_at),
    completed_at: asStringOrNull(rec.completed_at),
    duration_ms: asNumberOrNull(rec.duration_ms),
  };
}

function parseUserSubmit(raw: unknown): ScopeTraceUserSubmit | null {
  const rec = asRecord(raw);
  if (rec === null) return null;
  const id = asString(rec.id);
  if (id === null) return null;
  return {
    id,
    subtype: toScopeTraceUserSubmitSubtype(asString(rec.subtype)),
    turn_index: asNumberOrNull(rec.turn_index),
    created_at: asStringOrNull(rec.created_at),
    // text is the one intentional free-text field (safe tier by decision);
    // absent/non-string input degrades to '' (placeholder), never fabricated.
    text: asString(rec.text) ?? '',
  };
}

function toTimelineEntryKind(raw: unknown): ScopeTraceTimelineEntryKind | null {
  const s = asString(raw);
  if (s === null) return null;
  return (SCOPE_TRACE_TIMELINE_ENTRY_KINDS as readonly string[]).includes(s)
    ? (s as ScopeTraceTimelineEntryKind)
    : null;
}

function toToolTypeOrNull(raw: unknown): ScopeTraceToolType | null {
  const s = asString(raw);
  if (s === null) return null;
  return (SCOPE_TRACE_TOOL_TYPES as readonly string[]).includes(s) ? (s as ScopeTraceToolType) : null;
}

function parseTimelineEntry(raw: unknown): ScopeTraceTimelineEntry | null {
  const rec = asRecord(raw);
  if (rec === null) return null;
  const id = asString(rec.id);
  const kind = toTimelineEntryKind(rec.kind);
  const order = asNumber(rec.order);
  if (id === null || kind === null || order === null) return null;

  const entry: ScopeTraceTimelineEntry = { id, kind, order };
  const spanId = asStringOrNull(rec.span_id);
  if (spanId !== null) entry.span_id = spanId;
  const parentSpanId = asStringOrNull(rec.parent_span_id);
  if (parentSpanId !== null) entry.parent_span_id = parentSpanId;
  const toolType = toToolTypeOrNull(rec.tool_type);
  if (toolType !== null) entry.tool_type = toolType;
  const statusRaw = asString(rec.status);
  if (statusRaw !== null) entry.status = toScopeTraceStatus(statusRaw);
  const startedAt = asStringOrNull(rec.started_at);
  if (startedAt !== null) entry.started_at = startedAt;
  const completedAt = asStringOrNull(rec.completed_at);
  if (completedAt !== null) entry.completed_at = completedAt;
  const durationMs = asNumberOrNull(rec.duration_ms);
  if (durationMs !== null) entry.duration_ms = durationMs;
  return entry;
}

function parseBody(raw: unknown): { body: ScopeTraceBody } | { error: ScopeTraceParseError } {
  const rec = asRecord(raw);
  if (rec === null) {
    return { error: { code: 'not_an_object', field: 'body', message: '"body" must be an object.' } };
  }

  const subjectRec = asRecord(rec.subject);
  if (subjectRec === null) {
    return {
      error: { code: 'missing_field', field: 'body.subject', message: '"body.subject" is required and must be an object.' },
    };
  }
  const subject = parseSubject(subjectRec);
  if (subject === null) {
    return {
      error: {
        code: 'missing_field',
        field: 'body.subject',
        message: '"body.subject" requires string "kind", "id", and "display_id".',
      },
    };
  }

  const userSubmitsRaw = asArray(rec.user_submits) ?? [];
  const userSubmits: ScopeTraceUserSubmit[] = [];
  for (const item of userSubmitsRaw) {
    const parsed = parseUserSubmit(item);
    if (parsed !== null) userSubmits.push(parsed);
  }

  const timelineRaw = asArray(rec.timeline) ?? [];
  const timeline: ScopeTraceTimelineEntry[] = [];
  for (const item of timelineRaw) {
    const parsed = parseTimelineEntry(item);
    if (parsed !== null) timeline.push(parsed);
  }

  const body: ScopeTraceBody = {
    subject,
    status: toScopeTraceStatus(asString(rec.status)),
    timing: parseTiming(rec.timing),
    model: parseModel(rec.model),
    tokens: parseTokens(rec.tokens),
    cost: parseCost(rec.cost),
    user_submits: userSubmits,
    timeline,
  };
  return { body };
}

/**
 * Parses a .scopetrace document from a JSON string or an already-parsed value.
 * Rejects: invalid JSON, a non-object root, a missing/wrong "format", a
 * missing "schema_version", and an unsupported "schema_version" value.
 * Never throws.
 */
export function parseScopeTrace(input: unknown): ScopeTraceParseResult {
  let parsed: unknown;
  if (typeof input === 'string') {
    try {
      parsed = JSON.parse(input);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Could not parse JSON.';
      return { ok: false, error: { code: 'invalid_json', message } };
    }
  } else {
    parsed = input;
  }

  const rec = asRecord(parsed);
  if (rec === null) {
    return {
      ok: false,
      error: { code: 'not_an_object', field: '$', message: 'The .scopetrace document root must be a JSON object.' },
    };
  }

  if (!('format' in rec)) {
    return {
      ok: false,
      error: { code: 'missing_field', field: 'format', message: '"format" is required.' },
    };
  }
  const format = asString(rec.format);
  if (format !== SCOPE_TRACE_FORMAT) {
    return {
      ok: false,
      error: {
        code: 'invalid_field',
        field: 'format',
        message: `"format" must be "${SCOPE_TRACE_FORMAT}", got ${JSON.stringify(rec.format)}.`,
      },
    };
  }

  if (!('schema_version' in rec)) {
    return {
      ok: false,
      error: { code: 'missing_field', field: 'schema_version', message: '"schema_version" is required.' },
    };
  }
  const schemaVersion = asNumber(rec.schema_version);
  if (schemaVersion === null || schemaVersion !== SCOPE_TRACE_SCHEMA_VERSION) {
    return {
      ok: false,
      error: {
        code: 'unsupported_version',
        field: 'schema_version',
        found: rec.schema_version,
        supported: SCOPE_TRACE_SCHEMA_VERSION,
        message: `Unsupported .scopetrace schema_version ${JSON.stringify(rec.schema_version)}; this reader supports ${String(SCOPE_TRACE_SCHEMA_VERSION)}.`,
      },
    };
  }

  if (!('generated_at' in rec)) {
    return {
      ok: false,
      error: { code: 'missing_field', field: 'generated_at', message: '"generated_at" is required.' },
    };
  }
  const generatedAt = asString(rec.generated_at);
  if (generatedAt === null) {
    return {
      ok: false,
      error: { code: 'invalid_field', field: 'generated_at', message: '"generated_at" must be a string.' },
    };
  }

  if (!('generator' in rec)) {
    return {
      ok: false,
      error: { code: 'missing_field', field: 'generator', message: '"generator" is required.' },
    };
  }
  const generator = parseGenerator(rec.generator);
  if (generator === null) {
    return {
      ok: false,
      error: {
        code: 'invalid_field',
        field: 'generator',
        message: '"generator" must be an object with string "app" and "version".',
      },
    };
  }

  if (!('body' in rec)) {
    return { ok: false, error: { code: 'missing_field', field: 'body', message: '"body" is required.' } };
  }
  const bodyResult = parseBody(rec.body);
  if ('error' in bodyResult) {
    return { ok: false, error: bodyResult.error };
  }

  // format/schemaVersion are verified equal to the literal constants above, but their
  // static types remain the widened `string`/`number` (equality checks against a wide
  // type do not narrow to a literal member type) — use the constants directly here.
  const envelope: ScopeTraceEnvelope = {
    format: SCOPE_TRACE_FORMAT,
    schema_version: SCOPE_TRACE_SCHEMA_VERSION,
    generated_at: generatedAt,
    generator,
    body: bodyResult.body,
  };
  return { ok: true, value: envelope };
}

/** Serializes a ScopeTraceEnvelope to a JSON string. Pure passthrough; no redaction logic (see ST-03/ST-06). */
export function serializeScopeTrace(envelope: ScopeTraceEnvelope): string {
  return JSON.stringify(envelope);
}
