// =============================================================================
// Query key taxonomy re-export (T-2026-06-14-13, Lane F1 / R2).
//
// queryKeys and invalidationKeys live in queryClient.ts (collocated with the
// QueryClient config that owns their defaults). This file re-exports them from
// a dedicated module path so call-sites that only need keys do not import the
// QueryClient singleton, avoiding unwanted module-level side effects.
// =============================================================================

export { queryKeys, invalidationKeys } from './queryClient';
