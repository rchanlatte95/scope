// =============================================================================
// CostProvenanceChip (FE-1)
//
// Renders a cost value + provenance chip per decision cost-ui:
//   - Every visible cost shows value + provenance chip.
//   - Null cost -> em dash (—) + "Unmetered" or "Unavailable" (per provenance).
//   - Never renders null cost as $0.00.
//   - Aggregate variant -> coverage text (e.g. "3 of 5 metered"), NOT a mixed enum.
//
// PURE / TYPED: does NOT wire to live data. Does NOT invent a number->enum mapping.
// FE-3/FE-4 wire real cost_provenance values once FE-B emits the field.
//
// Constraint: uses only --scope-color-* tokens. No raw hex. No trace-tool tokens.
// =============================================================================

import React from 'react';
import {
  PROVENANCE_LABEL,
  PROVENANCE_TIER,
  type CostProvenance,
} from '../lib/provenance';
import { friendlyModelName } from '../lib/modelName';
import { formatMoneyUsd } from '../lib/accounting';
import type { CostEstimationBasis } from '../api/traceApi';
import styles from './CostProvenanceChip.module.css';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Formats a USD cost value for the chip. Delegates to the shared {@link formatMoneyUsd} so the chip
 * uses the same tiered-precision + "<$0.01" floor honesty rules as the Cost Ledger and accounting
 * surfaces (T-2026-06-20-20). Callers null-check costUsd before reaching here (null renders an em
 * dash + provenance label, never $0.00), so this only ever formats a real number.
 */
function formatCostUsd(value: number): string {
  return formatMoneyUsd(value);
}

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

/**
 * Props for a single-value cost display (individual run/span).
 */
export interface CostProvenanceSingleProps {
  variant: 'single';
  /**
   * The cost value in USD. Null means no cost was recorded.
   * Null MUST render as em dash + provenance label, never as $0.00.
   */
  costUsd: number | null | undefined;
  /**
   * The provenance classification for this cost value.
   * When costUsd is null, determines whether to show "Unmetered" or "Unavailable".
   */
  provenance: CostProvenance;
  /**
   * Estimation basis from the rollup API (cost_estimation_basis field).
   * When present, renders a secondary detail line showing the assumed model and prices-as-of date.
   * Null/undefined -> no basis line rendered (no noise on exact or unavailable costs).
   */
  estimationBasis?: CostEstimationBasis | null;
}

/**
 * Props for an aggregate cost display (e.g. a cron agent's run history total).
 * Shows coverage text rather than a mixed provenance enum.
 */
export interface CostProvenanceAggregateProps {
  variant: 'aggregate';
  /**
   * The aggregate cost value in USD. Null means no metered runs.
   * Null renders as em dash + coverage text.
   */
  costUsd: number | null | undefined;
  /**
   * How many runs/spans contributed metered cost.
   */
  meteredCount: number;
  /**
   * Total runs/spans in the aggregate window.
   */
  totalCount: number;
}

export type CostProvenanceChipProps =
  | CostProvenanceSingleProps
  | CostProvenanceAggregateProps;

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * CostProvenanceChip renders a cost value with honest provenance.
 *
 * Single variant: cost + provenance chip (null -> em dash + label).
 * Aggregate variant: cost + coverage text (e.g. "3 of 5 metered").
 *
 * Never shows $0.00 for null cost. Chip color encodes tier (exact/estimated/none)
 * so the distinction is visible at a glance without relying on text alone.
 */
export function CostProvenanceChip(props: CostProvenanceChipProps): React.ReactElement {
  if (props.variant === 'aggregate') {
    return <AggregateChip {...props} />;
  }
  return <SingleChip {...props} />;
}

// ---------------------------------------------------------------------------
// Estimation basis helper
// ---------------------------------------------------------------------------

/**
 * Builds the transparent basis label text for a cost estimation.
 *   recovered=false -> "Scope estimate - assumed Sonnet 4.6 - prices as of 2026-06-14"
 *   recovered=true  -> "Provider estimate - Sonnet 4.6 - prices as of 2026-06-14"
 *
 * When prices_as_of is null/absent, the date portion is omitted to avoid a fabricated date.
 */
function buildBasisLine(basis: CostEstimationBasis): string {
  const model = friendlyModelName(basis.assumed_model);
  const parts: string[] = [];

  if (basis.recovered) {
    parts.push('Provider estimate');
    parts.push(model);
  } else {
    parts.push('Scope estimate');
    parts.push(`assumed ${model}`);
  }

  const pao = basis.prices_as_of;
  if (pao != null && pao !== '') {
    // Render ISO date portion only (first 10 chars: YYYY-MM-DD) for compact display.
    const dateStr = pao.length >= 10 ? pao.slice(0, 10) : pao;
    parts.push(`prices as of ${dateStr}`);
  }

  return parts.join(' - ');
}

// ---------------------------------------------------------------------------
// Single variant
// ---------------------------------------------------------------------------

function SingleChip({ costUsd, provenance, estimationBasis }: CostProvenanceSingleProps): React.ReactElement {
  const tier = PROVENANCE_TIER[provenance];
  const chipLabel = PROVENANCE_LABEL[provenance];
  const basisLine = estimationBasis != null ? buildBasisLine(estimationBasis) : null;

  if (costUsd == null) {
    // Null/undefined cost: em dash + Unmetered/Unavailable (never $0.00).
    // Loose == catches undefined too: the backend omits a null total_cost_usd
    // (WhenWritingNull), so the value arrives as undefined, not null.
    return (
      <span className={styles.wrapper}>
        <span className={styles.emDash} aria-label="No cost recorded">
          &mdash;
        </span>
        <span className={styles.chip} data-tier="none">
          {chipLabel}
        </span>
      </span>
    );
  }

  return (
    <span className={styles.costBlock}>
      <span className={styles.wrapper}>
        <span className={styles.value}>{formatCostUsd(costUsd)}</span>
        <span className={styles.chip} data-tier={tier}>
          {chipLabel}
        </span>
      </span>
      {basisLine != null && (
        <span className={styles.basisLine} data-testid="cost-basis-line">
          {basisLine}
        </span>
      )}
    </span>
  );
}

// ---------------------------------------------------------------------------
// Aggregate variant
// ---------------------------------------------------------------------------

function AggregateChip({
  costUsd,
  meteredCount,
  totalCount,
}: CostProvenanceAggregateProps): React.ReactElement {
  const coverageText =
    totalCount === 0
      ? 'No runs'
      : `${String(meteredCount)} of ${String(totalCount)} metered`;

  if (costUsd == null) {
    return (
      <span className={styles.wrapper}>
        <span className={styles.emDash} aria-label="No metered cost">
          &mdash;
        </span>
        <span className={styles.coverage}>{coverageText}</span>
      </span>
    );
  }

  return (
    <span className={styles.wrapper}>
      <span className={styles.value}>{formatCostUsd(costUsd)}</span>
      <span className={styles.coverage}>{coverageText}</span>
    </span>
  );
}
