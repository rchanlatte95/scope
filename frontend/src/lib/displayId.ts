// =============================================================================
// scope-trace-v1 display-id + node-alias resolution (owner feedback item 3).
//
// The SERVER already computes the canonical id, the 7-char BASE36 display_id, and
// the {agentOrdinal} / {agentOrdinal}.{seq} alias for every node and entry
// (TraceIdCodec / TraceProjection). This module is the CONSUMER side: it resolves a
// user-spoken reference ("look at node 3.7", "node HEBIF8", "0hebif8") against the
// loaded document.
//
// Spec: design-scope-trace-v1.xml -> idScheme/displayId/resolutionNormalization +
// idScheme/nodeAlias/spokenUsage. The encoder is server-side; the contract's
// "resolution is case-insensitive, accepts dropped leading zeros, and maps O->0 and
// I/L->1 before matching" is implemented here. We never re-encode, so the forgiving
// input normalization costs nothing (one-way).
// =============================================================================

/** The BASE36 display-id width the contract pins (left-padded with '0'). */
export const DISPLAY_ID_LENGTH = 7;

/**
 * Normalizes a user-typed display id for matching, per the contract:
 * uppercase, map confusables O->0 and I/L->1, strip surrounding whitespace, then
 * left-pad with '0' to the fixed width (so a dropped leading zero still matches).
 * Returns null when the input cannot be a display id (empty, or has characters
 * outside the BASE36 alphabet after confusable mapping, or is too long).
 */
export function normalizeDisplayId(input: string): string | null {
  let s = input.trim().toUpperCase();
  if (s.length === 0) return null;
  // Confusable mapping borrowed from Crockford base32 (contract idScheme).
  s = s.replace(/O/g, '0').replace(/[IL]/g, '1');
  // After mapping, only BASE36 alphabet 0-9 A-Z may remain.
  if (!/^[0-9A-Z]+$/.test(s)) return null;
  if (s.length > DISPLAY_ID_LENGTH) return null;
  return s.padStart(DISPLAY_ID_LENGTH, '0');
}

/**
 * An alias is "{agentOrdinal}" (span) or "{agentOrdinal}.{seq}" (event), all digits.
 * Ordinal 1 is the root agent (unattached/span-less events use "1.{seq}").
 * Dispatched sub-agent spans start at ordinal 2 ("2.{seq}", "3.{seq}", ...).
 * No ordinal 0 is assigned. Normalization here is only trimming;
 * aliases are numeric and not confusable. Returns null for non-alias input.
 */
export function normalizeAlias(input: string): string | null {
  const s = input.trim();
  if (/^\d+$/.test(s) || /^\d+\.\d+$/.test(s)) return s;
  return null;
}

/** Minimal shape every resolvable node/entry exposes (display_id always present, alias optional). */
export interface Resolvable {
  id: string;
  display_id: string;
  alias?: string | null | undefined;
}

/** A resolution outcome: zero, exactly one, or several in-document candidates. */
export type ResolveResult<T extends Resolvable> =
  | { kind: 'none' }
  | { kind: 'one'; match: T }
  | { kind: 'ambiguous'; candidates: T[] };

/**
 * Resolves a spoken/typed reference against the loaded document (in-document match
 * only, per the contract's "matches within the loaded document first" posture).
 *
 * Resolution order:
 *   1. exact alias match (aliases are document-scoped and speakable: "3.7", "20")
 *   2. normalized display-id match (global-ish handle: "HEBIF8" -> "0HEBIF8")
 *
 * Display ids are resolution handles, not unique keys (32 random bits): 2+ matches
 * return 'ambiguous' and never auto-pick. Workspace-wide / cross-document resolution
 * is a server concern (not implemented here; the SPA loads one session at a time).
 */
export function resolveReference<T extends Resolvable>(query: string, nodes: readonly T[]): ResolveResult<T> {
  const alias = normalizeAlias(query);
  if (alias !== null) {
    const aliasMatches = nodes.filter((n) => n.alias === alias);
    if (aliasMatches.length === 1 && aliasMatches[0] !== undefined) return { kind: 'one', match: aliasMatches[0] };
    if (aliasMatches.length > 1) return { kind: 'ambiguous', candidates: aliasMatches };
  }

  const norm = normalizeDisplayId(query);
  if (norm !== null) {
    // BASE36 stored display ids CAN contain the confusable letters I/O/L (the encoder
    // uses the full alphabet, e.g. live ids 1F9XCBI, 09KZZ2I). Normalize BOTH sides so
    // a query and a stored id collapse to the same confusable-free form before matching;
    // otherwise a stored "...2I" would never match a query the resolver mapped to "...21".
    const idMatches = nodes.filter((n) => normalizeDisplayId(n.display_id) === norm);
    if (idMatches.length === 1 && idMatches[0] !== undefined) return { kind: 'one', match: idMatches[0] };
    if (idMatches.length > 1) return { kind: 'ambiguous', candidates: idMatches };
  }

  return { kind: 'none' };
}
