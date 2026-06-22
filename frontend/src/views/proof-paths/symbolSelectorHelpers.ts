// =============================================================================
// symbolSelectorHelpers.ts — PP-06: pure helpers for SymbolSelector.
//
// Extracted to a separate module so that SymbolSelector.tsx exports ONLY
// the React component (react-refresh/only-export-components compliance).
// =============================================================================

/**
 * Returns true when a query string is blank (empty or whitespace-only).
 * Guards against firing a search request for empty input (EDGE-API-6).
 */
export function isBlankQuery(q: string): boolean {
  return q.trim() === '';
}
