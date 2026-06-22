// =============================================================================
// dateRange.ts
//
// Pure helpers (no DOM, no React) for the DateRangePicker component.
//
// Timezone contract (D1 resolved, owner decision ST-01):
//   DISPLAY:  Local wall-clock (the user's browser/OS timezone).
//   EMIT:     ISO-8601 UTC with Z suffix -- both presets and custom.
//   DST:      The Date constructor parses "yyyy-MM-ddTHH:mm" as LOCAL time,
//             so it correctly handles the DST fold/gap automatically (the
//             browser's local timezone rules apply at parse time).
//   FRACTIONAL OFFSETS (e.g. UTC+5:30, UTC-3:30): handled identically --
//             new Date('yyyy-MM-ddTHH:mm').toISOString() yields the UTC
//             equivalent regardless of the offset width.
//
// Preset mapping (D3 resolved, owner decision):
//   ALL presets emit explicit { from, to } UTC ISO strings (rolling-from-now).
//   Callers pass `now` so the function is deterministic and testable.
//   This enables exact deep-link state and aligns with the custom-range path.
// =============================================================================

/** The two-key date range emitted by the picker and used by all callers. */
export interface DateRange {
  /** ISO-8601 UTC string with Z suffix, e.g. "2026-06-01T00:00:00.000Z". */
  from: string;
  /** ISO-8601 UTC string with Z suffix. */
  to: string;
}

/**
 * Ordered preset keys.  6mo = 180 calendar days; 1yr = 365 calendar days.
 * These fit within the existing backend [1, 365] day clamp when converted.
 */
export const PRESET_KEYS = ['24h', '7d', '30d', '90d', '6mo', '1yr'] as const;
export type PresetKey = (typeof PRESET_KEYS)[number];

/** Human-readable labels for each preset, displayed on the segmented control. */
export const PRESET_OPTIONS: readonly { key: PresetKey; label: string }[] = [
  { key: '24h', label: '24h' },
  { key: '7d',  label: '7d' },
  { key: '30d', label: '30d' },
  { key: '90d', label: '90d' },
  { key: '6mo', label: '6mo' },
  { key: '1yr', label: '1yr' },
];

/** Window sizes in milliseconds for each preset. */
const PRESET_MS: Record<PresetKey, number> = {
  '24h': 1   * 24 * 60 * 60 * 1000,
  '7d':  7   * 24 * 60 * 60 * 1000,
  '30d': 30  * 24 * 60 * 60 * 1000,
  '90d': 90  * 24 * 60 * 60 * 1000,
  '6mo': 180 * 24 * 60 * 60 * 1000,
  '1yr': 365 * 24 * 60 * 60 * 1000,
};

/** The earliest allowed custom start date.  Also guards preset lower bound. */
export const RANGE_FLOOR: Date = new Date('2020-01-01T00:00:00.000Z');

/**
 * Converts a named preset to a { from, to } UTC ISO pair relative to `now`.
 * Rolling-from-now: from = now - window, to = now.
 * The from value is clamped to RANGE_FLOOR if it would precede it.
 *
 * @param preset  One of the PresetKey values.
 * @param now     Reference timestamp for the "to" end; defaults to new Date().
 *                Inject a fixed Date in tests for deterministic output.
 */
export function presetToRange(preset: PresetKey, now: Date = new Date()): DateRange {
  const to = now;
  const fromMs = Math.max(to.getTime() - PRESET_MS[preset], RANGE_FLOOR.getTime());
  const from = new Date(fromMs);
  return {
    from: from.toISOString(),
    to:   to.toISOString(),
  };
}

/**
 * Converts a datetime-local value (yyyy-MM-ddTHH:mm in LOCAL wall-clock time)
 * to an ISO-8601 UTC string with Z suffix.
 *
 * The Date constructor interprets bare "yyyy-MM-ddTHH:mm" strings as LOCAL time
 * (no offset suffix), which correctly applies the browser DST rules for the
 * exact calendar date and hour.  Fractional UTC offsets (e.g. IST UTC+5:30,
 * NST UTC-3:30) are handled identically because the engine uses the full IANA
 * timezone rule set.
 *
 * Returns '' for empty or unparseable input.
 */
export function localInputToUtcIso(localValue: string): string {
  if (localValue === '') return '';
  const d = new Date(localValue);
  return Number.isNaN(d.getTime()) ? '' : d.toISOString();
}

/**
 * Converts a UTC ISO string back to a datetime-local input value
 * (yyyy-MM-ddTHH:mm in LOCAL wall-clock time) for pre-populating an input.
 *
 * Returns '' for null / empty / unparseable input.
 */
export function utcIsoToLocalInput(utcIso: string | null | undefined): string {
  if (utcIso == null || utcIso === '') return '';
  const d = new Date(utcIso);
  if (Number.isNaN(d.getTime())) return '';

  const pad = (n: number): string => String(n).padStart(2, '0');
  return (
    `${String(d.getFullYear())}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}` +
    `T${pad(d.getHours())}:${pad(d.getMinutes())}`
  );
}

/** Result of validateRange. */
export interface ValidationResult {
  valid: boolean;
  /** Human-readable error string; undefined when valid. */
  error?: string;
}

/**
 * Validates a date range expressed as UTC ISO strings.
 *
 * Rules:
 *   - Both `from` and `to` must be non-empty and parseable.
 *   - from <= to (equal is valid: a point-in-time range).
 *   - from >= floor (defaults to RANGE_FLOOR = 2020-01-01T00:00:00Z).
 */
export function validateRange(
  from: string,
  to: string,
  floor: Date = RANGE_FLOOR,
): ValidationResult {
  if (!from || !to) {
    return { valid: false, error: 'Start and end dates are required.' };
  }

  const fromDate = new Date(from);
  const toDate = new Date(to);

  if (Number.isNaN(fromDate.getTime()) || Number.isNaN(toDate.getTime())) {
    return { valid: false, error: 'Invalid date format.' };
  }

  if (fromDate < floor) {
    return {
      valid: false,
      error: `Start date cannot be before ${floor.toISOString().slice(0, 10)}.`,
    };
  }

  if (fromDate > toDate) {
    return { valid: false, error: 'Start date must be on or before end date.' };
  }

  return { valid: true };
}

/**
 * Returns the local datetime-local min attribute value corresponding to RANGE_FLOOR.
 * Used to set the `min` attribute on the custom-range start input.
 */
export function floorLocalInputMin(): string {
  return utcIsoToLocalInput(RANGE_FLOOR.toISOString());
}
