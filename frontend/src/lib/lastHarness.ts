// =============================================================================
// Last-harness persistence (T-2026-06-12-23).
//
// owner ruled (2026-06-12) that the Cron Agents create form should REMEMBER the
// user's last harness selection and PRE-FILL it on the next create: visible and
// freely changeable, both options always shown, nothing auto-committed. A
// first-time user (nothing stored) keeps the exact no-default sentinel behavior.
//
// This module is the only place that touches client storage. It is DOM-free
// except for an injectable Storage seam so the node-environment Vitest config can
// exercise it with a stub: window.localStorage does not exist under environment
// 'node'. Every storage access is wrapped in try/catch because storage may be
// unavailable (private mode, disabled cookies, SecurityError), and a read of an
// unknown/corrupt value resolves to null so the form falls back to the sentinel.
//
// Availability branch (verified 2026-06-12): the server (CronService.Create ->
// ValidateCreateRequest) only checks CronHarnesses.IsSupported(harness); it does
// NOT reject a definition whose harness capability is currently unavailable. So a
// stored harness is pre-filled REGARDLESS of current capability availability. The
// native <option disabled> on an unavailable harness still prevents a fresh pick,
// but a previously-used-and-stored value is honored.
// =============================================================================

import { HARNESS_IDS, type HarnessId } from './cron';

/** Namespaced storage key. Namespaced to avoid collisions with any future prefs. */
export const LAST_HARNESS_KEY = 'scope.cron.lastHarness';

/**
 * Minimal Storage-like surface this module needs. window.localStorage satisfies
 * it; tests pass a stub. Kept narrow so a stub only implements getItem/setItem.
 */
export interface HarnessStore {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

/** Type guard: is a raw string one of the known harness ids? */
function isHarnessId(value: string | null): value is HarnessId {
  return value !== null && (HARNESS_IDS as readonly string[]).includes(value);
}

/**
 * Resolves the default store: window.localStorage when present, otherwise null.
 * Returns null (rather than throwing) under the node Vitest env or any host that
 * exposes no localStorage, so callers degrade to the sentinel without crashing.
 */
function defaultStore(): HarnessStore | null {
  try {
    if (typeof window !== 'undefined') {
      return window.localStorage;
    }
  } catch {
    // Accessing window.localStorage can itself throw (SecurityError) in some hosts.
  }
  return null;
}

/**
 * Reads the last stored harness id. Returns null when nothing is stored, the
 * stored value is unknown/corrupt, storage is unavailable, or any access throws.
 * A null result means "no pre-fill": the form keeps the no-default sentinel.
 *
 * @param store Injectable storage seam; defaults to window.localStorage when present.
 */
export function readLastHarness(store: HarnessStore | null = defaultStore()): HarnessId | null {
  if (!store) return null;
  try {
    const raw = store.getItem(LAST_HARNESS_KEY);
    return isHarnessId(raw) ? raw : null;
  } catch {
    return null;
  }
}

/**
 * Persists the last successfully used harness id. Only known harness ids are
 * written (an unknown value is ignored, never persisted). Storage failures are
 * swallowed: remembering the choice is a convenience, never load-bearing.
 *
 * @param harness The harness id the user just created an agent with.
 * @param store Injectable storage seam; defaults to window.localStorage when present.
 */
export function writeLastHarness(
  harness: string,
  store: HarnessStore | null = defaultStore(),
): void {
  if (!store) return;
  if (!isHarnessId(harness)) return;
  try {
    store.setItem(LAST_HARNESS_KEY, harness);
  } catch {
    // Quota/SecurityError: a failed remember must not break a successful create.
  }
}
