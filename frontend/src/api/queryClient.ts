// =============================================================================
// TanStack Query client configuration (T-2026-06-14-13, Lane F1 / R2).
//
// Single source of truth for:
//   - Global staleTime / gcTime defaults.
//   - Retry filter + delay wired to retryConfig.ts.
//   - Global mutation retry = 0 (mutations NEVER auto-retried — agent spend risk).
//   - Per-endpoint staleTime constants (imported by future useQuery call-sites).
//   - Query key taxonomy (imported by all useQuery / invalidateQueries call-sites).
//   - Invalidation key map (recommended helpers for post-mutation cache invalidation).
//
// What is NOT here:
//   - QueryClientProvider setup lives in main.tsx.
//   - Per-endpoint useQuery hooks are the next wave (F2-F4); this file only
//     provides the infra they import. The existing view files are NOT touched.
// =============================================================================

import { QueryClient } from '@tanstack/react-query';
import { retryFilter, retryDelayWithHeader } from './retryConfig';

// ---------------------------------------------------------------------------
// Per-endpoint staleTime constants
// (ratified in frontend-resilience-research-2026-06-14.md, section 3)
// ---------------------------------------------------------------------------

/** staleTime for GET /api/cron/agents (list). Semi-static; mutations invalidate. */
export const STALE_AGENTS_MS = 30_000;

/** staleTime for GET /api/cron/agents/{label} (single agent detail). */
export const STALE_AGENT_MS = 30_000;

/** staleTime for GET /api/cron/agents/{label}/runs (run history). */
export const STALE_RUNS_MS = 15_000;

/**
 * staleTime for GET /api/cron/agents/{label}/runs/{id} (run-status polling endpoint).
 * Zero means data is immediately considered stale: every observer refetch goes to the network.
 */
export const STALE_RUN_STATUS_MS = 0;

/** staleTime for GET /api/command/status and GET /api/trace/* (historical endpoints). */
export const STALE_COMMANDS_TRACES_MS = 120_000;

/** staleTime for GET /api/trace/graph/* (near-static index data). */
export const STALE_GRAPH_MS = 300_000;

/** staleTime for GET /api/command/budget (cost ledger; re-fetched when window changes). */
export const STALE_BUDGET_MS = 120_000;

/**
 * staleTime for GET /api/command/base-launches (base-launch cost rollup; ST-07).
 * Same cadence as the budget endpoint — historical cost data that changes slowly.
 */
export const STALE_BASE_LAUNCHES_MS = 120_000;

// ---------------------------------------------------------------------------
// Query key taxonomy
// (ratified in frontend-resilience-research-2026-06-14.md, section 4)
//
// Use these factory functions everywhere useQuery and invalidateQueries appear.
// Prefix-matching: ['cron', 'agents'] matches ['cron', 'agents', label] for
// broad invalidations like "all agent queries".
// ---------------------------------------------------------------------------

