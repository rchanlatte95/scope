// =============================================================================
// Budget API client (T-05b Cost Ledger Explorer).
//
// Mirrors the wire contract for GET /api/command/budget exactly (snake_case).
// Errors surface as ScopeApiError per the shared contract.
//
// Contract:
//   GET /api/command/budget?window_days=N  (default 30, clamped 1..365)
//   Returns: CommandBudgetResponse
//
// All number fields in by_day, by_agent, by_model are always present (never
// null) on those sub-rows — zero-fill is server-side. total_spend_usd at the
// top level may be null (omitted by WhenWritingNull) when no metered spend
// exists in the window.
// =============================================================================

import { apiFetch } from './apiClient';
import type { CostBillingClass } from './traceApi';

// ---------------------------------------------------------------------------
// Wire types — mirror the endpoint contract exactly (snake_case)
// ---------------------------------------------------------------------------

/** One entry in the provenance_breakdown array. */
export interface BudgetProvenanceBreakdown {
  provenance: string;
  spend_usd: number;
  run_count: number;
}

/** One daily row in the by_day array (zero-filled by the server). */
export interface BudgetByDay {
  date: string;          // "YYYY-MM-DD"
  spend_usd: number;
  run_count: number;
}

/** One agent row in the by_agent array (desc by spend). */
export interface BudgetByAgent {
  label: string;
  spend_usd: number;
  run_count: number;
}

/** One model row in the by_model array (desc by spend). */
export interface BudgetByModel {
  model: string;
  spend_usd: number;
  input_tokens: number;
  output_tokens: number;
  run_count: number;
}

/** One tool-type bucket row: spend attributed to a TraceToolType taxonomy bucket. */
export interface BudgetByToolType {
  taxonomy_id: string;
  spend_usd: number;
  run_count: number;
}

/** One day+tool-type row for stacked time-series. */
export interface BudgetDayToolType {
  date: string;        // "YYYY-MM-DD"
  taxonomy_id: string;
  spend_usd: number;
  run_count: number;
}

/**
 * Wire response for GET /api/command/budget.
 *
 * total_spend_usd is optional/undefined when the server emits null (WhenWritingNull).
 * Callers must treat undefined as "no metered spend" and NEVER render it as $0.00.
 */
export interface CommandBudgetResponse {
  window_days: number;
  from_utc: string;
  to_utc: string;
  /** Null/omitted when no metered spend. Never render as $0.00. */
  total_spend_usd?: number | null;
  /** CostProvenance vocabulary string. */
  cost_provenance: string;
  provenance_breakdown: BudgetProvenanceBreakdown[];
  by_day: BudgetByDay[];
  by_agent: BudgetByAgent[];
  by_model: BudgetByModel[];
  /** May be absent when the backend predates T-05c; callers must treat undefined as []. */
  by_tool_type?: BudgetByToolType[];
  /** May be absent when the backend predates T-05c; callers must treat undefined as []. */
  by_day_tool_type?: BudgetDayToolType[];
  /**
   * Billing-class discriminator for the aggregate window (T-2026-06-19-01 Phase 4).
   * Absent on older backends; treat undefined as "unclassified" (no badge).
   * See CostBillingClass for the three known values.
   */
  cost_billing_class?: CostBillingClass | null;
}

// ---------------------------------------------------------------------------
// Fetcher
// ---------------------------------------------------------------------------

/**
 * Fetch the command budget for the given window.
 *
 * Two modes:
 *   1. Window days (legacy): pass `windowDays`; `from` and `to` are null/omitted.
 *   2. Explicit range (ST-02): pass `from` and `to` as ISO-8601 UTC strings.
 *      The backend accepts from_utc/to_utc and returns a custom window.
 *      `windowDays` is still required for query-key stability; pass 0 when using
 *      an explicit range so the key is stable and distinct from day-based queries.
 *
 * @param windowDays - Number of days to look back (1..365). Ignored when from+to provided.
 * @param signal     - Optional AbortSignal from the calling useQuery.
 * @param from       - Optional ISO-8601 UTC start (e.g. "2026-06-01T00:00:00.000Z").
 * @param to         - Optional ISO-8601 UTC end.
 * @throws {ScopeApiError} on transport failure or non-2xx response.
 */
export async function fetchCommandBudget(
  windowDays: number,
  signal?: AbortSignal,
  from?: string | null,
  to?: string | null,
): Promise<CommandBudgetResponse> {
  const init = signal !== undefined ? { signal } : {};

  // When explicit from/to are provided, prefer the from_utc/to_utc backend params (ST-02).
  if (from != null && from !== '' && to != null && to !== '') {
    const params = new URLSearchParams({ from_utc: from, to_utc: to });
    return apiFetch<CommandBudgetResponse>(`/api/command/budget?${params.toString()}`, init);
  }

  const clamped = Math.min(365, Math.max(1, Math.round(windowDays)));
  // exactOptionalPropertyTypes: only pass signal when defined.
  return apiFetch<CommandBudgetResponse>(
    `/api/command/budget?window_days=${String(clamped)}`,
    init,
  );
}
