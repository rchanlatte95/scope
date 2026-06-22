// =============================================================================
// useSingleFireMutation (T-2026-06-14-13, Lane F1 / R3).
//
// Wraps TanStack Query's useMutation with a useRef in-flight guard so the
// mutation fires at most once concurrently, even if the button is clicked
// twice before React re-renders and the `disabled` attribute propagates to
// the DOM.
//
// Background (TQ GitHub discussion #10041): `isPending` alone is not sufficient
// because async handlers (especially those going through validation layers like
// react-hook-form.handleSubmit) can fire twice before React re-renders. The TQ
// maintainers confirmed this gap; the recommended pattern is:
//   1. `<button disabled={mutation.isPending}>` — visual guard, prevents normal clicks.
//   2. A `useRef<boolean>` guard inside the handler — structural guard for the race.
//
// Usage:
//   const { safeMutate, isPending } = useSingleFireMutation({ mutationFn: myFn });
//   <button onClick={() => safeMutate(vars)} disabled={isPending}>...</button>
//
// For run-now mutations, attach an `Idempotency-Key` header in the mutationFn:
//   mutationFn: ({ label }) => runCronAgent(label, crypto.randomUUID())
// The backend honours this header and returns 409 if a run is already in-flight.
// The 409 surfaces as a ScopeApiError with status 409; callers may treat it as a
// typed non-error "already running" outcome and ignore it (see cronApi.runCronAgent).
// =============================================================================

import { useRef } from 'react';
import { useMutation } from '@tanstack/react-query';
import type { UseMutationOptions, UseMutationResult } from '@tanstack/react-query';

/**
 * The result of useSingleFireMutation: the full UseMutationResult PLUS
 * `safeMutate` — the guarded invocation function to use instead of `mutate`.
 */
export type SingleFireMutationResult<
  TData,
  TError,
  TVariables,
  TContext,
> = UseMutationResult<TData, TError, TVariables, TContext> & {
  /**
   * Guarded mutate function. Ignores invocations while a mutation is in-flight
   * (both the isPending React state and the useRef structural guard are checked).
   * Use this instead of `mutation.mutate` on all costly or non-idempotent actions:
   * run-now, create, delete, enable/disable.
   */
  safeMutate: (variables: TVariables) => void;
};

/**
 * Wraps useMutation with an in-flight ref guard for double-submit protection.
 *
 * The guard fires at most once per concurrent mutation cycle. On settlement
 * (success or error), the guard resets and a new call to safeMutate is accepted.
 *
 * @param options - Standard useMutation options. Do NOT pass `retry` here;
 *   mutations are globally set to retry:0 in the QueryClient (never auto-retry
 *   non-idempotent mutations — agent spend risk).
 */
export function useSingleFireMutation<
  TData = unknown,
  TError = unknown,
  TVariables = void,
  TContext = unknown,
>(
  options: UseMutationOptions<TData, TError, TVariables, TContext>,
): SingleFireMutationResult<TData, TError, TVariables, TContext> {
  const inFlight = useRef(false);
  const mutation = useMutation<TData, TError, TVariables, TContext>(options);

  function safeMutate(variables: TVariables): void {
    // Structural guard: ignore if already in-flight (catches the double-click race
    // that isPending / disabled alone cannot catch before React re-renders).
    if (inFlight.current) return;
    // Also guard on the React state: if mutation is pending (e.g. component re-mounted
    // and inFlight was reset, but TQ still has a pending mutation in-flight), bail.
    if (mutation.isPending) return;

    inFlight.current = true;
    mutation.mutate(variables, {
      onSettled: () => {
        inFlight.current = false;
      },
    });
  }

  return { ...mutation, safeMutate };
}
