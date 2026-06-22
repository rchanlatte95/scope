// =============================================================================
// RunStatus union and display maps (FE-1).
//
// RunStatus is the authoritative closed enum for operational run state across
// the Scope SPA. Use statusLabel for visible text; use statusShape for the
// non-color shape/icon name; use statusToken for the --scope-color-* bucket.
//
// Boundary narrowing: toRunStatus() accepts the cron API status: string and
// returns a valid RunStatus or the safe fallback "failed". Never throws.
// =============================================================================

/** Closed union for operational run/agent state. */
export type RunStatus =
  | 'ready'
  | 'queued'
  | 'running'
  | 'paused'
  | 'skipped'
  | 'failed'
  | 'blocked'
  | 'complete'
  | 'unknown';

/** All valid RunStatus values in declaration order. */
export const RUN_STATUSES: readonly RunStatus[] = [
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

/**
 * Visible text label for each RunStatus. Every value maps to a non-empty,
 * human-readable string suitable for display in a badge or table cell.
 */
export const STATUS_LABEL: Readonly<Record<RunStatus, string>> = {
  ready:    'Ready',
  queued:   'Queued',
  running:  'Running',
  paused:   'Paused',
  skipped:  'Skipped',
  failed:   'Failed',
  blocked:  'Blocked',
  complete: 'Complete',
  unknown:  'Unknown',
};

/**
 * Non-color shape/icon name for each RunStatus. These names reference
 * semantic icon identifiers (e.g. Heroicons/Phosphor names) — the consuming
 * component renders them as <svg> or a glyph font. Every value is distinct
 * so status is identifiable in grayscale or without color perception.
 */
export const STATUS_SHAPE: Readonly<Record<RunStatus, string>> = {
  ready:    'circle-dot',       // hollow dot: ready to go, not yet queued
  queued:   'clock',            // clock: waiting in queue
  running:  'arrow-path',       // spinning arrows: active execution
  paused:   'pause-circle',     // pause: intentionally suspended
  skipped:  'forward',          // forward skip: explicitly bypassed
  failed:   'x-circle',         // X: error / failure
  blocked:  'no-symbol',        // prohibited: externally blocked
  complete: 'check-circle',     // checkmark: successfully finished
  unknown:  'question-mark-circle', // neutral: unrecognized status (never implies failure)
};

/**
 * The --scope-color-* semantic bucket for each RunStatus.
 * Used as `data-status-token` on the badge element so CSS can apply the
 * correct token via attribute selector without hard-coding hex values.
 */
export const STATUS_TOKEN: Readonly<Record<RunStatus, 'success' | 'warning' | 'danger' | 'info' | 'muted'>> = {
  ready:    'success',
  queued:   'info',
  running:  'info',
  paused:   'warning',
  skipped:  'warning',
  failed:   'danger',
  blocked:  'danger',
  complete: 'success',
  unknown:  'muted',
};

/**
 * Returns the visible label for a RunStatus.
 * Convenience wrapper; prefer STATUS_LABEL map for batch access.
 */
export function statusLabel(status: RunStatus): string {
  return STATUS_LABEL[status];
}

/**
 * Returns the shape/icon name for a RunStatus.
 * Convenience wrapper; prefer STATUS_SHAPE map for batch access.
 */
export function statusShape(status: RunStatus): string {
  return STATUS_SHAPE[status];
}

/** Set of all valid RunStatus values for O(1) membership tests. */
const RUN_STATUS_SET = new Set<string>(RUN_STATUSES);

/**
 * Maps backend/host status vocabulary to the canonical RunStatus enum. The backend emits
 * 'completed' (cron_runs.status CHECK is running|completed|failed|skipped; obs span/event
 * statuses vary) while the enum value is 'complete' — without this map 'completed' fell through
 * to the fallback and rendered as a red FAILED badge (T-2026-06-16: "completed shows a red X").
 * Keys are lower-cased; only genuine failure terms map to 'failed'. Unrecognized values become
 * 'unknown' (muted), NEVER 'failed', so the trust surface never mislabels a non-failure as failed.
 */
const STATUS_ALIASES: Readonly<Record<string, RunStatus>> = {
  completed:     'complete',
  complete:      'complete',
  success:       'complete',
  succeeded:     'complete',
  done:          'complete',
  ok:            'complete',
  running:       'running',
  in_progress:   'running',
  'in-progress': 'running',
  active:        'running',
  started:       'running',
  queued:        'queued',
  pending:       'queued',
  waiting:       'queued',
  ready:         'ready',
  paused:        'paused',
  suspended:     'paused',
  skipped:       'skipped',
  cancelled:     'skipped',
  canceled:      'skipped',
  blocked:       'blocked',
  failed:        'failed',
  failure:       'failed',
  error:         'failed',
  errored:       'failed',
};

/**
 * Narrows an arbitrary string (from the cron/obs API `status` field) to a RunStatus.
 * Resolution: exact enum match -> alias map (case-insensitive) -> 'unknown' (muted, never 'failed').
 * Never throws. Returning 'unknown' instead of 'failed' for unrecognized input keeps the badge
 * from screaming red on statuses the SPA simply does not know about.
 *
 * Usage: const s = toRunStatus(cronRun.status);
 */
export function toRunStatus(raw: string | null | undefined): RunStatus {
  if (raw == null || raw === '') return 'unknown';
  if (RUN_STATUS_SET.has(raw)) return raw as RunStatus;
  return STATUS_ALIASES[raw.trim().toLowerCase()] ?? 'unknown';
}
