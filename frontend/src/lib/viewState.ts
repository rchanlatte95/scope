/**
 * viewState.ts — Shared URL view-state codec (T-2026-06-16-18 / ST-01).
 *
 * Encodes and decodes view-specific state as URL search-param subsets carried
 * by the Route fields defined in router.ts. This module is the single place
 * that owns the mapping between typed view-state objects and URL param names.
 *
 * Consumers:
 *   - ST-02: /trace view wiring reads/writes TraceViewState.
 *   - ST-03: /cost-ledger view wiring reads/writes CostLedgerViewState.
 *   - T-16-14: date-picker fills `from`/`to` on both surfaces.
 *
 * Design rules:
 *   - No DOM/window/localStorage access at import time. Pure functions only.
 *   - Unknown params are silently ignored (graceful forward-compat).
 *   - Empty or missing params normalize to null (never undefined).
 *   - Round-trips are stable: decode(encode(state)) deep-equals state.
 *   - The legacy `session` param on /trace is preserved byte-compatible.
 */

import type { Route } from './router';

// =============================================================================
// Cost Ledger view state
// =============================================================================

/**
 * View state for the /cost-ledger page.
 *
 * - `agent`:     selected agent name/key (row filter). Null = show all.
 * - `tab`:       active breakdown tab name (e.g. "by-model", "by-tool").
 * - `detailTab`: active detail-panel tab (separate from the main breakdown tab).
 * - `from`:      date-range start as ISO-8601 UTC string. Null = unbounded.
 * - `to`:        date-range end as ISO-8601 UTC string. Null = unbounded.
 *
 * Note: NO per-agent DETAIL view exists in scope (A13, owner 2026-06-16).
 * `agent` encodes the selected/filtered agent row only, not a detail segment.
 * HIDE-ZERO IS SCRAPPED (A6, owner 2026-06-16): no hideZero field.
 */
export interface CostLedgerViewState {
  agent: string | null;
  tab: string | null;
  detailTab: string | null;
  from: string | null;
  to: string | null;
}

/** Null/empty state sentinel for cost-ledger. */
export const COST_LEDGER_EMPTY_STATE: CostLedgerViewState = {
  agent: null,
  tab: null,
  detailTab: null,
  from: null,
  to: null,
};

/**
 * Encode a CostLedgerViewState into Route fields.
 * Returns a partial Route suitable for merging with `{ name: 'cost-ledger' }`.
 */
export function encodeCostLedgerState(state: CostLedgerViewState): Pick<Route, 'agent' | 'tab' | 'detailTab' | 'from' | 'to'> {
  return {
    agent: nvl(state.agent),
    tab: nvl(state.tab),
    detailTab: nvl(state.detailTab),
    from: nvl(state.from),
    to: nvl(state.to),
  };
}

/**
 * Decode the cost-ledger view state from a Route.
 * Unknown or missing fields default to null.
 * Safe to call on any Route (ignores non-cost-ledger routes gracefully).
 */
export function decodeCostLedgerState(route: Route): CostLedgerViewState {
  return {
    agent: nvl(route.agent),
    tab: nvl(route.tab),
    detailTab: nvl(route.detailTab),
    from: nvl(route.from),
    to: nvl(route.to),
  };
}

// =============================================================================
// Trace view state
// =============================================================================

/**
 * View state for the /trace page (list view and detail view).
 *
 * - `selectedSession`: the selected session key in the trace list. Null = none.
 *   Distinct from the legacy `session` param used for cron-run deep-links
 *   (#/trace?session=KEY). Consumers must NOT confuse them.
 * - `detailTab`:       the active tab in the trace detail panel.
 * - `from`:            date-range start as ISO-8601 UTC string. Null = unbounded.
 * - `to`:              date-range end as ISO-8601 UTC string. Null = unbounded.
 * - `sessionsPage`:    current page (1-based) for the session LIST. Null = page 1 (default).
 * - `sessionsPageSize`: page size for the session LIST. Null = 10 (default).
 * - `eventsPage`:      current page (1-based) for the event-card TIMELINE. Null = page 1.
 * - `eventsPageSize`:  page size for the event-card TIMELINE. Null = 10 (default).
 *
 * Page/pageSize are encoded as decimal strings in the URL (e.g. sessionsPage=2).
 * Missing or invalid values decode to null (use defaults). Page-size is persisted
 * per-surface in localStorage (prefs); URL encodes the current page only (deep link).
 *
 * The legacy `session` param is NOT part of this view state. It lives directly
 * on the Route and is byte-compatible with the original #/trace?session=KEY
 * deep-link contract (e2e-pinned). ST-02 must read `route.session` separately.
 */
