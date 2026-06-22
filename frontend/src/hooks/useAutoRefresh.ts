/**
 * useAutoRefresh.ts — Portable auto-refresh primitive (TRACE-04).
 *
 * Returns the state and actions needed to wire TanStack Query's built-in
 * `refetchInterval` into any view, plus a manual `refreshNow()` trigger.
 *
 * Design contract:
 * - Uses TanStack Query refetchInterval — does NOT hand-roll setInterval.
 * - Default: enabled=true, interval=60 000 ms (60 s).
 * - Presets: 30 s / 1 m / 5 m (standard choices; caller may extend).
 * - `refetchInterval` is the value to forward directly to useQuery options:
 *     enabled ? intervalMs : false
 * - `refreshNow()` is a callback the caller invokes to imperatively refetch.
 *   The hook provides the state; the caller binds it to queryClient.invalidateQueries
 *   or query.refetch() — the hook itself never touches a queryClient.
 *
 * Usage:
 *   const { enabled, setEnabled, intervalMs, setIntervalMs,
 *           refetchInterval, refreshNow } = useAutoRefresh();
 *   const query = useQuery({ queryKey: ..., refetchInterval });
 *   // call refreshNow() by wiring it to a button; inside it call query.refetch().
 *
 * This hook is purely logic (no DOM, no refs). Unit-testable without a renderer.
 */

import { useCallback, useState } from 'react';

// ---------------------------------------------------------------------------
// Interval presets
// ---------------------------------------------------------------------------

/** Human-readable label / millisecond pairs for the interval picker. */
export const REFRESH_PRESETS: { label: string; ms: number }[] = [
  { label: '30 s', ms: 30_000 },
  { label: '1 m', ms: 60_000 },
  { label: '5 m', ms: 300_000 },
];

/** Default interval in milliseconds. */
export const DEFAULT_REFRESH_INTERVAL_MS = 60_000;

// ---------------------------------------------------------------------------
// Hook types
// ---------------------------------------------------------------------------

export interface AutoRefreshState {
  /** Whether auto-refresh is currently enabled. */
  enabled: boolean;
  /** Current interval in milliseconds. */
  intervalMs: number;
  /**
   * The value to forward to useQuery's `refetchInterval` option.
   * When enabled: intervalMs. When disabled: false (TQ treats false as off).
   */
  refetchInterval: number | false;
}

export interface AutoRefreshActions {
  /** Toggle auto-refresh on or off. */
  setEnabled: (on: boolean) => void;
  /** Change the interval (should be one of REFRESH_PRESETS). */
  setIntervalMs: (ms: number) => void;
  /**
   * Trigger an immediate refresh. The CALLER wires this to the actual
   * query refetch / cache invalidation — the hook does not touch queryClient.
   * A typical pattern:
   *   const refresh = useCallback(() => {
   *     void query.refetch();
   *   }, [query]);
   *   useAutoRefresh({ onRefreshNow: refresh });
   *
   * When `onRefreshNow` is provided it is called on every manual trigger.
   */
  refreshNow: () => void;
}

export interface UseAutoRefreshOptions {
  /** Initial enabled state. Default: true. */
  defaultEnabled?: boolean;
  /** Initial interval in milliseconds. Default: DEFAULT_REFRESH_INTERVAL_MS. */
  defaultIntervalMs?: number;
  /**
   * Called on every manual refresh trigger (the "Refresh" button).
   * The hook itself never fetches — the caller binds this to query.refetch().
   */
  onRefreshNow?: () => void;
}

export interface UseAutoRefreshReturn extends AutoRefreshState, AutoRefreshActions {}

// ---------------------------------------------------------------------------
// Hook
// ---------------------------------------------------------------------------

/**
 * useAutoRefresh — portable auto-refresh state for TanStack Query views.
 *
 * Returns enabled/intervalMs state and the derived `refetchInterval` value
 * (ready for `useQuery({ refetchInterval })`), plus setEnabled/setIntervalMs
 * actions and a `refreshNow()` trigger.
 */
export function useAutoRefresh({
  defaultEnabled = true,
  defaultIntervalMs = DEFAULT_REFRESH_INTERVAL_MS,
  onRefreshNow,
}: UseAutoRefreshOptions = {}): UseAutoRefreshReturn {
  const [enabled, setEnabled] = useState<boolean>(defaultEnabled);
  const [intervalMs, setIntervalMs] = useState<number>(defaultIntervalMs);

  const refetchInterval: number | false = enabled ? intervalMs : false;

  const refreshNow = useCallback(() => {
    onRefreshNow?.();
  }, [onRefreshNow]);

  return {
    enabled,
    intervalMs,
    refetchInterval,
    setEnabled,
    setIntervalMs,
    refreshNow,
  };
}