export const queryKeys = {
  /** All cron agents list: GET /api/cron/agents */
  agents: () => ['cron', 'agents'] as const,

  /** Single cron agent: GET /api/cron/agents/{label} */
  agent: (label: string) => ['cron', 'agent', label] as const,

  /** Run history for an agent: GET /api/cron/agents/{label}/runs */
  runs: (label: string) => ['cron', 'runs', label] as const,

  /** Single run status (polling): GET /api/cron/agents/{label}/runs/{runId} */
  runStatus: (label: string, runId: string) => ['cron', 'run-status', label, runId] as const,

  /** Cron harness capabilities: GET /api/cron/capabilities */
  capabilities: () => ['cron', 'capabilities'] as const,

  /** Command status: GET /api/command/status */
  commandStatus: () => ['commands'] as const,

  /**
   * Proof Paths impact report: GET /api/impact?symbol=<FQN> (PP-12a).
   * Keyed on the anchor symbol FQN (null when no symbol selected so the
   * `enabled` guard suppresses the fetch). Each distinct symbol is a separate
   * cache entry; swapping the symbol cancels the superseded fetch.
   */
  proofPaths: (symbol: string | null) => ['proof-paths', 'impact', symbol ?? null] as const,

  /**
   * Trace sessions list: GET /api/trace/sessions (with optional from/to/includeDeleted/page/pageSize).
   * page and pageSize default to 1/10; their inclusion in the key ensures separate cache
   * entries per page so page navigation doesn't evict adjacent pages from the cache.
   */
  traceSessions: (from?: string | null, to?: string | null, includeDeleted?: boolean, page?: number, pageSize?: number) =>
    ['traces', 'sessions', from ?? null, to ?? null, includeDeleted ?? false, page ?? 1, pageSize ?? 10] as const,

  /** Cron sessions array: GET /api/trace/cron-sessions (with optional from/to/includeDeleted). */
  cronSessions: (from?: string | null, to?: string | null, includeDeleted?: boolean) =>
    ['traces', 'cron-sessions', from ?? null, to ?? null, includeDeleted ?? false] as const,

  /** Session graph: GET /api/trace/graph?session= */
  traceGraph: (sessionKey: string) => ['traces', 'graph', sessionKey] as const,

  /**
   * Full trace: GET /api/trace/full?session= (with optional page/pageSize for server-side pagination).
   * cursor is null in page-based mode; page/pageSize are undefined in cursor-based mode.
   * Including all three in the key ensures correct cache isolation between modes and pages.
   */
  traceFull: (sessionKey: string, cursor: string | null, page?: number, pageSize?: number) =>
    ['traces', 'full', sessionKey, cursor, page ?? null, pageSize ?? null] as const,

  /** Rollup: GET /api/trace/rollup?session= */
  traceRollup: (sessionKey: string) => ['traces', 'rollup', sessionKey] as const,

  /** Top actions: GET /api/trace/top-actions?session= */
  traceTopActions: (sessionKey: string) => ['traces', 'top-actions', sessionKey] as const,

  /**
   * Command budget: GET /api/command/budget.
   * Keyed on windowDays + optional from/to so preset and custom-range queries are
   * deduplicated correctly. Pass from=null and to=null for day-based queries.
   */
  commandBudget: (windowDays: number, from?: string | null, to?: string | null) =>
    ['commands', 'budget', windowDays, from ?? null, to ?? null] as const,

  /**
   * Base-launch cost rollup: GET /api/command/base-launches (ST-07).
   * Keyed on windowDays + optional from/to (same semantics as commandBudget).
   * Used by SessionPicker to group CLI generations under their base launch.
   */
  baseLaunches: (windowDays: number, from?: string | null, to?: string | null) =>
    ['commands', 'base-launches', windowDays, from ?? null, to ?? null] as const,
} as const;

// ---------------------------------------------------------------------------
// Invalidation key map
//
// These are the recommended queryClient.invalidateQueries calls to use in
// mutation onSuccess handlers. They are provided as constants (not functions)
// so callers always use the same prefix, never accidentally over-invalidate.
//
// Pattern:
//   onSuccess: () => void queryClient.invalidateQueries({ queryKey: invalidationKeys.agents })
// ---------------------------------------------------------------------------

export const invalidationKeys = {
  /** Invalidate all cron agent queries (list + any single-agent detail). */
  agents: ['cron', 'agents'] as const,

  /** Invalidate all cron queries (agents + runs + capabilities). */
  allCron: ['cron'] as const,

  /** Invalidate the command status. */
  commandStatus: ['commands'] as const,

  /** Invalidate the trace sessions list. */
  traceSessions: ['traces', 'sessions'] as const,

  /** Invalidate the cron sessions list (all variants). */
  cronSessions: ['traces', 'cron-sessions'] as const,

  /** Invalidate all trace queries. */
  allTraces: ['traces'] as const,
} as const;

// ---------------------------------------------------------------------------
// QueryClient singleton
//
// Defaults follow the research doc ratifications:
//   staleTime:           60 000 ms (global; overridden per-query via queryKeys + STALE_* constants)
//   gcTime:             300 000 ms (TQ default; explicit for clarity)
//   retry:              retryFilter (network + 5xx + 429 only; max 3 attempts)
//   retryDelay:         retryDelayWithHeader (full-jitter + Retry-After honour)
//   refetchOnWindowFocus: false (deliberate: a local governance dashboard does not need to
//                         refetch every query on tab-focus. Freshness is covered by per-query
//                         staleTime, explicit invalidation after mutations, and the run-status
//                         refetchInterval. Focus-refetch caused nondeterministic UI churn — it
//                         re-rendered the cron run-confirm subtree mid-interaction and discarded
//                         the trace load-more pages — so it is off.)
//   mutations.retry:    0 (NEVER auto-retry mutations — run-now incurs agent spend)
// ---------------------------------------------------------------------------

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 60_000,
      gcTime: 300_000,
      retry: retryFilter,
      retryDelay: retryDelayWithHeader,
      refetchOnWindowFocus: false,
    },
    mutations: {
      retry: 0,
    },
  },
});
