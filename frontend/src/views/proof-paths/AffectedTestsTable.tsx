// =============================================================================
// AffectedTestsTable (PP-08a, Proof Paths)
//
// Renders the affected-tests evidence table from an ImpactReport.
// This table LEADS (strongest evidence: "what covers this change?").
//
// Design mandates:
//   - Real semantic <table> via EvidenceTable (G6-01, caption + header scope).
//   - Tier shown as TEXT + GLYPH + RAW NUMBER; never color alone (EDGE-A11Y-1,
//     EDGE-DATA-6, OQ-8). Uses testConfidenceTier (PP-04).
//   - Strongest-first ordering: sort mirrors server confidence desc via
//     compareByRankDesc. Sort is URL-owned: onSortChange -> navigate()
//     (not hidden state). aria-sort flips per column (EDGE-A11Y-3).
//   - Cap-with-disclosure: first N rows + explicit "Showing N of M" line +
//     "show all" expander. Never silently drop rows (EDGE-DATA-2).
//   - Server indirect-test cap surfaced honestly (EDGE-DATA-2).
//   - Section-local empty sub-state when affected_tests empty (EDGE-UX-4).
//   - Stable row key = symbol_id (FQN); memoized derivation (EDGE-PERF-1).
//   - No fabricated values: only real confidence number + derived tier
//     (EDGE-HON-2).
//
// Seams:
//   - EvidenceTable: frontend/src/components/EvidenceTable.tsx
//   - AffectedTest: frontend/src/api/impactApi.ts (PP-02)
//   - testConfidenceTier, compareByRankDesc: frontend/src/lib/proofPathTier.ts (PP-04)
//   - ProofPathChain: frontend/src/components/ProofPathChain.tsx (PP-07)
//
// Design contract: --scope-* tokens only; sharp radius 0; 44px targets;
// status text+shape+color never color-only.
// =============================================================================

import React, { useMemo, useState } from 'react';
import { EvidenceTable } from '../../components/EvidenceTable';
import type { EvidenceColumn, SortOrder } from '../../components/EvidenceTable';
import type { AffectedTest } from '../../api/impactApi';
import { testConfidenceTier, compareByRankDesc } from '../../lib/proofPathTier';
import ProofPathChain from '../../components/ProofPathChain';
import styles from './AffectedTestsTable.module.css';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Initial number of rows shown before "show all" expander. */
const DEFAULT_ROW_CAP = 100;

/**
 * Server caps indirect test discovery to 25 entries (ImpactAnalyzer.cs ~210).
 * Surfaced honestly in the UI so users understand the data completeness.
 */
const INDIRECT_TEST_DISCOVERY_CAP = 25;

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface AffectedTestsTableProps {
  /** Affected tests from the impact report. May be empty. */
  affectedTests: AffectedTest[];

  /**
   * Whether the overall impact report has any content beyond tests (e.g.
   * affected_symbols). Used to choose between "global empty" and "section
   * empty" (EDGE-UX-4).
   */
  hasOtherContent: boolean;

  /**
   * Current sort column key (URL-owned, passed from parent).
   * Null/undefined means no explicit sort (server order = confidence desc).
   */
  sortKey?: string | null;

  /**
   * Current sort direction (URL-owned, passed from parent).
   */
  sortOrder?: SortOrder;

  /**
   * Called when a sortable column header is clicked.
   * Parent updates URL state; this component never writes state or URL.
   */
  onSortChange?: (key: string, order: SortOrder) => void;
}

// ---------------------------------------------------------------------------
// Derived row type
// ---------------------------------------------------------------------------

/** AffectedTest extended with derived tier data for memoized rendering. */
interface DerivedTestRow {
  test: AffectedTest;
  tierLabel: string;
  tierGlyph: string;
  tierRank: number;
}

// ---------------------------------------------------------------------------
// Pure sort helper for derived rows
// ---------------------------------------------------------------------------

/**
 * Returns a sorted copy of derivedRows according to sortKey + sortOrder.
 * Falls back to confidence desc (tier rank desc) when sortKey is null or
 * unrecognized. This mirrors the server's default pre-sort (EDGE-A11Y-3).
 */
