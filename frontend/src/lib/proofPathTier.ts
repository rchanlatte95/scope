// =============================================================================
// proofPathTier.ts — Pure tier / confidence-source derivation for Proof Paths.
//
// Maps the `relationship` field from AffectedSymbol (one of the six CHECK-
// constrained values in SchemaManager.cs ~24-30: inherits, implements, calls,
// constructs, references, tests) to a human-readable confidence-source label,
// a non-color shape glyph, and a numeric rank.
//
// Maps AffectedTest.confidence (0.95 / 0.80 / 0.70 today) to a discrete
// discovery-method text tier via threshold ranges, not exact equality.
//
// Rank values MUST equal the server's RelevanceForRelationship weights
// (ImpactAnalyzer.cs ~300-309: implements=0.85, inherits=0.85, calls=0.80,
// constructs=0.70, tests=0.60, references=0.50, unknown=0.40) so that
// client-side sort order matches the server's pre-sorted order. Deviation
// would reintroduce the cross-surface ordering bug (cf. Cost Ledger drift).
//
// No React, no color, no I/O. All exports are pure functions.
// =============================================================================

// ---------------------------------------------------------------------------
// ConfidenceSource — derived from AffectedSymbol.relationship
// ---------------------------------------------------------------------------

/**
 * Confidence-source descriptor for an affected-symbol relationship edge.
 *
 * - `label` is the human-readable relationship type shown to the user.
 * - `glyph` is a non-color shape token that pairs with the label (status =
 *   text + shape, never color alone; WCAG 2.2 rule).
 * - `rank` mirrors RelevanceForRelationship (ImpactAnalyzer.cs ~300-309)
 *   exactly; higher = stronger structural evidence.
 */
export interface ConfidenceSource {
  label: string;
  glyph: string;
  rank: number;
}

/**
 * Total mapping from AffectedSymbol.relationship to a ConfidenceSource.
 *
 * Covers every value in the CHECK constraint
 * (SchemaManager.cs ~24-30: inherits, implements, calls, constructs,
 * references, tests) plus an explicit default for unrecognized strings.
 *
 * Never throws, never returns a blank label. (EDGE-HON-3.)
 * Rank order: implements = inherits > calls > constructs > tests >
 *             references > unknown. (EDGE-HON-4, ground-truth #6.)
 */
export function relationshipToConfidenceSource(rel: string): ConfidenceSource {
  // Ranks match RelevanceForRelationship (ImpactAnalyzer.cs ~300-309).
  switch (rel) {
    case 'implements':
      return { label: 'interface implementation', glyph: '⊢', rank: 0.85 };
    case 'inherits':
      return { label: 'inheritance', glyph: '↑', rank: 0.85 };
    case 'calls':
      return { label: 'direct call', glyph: '→', rank: 0.80 };
    case 'constructs':
      // OQ-2: constructs maps to "construction", ranked 0.70 (between calls
      // and references), matching the server weight (ImpactAnalyzer.cs ~305).
      return { label: 'construction', glyph: '⊕', rank: 0.70 };
    case 'tests':
      return { label: 'test relationship', glyph: '✓', rank: 0.60 };
    case 'references':
      return { label: 'reference', glyph: '~', rank: 0.50 };
    default:
      // EDGE-HON-3: unknown string -> explicit neutral default; never throws
      // or returns blank. Rank 0.40 matches the server's unknown weight.
      return { label: 'unrecognized relationship', glyph: '?', rank: 0.40 };
  }
}

// ---------------------------------------------------------------------------
// TestTier — derived from AffectedTest.confidence by threshold range
// ---------------------------------------------------------------------------

/**
 * Discrete discovery-method tier for an affected test.
 *
 * - `label` is the explicit discovery-method text shown alongside the raw
 *   confidence number (OQ-8, EDGE-DATA-6).
 * - `glyph` is a non-color shape token.
 * - `rank` is used by compareByRankDesc for strongest-first ordering.
 */
export interface TestTier {
  label: string;
  glyph: string;
  rank: number;
}

/**
 * Maps AffectedTest.confidence to a TestTier by threshold range, not
 * exact equality, so future analyzer values and boundary cases map sanely.
 * (EDGE-DATA-6.)
 *
 * Thresholds:
 *   >= 0.9  -> "direct test"           (direct `tests` edge; 0.95 today)
 *   >= 0.75 -> "covers containing type" (containing-type test; 0.80 today)
 *   >  0    -> "indirect test"          (depth-2 chain; 0.70 today)
 *   <= 0 or NaN -> "unknown confidence" (explicit neutral tier)
 *
 * Caller is expected to also display the raw confidence number alongside
 * the label so nothing is hidden (OQ-8, EDGE-DATA-6).
 */
export function testConfidenceTier(confidence: number): TestTier {
  if (!isFinite(confidence) || isNaN(confidence)) {
    return { label: 'unknown confidence', glyph: '?', rank: 0 };
  }
  if (confidence >= 0.9) {
    return { label: 'direct test', glyph: '✓✓', rank: 3 };
  }
  if (confidence >= 0.75) {
    return { label: 'covers containing type', glyph: '✓~', rank: 2 };
  }
  if (confidence > 0) {
    return { label: 'indirect test', glyph: '~', rank: 1 };
  }
  // confidence <= 0
  return { label: 'unknown confidence', glyph: '?', rank: 0 };
}

// ---------------------------------------------------------------------------
// Sort helper
// ---------------------------------------------------------------------------

/**
 * Comparator for Array.sort that orders highest rank first.
 * Works for both ConfidenceSource and TestTier (both carry `rank`).
 *
 * Usage:
 *   affectedSymbols.sort(compareByRankDesc) — mirrors server pre-sort order.
 */
export function compareByRankDesc(
  a: { rank: number },
  b: { rank: number },
): number {
  return b.rank - a.rank;
}
