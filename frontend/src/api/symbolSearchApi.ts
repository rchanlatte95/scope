// =============================================================================
// symbolSearchApi.ts — client for GET /api/symbols/search (PP-03, Proof Paths).
//
// Wire contract: bare JSON array of QueryResultEntry (ScopeApp.cs ~3112-3122).
// No wrapper object; selection key is `fqn`.
//
// Endpoint: GET /api/symbols/search?q=<query>
//   - 400 on blank/whitespace `q` (Program.cs ~135-136).
//   - Returns SymbolSearchResult[] (bare array).
//
// Mirrors the fetchCommandBudget pattern from budgetApi.ts:
//   - Guard blank query client-side (never fire a 400).
//   - Forward AbortSignal only when defined (exactOptionalPropertyTypes).
//   - Let ScopeApiError propagate; never catch/transform.
// =============================================================================

import { apiFetch } from './apiClient';

// ---------------------------------------------------------------------------
// Wire type — mirrors QueryResultEntry (ScopeApp.cs ~3112-3122) exactly.
// No symbol_id or display_name exist on a search row; the selection key is fqn.
// ---------------------------------------------------------------------------

export interface SymbolSearchResult {
  fqn: string;
  kind: string;
  file: string;
  line_start: number;
  line_end: number;
  token_count: number;
}

// ---------------------------------------------------------------------------
// Client
// ---------------------------------------------------------------------------

/**
 * Queries /api/symbols/search?q=<q> and returns a bare array of results.
 *
 * Guard: blank/whitespace `q` returns [] immediately without firing a request
 * (the server returns 400 on blank q — EDGE-API-6).
 *
 * Signal is forwarded only when defined to satisfy exactOptionalPropertyTypes.
 *
 * @throws {ScopeApiError} on transport failure or non-2xx response.
 */
export async function fetchSymbolSearch(
  q: string,
  signal?: AbortSignal,
): Promise<SymbolSearchResult[]> {
  if (q.trim() === '') return [];

  // exactOptionalPropertyTypes: only pass signal when defined.
  const init = signal !== undefined ? { signal } : {};
  return apiFetch<SymbolSearchResult[]>(
    '/api/symbols/search?q=' + encodeURIComponent(q),
    init,
  );
}
