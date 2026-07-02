import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  fetchTraceRollup,
  fetchTraceTopActions,
  type RollupRow,
  type TopActionRow,
  type CostEstimationBasis,
  type CostBreakdown,
  type TokenAggregate,
  type ProvenanceBreakdownRow,
} from '../api/traceApi';
import { queryKeys, STALE_COMMANDS_TRACES_MS } from '../api/queryClient';
import { taxonomyFor } from '../lib/taxonomy';
import { formatAbsoluteDate } from '../lib/time';
import {
  formatTokens,
  formatCost,
  formatMs,
  pricesAsOfLabel,
  coverageCaption,
  isLowerBound,
  checkCoherence,
  formatBreakdownLine,
  formatTokenLine,
  hasTokenBreakdown,
  sumTokenComponents,
} from '../lib/accounting';
import { friendlyModelName } from '../lib/modelName';
import { readPrefs } from '../lib/prefs';
import SourceBadge from './SourceBadge';
import { CostProvenanceChip } from './CostProvenanceChip';
import { CostBillingClassBadge } from './CostBillingClassBadge';
import { computeKnownAssumedSplit, toCostProvenance } from '../lib/provenance';
import CopyChip from './CopyChip';
import Tooltip from './Tooltip';
import styles from './AccountingPanel.module.css';

// ---------------------------------------------------------------------------
// Two-bucket known/assumed breakdown note (T-2026-06-16-06)
// ---------------------------------------------------------------------------

/** Two-bucket known/assumed breakdown note showing model-known vs model-assumed spend.
 *
 * @param tableCell - When true, applies the table-cell variant: allows buckets to wrap
 *   and keeps the note contained within a narrow <td>. Use this in the tfoot totals row.
 *   The default (false) keeps the tight single-row nowrap form for the header summary card. */
function KnownAssumedNote({ breakdown, tableCell = false }: { breakdown: ProvenanceBreakdownRow[]; tableCell?: boolean }) {
  const { knownUsd, assumedUsd, pricedTotal } = computeKnownAssumedSplit(breakdown);
  if (pricedTotal === 0) return null;
  const fmt = (v: number) =>
    `$${v.toLocaleString('en-US', { minimumFractionDigits: 6, maximumFractionDigits: 6 })}`;
  const assumedTipId = `assumed-info-${pricedTotal.toFixed(9).replace('.', '-')}`;
  const assumedProseContent = assumedUsd > 0
    ? (
      <span className={styles.assumedInfoTooltip}>
        {fmt(assumedUsd)} of spend was priced with an assumed model rate because the model was not
        captured; {fmt(knownUsd)} is priced from the identified model.
      </span>
    )
    : null;
  const noteClass = tableCell
    ? [styles.knownAssumedNote, styles.knownAssumedNoteTableCell].filter(Boolean).join(' ')
    : styles.knownAssumedNote;
  return (
    <span className={noteClass}>
      {knownUsd > 0 && <span className={styles.knownBucket}>known {fmt(knownUsd)}</span>}
      {assumedUsd > 0 && (
        <span className={styles.assumedBucket}>
          assumed {fmt(assumedUsd)}
          {assumedProseContent != null && (
            <Tooltip content={assumedProseContent} id={assumedTipId}>
              <button
                type="button"
                className={styles.assumedInfoNote}
                aria-label="Why is part of the cost assumed?"
              >
                (i)
              </button>
            </Tooltip>
          )}
        </span>
      )}
    </span>
  );
}

// ---------------------------------------------------------------------------
// Cost breakdown tooltip (T-2026-06-15-11b)
// ---------------------------------------------------------------------------

/**
 * The rendered content of a cost breakdown tooltip bubble.
 * Each component line shows: "N kind x $rate / 1M = $cost"
 * The pinned/live indicator uses text + glyph (never color alone): WCAG 1.4.1.
 */
