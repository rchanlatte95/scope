// =============================================================================
// Timestamp rendering (owner feedback item 9 / 9a / 9b).
//
// The contract stores ALL timestamps as UTC ISO-8601 with a trailing Z
// (design-scope-trace-v1.xml/timestamps). Display conversion is "exclusively the
// frontend's job". This module is that job:
//  - 9   storage stays UTC; this is render-only.
//  - 9a  a UTC <-> USER-SELECTED-ZONE toggle (T-2026-06-13-46).
//  - 9b  DATES shown alongside times (the dogfooding session spans multiple days, so
//        a bare clock time is ambiguous).
//
// The pref-aware formatters (formatAbsolute / formatAbsoluteDate / formatAbsoluteTimeOfDay /
// zoneAbbr) accept any IANA timezone string and are the primary surface for new code.
// The legacy DisplayZone ('UTC' only — 'EST' removed) and formatTimestamp/formatDate/
// formatTimeOfDay remain for any lingering callers but point to the same IANA machinery.
// Parsing a malformed/empty stamp yields a stable em-dash sentinel rather than "Invalid Date".
// =============================================================================

/**
 * Legacy two-value render zone type. 'EST' has been removed (T-2026-06-13-46):
 * the trace surface now uses a user-selected IANA zone via the pref-aware formatters.
 * Kept as 'UTC' only so the type does not break any remaining callers that only use 'UTC'.
 * @deprecated Prefer passing an explicit IANA zone string to formatAbsolute et al.
 */
export type DisplayZone = 'UTC';

const SENTINEL = '—'; // em dash for missing/unparseable timestamps

// UTC formatter only — the sole surviving legacy named formatter.
const UTC_OPTS: Intl.DateTimeFormatOptions = {
  timeZone: 'UTC',
  year: 'numeric',
  month: 'short',
  day: '2-digit',
  hour: 'numeric',
  minute: '2-digit',
  second: '2-digit',
  hour12: true,
  timeZoneName: 'short',
};

// Kept as a named const for the legacy FORMATTERS record below.
const UTC_FORMATTER = new Intl.DateTimeFormat('en-US', UTC_OPTS);

/**
 * @deprecated Legacy record for the two-value DisplayZone. Now only 'UTC' is valid.
 * @internal
 */
const FORMATTERS = {
  UTC: UTC_FORMATTER,
};

// =============================================================================
// Pref-aware / arbitrary-zone formatters (additive — new in T-2026-06-13-41).
//
// These accept any IANA timezone string rather than the two-value DisplayZone.
// Existing exports above remain unchanged.
// =============================================================================

// Cache for formatters keyed by IANA zone string so we build each at most once.
// Pre-populate from the UTC formatter so we never construct it twice.
const _formatterCache = new Map<string, Intl.DateTimeFormat>([
  ['UTC', UTC_FORMATTER],
]);

/**
 * Returns (or builds and caches) an Intl.DateTimeFormat for the given IANA zone.
 * Includes date + 12-hour clock + timezone abbreviation.
 * @internal
 */
function getFormatter(ianaZone: string): Intl.DateTimeFormat {
  const cached = _formatterCache.get(ianaZone);
  if (cached) return cached;
  const fmt = new Intl.DateTimeFormat('en-US', {
    timeZone: ianaZone,
    year: 'numeric',
    month: 'short',
    day: '2-digit',
    hour: 'numeric',
    minute: '2-digit',
    second: '2-digit',
    hour12: true,
    timeZoneName: 'short',
  });
  _formatterCache.set(ianaZone, fmt);
  return fmt;
}

/**
 * Maps the timezone preference sentinel 'system' to the runtime browser/OS
 * IANA zone (via Intl.DateTimeFormat resolved options). Returns any other value
 * as-is (assumed to be a valid IANA zone string).
 *
 * Examples:
 *   resolveZone('system')            // -> 'America/New_York' (or whatever the OS is)
 *   resolveZone('UTC')               // -> 'UTC'
 *   resolveZone('Europe/London')     // -> 'Europe/London'
 */
export function resolveZone(pref: string): string {
  if (pref === 'system') {
    return Intl.DateTimeFormat().resolvedOptions().timeZone;
  }
  return pref;
}

/**
 * Formats a contract UTC timestamp for display in an arbitrary IANA zone,
 * 12-hour, WITH the date (mirrors formatTimestamp but for any zone).
 * Pass the resolved zone string — call resolveZone(pref) first if needed.
 *
 * Returns the em-dash sentinel for missing/unparseable input.
 *
 * Example: formatAbsolute('2026-06-12T01:26:50Z', 'America/Los_Angeles')
 *   -> "Jun 11, 2026, 5:26:50 PM PDT"
 */
export function formatAbsolute(ts: string | null | undefined, ianaZone: string): string {
  const d = parseUtc(ts);
  if (d === null) return SENTINEL;
  return getFormatter(ianaZone).format(d);
}

/**
 * Formats only the date portion for an arbitrary IANA zone, e.g. "Jun 12, 2026".
 * Returns the em-dash sentinel for missing/unparseable input.
 */
export function formatAbsoluteDate(ts: string | null | undefined, ianaZone: string): string {
  const d = parseUtc(ts);
  if (d === null) return SENTINEL;
  return new Intl.DateTimeFormat('en-US', {
    timeZone: ianaZone,
    year: 'numeric',
    month: 'short',
    day: '2-digit',
  }).format(d);
}

/**
 * Formats only the clock portion (no date) for an arbitrary IANA zone, 12-hour,
 * e.g. "5:26:50 PM PDT". Returns the em-dash sentinel for missing input.
 */
