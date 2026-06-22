// =============================================================================
// Cron page pure logic (T-2026-06-12-20, CR-M1E; humanizer added T-2026-06-13-35).
//
// DOM-free helpers so they unit-test under the node-environment Vitest config.
// These are CLIENT-SIDE HINTS only: the server (CronService) is the authoritative
// validator. The form uses these to give fast feedback and to decide whether to
// attempt a submit, but it always surfaces the server's verbatim 400 body when the
// server disagrees. Nothing here re-implements server validation as truth.
// =============================================================================

import cronstrue from 'cronstrue';

/** Harness ids the create form offers. No default is implied by ordering. */
export const HARNESS_IDS = ['claude-code', 'codex'] as const;
export type HarnessId = (typeof HARNESS_IDS)[number];

/** The sentinel the harness <select> starts on: forces an explicit user choice. */
export const HARNESS_UNCHOSEN = '';

/** Common 5-field cron shorthands surfaced as sugar, expanded client-side for the hint only. */
export const SCHEDULE_SUGAR: Record<string, string> = {
  '@hourly': '0 * * * *',
  '@daily': '0 0 * * *',
};

/** Expands an @hourly/@daily shorthand to a 5-field expression, or returns the input as-is. */
export function expandScheduleSugar(schedule: string): string {
  const trimmed = schedule.trim();
  return SCHEDULE_SUGAR[trimmed.toLowerCase()] ?? trimmed;
}

/**
 * Lightweight 5-field cron shape check (client hint only). Accepts the @hourly/@daily
 * sugar, then verifies exactly five whitespace-separated fields each made of the cron
 * vocabulary (digits, * , - /). Does NOT validate ranges/step semantics -- the server
 * owns that. Returns null when the shape is plausible, or a short reason otherwise.
 */
export function scheduleHint(schedule: string): string | null {
  const value = expandScheduleSugar(schedule);
  if (value === '') return 'Enter a 5-field cron expression (UTC), or @hourly / @daily.';
  const fields = value.split(/\s+/);
  if (fields.length !== 5) {
    return `A cron expression has 5 fields (min hour day month weekday); got ${String(fields.length)}.`;
  }
  const fieldShape = /^[*\d,\-/]+$/;
  for (const field of fields) {
    if (!fieldShape.test(field)) {
      return `Field "${field}" is not a valid cron field. The server validates ranges and steps.`;
    }
  }
  return null;
}

/** Result of the form's pre-submit shape check: ok, or the first blocking reason. */
export interface FormValidation {
  ok: boolean;
  reason: string | null;
}

/** Inputs the form gathers before a create or edit attempt. */
export interface CronFormState {
  label: string;
  repoPath: string;
  schedule: string;
  prompt: string;
  harness: string;
  model: string;
  tokenBudget: string;
  /**
   * R7 concurrency guard toggle (T-2026-06-17-05). True blocks a second concurrent run-now.
   * Surfaced in both create and edit so it is no longer stuck at its creation default.
   */
  blockConcurrentRuns: boolean;
  /**
   * Optional per-agent wall-clock cutoff in seconds, as a raw string (empty = use the global
   * default). Validated the same way as the token budget: blank -> omit, else a positive integer.
   */
  maxRuntimeSeconds: string;
}

/**
 * Pre-submit shape check (client hint). Enforces presence of required fields and the
 * EXPLICIT, no-default harness choice; surfaces schedule and token-budget shape hints.
 * The server still validates everything; this only stops obviously-empty submits and
 * makes the no-default harness rule a hard client gate.
 */
export function validateForm(state: CronFormState): FormValidation {
  if (!state.label.trim()) return { ok: false, reason: 'Label is required.' };
  if (!state.repoPath.trim()) return { ok: false, reason: 'Repository path is required.' };
  if (!state.prompt.trim()) return { ok: false, reason: 'Prompt is required.' };

  // Harness is intentionally not defaulted: the user must actively choose a harness.
  if (state.harness === HARNESS_UNCHOSEN) {
    return { ok: false, reason: 'Choose a harness (claude-code or codex). There is no default.' };
  }

  const schedule = scheduleHint(state.schedule);
  if (schedule != null && schedule !== '') return { ok: false, reason: schedule };

  const budget = parseTokenBudget(state.tokenBudget);
  if (budget.error != null && budget.error !== '') return { ok: false, reason: budget.error };

  const maxRuntime = parseMaxRuntimeSeconds(state.maxRuntimeSeconds);
  if (maxRuntime.error != null && maxRuntime.error !== '') return { ok: false, reason: maxRuntime.error };

  return { ok: true, reason: null };
}

/** Parsed token-budget: empty -> null (omit), a positive integer, or an error reason. */
export function parseTokenBudget(raw: string): { value: number | null; error: string | null } {
  const trimmed = raw.trim();
  if (!trimmed) return { value: null, error: null };
  if (!/^\d+$/.test(trimmed)) return { value: null, error: 'Token budget must be a whole number.' };
  const value = Number.parseInt(trimmed, 10);
  if (value <= 0) return { value: null, error: 'Token budget must be greater than zero.' };
  return { value, error: null };
}

