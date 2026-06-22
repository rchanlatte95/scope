// =============================================================================
// CostProvenance union and display maps (FE-1).
//
// CostProvenance = wire_exact | joined_exact | provider_estimated |
//                  scope_estimated | unmetered | unavailable
//
// Matches FE-B's emitted snake_case field. The tier classification drives
// CostProvenanceChip styling: exact values get the "exact" tier, estimated
// values get the "estimated" tier, and the absence-values (unmetered /
// unavailable) get the "none" tier.
//
// Exhaustive vitest in provenance.test.ts covers all 6 values.
// =============================================================================

/** Closed union matching FE-B's emitted snake_case cost_provenance field. */
export type CostProvenance =
  | 'wire_exact'
  | 'joined_exact'
  | 'provider_estimated'
  | 'scope_estimated'
  | 'unmetered'
  | 'unavailable';

/** All valid CostProvenance values in declaration order. */
export const COST_PROVENANCES: readonly CostProvenance[] = [
  'wire_exact',
  'joined_exact',
  'provider_estimated',
  'scope_estimated',
  'unmetered',
  'unavailable',
] as const;

/**
 * Quality tier drives chip styling.
 * - exact: value is precise and verified (wire or joined wire)
 * - estimated: value is present but approximated
 * - none: no cost value (unmetered work or cost data unavailable)
 */
export type ProvenanceTier = 'exact' | 'estimated' | 'none';

/**
 * Visible label for each CostProvenance value. Every label is non-empty and
 * operationally precise (no "Optimized", no vague words).
 */
export const PROVENANCE_LABEL: Readonly<Record<CostProvenance, string>> = {
  wire_exact:          'Wire exact',
  joined_exact:        'Joined exact',
  provider_estimated:  'Provider estimate',
  scope_estimated:     'Scope estimate',
  unmetered:           'Unmetered',
  unavailable:         'Unavailable',
};

/**
 * Tier classification for each CostProvenance value. Used by
 * CostProvenanceChip to choose the chip style without hard-coding colors.
 */
export const PROVENANCE_TIER: Readonly<Record<CostProvenance, ProvenanceTier>> = {
  wire_exact:          'exact',
  joined_exact:        'exact',
  provider_estimated:  'estimated',
  scope_estimated:     'estimated',
  unmetered:           'none',
  unavailable:         'none',
};

/**
 * Returns the visible label for a CostProvenance value.
 * Convenience wrapper; prefer PROVENANCE_LABEL map for batch access.
 */
export function provenanceLabel(p: CostProvenance): string {
  return PROVENANCE_LABEL[p];
}

/**
 * Returns the quality tier for a CostProvenance value.
 * Convenience wrapper; prefer PROVENANCE_TIER map for batch access.
 */
export function provenanceTier(p: CostProvenance): ProvenanceTier {
  return PROVENANCE_TIER[p];
}

/** Set of all valid CostProvenance values for O(1) membership tests. */
const COST_PROVENANCE_SET = new Set<string>(COST_PROVENANCES);

/**
 * Narrows an arbitrary string (from the cron API or trace API cost_provenance field)
 * to a CostProvenance. Unknown values return 'unavailable' as the safe fallback.
 * Never throws.
 */
export function toCostProvenance(raw: string | null | undefined): CostProvenance {
  if (raw != null && raw !== '' && COST_PROVENANCE_SET.has(raw)) return raw as CostProvenance;
  return 'unavailable';
}

/**
 * Two-bucket known/assumed classification for provenance values.
 * KNOWN: wire_exact + joined_exact + provider_estimated (model was identified)
 * ASSUMED: scope_estimated (model was NOT captured; assumed Sonnet rate)
 * NO-DATA: unmetered | unavailable (excluded from priced buckets)
 */
export type KnownAssumedBucket = 'known' | 'assumed' | 'no-data';

export const PROVENANCE_BUCKET: Readonly<Record<CostProvenance, KnownAssumedBucket>> = {
  wire_exact:          'known',
  joined_exact:        'known',
  provider_estimated:  'known',
  scope_estimated:     'assumed',
  unmetered:           'no-data',
  unavailable:         'no-data',
};

export interface KnownAssumedSplit {
  knownUsd: number;
  assumedUsd: number;
  pricedTotal: number;
}

/**
 * Computes the known/assumed spend split from a provenance_breakdown array.
 * KNOWN = wire_exact + joined_exact + provider_estimated
 * ASSUMED = scope_estimated
 * NO-DATA buckets (unmetered, unavailable) are excluded from priced totals.
 */
export function computeKnownAssumedSplit(
  breakdown: readonly { provenance: string; spend_usd: number }[],
): KnownAssumedSplit {
  let knownUsd = 0;
  let assumedUsd = 0;
  for (const row of breakdown) {
    const prov = toCostProvenance(row.provenance);
    const bucket = PROVENANCE_BUCKET[prov];
    if (bucket === 'known') knownUsd += row.spend_usd;
    else if (bucket === 'assumed') assumedUsd += row.spend_usd;
    // 'no-data' excluded
  }
  return { knownUsd, assumedUsd, pricedTotal: knownUsd + assumedUsd };
}