function CostBreakdownTooltipContent({ bd }: { bd: CostBreakdown }) {
  // Unicode glyphs: lock for pinned (immutable snapshot), refresh for live estimate.
  const pinnedGlyph = bd.pinned ? '\u{1F512}' : '↻'; // lock or circular arrow
  const pinnedLabel = bd.pinned ? 'pinned at capture' : 'live estimate at current rates';
  return (
    <span className={styles.breakdownContent}>
      <span className={styles.breakdownModel}>
        {friendlyModelName(bd.model_id)}
        {' '}
        <span aria-hidden="true">{pinnedGlyph}</span>
        {' '}
        {pinnedLabel}
      </span>
      <span className={styles.breakdownDate}>prices as of {bd.prices_as_of}</span>
      {bd.components.map((c) => (
        <span key={c.kind} className={styles.breakdownLine}>
          {formatBreakdownLine(c)}
        </span>
      ))}
      <span className={styles.breakdownTotal}>
        Total: ${bd.total_usd.toLocaleString('en-US', { minimumFractionDigits: 6, maximumFractionDigits: 6 })}
      </span>
    </span>
  );
}

let tooltipIdCounter = 0;

/**
 * Wraps a cost value with a Tooltip showing the breakdown when breakdown is present.
 * When breakdown is null/absent, renders children as-is with no tooltip wrapper.
 *
 * The trigger is a <button> styled inline so it is keyboard-focusable (in the tab
 * order) without disrupting the table layout. This satisfies the a11y requirement:
 * a bare <span> is not tabbable.
 */
function CostWithTooltip({
  formattedCost,
  breakdown,
  tooltipId,
}: {
  formattedCost: string;
  breakdown: CostBreakdown | null | undefined;
  tooltipId: string;
}) {
  if (breakdown == null) {
    return <span>{formattedCost}</span>;
  }
  return (
    <Tooltip content={<CostBreakdownTooltipContent bd={breakdown} />} id={tooltipId}>
      <button
        type="button"
        className={styles.costTrigger}
        aria-label={`Cost ${formattedCost} - activate to see breakdown`}
      >
        {formattedCost}
      </button>
    </Tooltip>
  );
}

// Stable id generator for tooltip association (idempotent across re-renders via useMemo below).
function nextTooltipId(): string {
  tooltipIdCounter += 1;
  return `cost-breakdown-tip-${String(tooltipIdCounter)}`;
}

function nextTokenTipId(): string {
  tooltipIdCounter += 1;
  return `token-breakdown-tip-${String(tooltipIdCounter)}`;
}

// ---------------------------------------------------------------------------
// Token breakdown tooltip (T-2026-06-15-12)
// ---------------------------------------------------------------------------

/**
 * The rendered content of a token breakdown tooltip bubble.
 * Shows input / output / cache_read / cache_write counts and their source.
 * Status is communicated by text labels, not color alone (WCAG 1.4.1).
 * No em dash in any copy (house rule).
 *
 * Reconciling line (ST-01): when total > (input + output) and cache fields are
 * absent (exact-path rollup), a "cache (included in total)" line is rendered so
 * the visible sum equals Total with no unexplained delta.
 */
function TokenBreakdownTooltipContent({ tokens }: { tokens: TokenAggregate }) {
  const lines: { kind: string; count: number }[] = [];
  if (tokens.input != null)         lines.push({ kind: 'input',       count: tokens.input });
  if (tokens.output != null)        lines.push({ kind: 'output',      count: tokens.output });
  if (tokens.cache_read != null)    lines.push({ kind: 'cache_read',  count: tokens.cache_read });
  if (tokens.cache_creation != null) lines.push({ kind: 'cache_write', count: tokens.cache_creation });

  const displayedTotal = tokens.total ?? sumTokenComponents(tokens);

  // Reconciling line: when cache fields are absent but total > sum(visible lines), show the
  // delta as "cache (included in total)" so there is no unexplained gap. This handles the
  // exact-path rollup where cache_read/cache_creation are null while the total is cache-inclusive.
  const cacheFieldsAbsent = tokens.cache_read == null && tokens.cache_creation == null;
  const visibleSum = lines.reduce((acc, l) => acc + l.count, 0);
  const cacheDelta = displayedTotal - visibleSum;
  const showReconcilingLine = cacheFieldsAbsent && cacheDelta > 0;

  return (
    <span className={styles.breakdownContent}>
      <span className={styles.breakdownModel}>Token breakdown ({tokens.source})</span>
      {lines.map((l) => (
        <span key={l.kind} className={styles.breakdownLine}>
          {formatTokenLine(l.kind, l.count)}
        </span>
      ))}
      {showReconcilingLine && (
        <span className={styles.breakdownLine}>
          {cacheDelta.toLocaleString('en-US')} cache tokens (included in total)
        </span>
      )}
      <span className={styles.breakdownTotal}>
        Total: {displayedTotal.toLocaleString('en-US')} tokens
      </span>
    </span>
  );
}