/**
 * Parsed max-runtime seconds: empty -> null (omit, use the global default), a positive integer,
 * or an error reason. Mirrors parseTokenBudget so the client hint matches the server validator
 * ("Max runtime seconds must be greater than zero.").
 */
export function parseMaxRuntimeSeconds(raw: string): { value: number | null; error: string | null } {
  const trimmed = raw.trim();
  if (!trimmed) return { value: null, error: null };
  if (!/^\d+$/.test(trimmed)) return { value: null, error: 'Max runtime must be a whole number of seconds.' };
  const value = Number.parseInt(trimmed, 10);
  if (value <= 0) return { value: null, error: 'Max runtime must be greater than zero.' };
  return { value, error: null };
}

/** The editable cron-agent fields shared by the create and update request bodies. */
interface CronEditableBody {
  schedule: string;
  prompt: string;
  repo_path: string;
  harness: string;
  model?: string;
  token_budget?: number;
  block_concurrent_runs: boolean;
  max_runtime_seconds?: number;
}

/** Builds the editable-field portion of a request body, omitting empty optionals. Shared by
 * create and update so the two wire shapes cannot drift on the FE either. */
function buildEditableBody(state: CronFormState): CronEditableBody {
  const body: CronEditableBody = {
    schedule: expandScheduleSugar(state.schedule),
    prompt: state.prompt.trim(),
    repo_path: state.repoPath.trim(),
    harness: state.harness,
    block_concurrent_runs: state.blockConcurrentRuns,
  };
  const model = state.model.trim();
  if (model) body.model = model;
  const budget = parseTokenBudget(state.tokenBudget);
  if (budget.value !== null) body.token_budget = budget.value;
  const maxRuntime = parseMaxRuntimeSeconds(state.maxRuntimeSeconds);
  if (maxRuntime.value !== null) body.max_runtime_seconds = maxRuntime.value;
  return body;
}

/** Builds the create request body from form state, omitting empty optionals. */
export function buildCreateRequest(state: CronFormState): { label: string } & CronEditableBody {
  return { label: state.label.trim(), ...buildEditableBody(state) };
}

/**
 * Builds the update (FULL REPLACE) request body from form state (T-2026-06-17-05). The label is the
 * immutable URL path key and is NOT part of the body; enabled stays on the enable/disable endpoints
 * and is likewise absent. Otherwise this mirrors buildCreateRequest exactly.
 */
export function buildUpdateRequest(state: CronFormState): CronEditableBody {
  return buildEditableBody(state);
}

/**
 * Prefills a form state from an existing agent for the edit form (T-2026-06-17-05). The label is
 * carried through (read-only in the edit form; identity), and the new toggles default to the
 * server values: block_concurrent_runs defaults to true when the field is absent, and a null
 * max_runtime_seconds becomes the empty string (use global default).
 */
export function formStateFromAgent(agent: {
  label: string;
  repo_path: string;
  schedule: string;
  prompt: string;
  harness: string;
  model?: string | null;
  token_budget?: number | null;
  block_concurrent_runs?: boolean;
  max_runtime_seconds?: number | null;
}): CronFormState {
  return {
    label: agent.label,
    repoPath: agent.repo_path,
    schedule: agent.schedule,
    prompt: agent.prompt,
    harness: agent.harness,
    model: agent.model ?? '',
    tokenBudget: agent.token_budget == null ? '' : String(agent.token_budget),
    // Absent -> the server default is true (guard ON).
    blockConcurrentRuns: agent.block_concurrent_runs ?? true,
    maxRuntimeSeconds: agent.max_runtime_seconds == null ? '' : String(agent.max_runtime_seconds),
  };
}

/**
 * A harness option's UI state derived from the capabilities probe. A harness with no
 * capability row, or a non-Available status, is offered but DISABLED with the detail
 * shown -- never hidden, never silently dropped (CLAUDE.md accessibility + honesty).
 */
export interface HarnessOption {
  id: string;
  available: boolean;
  detail: string;
}

/** The capability status string that means a harness can be selected and run. */
export const CAPABILITY_AVAILABLE = 'Available';

/**
 * Joins the static harness ids with the capabilities probe into option rows. A missing
 * probe row is treated as unavailable with an honest "not reported" detail rather than
 * assuming availability.
 */
export function deriveHarnessOptions(
  capabilities: { harness: string; status: string; detail: string }[],
): HarnessOption[] {
  const byId = new Map(capabilities.map((c) => [c.harness, c]));
  return HARNESS_IDS.map((id) => {
    const cap = byId.get(id);
    if (!cap) {
      return { id, available: false, detail: 'Capability not reported by the server.' };
    }
    return {
      id,
      available: cap.status === CAPABILITY_AVAILABLE,
      detail: cap.detail,
    };
  });
}