export interface TraceViewState {
  selectedSession: string | null;
  detailTab: string | null;
  from: string | null;
  to: string | null;
  /** 1-based current page for the session LIST. Null = 1. */
  sessionsPage: number | null;
  /** Page size for the session LIST (10|25|50|100). Null = 10. */
  sessionsPageSize: number | null;
  /** 1-based current page for the event-card TIMELINE. Null = 1. */
  eventsPage: number | null;
  /** Page size for the event-card TIMELINE (10|25|50|100). Null = 10. */
  eventsPageSize: number | null;
}

/** Null/empty state sentinel for trace. */
export const TRACE_EMPTY_STATE: TraceViewState = {
  selectedSession: null,
  detailTab: null,
  from: null,
  to: null,
  sessionsPage: null,
  sessionsPageSize: null,
  eventsPage: null,
  eventsPageSize: null,
};

/** Valid page sizes for both trace surfaces. */
export const VALID_PAGE_SIZES: ReadonlySet<number> = new Set([10, 25, 50, 100]);

/** Default page size for all trace pagination surfaces. */
export const DEFAULT_PAGE_SIZE = 10;

/**
 * Encode a TraceViewState into Route fields.
 * Returns a partial Route suitable for merging with `{ name: 'trace' }`.
 * Does NOT touch the legacy `session` field — callers must set it separately.
 * Page 1 and default page size (10) encode as null to keep URLs clean.
 */
export function encodeTraceState(state: TraceViewState): Pick<Route, 'selectedSession' | 'detailTab' | 'from' | 'to' | 'sessionsPage' | 'sessionsPageSize' | 'eventsPage' | 'eventsPageSize'> {
  return {
    selectedSession: nvl(state.selectedSession),
    detailTab: nvl(state.detailTab),
    from: nvl(state.from),
    to: nvl(state.to),
    sessionsPage: (state.sessionsPage != null && state.sessionsPage > 1) ? state.sessionsPage : null,
    sessionsPageSize: (state.sessionsPageSize != null && state.sessionsPageSize !== DEFAULT_PAGE_SIZE) ? state.sessionsPageSize : null,
    eventsPage: (state.eventsPage != null && state.eventsPage > 1) ? state.eventsPage : null,
    eventsPageSize: (state.eventsPageSize != null && state.eventsPageSize !== DEFAULT_PAGE_SIZE) ? state.eventsPageSize : null,
  };
}

/**
 * Decode the trace view state from a Route.
 * Unknown or missing fields default to null.
 * Safe to call on any Route (ignores non-trace routes gracefully).
 * Does NOT include the legacy `session` field — read `route.session` separately.
 */
export function decodeTraceState(route: Route): TraceViewState {
  return {
    selectedSession: nvl(route.selectedSession),
    detailTab: nvl(route.detailTab),
    from: nvl(route.from),
    to: nvl(route.to),
    sessionsPage: nvlInt(route.sessionsPage),
    sessionsPageSize: nvlPageSize(route.sessionsPageSize),
    eventsPage: nvlInt(route.eventsPage),
    eventsPageSize: nvlPageSize(route.eventsPageSize),
  };
}

// =============================================================================
// Shared range helpers (date-range slots — T-16-14 date-picker will fill these)
// =============================================================================

/**
 * ISO-8601 UTC date-range pair. The from/to slots are shared by both
 * CostLedgerViewState and TraceViewState (CF-1: date-picker ST-05 fills them).
 */
export interface DateRange {
  from: string | null;
  to: string | null;
}

/** Extract just the range fields from a Route (works for both trace and cost-ledger). */
export function decodeRangeFromRoute(route: Route): DateRange {
  return {
    from: nvl(route.from),
    to: nvl(route.to),
  };
}

/**
 * Returns a partial Route with range fields set.
 * Suitable for merging: `{ ...currentRoute, ...encodeRange({ from, to }) }`.
 */
export function encodeRange(range: DateRange): Pick<Route, 'from' | 'to'> {
  return {
    from: nvl(range.from),
    to: nvl(range.to),
  };
}

// =============================================================================
// Internal helpers
// =============================================================================

/**
 * Normalize to null: empty string and undefined both become null.
 * This ensures round-trip stability (null/undefined/"" are all "no value").
 */
function nvl(value: string | null | undefined): string | null {
  if (value == null || value === '') return null;
  return value;
}

/**
 * Parse an integer from a string or number, returning null for invalid/missing values.
 * Used for page number route params.
 */
function nvlInt(value: number | string | null | undefined): number | null {
  if (value == null) return null;
  if (typeof value === 'number') return Number.isFinite(value) && value > 0 ? value : null;
  const n = parseInt(value, 10);
  return Number.isFinite(n) && n > 0 ? n : null;
}

/**
 * Parse a page size from a string or number, validating against VALID_PAGE_SIZES.
 * Returns null for invalid/missing values (callers use DEFAULT_PAGE_SIZE as the fallback).
 */
function nvlPageSize(value: number | string | null | undefined): number | null {
  const n = nvlInt(value);
  if (n == null) return null;
  return VALID_PAGE_SIZES.has(n) ? n : null;
}