/**
 * Wraps a token value with a Tooltip showing the breakdown when components are available.
 * When no split is available (all components null), renders children as-is with no tooltip.
 *
 * The trigger is a <button> styled inline so it is keyboard-focusable (in the tab
 * order) without disrupting the table layout. This satisfies the a11y requirement:
 * a bare <span> is not tabbable.
 */
function TokensWithTooltip({
  formattedTokens,
  tokens,
  tooltipId,
}: {
  formattedTokens: string;
  tokens: TokenAggregate | null | undefined;
  tooltipId: string;
}) {
  if (tokens == null || !hasTokenBreakdown(tokens)) {
    return <span>{formattedTokens}</span>;
  }
  return (
    <Tooltip content={<TokenBreakdownTooltipContent tokens={tokens} />} id={tooltipId}>
      <button
        type="button"
        className={styles.tokenTrigger}
        aria-label={`Tokens ${formattedTokens} - activate to see breakdown`}
      >
        {formattedTokens}
      </button>
    </Tooltip>
  );
}

// ---------------------------------------------------------------------------
// Estimation basis helper (T-2026-06-15-02)
// ---------------------------------------------------------------------------

/**
 * Builds the transparent basis detail text for a cost estimation.
 *
 * When `source` is present (T-2026-06-15-04 3-tier contract), it takes precedence:
 *   source="wire"     -> "wire-measured - <model> - prices as of <date>"
 *   source="recorded" -> "from requested model - <model> - prices as of <date>"
 *   source="assumed"  -> "assumed default - assumed <model> - prices as of <date>"
 *
 * Fallback (older payloads where `source` is absent): uses `recovered` bool:
 *   recovered=false -> "Scope estimate - assumed <model> - prices as of <date>"
 *   recovered=true  -> "Provider estimate - <model> - prices as of <date>"
 *
 * When prices_as_of is null/absent, the date portion is omitted.
 * Returns null when basis is null/undefined (callers can skip rendering).
 */
function buildBasisDetail(basis: CostEstimationBasis | null | undefined): string | null {
  if (basis == null) return null;
  const model = friendlyModelName(basis.assumed_model);
  const parts: string[] = [];

  if (basis.source != null) {
    // 3-tier source enum (T-2026-06-15-04): honest per-tier label.
    switch (basis.source) {
      case 'wire':
        parts.push('wire-measured');
        parts.push(model);
        break;
      case 'recorded':
        parts.push('from requested model');
        parts.push(model);
        break;
      case 'assumed':
        parts.push('assumed default');
        parts.push(`assumed ${model}`);
        break;
      default:
        // Unknown future value: fall through to recovered-based fallback.
        break;
    }
  }

  // Recovered-based fallback: used when source is absent (older payloads) or
  // if the switch above produced no parts (unknown future source value).
  if (parts.length === 0) {
    if (basis.recovered) {
      parts.push('Provider estimate');
      parts.push(model);
    } else {
      parts.push('Scope estimate');
      parts.push(`assumed ${model}`);
    }
  }

  const pao = basis.prices_as_of;
  if (pao != null && pao !== '') {
    const dateStr = pao.length >= 10 ? pao.slice(0, 10) : pao;
    parts.push(`prices as of ${dateStr}`);
  }

  return parts.join(' - ');
}