/**
 * Honest per-run cost label: a null total means the run was not metered yet (cost
 * lookup pending or absent), never 0. Returns a placeholder string for null so the UI
 * never renders a fake "$0.00".
 */
export function formatRunCost(totalCostUsd: number | null): string {
  // Loose null check: the backend omits null fields (WhenWritingNull) so undefined arrives at
  // runtime even though the type says number | null. Using == catches both null (typed) and
  // undefined (untyped runtime omission). This is a deliberate runtime-safety guard.
  if (totalCostUsd == null) return 'not yet metered';
  return `$${totalCostUsd.toFixed(4)}`;
}

/** Whether a run status string represents a terminal failure (for non-color cueing). */
export function isFailureStatus(status: string): boolean {
  const s = status.toLowerCase();
  return s === 'failed' || s === 'error';
}

/** Whether a run status string represents a skipped run (for non-color cueing). */
export function isSkippedStatus(status: string): boolean {
  return status.toLowerCase() === 'skipped';
}

/** Whether a run status string represents success (for non-color cueing). */
export function isSuccessStatus(status: string): boolean {
  const s = status.toLowerCase();
  return s === 'succeeded' || s === 'success' || s === 'completed';
}

/**
 * A non-color status glyph so run state is never encoded by color alone (CLAUDE.md
 * accessibility rule). Paired with a text label in the UI.
 */
export function statusGlyph(status: string): string {
  if (isSuccessStatus(status)) return '✓'; // check
  if (isFailureStatus(status)) return '✗'; // ballot x
  if (isSkippedStatus(status)) return '—'; // em dash placeholder for skipped
  return '•'; // bullet for running/unknown
}

// =============================================================================
// Human-readable schedule column (T-2026-06-13-35).
//
// Wraps cronstrue to convert a 5-field UTC cron expression into plain language.
// The schedule is stored in UTC; when the user's timezone differs, this function
// can append an "(in <zone>)" note for expressions that include a specific time.
// Raw expression is never replaced -- callers should show both.
// =============================================================================

/**
 * Result of humanizing a cron expression.
 * - `text`: the human-readable description, e.g. "Every day at 6:00 AM"
 * - `tzNote`: optional timezone note when the expression includes a clock time
 *   and the user's resolved zone differs from UTC, e.g. "(UTC)" or "(America/New_York)"
 * - `error`: truthy if the expression could not be parsed; `text` is a safe fallback.
 */
export interface HumanScheduleResult {
  text: string;
  tzNote: string | null;
  error: boolean;
}

/**
 * Returns true when the 5-field cron expression contains a concrete clock time
 * (i.e., neither the minute nor the hour field is the wildcard '*').
 * Used to decide whether to show a timezone note.
 */
export function cronHasTime(expr: string): boolean {
  const expanded = expandScheduleSugar(expr);
  const fields = expanded.trim().split(/\s+/);
  if (fields.length < 2) return false;
  // A field with * (possibly combined with step/range) still includes the wildcard;
  // only pure numeric / list / range without a wildcard counts as a concrete time.
  const minute = fields[0] ?? '';
  const hour = fields[1] ?? '';
  // Concrete when both minute AND hour contain no wildcard.
  return !minute.includes('*') && !hour.includes('*');
}

/**
 * Converts a 5-field cron expression to a human-readable description via cronstrue.
 *
 * - The expression is stored UTC; the `ianaZone` parameter is the user's resolved
 *   timezone (from resolveZone(prefs.timezone)).
 * - When the expression contains a specific time AND the zone is not UTC,
 *   `tzNote` is set to "(UTC)" to clarify that the displayed time is UTC, not local.
 *   This keeps the description honest: the cron fires at the given UTC time regardless
 *   of the user's display timezone.
 * - Returns `{ text: expr, error: true }` when cronstrue throws (invalid expression).
 *
 * @param expr - The 5-field cron expression (may include @daily/@hourly sugar).
 * @param ianaZone - Resolved IANA zone string for the user's timezone preference.
 */
export function humanSchedule(expr: string, ianaZone: string): HumanScheduleResult {
  const expanded = expandScheduleSugar(expr.trim());
  if (!expanded) {
    return { text: expr, tzNote: null, error: true };
  }
  try {
    const text = cronstrue.toString(expanded, { use24HourTimeFormat: false, verbose: false });
    const hasTime = cronHasTime(expanded);
    // Only show a tz note when there is a specific time AND the user is not in UTC
    // (so they understand the displayed time is in UTC, not their local zone).
    const tzNote = hasTime && ianaZone !== 'UTC' ? '(UTC)' : null;
    return { text, tzNote, error: false };
  } catch {
    // cronstrue throws for invalid expressions; return the raw expr as a safe fallback.
    return { text: expr, tzNote: null, error: true };
  }
}
