// =============================================================================
// AffectedSymbolsTable (PP-08b, Proof Paths)
//
// Affected-symbols (dependents) evidence table for the Proof Paths view.
//
// Renders AffectedSymbol[] from the impact API as a real semantic <table>
// via EvidenceTable, with:
//   - Dependent display_name (monospace, identifier)
//   - Relationship type as a typed edge: glyph-block + source label from
//     relationshipToConfidenceSource (PP-04). Text + shape, NEVER color alone.
//     NO fabricated percentage — dependents have no published confidence number.
//     RelevanceForRelationship is internal; never exposed to the user. (EDGE-HON-2.)
//   - Proof-path chain via ProofPathChain (PP-07)
//   - Path length (numeric)
//
// Data correctness:
//   - Self-edges (symbol_id === anchorFqn) are dropped (EDGE-DATA-5).
//   - Only exact symbol_id + relationship duplicates are collapsed (EDGE-DATA-4).
//   - Cap-with-disclosure at AFFECTED_SYMBOLS_CAP rows; never silent (EDGE-DATA-2).
//   - Section-local "No affected dependents" empty sub-state (EDGE-UX-4).
//
// Sort: URL-owned via props (sortKey + sortOrder + onSortChange).
//   Structural edges (implements/inherits/calls/constructs) visibly outrank
//   references via PP-04 rank (mirrors server order). (EDGE-HON-4, EDGE-A11Y-3.)
//   aria-sort is wired through EvidenceTable.
//
// Memoization: derived + sorted rows are memoized by useMemo (EDGE-PERF-1).
//
// Design contract: --scope-* tokens only, sharp (radius 0), 44px targets,
//   status = text + shape + color (never color alone).
//
// Seams:
//   EvidenceTable (FE-1): real <table> semantics + sort header.
//   AffectedSymbol (PP-02): wire type from impactApi.
//   relationshipToConfidenceSource + compareByRankDesc (PP-04): tier logic.
//   ProofPathChain (PP-07): proof-path chain display.
// =============================================================================

import React, { useMemo } from 'react';
import { EvidenceTable } from '../../components/EvidenceTable';
import type { EvidenceColumn, SortOrder } from '../../components/EvidenceTable';
import type { AffectedSymbol } from '../../api/impactApi';
import ProofPathChain from '../../components/ProofPathChain';
import {
  processSymbols,
  disclosureLine,
  isAffectedSymbolSortKey,
  AFFECTED_SYMBOLS_CAP,
} from './affectedSymbolsTableHelpers';
import type { AffectedSymbolSortKey } from './affectedSymbolsTableHelpers';
import type { DerivedAffectedRow } from './affectedSymbolsTableHelpers';
import styles from './AffectedSymbolsTable.module.css';

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface AffectedSymbolsTableProps {
  /**
   * Raw server array from ImpactReport.affected_symbols.
   * Pre-sorted by RelevanceForRelationship desc (ImpactAnalyzer.cs ~38).
   * Processing: self-edges dropped, duplicates collapsed, re-sorted by sortKey.
   */
  symbols: AffectedSymbol[];

  /**
   * The anchor FQN for the current impact query.
   * Rows where symbol_id === anchorFqn are dropped (EDGE-DATA-5).
   */
  anchorFqn: string;

  /**
   * Whether there are affected tests in the same impact report.
   * When true and symbols is empty, renders "No affected dependents" sub-state
   * to distinguish "no dependents but has tests" from "no dependents and no tests".
   * (EDGE-UX-4.)
   */
  hasTests: boolean;

  // ---------------------------------------------------------------------------
  // Sort state — URL-owned by the parent (EvidenceTable controlled sort pattern).
  // This component NEVER reads/writes URL params directly.
  // ---------------------------------------------------------------------------

  /** Active sort column key. Null means rank/server order. */
  sortKey?: AffectedSymbolSortKey | null;

  /** Active sort direction. Ignored when sortKey is null. */
  sortOrder?: SortOrder | null;

  /**
   * Called when the user clicks a sortable column header.
   * The parent updates URL state; updated props flow back on next render.
   */
  onSortChange?: (key: string, order: SortOrder) => void;
}

// ---------------------------------------------------------------------------
// Column definitions
// ---------------------------------------------------------------------------

/**
 * Build column definitions for EvidenceTable.
 * Defined outside the component body so the reference is stable
 * and useMemo dependencies remain correct.
 */