// =============================================================================
// AccountingPanel (trace-ux-v2 ST-15; owner feedback items 6 / 6a / 6b / 10).
// FE-4a migration: all colors via --scope-color-* tokens; no raw hex.
//
// Consumes /api/trace/rollup and /api/trace/top-actions and renders the accounting
// half of the trace dashboard:
//  - 6    header totals (tokens + estimated cost) with the "prices as of" stamp from
//         Money.pricing_version. When pricing_version is null, cost is labeled
//         "unavailable" -- NEVER a fabricated number.
//  - 6a/b a per-task table of by_task rows (tokens AND time per task) plus the
//         unattributed remainder, with the coherence made VISIBLE.
//  - 6    a source badge (exact | wire | estimate | unknown) on every token and cost
//         value, so every number is honestly labeled.
//  - 10   top actions by count AND by time, side by side. Tool colors use the
//         sanctioned trace-tool context via inline style on a card-surface background.
//
// Time/date rendering honors the UTC/EST zone toggle (item 9) via lib/time.
// =============================================================================

interface AccountingPanelProps {
  sessionKey: string;
  /** Resolved IANA timezone string (e.g. 'UTC', 'America/Chicago'). */
  zone: string;
}

export default function AccountingPanel({ sessionKey, zone }: AccountingPanelProps) {
  // Read the assumed cost model pref on every render so that a change in SettingsView
  // (which writes to localStorage and triggers a re-render via parent state) causes a
  // re-fetch with the new assumedModel query param. assumedModel is part of the rollup
  // query key (queryKeys.traceRollup) so a changed value is a distinct cache entry
  // (fresh fetch) while an unchanged value serves from cache under the shared
  // TanStack Query cache governance (T-2026-06-16-07).
  const assumedModel = readPrefs().assumedCostModel;

  // Stable tooltip ids for the header totals and the tfoot row (one per mounted panel).
  const totalsCostTipId = useMemo(() => nextTooltipId(), []);
  const totalsTokenTipId = useMemo(() => nextTokenTipId(), []);
  const totalsFooterTokenTipId = useMemo(() => nextTokenTipId(), []);

  // R2 (T-2026-06-16-07): rollup + top-actions via useQuery under STALE_COMMANDS_TRACES_MS.
  // Rapid Trace<->Costs tab toggling remounts this component (TraceView conditionally
  // renders it), but the shared queryClient cache means a remount within staleTime
  // serves cached data with no network refetch.
  const rollupQuery = useQuery({
    queryKey: queryKeys.traceRollup(sessionKey, assumedModel),
    queryFn: () => fetchTraceRollup(sessionKey, assumedModel),
    staleTime: STALE_COMMANDS_TRACES_MS,
  });
  const topActionsQuery = useQuery({
    queryKey: queryKeys.traceTopActions(sessionKey),
    queryFn: () => fetchTraceTopActions(sessionKey, 20),
    staleTime: STALE_COMMANDS_TRACES_MS,
  });

  if (rollupQuery.isLoading || topActionsQuery.isLoading) {
    return <p className={styles.notice}>Loading accounting…</p>;
  }
  if (rollupQuery.isError || topActionsQuery.isError) {
    const err = rollupQuery.error ?? topActionsQuery.error;
    const message = err instanceof Error ? err.message : String(err);
    return <p className={styles.error}>Failed to load accounting: {message}</p>;
  }

  // At this point isLoading and isError are both false for both queries, so data is defined.
  const rollup = rollupQuery.data?.body ?? null;
  const top = topActionsQuery.data?.body ?? null;
  if (rollup === null || top === null) return null;

  const totals = rollup.totals;
  const stamp = pricesAsOfLabel(totals.cost.pricing_version, (ts) => formatAbsoluteDate(ts, zone));
  const tokenCaption = coverageCaption(totals.tokens.coverage, 'tasks measured');
  const tokenLowerBound = isLowerBound(totals.tokens);
  const coherence = checkCoherence(totals, rollup.by_task, rollup.unattributed);

  return (
    <div className={styles.panel}>
      {/* ---- header totals (item 6) ---- */}
      <div className={styles.totals}>
        <div className={styles.totalCard}>
          <span className={styles.totalLabel}>Tokens</span>
          <span className={styles.totalValue}>
            {tokenLowerBound && <span className={styles.atLeast}>at least </span>}
            <TokensWithTooltip
              formattedTokens={formatTokens(totals.tokens.total)}
              tokens={totals.tokens}
              tooltipId={totalsTokenTipId}
            />
            <SourceBadge source={totals.tokens.source} />
          </span>
          {tokenCaption != null && tokenCaption !== '' && <span className={styles.totalSub}>{tokenCaption}</span>}
        </div>

        <div className={styles.totalCard}>
          <span className={styles.totalLabel}>Estimated cost</span>
          <span className={styles.totalValue}>
            <CostWithTooltip
              formattedCost={formatCost(totals.cost)}
              breakdown={totals.cost.breakdown}
              tooltipId={totalsCostTipId}
            />
            {totals.cost_provenance_breakdown != null && totals.cost_provenance_breakdown.length > 0
              ? <KnownAssumedNote breakdown={totals.cost_provenance_breakdown} />
              : <SourceBadge source={totals.cost.source} />
            }
            <CostBillingClassBadge billingClass={totals.cost_billing_class} />
          </span>
          <span className={styles.totalSub}>
            {stamp ?? 'no pricing configured - cost unavailable'}
          </span>
          {buildBasisDetail(totals.cost_estimation_basis) != null && (
            <span className={styles.basisDetail} data-testid="cost-basis-detail">
              {buildBasisDetail(totals.cost_estimation_basis)}
            </span>
          )}
        </div>

        <div className={styles.totalCard}>
          <span className={styles.totalLabel}>Busy time (Σ per task)</span>
          <span className={styles.totalValue}>{formatMs(totals.busy_ms_sum)}</span>
          <span className={styles.totalSub}>
            wall {formatMs(totals.wall_ms)} · {totals.time_coverage.measured} measured ·{' '}
            {totals.time_coverage.zero_duration} zero-dur
          </span>
        </div>

        <div className={styles.totalCard}>
          <span className={styles.totalLabel}>Activity</span>
          <span className={styles.totalValue}>{totals.request_count.toLocaleString('en-US')} req</span>
          <span className={styles.totalSub}>{totals.event_count.toLocaleString('en-US')} events</span>
        </div>
      </div>

      {/* ---- coherence banner: per-task sums + unattributed == totals, VISIBLE (item 6a/6b) ---- */}
      <div className={`${styles.coherence ?? ''} ${coherence.balanced ? (styles.balanced ?? '') : (styles.mismatch ?? '')}`}>
        {coherence.balanced ? (
          <>
            <strong>balanced</strong> · Σ(by_task) + unattributed = totals · tokens{' '}
            {formatTokens(coherence.sumTokens)} = {formatTokens(coherence.totalTokens)} · busy{' '}
            {formatMs(coherence.sumBusyMs)} = {formatMs(coherence.totalBusyMs)}
          </>
        ) : (
          <>
            <strong>MISMATCH</strong> · the partition does not sum to the header totals · tokens{' '}
            {formatTokens(coherence.sumTokens)} vs {formatTokens(coherence.totalTokens)} · busy{' '}
            {formatMs(coherence.sumBusyMs)} vs {formatMs(coherence.totalBusyMs)}
          </>
        )}
      </div>

      {/* ---- per-task accounting (item 6a/6b) ---- */}
      <h3 className={styles.heading}>Per-task accounting</h3>
      <table className={styles.table}>
        <thead>
          <tr>
            <th>Task</th>
            <th className={styles.num}>Tokens</th>
            <th>Token src</th>
            <th className={styles.num}>Cost</th>
            <th>Cost src</th>
            <th className={styles.num}>Busy</th>
            <th>Time src</th>
            <th className={styles.num}>Req</th>
            <th className={styles.num}>Events</th>
          </tr>
        </thead>
        <tbody>
          {rollup.by_task.map((row) => (
            <TaskRow key={row.subject.id || row.subject.display_id} row={row} />
          ))}
          <TaskRow row={rollup.unattributed} unattributed />
        </tbody>
        <tfoot>
          <tr className={styles.totalRow}>
            <td>Totals</td>
            <td className={styles.num}>
              <TokensWithTooltip
                formattedTokens={formatTokens(totals.tokens.total)}
                tokens={totals.tokens}
                tooltipId={totalsFooterTokenTipId}
              />
            </td>
            <td>
              <SourceBadge source={totals.tokens.source} />
            </td>
            <td className={styles.num}>{formatCost(totals.cost)}</td>
            <td>
              {totals.cost_provenance_breakdown != null && totals.cost_provenance_breakdown.length > 0
                ? <KnownAssumedNote breakdown={totals.cost_provenance_breakdown} tableCell />
                : <SourceBadge source={totals.cost.source} />
              }
            </td>
            <td className={styles.num}>{formatMs(totals.busy_ms_sum)}</td>
            <td />
            <td className={styles.num}>{totals.request_count.toLocaleString('en-US')}</td>
            <td className={styles.num}>{totals.event_count.toLocaleString('en-US')}</td>
          </tr>
        </tfoot>
      </table>

      {/* ---- top actions by count AND by time (item 10) ---- */}
      <h3 className={styles.heading}>Top actions</h3>
      <div className={styles.topGrid}>
        <TopList title="By count" rows={top.by_count} metric="count" />
        <TopList title="By time" rows={top.by_time} metric="time" />
      </div>
    </div>
  );
}