function sortRows(
  rows: DerivedTestRow[],
  sortKey: string | null | undefined,
  sortOrder: SortOrder | undefined,
): DerivedTestRow[] {
  const copy = [...rows];
  const direction = sortOrder === 'asc' ? 1 : -1;

  switch (sortKey) {
    case 'name':
      copy.sort((a, b) => direction * a.test.display_name.localeCompare(b.test.display_name));
      break;
    case 'confidence':
      // compareByRankDesc is highest-first; flip direction for explicit asc
      copy.sort((a, b) => direction * compareByRankDesc(
        { rank: a.tierRank },
        { rank: b.tierRank },
      ));
      break;
    case 'path_length':
      copy.sort((a, b) => direction * (a.test.proof_path.length - b.test.proof_path.length));
      break;
    case 'symbol_id':
      copy.sort((a, b) => direction * a.test.symbol_id.localeCompare(b.test.symbol_id));
      break;
    case null:
    case undefined:
    default:
      // No sort key or unrecognized: mirror server confidence desc
      copy.sort((a, b) => compareByRankDesc({ rank: a.tierRank }, { rank: b.tierRank }));
      break;
  }

  return copy;
}

// ---------------------------------------------------------------------------
// TierCell — tier text + glyph + raw number (never color alone)
// ---------------------------------------------------------------------------

interface TierCellProps {
  tierLabel: string;
  tierGlyph: string;
  confidence: number;
}

function TierCell({ tierLabel, tierGlyph, confidence }: TierCellProps): React.ReactElement {
  // Format confidence as a percentage with one decimal for readability.
  // The raw number is always shown so nothing is hidden (OQ-8, EDGE-DATA-6).
  const rawPct = `${(confidence * 100).toFixed(0)}%`;

  return (
    <span className={styles.tierCell}>
      {/* Glyph: non-color shape token (status must have text + shape, never color alone) */}
      <span className={styles.tierGlyph} aria-hidden="true">
        {tierGlyph}
      </span>
      {/* Tier label: discovery method text */}
      <span className={styles.tierLabel}>{tierLabel}</span>
      {/* Raw confidence number: always shown alongside the label (OQ-8) */}
      <span className={styles.confidenceRaw} title={`Raw confidence: ${String(confidence)}`}>
        {rawPct}
      </span>
    </span>
  );
}

// ---------------------------------------------------------------------------
// AffectedTestsTable — public component
// ---------------------------------------------------------------------------

/**
 * AffectedTestsTable renders the affected-tests evidence table for a Proof
 * Paths impact report. This table leads (strongest evidence).
 *
 * Sort is fully controlled: sortKey, sortOrder, and onSortChange are passed
 * from the parent which owns URL state. This component never writes state or URL.
 *
 * Cap-with-disclosure: renders first DEFAULT_ROW_CAP rows, then shows an
 * explicit "Showing N of M" line + "show all" expander. Never silently drops
 * rows (EDGE-DATA-2).
 *
 * Section-local empty sub-state: when affectedTests is empty, renders a
 * "No affected tests found" message inside the section rather than relying on
 * the global empty state (EDGE-UX-4).
 */
