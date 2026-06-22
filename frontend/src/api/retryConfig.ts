// =============================================================================
// Retry configuration for TanStack Query (T-2026-06-14-13, Lane F1 / R1).
//
// Algorithm: full-jitter exponential backoff.
//   delay = random(0, min(cap, base * 2^attempt))
//
// Retry policy:
//   - Only retry idempotent GETs (mutations set retry:0 globally in QueryClient).
//   - Retry on: network error (status 0), 5xx, 429.
//   - NEVER retry 4xx (except 429) — client errors won't resolve on retry.
//   - Max 3 attempts (failureCount 0-indexed, TQ passes 0 on first retry).
//   - Honour Retry-After header when present on 429 responses.
//
// References:
//   AWS Architecture Blog — Exponential Backoff And Jitter (canonical full-jitter source)
//   RFC 9110 — Retry-After header
// =============================================================================

import type { ScopeApiError } from './apiClient';

/** Status codes that indicate transient server-side failures safe to retry. */
const RETRYABLE_STATUSES = new Set([429, 500, 502, 503, 504]);

/** Maximum retry attempts (0-indexed failureCount). */
const MAX_ATTEMPTS = 3;

/** Base delay for exponential backoff in ms. */
const BASE_MS = 1_000;

/** Cap for exponential backoff in ms. */
const CAP_MS = 30_000;

/**
 * Full-jitter exponential backoff.
 *
 * delay = random(0, min(CAP_MS, BASE_MS * 2^attempt))
 *
 * TanStack Query passes `attempt` as 0-indexed (0 on the first retry).
 * Produces a uniform random value in [0, ceiling) — minimises thundering herd.
 */
export function retryDelay(attempt: number): number {
  const ceiling = Math.min(CAP_MS, BASE_MS * Math.pow(2, attempt));
  return Math.random() * ceiling;
}

/**
 * Full-jitter delay that honours the `Retry-After` header from a 429 response.
 *
 * When the error carries `retryAfterMs` (parsed from Retry-After in apiFetch),
 * that value takes precedence over the computed jitter delay.
 *
 * This is the function to register as `retryDelay` in the QueryClient defaults.
 */
export function retryDelayWithHeader(attempt: number, error: unknown): number {
  const retryAfterMs = (error as Partial<ScopeApiError>).retryAfterMs;
  if (typeof retryAfterMs === 'number' && retryAfterMs > 0) return retryAfterMs;
  return retryDelay(attempt);
}

/**
 * Retry filter for TanStack Query queries.
 *
 * Returns true (= retry) only for:
 *   - Transport failures (status 0 — network unreachable, DNS, not-yet-aborted).
 *   - Transient server errors: 5xx + 429 (RETRYABLE_STATUSES).
 *
 * Returns false (= give up) for:
 *   - 4xx client errors (except 429) — retrying a 400/401/403/404 never helps.
 *   - Exceeded MAX_ATTEMPTS.
 *
 * Mutations are NEVER auto-retried: the QueryClient sets mutations.retry = 0 globally,
 * so this filter is only ever called for queries.
 */
export function retryFilter(failureCount: number, error: unknown): boolean {
  if (failureCount >= MAX_ATTEMPTS) return false;

  const status = (error as Partial<ScopeApiError>).status;
  if (typeof status === 'number') {
    // status 0 = transport failure (network, DNS) — always retry.
    if (status === 0) return true;
    return RETRYABLE_STATUSES.has(status);
  }
  // No status field = not a ScopeApiError (unexpected error type) — retry cautiously.
  return true;
}