export function formatAbsoluteTimeOfDay(ts: string | null | undefined, ianaZone: string): string {
  const d = parseUtc(ts);
  if (d === null) return SENTINEL;
  return new Intl.DateTimeFormat('en-US', {
    timeZone: ianaZone,
    hour: 'numeric',
    minute: '2-digit',
    second: '2-digit',
    hour12: true,
    timeZoneName: 'short',
  }).format(d);
}

/**
 * Returns the short timezone abbreviation for the given IANA zone at the
 * current moment (e.g. 'PDT', 'EST', 'UTC'). Useful for labels.
 */
export function zoneAbbr(ianaZone: string): string {
  // Format a dummy date and extract the timeZoneName part.
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: ianaZone,
    timeZoneName: 'short',
  }).formatToParts(new Date());
  return parts.find((p) => p.type === 'timeZoneName')?.value ?? ianaZone;
}

/** Parses a contract UTC stamp; returns null for null/empty/unparseable input. */
export function parseUtc(ts: string | null | undefined): Date | null {
  if (ts == null || ts === '') return null;
  const d = new Date(ts);
  return Number.isNaN(d.getTime()) ? null : d;
}

/**
 * Formats a contract UTC timestamp for display in the chosen zone, 12-hour, WITH the
 * date (item 9b). Returns an em-dash for a missing/unparseable stamp. Example:
 * "Jun 12, 2026, 1:26:50 AM UTC".
 *
 * @deprecated Use formatAbsolute(ts, ianaZone) instead. DisplayZone is now 'UTC' only.
 */
// eslint-disable-next-line @typescript-eslint/no-deprecated -- deprecated function uses its own deprecated type
export function formatTimestamp(ts: string | null | undefined, zone: DisplayZone): string {
  const d = parseUtc(ts);
  if (d === null) return SENTINEL;
  // DisplayZone is now 'UTC' only — always use UTC formatter.
  void zone; // parameter retained for API compatibility
  // eslint-disable-next-line @typescript-eslint/no-deprecated -- deprecated internal constant
  return FORMATTERS.UTC.format(d);
}

/**
 * Formats only the date portion in the chosen zone (for grouping headers / compact
 * labels), e.g. "Jun 12, 2026". Em-dash for missing input.
 *
 * @deprecated Use formatAbsoluteDate(ts, ianaZone) instead. DisplayZone is now 'UTC' only.
 */
// eslint-disable-next-line @typescript-eslint/no-deprecated -- deprecated function uses its own deprecated type
export function formatDate(ts: string | null | undefined, zone: DisplayZone): string {
  const d = parseUtc(ts);
  if (d === null) return SENTINEL;
  void zone; // parameter retained for API compatibility
  return new Intl.DateTimeFormat('en-US', {
    timeZone: 'UTC',
    year: 'numeric',
    month: 'short',
    day: '2-digit',
  }).format(d);
}

/**
 * Formats only the clock time (no date) in the chosen zone, 12-hour, e.g.
 * "1:26:50 AM UTC". Used inside cards where the date header is shown once per group.
 *
 * @deprecated Use formatAbsoluteTimeOfDay(ts, ianaZone) instead. DisplayZone is now 'UTC' only.
 */
// eslint-disable-next-line @typescript-eslint/no-deprecated -- deprecated function uses its own deprecated type
export function formatTimeOfDay(ts: string | null | undefined, zone: DisplayZone): string {
  const d = parseUtc(ts);
  if (d === null) return SENTINEL;
  void zone; // parameter retained for API compatibility
  return new Intl.DateTimeFormat('en-US', {
    timeZone: 'UTC',
    hour: 'numeric',
    minute: '2-digit',
    second: '2-digit',
    hour12: true,
    timeZoneName: 'short',
  }).format(d);
}

/**
 * Human-readable label for the toggle button's current zone.
 *
 * @deprecated Use zoneAbbr(ianaZone) instead. DisplayZone is now 'UTC' only.
 */
// eslint-disable-next-line @typescript-eslint/no-deprecated -- deprecated function uses its own deprecated type
export function zoneLabel(zone: DisplayZone): string {
  void zone; // parameter retained for API compatibility
  return 'UTC';
}

// =============================================================================
// Staleness / relative-age helpers (PP-11, OQ-6).
// =============================================================================

/**
 * Age threshold in hours after which the UI shows a cosmetic staleness advisory
 * on the Proof Paths header (OQ-6). The server enforces a hard StaleIndex 409
 * on the impact call itself (EDGE-API-3); this constant is advisory-only.
 */
export const STALE_INDEX_WARN_HOURS = 24;

/**
 * Returns a human-readable relative age string for a UTC ISO-8601 timestamp,
 * e.g. "just now", "45s ago", "12m ago", "3h ago", "2d ago".
 *
 * The `now` parameter is injectable for deterministic testing (default: current
 * wall-clock time). Empty or unparseable `iso` returns "unknown" rather than
 * throwing.
 *
 * @param iso - UTC ISO-8601 timestamp string (e.g. from indexed_at).
 * @param now - Reference instant; defaults to `new Date()`.
 */
export function relativeAge(iso: string, now: Date = new Date()): string {
  const d = parseUtc(iso);
  if (d === null) return 'unknown';

  const diffMs = now.getTime() - d.getTime();
  // Treat future timestamps (clock skew) as "just now".
  if (diffMs < 0) return 'just now';

  const diffSec = Math.floor(diffMs / 1000);
  if (diffSec < 60) return diffSec < 5 ? 'just now' : `${String(diffSec)}s ago`;

  const diffMin = Math.floor(diffSec / 60);
  if (diffMin < 60) return `${String(diffMin)}m ago`;

  const diffHr = Math.floor(diffMin / 60);
  if (diffHr < 24) return `${String(diffHr)}h ago`;

  const diffDay = Math.floor(diffHr / 24);
  return `${String(diffDay)}d ago`;
}
