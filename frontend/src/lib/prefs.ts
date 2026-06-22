/**
 * prefs.ts — Display preference storage seam and pure root-attribute applier.
 *
 * Rules:
 * - PURE and node-testable: no window/DOM/localStorage globals assumed at
 *   import time. All access is guarded and injected.
 * - Approved localStorage keys only (theme, fontScale, readableFont, visualCues).
 * - Validation + defaults applied on read; invalid stored values fall back to default.
 * - The applier is a pure function: accepts a prefs object and a root-like element;
 *   sets the four data-* attributes. No side effects beyond setAttribute calls.
 * - No UI: Settings UI is FE-2b's job.
 */

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export type Theme = 'system' | 'light' | 'dark';
export type FontScale = 100 | 115 | 130;
export type ReadableFont = 'off' | 'atkinson' | 'opendyslexic';
export type VisualCues = 'standard' | 'strong';
/**
 * Timezone preference. The sentinel value 'system' means "use the runtime
 * browser/OS timezone". All other values are valid IANA zone identifiers,
 * e.g. 'America/New_York', 'Europe/London', 'UTC'.
 *
 * Note: using (string & {}) preserves the 'system' literal in tooling/docs while
 * avoiding the "overridden by string" redundant-type-constituents lint error.
 */
export type TimezonePreference = 'system' | (string & Record<never, never>);

/**
 * The model tier used to peg cost estimates when no model is known (scope_estimated path).
 * Changing this causes AccountingPanel to re-fetch the rollup with the new assumedModel
 * query param, so both the cost value and the basis label update live.
 *
 * Allowed values are the model ids recognized by the backend pricing table.
 * Default: 'claude-sonnet-4-6' (Sonnet-tier, unchanged from the original hard-coded default).
 */
export type AssumedCostModel =
  | 'claude-sonnet-4-6'
  | 'claude-opus-4-8'
  | 'claude-haiku-4-5'
  | 'claude-fable-5';

export interface Prefs {
  theme: Theme;
  fontScale: FontScale;
  readableFont: ReadableFont;
  visualCues: VisualCues;
  timezone: TimezonePreference;
  assumedCostModel: AssumedCostModel;
}

// Element-like interface for testing without real DOM
export interface RootElement {
  setAttribute(name: string, value: string): void;
}

// ---------------------------------------------------------------------------
// Defaults
// ---------------------------------------------------------------------------

export const DEFAULTS: Prefs = {
  theme: 'system',
  fontScale: 100,
  readableFont: 'off',
  visualCues: 'standard',
  timezone: 'system',
  assumedCostModel: 'claude-sonnet-4-6',
};

// ---------------------------------------------------------------------------
// localStorage key constants
// ---------------------------------------------------------------------------

const KEYS = {
  theme: 'scope.prefs.theme',
  fontScale: 'scope.prefs.fontScale',
  readableFont: 'scope.prefs.readableFont',
  visualCues: 'scope.prefs.visualCues',
  timezone: 'scope.prefs.timezone',
  assumedCostModel: 'scope.prefs.assumedCostModel',
} as const;

// ---------------------------------------------------------------------------
// Storage access (guarded — safe in node env, SSR, or restricted contexts)
// ---------------------------------------------------------------------------

function safeGetItem(key: string): string | null {
  try {
    if (typeof localStorage === 'undefined') return null;
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function safeSetItem(key: string, value: string): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(key, value);
  } catch {
    // Storage unavailable (private mode, quota exceeded, etc.) — silently no-op.
  }
}

// ---------------------------------------------------------------------------
// Validators
// ---------------------------------------------------------------------------

function validTheme(raw: string | null): Theme {
  if (raw === 'system' || raw === 'light' || raw === 'dark') return raw;
  return DEFAULTS.theme;
}

function validFontScale(raw: string | null): FontScale {
  if (raw === '100') return 100;
  if (raw === '115') return 115;
  if (raw === '130') return 130;
  return DEFAULTS.fontScale;
}