export function AffectedTestsTable({
  affectedTests,
  hasOtherContent,
  sortKey,
  sortOrder,
  onSortChange,
}: AffectedTestsTableProps): React.ReactElement {
  // ------------------------------------------------------------------
  // Disclosure state: show all or capped
  // ------------------------------------------------------------------
  const [showAll, setShowAll] = useState(false);

  // ------------------------------------------------------------------
  // Derive tier data (memoized keyed on data + sort params) (EDGE-PERF-1)
  // ------------------------------------------------------------------
  const derivedRows = useMemo<DerivedTestRow[]>(() => {
    const rows = affectedTests.map((test) => {
      const tier = testConfidenceTier(test.confidence);
      return {
        test,
        tierLabel: tier.label,
        tierGlyph: tier.glyph,
        tierRank: tier.rank,
      };
    });
    return sortRows(rows, sortKey, sortOrder);
  }, [affectedTests, sortKey, sortOrder]);

  const totalCount = derivedRows.length;
  const cappedRows = showAll ? derivedRows : derivedRows.slice(0, DEFAULT_ROW_CAP);
  const isCapped = !showAll && totalCount > DEFAULT_ROW_CAP;

  // Whether the server may have capped indirect tests. An indirect-test row
  // has a tier with rank === 1 (confidence in (0, 0.75)). If the total
  // indirect count hits the cap the server would have truncated; surface this.
  const indirectCount = derivedRows.filter((r) => r.tierRank === 1).length;
  const indirectMayCapped = indirectCount >= INDIRECT_TEST_DISCOVERY_CAP;

  // ------------------------------------------------------------------
  // Column definitions
  // ------------------------------------------------------------------
  const columns = useMemo<EvidenceColumn<DerivedTestRow>[]>(() => [
    {
      key: 'name',
      header: 'Test',
      sortable: true,
      render: (row) => (
        <span className={styles.displayName}>{row.test.display_name}</span>
      ),
    },
    {
      key: 'confidence',
      header: 'Confidence',
      sortable: true,
      render: (row) => (
        <TierCell
          tierLabel={row.tierLabel}
          tierGlyph={row.tierGlyph}
          confidence={row.test.confidence}
        />
      ),
    },
    {
      key: 'proof_path',
      header: 'Proof path',
      sortable: false,
      render: (row) => (
        <ProofPathChain
          proofPath={row.test.proof_path}
          symbolId={row.test.symbol_id}
        />
      ),
    },
    {
      key: 'path_length',
      header: 'Length',
      sortable: true,
      render: (row) => (
        <span className={styles.pathLength}>{row.test.proof_path.length}</span>
      ),
    },
    {
      key: 'symbol_id',
      header: 'ID',
      sortable: true,
      render: (row) => (
        <span className={styles.symbolId} title={row.test.symbol_id}>
          {row.test.symbol_id}
        </span>
      ),
    },
  ], []);

  // ------------------------------------------------------------------
  // Section-local empty sub-state (EDGE-UX-4)
  // ------------------------------------------------------------------
  if (totalCount === 0) {
    return (
      <section className={styles.section} aria-labelledby="affected-tests-heading">
        <header className={styles.sectionHeader}>
          <h2 id="affected-tests-heading" className={styles.sectionTitle}>
            Affected tests
          </h2>
        </header>
        <p className={styles.emptyMessage}>
          {hasOtherContent
            ? 'No affected tests found for this symbol. Dependent symbols may exist — see below.'
            : 'No affected tests found for this symbol.'}
        </p>
      </section>
    );
  }

  // ------------------------------------------------------------------
  // Populated view
  // ------------------------------------------------------------------
  return (
    <section className={styles.section} aria-labelledby="affected-tests-heading">
      <header className={styles.sectionHeader}>
        <h2 id="affected-tests-heading" className={styles.sectionTitle}>
          Affected tests
        </h2>
        <p className={styles.sectionNote}>
          Tests that cover this change, ordered by discovery confidence.
        </p>
      </header>

      {/* Indirect-test cap disclosure (EDGE-DATA-2): surfaced when the server
          may have capped indirect-test discovery at 25. */}
      {indirectMayCapped && (
        <p className={styles.capNotice} role="note">
          Indirect test discovery limited to {INDIRECT_TEST_DISCOVERY_CAP}. Some
          depth-2 test relationships may not be shown.
        </p>
      )}

      <EvidenceTable<DerivedTestRow>
        caption={`Affected tests (${String(totalCount)})`}
        columns={columns}
        rows={cappedRows}
        getRowKey={(row) => row.test.symbol_id}
        {...(sortKey !== undefined ? { sortKey } : {})}
        {...(sortOrder !== undefined ? { sortOrder } : {})}
        {...(onSortChange !== undefined ? { onSortChange } : {})}
        emptyMessage="No affected tests found."
      />

      {/* Disclosure line: always shows count; never silently drops rows (EDGE-DATA-2) */}
      <div className={styles.disclosureBar} role="status" aria-live="polite">
        <span className={styles.disclosureCount}>
          {isCapped
            ? `Showing ${String(cappedRows.length)} of ${String(totalCount)} affected tests`
            : totalCount === 1
              ? '1 affected test'
              : `${String(totalCount)} affected tests`}
        </span>

        {isCapped && (
          <button
            type="button"
            className={styles.showAllBtn}
            onClick={() => { setShowAll(true); }}
            aria-label={`Show all ${String(totalCount)} affected tests`}
          >
            Show all {totalCount}
          </button>
        )}
      </div>
    </section>
  );
}
