// =============================================================================
// Impact API client (PP-02, Proof Paths).
//
// Mirrors the wire contract for GET /api/impact?symbol=<FQN> exactly
// (snake_case, 1:1 with src/Scope.Core/Impact/ImpactReport.cs).
// Errors surface as ScopeApiError per the shared contract.
//
// Contract:
//   GET /api/impact?symbol=<FQN>
//   Returns: ImpactReport
//
// Non-2xx responses propagate as ScopeApiError (status preserved).
// A blank/whitespace symbol is rejected locally without firing the request
// (server would 400; EDGE-API-6).
//
// AbortSignal is forwarded only when defined so that exactOptionalPropertyTypes
// is satisfied and no { signal: undefined } leaks into the RequestInit
// (EDGE-API-5, mirrors budgetApi.ts pattern).
// =============================================================================

import { apiFetch } from './apiClient';

// ---------------------------------------------------------------------------
// Wire types — mirror ImpactReport.cs [JsonPropertyName] fields exactly
// ---------------------------------------------------------------------------

/**
 * A changed file or symbol entry in the impact report.
 * `symbol_id` and `display_name` are optional (C# string? SymbolId / string? DisplayName).
 */
export interface ChangedItem {
  file_path: string;
  symbol_id?: string | null;
  display_name?: string | null;
  change_kind: string;
}

/**
 * A symbol that is affected by the change, with the typed relationship edge
 * and the compiler-derived proof path chain.
 *
 * `relationship` is one of the six CHECK-constrained values from
 * SchemaManager.cs relationships.rel_type:
 *   inherits | implements | calls | constructs | references | tests
 */
export interface AffectedSymbol {
  symbol_id: string;
  display_name: string;
  relationship: string;
  proof_path: string[];
}

/**
 * A test that covers the changed symbol, with its discovery confidence.
 *
 * Confidence values (ImpactAnalyzer.cs):
 *   0.95 — direct `tests` edge
 *   0.80 — test of the containing type
 *   0.70 — indirect depth-2 chain
 */
export interface AffectedTest {
  symbol_id: string;
  display_name: string;
  proof_path: string[];
  confidence: number;
}

/**
 * A heuristic risk flag detected by ImpactAnalyzer.DetectRisks.
 * `symbol_id` is optional (C# string? SymbolId).
 *
 * `kind` values: interface_changed | constructor_changed | public_api_changed | sql_string_touched
 */
export interface ImpactRisk {
  kind: string;
  message: string;
  symbol_id?: string | null;
}

/**
 * Top-level wire response for GET /api/impact?symbol=<FQN>.
 * All arrays are always present (server returns [] when empty).
 */
export interface ImpactReport {
  changed: ChangedItem[];
  affected_symbols: AffectedSymbol[];
  affected_tests: AffectedTest[];
  risks: ImpactRisk[];
}

// ---------------------------------------------------------------------------
// Fetcher
// ---------------------------------------------------------------------------

/**
 * Fetch the impact report for the given fully-qualified symbol name.
 *
 * @param symbol - Fully-qualified symbol name (e.g. "Ns.Foo<T>.Bar(int)").
 *   Must be non-blank; a blank/whitespace value throws locally without
 *   firing a request (EDGE-API-6).
 * @param signal - Optional AbortSignal from the calling useQuery.
 *   Forwarded only when defined (exactOptionalPropertyTypes, EDGE-API-5).
 * @throws {Error} when `symbol` is blank/whitespace (local guard).
 * @throws {ScopeApiError} on transport failure or non-2xx response.
 */
export async function fetchImpact(
  symbol: string,
  signal?: AbortSignal,
): Promise<ImpactReport> {
  // Local guard: blank symbol would produce a server 400; fail fast locally
  // without a round-trip (EDGE-API-6).
  if (symbol.trim() === '') {
    throw new Error('symbol is required');
  }

  // exactOptionalPropertyTypes: only pass signal when defined to avoid
  // { signal: undefined } leaking into the RequestInit (EDGE-API-5,
  // mirrors budgetApi.ts pattern).
  const init = signal !== undefined ? { signal } : {};

  return apiFetch<ImpactReport>(
    '/api/impact?symbol=' + encodeURIComponent(symbol),
    init,
  );
}