function validReadableFont(raw: string | null): ReadableFont {
  if (raw === 'off' || raw === 'atkinson' || raw === 'opendyslexic') return raw;
  // Migrate legacy stored value 'on' -> 'atkinson' (decision fonts, 2026-06-13).
  if (raw === 'on') return 'atkinson';
  return DEFAULTS.readableFont;
}

function validVisualCues(raw: string | null): VisualCues {
  if (raw === 'standard' || raw === 'strong') return raw;
  return DEFAULTS.visualCues;
}

/**
 * Validates a raw timezone pref value.
 * Accepts the sentinel 'system' plus any non-empty string that passes a basic
 * IANA zone format check (non-empty, no raw whitespace, max 64 chars).
 * Unknown-but-plausible IANA strings are accepted so we do not regress on
 * zones not known at compile time. Junk (empty, whitespace, too long) falls
 * back to 'system'.
 */
function validTimezone(raw: string | null): TimezonePreference {
  if (raw === null || raw === '') return DEFAULTS.timezone;
  if (raw === 'system') return 'system';
  // Basic sanity: no surrounding whitespace, max 64 chars, at least 1 char.
  const trimmed = raw.trim();
  if (trimmed.length === 0 || trimmed.length > 64 || trimmed !== raw) return DEFAULTS.timezone;
  // Must look like a plausible IANA zone: letters, digits, underscore, slash, dash.
  if (!/^[A-Za-z][A-Za-z0-9/_\-+]*$/.test(trimmed)) return DEFAULTS.timezone;
  return trimmed;
}

/**
 * Validates the raw assumedCostModel pref value.
 * Only the four allowed model ids are accepted; any other value falls back to the default.
 */
function validAssumedCostModel(raw: string | null): AssumedCostModel {
  if (
    raw === 'claude-sonnet-4-6' ||
    raw === 'claude-opus-4-8' ||
    raw === 'claude-haiku-4-5' ||
    raw === 'claude-fable-5'
  ) return raw;
  return DEFAULTS.assumedCostModel;
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/**
 * Read all approved display preferences from localStorage.
 * Returns defaults for any missing or invalid value.
 */
export function readPrefs(): Prefs {
  return {
    theme: validTheme(safeGetItem(KEYS.theme)),
    fontScale: validFontScale(safeGetItem(KEYS.fontScale)),
    readableFont: validReadableFont(safeGetItem(KEYS.readableFont)),
    visualCues: validVisualCues(safeGetItem(KEYS.visualCues)),
    timezone: validTimezone(safeGetItem(KEYS.timezone)),
    assumedCostModel: validAssumedCostModel(safeGetItem(KEYS.assumedCostModel)),
  };
}

/**
 * Write a single approved preference to localStorage.
 * Other prefs are not touched.
 */
export function writePref(key: keyof Prefs, value: Prefs[typeof key]): void {
  safeSetItem(KEYS[key], String(value));
}

/**
 * Write all display preferences to localStorage at once.
 */
export function writePrefs(prefs: Prefs): void {
  safeSetItem(KEYS.theme, prefs.theme);
  safeSetItem(KEYS.fontScale, String(prefs.fontScale));
  safeSetItem(KEYS.readableFont, prefs.readableFont);
  safeSetItem(KEYS.visualCues, prefs.visualCues);
  safeSetItem(KEYS.timezone, prefs.timezone);
  safeSetItem(KEYS.assumedCostModel, prefs.assumedCostModel);
}

/**
 * Pure applier: given prefs and a root element (pass the element in so it is
 * testable without real DOM), sets the four data-* attributes.
 *
 * Accepts any object with setAttribute — real HTMLElement or a test stub.
 */
export function applyPrefs(prefs: Prefs, root: RootElement): void {
  root.setAttribute('data-theme', prefs.theme);
  root.setAttribute('data-font-scale', String(prefs.fontScale));
  root.setAttribute('data-readable-font', prefs.readableFont);
  root.setAttribute('data-visual-cues', prefs.visualCues);
}