function TaskRow({ row, unattributed = false }: { row: RollupRow; unattributed?: boolean }) {
  const tokenLb = isLowerBound(row.tokens);
  const name = unattributed ? 'unattributed' : row.subject.alias ?? row.subject.display_id;
  // Stable tooltip ids for this task's cost and token cells (one per row mount).
  const taskCostTipId = useMemo(() => nextTooltipId(), []);
  const taskTokenTipId = useMemo(() => nextTokenTipId(), []);
  return (
    <tr className={unattributed ? styles.unattributedRow : undefined}>
      <td>
        <span className={styles.taskName}>{name}</span>
        {!unattributed && <CopyChip value={row.subject.display_id} kind="id" label="task id" />}
      </td>
      <td className={styles.num}>
        {tokenLb && <span className={styles.atLeast}>≥ </span>}
        <TokensWithTooltip
          formattedTokens={formatTokens(row.tokens.total)}
          tokens={row.tokens}
          tooltipId={taskTokenTipId}
        />
      </td>
      <td>
        <SourceBadge source={row.tokens.source} />
      </td>
      <td className={styles.num}>
        <CostWithTooltip
          formattedCost={formatCost(row.cost)}
          breakdown={row.cost.breakdown}
          tooltipId={taskCostTipId}
        />
      </td>
      <td>
        {row.cost.cost_provenance != null
          ? <CostProvenanceChip variant="single" costUsd={row.cost.value_usd ?? null} provenance={toCostProvenance(row.cost.cost_provenance)} />
          : <SourceBadge source={row.cost.source} />
        }
      </td>
      <td className={styles.num}>{formatMs(row.busy_ms)}</td>
      <td className={styles.timeSrc}>{row.time_source}</td>
      <td className={styles.num}>{row.request_count.toLocaleString('en-US')}</td>
      <td className={styles.num}>{row.event_count.toLocaleString('en-US')}</td>
    </tr>
  );
}

