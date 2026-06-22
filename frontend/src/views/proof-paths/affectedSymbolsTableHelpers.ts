// =============================================================================
// affectedSymbolsTableHelpers.ts (PP-08b, Proof Paths)
//
// Pure helpers for AffectedSymbolsTable.tsx.
// Separated from the .tsx file so it exports only non-component functions,
// satisfying the react-refresh/only-export-components ESLint rule.
//
// No React, no I/O, no color, no URL access.
// =============================================================================

import type { AffectedSymbol } from '../../api/impactApi';
import {
  relationshipToConfidenceSource,
  compareByRankDesc,
} from '../../lib/proofPathTier';

// ---------------------------------------------------------------------------
// Cap constant (Step 5 of PP-08b spec)
// ---------------------------------------------------------------------------

/**
 * Maximum number of affected-symbol rows shown before the disclosure line.
 * Server may return hundreds of dependents; cap + disclose rather than silent slice.
 * (EDGE-DATA-2.)
 */
export const AFFECTED_SYMBOLS_CAP = 50;

// ---------------------------------------------------------------------------
// Row deduplication (Step 7 of PP-08b spec)
// ---------------------------------------------------------------------------

/**
 * Returns a deduplication key for an AffectedSymbol row.
 * Only exact symbol_id + relationship duplicates are collapsed;
 * distinct relationship edges for the same symbol are kept.
 * (EDGE-DATA-4.)
 */
export function rowDedupKey(row: AffectedSymbol): string {
  return `${row.symbol_id}\x00${row.relationship}`;
}

// ---------------------------------------------------------------------------
// Self-edge filter (Step 6 of PP-08b spec)
// ---------------------------------------------------------------------------

/**
 * Returns true when the row is a self-edge (symbol_id === anchorFqn).
 * Self-edges are dropped belt-and-braces even though the server-side
 * `visited` set already prevents them in practice.
 * (EDGE-DATA-5.)
 */
export function isSelfEdge(row: AffectedSymbol, anchorFqn: string): boolean {
  return row.symbol_id === anchorFqn;
}

// ---------------------------------------------------------------------------
// Valid sort keys for URL ownership
// ---------------------------------------------------------------------------

/** Column keys that can be used as sort keys in the URL. */
export type AffectedSymbolSortKey = 'name' | 'relationship' | 'path_length';

/** Type-guard: returns true when `key` is a valid AffectedSymbolSortKey. */
export function isAffectedSymbolSortKey(key: string): key is AffectedSymbolSortKey {
  return key === 'name' || key === 'relationship' || key === 'path_length';
}

// ---------------------------------------------------------------------------
// Derived row type (memoized, Step 4 of PP-08b spec)
// ---------------------------------------------------------------------------

/**
 * AffectedSymbol row augmented with pre-derived sort keys for memoization.
 * Computing rank once per row avoids repeated switch() calls in comparators.
 * (EDGE-PERF-1.)
 */
export interface DerivedAffectedRow {
  original: AffectedSymbol;
  /** Rank from proofPathTier.relationshipToConfidenceSource (mirrors server weight). */
  rank: number;
  /** Human-readable relationship label. */
  label: string;
  /** Shape glyph (non-color shape token). */
  glyph: string;
  /** proof_path.length, pre-computed for sort. */
  pathLength: number;
}

/**
 * Derive a DerivedAffectedRow from a raw AffectedSymbol.
 * Called once per input row during memoization.
 */
export function deriveRow(row: AffectedSymbol): DerivedAffectedRow {
  const source = relationshipToConfidenceSource(row.relationship);
  return {
    original: row,
    rank: source.rank,
    label: source.label,
    glyph: source.glyph,
    pathLength: row.proof_path.length,
  };
}

// ---------------------------------------------------------------------------
// Row processing: filter self-edges, deduplicate, derive, sort
// ---------------------------------------------------------------------------

/**
 * Process raw AffectedSymbol[] from the server into a sorted, deduplicated,
 * self-edge-free list of DerivedAffectedRow.
 *
 * Order of operations:
 *   1. Drop self-edges (symbol_id === anchorFqn).
 *   2. Deduplicate exact symbol_id + relationship pairs.
 *   3. Derive rank/label/glyph for each row.
 *   4. Sort by the requested column (or by rank desc = server order).
 *
 * This function is pure (no side effects) and memoizable by the caller.
 *
 * @param symbols - Raw server array (pre-sorted by RelevanceForRelationship desc).
 * @param anchorFqn - The anchor FQN; rows where symbol_id === this are dropped.
 * @param sortKey - The active sort column (or null for rank/server order).
 * @param sortOrder - 'asc' | 'desc' (only used when sortKey is non-null).
 */
export function processSymbols(
  symbols: AffectedSymbol[],
  anchorFqn: string,
  sortKey: AffectedSymbolSortKey | null,
  sortOrder: 'asc' | 'desc',
): DerivedAffectedRow[] {
  // Step 1: Drop self-edges (EDGE-DATA-5).
  // Step 2: Deduplicate exact symbol_id + relationship pairs (EDGE-DATA-4).
  const seen = new Set<string>();
  const filtered: AffectedSymbol[] = [];
  for (const row of symbols) {
    if (isSelfEdge(row, anchorFqn)) continue;
    const key = rowDedupKey(row);
    if (seen.has(key)) continue;
    seen.add(key);
    filtered.push(row);
  }

  // Step 3: Derive rank/label/glyph once per row (EDGE-PERF-1).
  const derived = filtered.map(deriveRow);

  // Step 4: Sort.
  // Default (no explicit sortKey): sort by rank desc, mirroring server pre-sort.
  // Explicit sortKey: sort by the chosen column.
  const dir = sortOrder === 'asc' ? 1 : -1;

  derived.sort((a, b) => {
    if (sortKey === 'name') {
      const cmp = a.original.display_name.localeCompare(b.original.display_name);
      return cmp * dir;
    }
    if (sortKey === 'relationship') {
      // Sort by rank desc by default (structural outranks references);
      // when asc is requested, reverse.
      return compareByRankDesc({ rank: a.rank }, { rank: b.rank }) * dir;
    }
    if (sortKey === 'path_length') {
      const cmp = a.pathLength - b.pathLength;
      return cmp * dir;
    }
    // Default: rank desc (mirrors server order, structural above references).
    return compareByRankDesc({ rank: a.rank }, { rank: b.rank });
  });

  return derived;
}

// ---------------------------------------------------------------------------
// Disclosure line text (Step 5 of PP-08b spec)
// ---------------------------------------------------------------------------

/**
 * Returns the disclosure line string when visible count < total count.
 * Returns null when all symbols are shown (no cap applied).
 * (EDGE-DATA-2, never silent.)
 *
 * Example: "Showing 50 of 120 affected symbols"
 */
export function disclosureLine(visibleCount: number, totalCount: number): string | null {
  if (visibleCount >= totalCount) return null;
  return `Showing ${visibleCount.toString()} of ${totalCount.toString()} affected symbols`;
}
