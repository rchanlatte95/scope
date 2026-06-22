// =============================================================================
// buildFlags.ts — compile-time build configuration flags.
//
// EXTERNAL_BUILD (default: true for the shipped product build)
// ------------------------------------------------------------
// When true, surfaces that are internal/diagnostic-only or not yet shipped
// (SaaS-sensitive) are hidden from nav and their routes are neutralized so
// deep-links do not expose them.
//
// The external build is the default. To build an internal/diagnostic build
// (e.g. for Scope developers):
//
//   VITE_EXTERNAL_BUILD=false npm run build
//   VITE_EXTERNAL_BUILD=false npm run dev
//
// The flag is read from the Vite env prefix VITE_EXTERNAL_BUILD. Any value
// other than the string "false" (case-insensitive) is treated as true, so
// omitting the variable or setting it to "true" / "1" / "" all produce the
// external (default) build.
//
// Gated nav areas (PP-15 + T-2026-06-20-10):
//   - context-packs      (internal/diagnostic surface)
//   - security-controls  (SaaS-sensitive, not yet available)
//   - audit-log          (SaaS-sensitive, not yet available)
//   - org-uplink         (SaaS-sensitive, not yet available)
//   - billing            (SaaS-sensitive, not yet available)
//
// In external builds:
//   - All five are filtered from the nav rail.
//   - Deep-links to gated routes fall through to Command (default), NOT to
//     a "coming soon" page.
//
// GATED_AREAS and isAreaAvailable are exported as a stable contract for other
// modules (e.g. CommandView's dead-link cleanup in Task A).
// =============================================================================

/**
 * True in the standard external product build (default).
 * Set VITE_EXTERNAL_BUILD=false to produce an internal/diagnostic build that
 * exposes all surfaces including gated areas.
 */
export const IS_EXTERNAL_BUILD: boolean =
  // Vite injects VITE_* env vars into import.meta.env. TypeScript types this as
  // ImportMetaEnv, which only has a string index signature returning `string`.
  // The optional-chain + toLowerCase is safe here because import.meta.env members
  // are strings (or undefined for missing vars).
  (import.meta.env.VITE_EXTERNAL_BUILD as string | undefined)?.toLowerCase() !== 'false';

/**
 * The set of nav area route names that are hidden in external builds.
 *
 * These areas are either internal/diagnostic surfaces (context-packs) or
 * SaaS-sensitive areas not yet shipped (security-controls, audit-log,
 * org-uplink, billing).
 *
 * The values are RouteName strings from router.ts. The type is ReadonlySet<string>
 * here (not ReadonlySet<RouteName>) to avoid a circular import — router.ts imports
 * buildFlags.ts at runtime, so buildFlags.ts must not import router.ts at runtime.
 * Consumers that import this set alongside RouteName from router.ts get the correct
 * narrowing via their own type context.
 *
 * Contract: this name is stable — Task A (CommandView dead-link cleanup)
 * imports GATED_AREAS directly. Do not rename.
 */
export const GATED_AREAS: ReadonlySet<string> = new Set([
  'context-packs',
  'security-controls',
  'audit-log',
  'org-uplink',
  'billing',
]);

/**
 * Returns true if the named area is available in the current build.
 *
 * In internal builds (IS_EXTERNAL_BUILD=false) all areas are available.
 * In external builds (IS_EXTERNAL_BUILD=true, the default) areas in
 * GATED_AREAS are not available.
 *
 * Contract: this name is stable — Task A (CommandView dead-link cleanup)
 * imports isAreaAvailable directly. Do not rename.
 *
 * @param name  A route area name (RouteName from router.ts).
 * @returns     true when the area should be shown/linked; false when hidden.
 */
export function isAreaAvailable(name: string): boolean {
  return !(IS_EXTERNAL_BUILD && GATED_AREAS.has(name));
}