function TopList({ title, rows, metric }: { title: string; rows: TopActionRow[]; metric: 'count' | 'time' }) {
  if (rows.length === 0) {
    return (
      <div className={styles.topCol}>
        <h4 className={styles.topTitle}>{title}</h4>
        <p className={styles.notice}>no actions recorded</p>
      </div>
    );
  }
  const max = Math.max(...rows.map((r) => (metric === 'count' ? r.count : r.total_ms)), 1);
  return (
    <div className={styles.topCol}>
      <h4 className={styles.topTitle}>{title}</h4>
      <ul className={styles.topUl}>
        {rows.map((r) => {
          const cat = taxonomyFor(r.taxonomy_id);
          const value = metric === 'count' ? r.count : r.total_ms;
          const pct = Math.round((value / max) * 100);
          // cat.color is a var(--scope-trace-tool-X) ref - valid on card-surface background.
          return (
            <li key={r.taxonomy_id} className={styles.topItem}>
              <span className={styles.topLabel} style={{ color: cat.color }}>
                {cat.label}
              </span>
              <span className={styles.topBarWrap}>
                <span className={styles.topBar} style={{ width: `${String(pct)}%`, backgroundColor: cat.color }} />
              </span>
              <span className={styles.topNum}>
                {metric === 'count' ? r.count.toLocaleString('en-US') : formatMs(r.total_ms)}
              </span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
