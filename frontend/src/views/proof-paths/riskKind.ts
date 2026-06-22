// =============================================================================
// riskKind.ts (PP-10, Proof Paths)
//
// Pure helpers for ImpactRisk kind labels and shape names.
// Separated from RisksPanel.tsx so the .tsx file exports components only
// (react-refresh/only-export-components rule; eslint.config.js line 83).
//
// Exported for colocated Vitest tests (RisksPanel.test.ts).
// =============================================================================

// ---------------------------------------------------------------------------
// Kind labels — text label for each known ImpactRisk kind.
// Covers the four values from ImpactAnalyzer.DetectRisks:
//   interface_changed | constructor_changed | public_api_changed | sql_string_touched
// ---------------------------------------------------------------------------

/** Human-readable text label for each known risk kind. */
export const KIND_LABEL: Record<string, string> = {
  interface_changed:    'Interface changed',
  constructor_changed:  'Constructor changed',
  public_api_changed:   'Public API changed',
  sql_string_touched:   'SQL string touched',
};

/** Inline SVG shape name (aria-hidden; text label carries meaning). */
export const KIND_SHAPE: Record<string, string> = {
  interface_changed:    'triangle',
  constructor_changed:  'triangle',
  public_api_changed:   'triangle',
  sql_string_touched:   'diamond',
};

/**
 * Returns the display label for a risk kind.
 * Falls back to the raw kind string when unrecognized (future-proof).
 */
export function kindLabel(kind: string): string {
  return KIND_LABEL[kind] ?? kind;
}

/**
 * Returns the shape name for a risk kind.
 * Falls back to 'triangle' (warning shape) when unrecognized.
 */
export function kindShape(kind: string): string {
  return KIND_SHAPE[kind] ?? 'triangle';
}

/**
 * The panel heading. Exported for test assertion: must be "Heuristic risk flags"
 * to keep these visibly distinct from compiler-proven proof paths (EDGE-HON-5).
 */
export const PANEL_TITLE = 'Heuristic risk flags';