function buildColumns(): EvidenceColumn<DerivedAffectedRow>[] {
  return [
    {
      key: 'name',
      header: 'Dependent',
      sortable: true,
      render: (row) => (
        <span className={styles.nameCell} title={row.original.symbol_id}>
          {row.original.display_name}
        </span>
      ),
    },
    {
      key: 'relationship',
      header: 'Relationship',
      sortable: true,
      render: (row) => (
        // Text + shape glyph-block. NEVER color alone.
        // NO fabricated percentage (EDGE-HON-2).
        // RelevanceForRelationship (rank) is internal; never shown as a %.
        <span className={styles.relCell}>
          {/* Glyph-block: non-color shape indicator (WCAG text+shape rule) */}
          <span
            className={styles.relGlyph}
            aria-hidden="true"
            title={row.label}
          >
            {row.glyph}
          </span>
          {/* Text label carries the semantic meaning (WCAG text rule) */}
          <span className={styles.relLabel}>{row.label}</span>
        </span>
      ),
    },
    {
      key: 'proof_path',
      header: 'Proof path',
      sortable: false,
      render: (row) => (
        <ProofPathChain
          proofPath={row.original.proof_path}
          symbolId={row.original.symbol_id}
        />
      ),
    },
    {
      key: 'path_length',
      header: 'Path length',
      sortable: true,
      render: (row) => (
        <span className={styles.pathLengthCell} aria-label={`${row.pathLength.toString()} hops`}>
          {row.pathLength}
        </span>
      ),
    },
  ];
}

// Stable column array (constant across all renders — no row-level state).
const COLUMNS = buildColumns();

// ---------------------------------------------------------------------------
// AffectedSymbolsTable
// ---------------------------------------------------------------------------

/**
 * AffectedSymbolsTable renders the affected dependents for a proof-paths query.
 *
 * Sort is fully controlled: the parent owns sortKey/sortOrder via URL state;
 * this component fires onSortChange but never writes to the URL or local state.
 * (Mirrors EvidenceTable's controlled sort pattern.)
 *
 * Structural edges (implements/inherits/calls/constructs) visibly outrank
 * references via PP-04 rank when no explicit sortKey is active. When the user
 * sorts by "Relationship", the same rank order is applied (desc = strongest
 * first; asc = weakest first).
 */
export function AffectedSymbolsTable({
  symbols,
  anchorFqn,
  hasTests,
  sortKey = null,
  sortOrder = null,
  onSortChange,
}: AffectedSymbolsTableProps): React.ReactElement {
  // Resolve effective sort order. Default to 'desc' for relationship sort
  // (strongest structural evidence first) and 'asc' for name/path_length.
  const effectiveSortOrder = sortOrder ?? 'asc';

  // Memoize processed rows: self-edge drop, dedup, derive, sort.
  // Re-runs only when inputs change. (EDGE-PERF-1.)
  //
  // sortKey defaults to null (from props default), so only the null check is
  // needed — the compiler already narrows undefined away via the default param.
  const processedRows = useMemo((): DerivedAffectedRow[] => {
    const key: AffectedSymbolSortKey | null =
      sortKey !== null && isAffectedSymbolSortKey(sortKey) ? sortKey : null;
    return processSymbols(symbols, anchorFqn, key, effectiveSortOrder);
  }, [symbols, anchorFqn, sortKey, effectiveSortOrder]);

  // Total count (after self-edge drop + dedup) for disclosure.
  const totalCount = processedRows.length;

  // Cap: show at most AFFECTED_SYMBOLS_CAP rows (EDGE-DATA-2).
  const visibleRows = processedRows.slice(0, AFFECTED_SYMBOLS_CAP);
  const disclosure = disclosureLine(visibleRows.length, totalCount);

  // Section-local empty sub-state (EDGE-UX-4).
  if (processedRows.length === 0) {
    return (
      <section aria-label="Affected dependents">
        <p className={styles.emptyState}>
          {hasTests
            ? 'No affected dependents found (affected tests may still exist below).'
            : 'No affected dependents found.'}
        </p>
      </section>
    );
  }

  // exactOptionalPropertyTypes: only pass optional EvidenceTable props when
  // they are defined, to avoid { onSortChange: undefined } leaking into props.
  const tableProps = {
    caption: 'Affected dependents',
    columns: COLUMNS,
    rows: visibleRows,
    getRowKey: (row: DerivedAffectedRow) => `${row.original.symbol_id}\x00${row.original.relationship}`,
    sortKey: sortKey ?? null,
    sortOrder: effectiveSortOrder,
    emptyMessage: 'No affected dependents.',
    ...(onSortChange !== undefined ? { onSortChange } : {}),
  } as const;

  return (
    <section className={styles.section} aria-label="Affected dependents">
      <EvidenceTable<DerivedAffectedRow>
        {...tableProps}
      />

      {/* Cap-with-disclosure: never silently hide rows (EDGE-DATA-2). */}
      {disclosure !== null && (
        <p className={styles.disclosure} role="note" aria-live="polite">
          {disclosure}
        </p>
      )}
    </section>
  );
}
