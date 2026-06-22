// =============================================================================
// CostLedgerView — Cost Ledger Explorer (T-05b)
//
// Replaces the ComingSoonPage placeholder with a real view consuming
// GET /api/command/budget.
//
// Design contract (binding):
//   - No raw hex. Semantic CSS tokens only.
//   - Status/provenance = text + shape + color, NEVER color alone.
//   - TABLES for tabular data (every chart has a keyboard-accessible table fallback).
//   - WCAG 2.2 AA: keyboard, visible focus both themes, 320px reflow, 44px targets.
//   - prefers-reduced-motion respected (CSS handles animation suppression).
//   - Cost honesty: null spend = em dash + label, never $0.00.
//   - provenance_breakdown shown prominently as a trust surface.
// =============================================================================

import React, { useState, useId, useRef, useCallback, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ParentSize } from '@visx/responsive';
import { Group } from '@visx/group';
import { LinePath } from '@visx/shape';
import { scaleLinear, scaleBand, scaleTime } from '@visx/scale';
import { AxisBottom, AxisLeft } from '@visx/axis';

import { BarStack } from '@visx/shape';

import { fetchCommandBudget, type CommandBudgetResponse, type BudgetByDay, type BudgetByAgent, type BudgetByModel, type BudgetByToolType, type BudgetDayToolType } from '../api/budgetApi';
import { formatTooltipDate } from '../lib/costLedger';
import { formatMoneyUsd } from '../lib/accounting';
import { queryKeys, STALE_BUDGET_MS } from '../api/queryClient';
import { ScopeApiError } from '../api/apiClient';
import { CostProvenanceChip } from '../components/CostProvenanceChip';
import { CostBillingClassBadge } from '../components/CostBillingClassBadge';
import { ExportCsvButton } from '../components/ExportCsvButton';
import { PageHeader } from '../components/PageHeader';
import { toCostProvenance, PROVENANCE_LABEL, computeKnownAssumedSplit } from '../lib/provenance';
import type { CsvColumn } from '../lib/exportCsv';
import { DateRangePicker } from '../components/DateRangePicker';
import { CopyLinkButton } from '../components/CopyLinkButton';
import { type DateRange, presetToRange } from '../lib/dateRange';
import { currentRoute, navigate } from '../lib/router';
import { decodeRangeFromRoute, encodeRange, decodeCostLedgerState, encodeCostLedgerState } from '../lib/viewState';
import styles from './CostLedgerView.module.css';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default date range: last 30 days (matching the legacy WINDOW_OPTIONS default). */
const DEFAULT_DATE_RANGE: DateRange = presetToRange('30d');

/** Max bars to show in chart (full data still in table). */
const TOP_N_CHART = 10;

/** Chart margins. Time-series charts (line / stacked) only need room for "$0.000" on the left. */
const MARGIN = { top: 12, right: 16, bottom: 48, left: 70 };

/**
 * Wider left gutter for the horizontal category bar charts (by-agent / by-model / by-tool-type)
 * whose y-axis tick labels are arbitrary-length identifiers (cron labels, raw session keys, model
 * ids). The gutter is widened AND labels are honestly truncated with a hover <title> so a long
 * identifier is never silently clipped at the SVG edge (T-2026-06-16-13).
 */
const CATEGORY_MARGIN = { top: 12, right: 16, bottom: 48, left: 148 };

/** Approx px per character at the 11px axis font; used to fit/truncate y-axis category labels. */
const AXIS_CHAR_PX = 6.2;

/** Hover-intent delay (ms) before showing a chart tooltip on mouse enter. */
const CHART_TOOLTIP_HOVER_MS = 200;

// ---------------------------------------------------------------------------
// Breakdown tabs — the four breakdown sections are presented as a tabbed UI.
// The active tab is stored in the URL as the `tab` param.
// ---------------------------------------------------------------------------

/** Valid breakdown tab ids (ST-03 view-state wiring). */
const BREAKDOWN_TAB_IDS = ['by_day', 'by_agent', 'by_model', 'by_tool_type'] as const;
type BreakdownTabId = typeof BREAKDOWN_TAB_IDS[number];

/** Human-readable labels for each breakdown tab. */
const BREAKDOWN_TAB_LABEL: Record<BreakdownTabId, string> = {
  by_day:       'Over time',
  by_agent:     'By agent',
  by_model:     'By model',
  by_tool_type: 'By tool type',
};

/** Default tab shown when no `tab` param is in the URL. */
const DEFAULT_TAB: BreakdownTabId = 'by_day';

/** Coerce an arbitrary string from the URL to a valid BreakdownTabId (or the default). */
function coerceTab(raw: string | null): BreakdownTabId {
  if (raw === null) return DEFAULT_TAB;
  // BREAKDOWN_TAB_IDS is readonly; use explicit include check.
  if ((BREAKDOWN_TAB_IDS as readonly string[]).includes(raw)) return raw as BreakdownTabId;
  return DEFAULT_TAB;
}

/**
 * Advance the tab index by `delta` with wrapping.
 * Returns a valid BreakdownTabId — the array is non-empty and the modulo is
 * always in-bounds. We use Array.from to produce a mutable array whose element
 * type is BreakdownTabId (not BreakdownTabId | undefined), letting TypeScript
 * confirm the return without a non-null assertion or unsafe cast.
 */
function advanceTab(current: BreakdownTabId, delta: number): BreakdownTabId {
  // Spread into a mutable array so indexing returns BreakdownTabId, not
  // BreakdownTabId | undefined (noUncheckedIndexedAccess affects readonly tuples).
  const tabs: BreakdownTabId[] = [...BREAKDOWN_TAB_IDS];
  const idx = tabs.indexOf(current);
  const next = (idx + delta + tabs.length) % tabs.length;
  const result = tabs[next];
  // tabs is always non-empty (4 entries, all known at compile time);
  // modulo guarantees next is in-range. Fallback to DEFAULT_TAB is unreachable
  // in practice but satisfies the strict null check.
  return result ?? DEFAULT_TAB;
}

// ---------------------------------------------------------------------------
// Chart tooltip overlay — rendered as HTML over the SVG container
// ---------------------------------------------------------------------------

/**
 * Tooltip position and content for chart overlays.
 * x/y are percentages (0-100) within the chart container div so the tooltip
 * stays within bounds on resize. `side` hints whether to anchor left or right
 * of the point to avoid clipping the right edge.
 */
interface ChartTooltipState {
  visible: boolean;
  /** Tooltip content to display. */
  label: string;
  /** X offset in px from the container left edge (used for data-point-relative positioning). */
  xPx: number;
  /** Y offset in px from the container top edge. */
  yPx: number;
}

/**
 * A positioned HTML tooltip overlay rendered inside a `position: relative` chart container.
 *
 * Props:
 *  - state: current tooltip state (visibility, label, px position)
 *  - id: stable id for aria-describedby wiring
 *
 * The tooltip is clamped so it does not escape beyond the container width.
 * Max-width is bounded in CSS; overflow shows ellipsis.
 */
function ChartTooltipOverlay({
  state,
  id,
  containerWidth,
}: {
  state: ChartTooltipState;
  id: string;
  containerWidth: number;
}): React.ReactElement | null {
  if (!state.visible) return null;

  // Anchor above the point by default; shift left if near the right edge.
  // Tooltip max-width is 280px (see CSS); clamp so it doesn't overflow the container.
  const TOOLTIP_MAX_W = 280;
  const left = Math.min(state.xPx, Math.max(0, containerWidth - TOOLTIP_MAX_W));
  const top = Math.max(0, state.yPx);

  return (
    <div
      id={id}
      role="tooltip"
      className={styles.chartTooltip}
      style={{ left, top }}
      aria-live="off"
    >
      {state.label}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Formatting helpers
// ---------------------------------------------------------------------------

/**
 * Cost Ledger USD formatter. Delegates to the shared {@link formatMoneyUsd} so the headline AND
 * every row (by_tool_type, by_model, by_agent, by_day, the per-day pivot total) render through the
 * single tiered-precision + "<$0.01" floor + honesty path — the same one the trace Accounting tab
 * uses — instead of a divergent local rule (T-2026-06-20-20).
 *
 * Rows arriving here are RAW (full precision) from the backend; any FE-summed total (e.g. the
 * per-day pivot Day total) sums RAW row values and is formatted ONCE here, never summed from
 * already-rendered strings.
 */
function formatUsd(value: number): string {
  return formatMoneyUsd(value);
}

function formatTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
}

/**
 * Truncates a y-axis category label to fit the available gutter width (in px), appending an ellipsis
 * when shortened. Returns both the display text and whether it was truncated so the caller can attach
 * an honest hover <title> with the FULL value (never a silent clip).
 */
function fitAxisLabel(label: string, gutterPx: number): { display: string; truncated: boolean } {
  const maxChars = Math.max(4, Math.floor(gutterPx / AXIS_CHAR_PX));
  if (label.length <= maxChars) return { display: label, truncated: false };
  // Keep the leading, most-identifying portion; ellipsis marks the elision.
  return { display: `${label.slice(0, maxChars - 1)}…`, truncated: true };
}

/**
 * A visx AxisLeft tickComponent that truncates long category labels to the left gutter and exposes the
 * FULL label via an SVG <title> (hover tooltip). The full text is always available here AND in the
 * keyboard-accessible table fallback, so truncation is honest, never lossy.
 */
function makeCategoryTickComponent(gutterPx: number) {
  // gutterPx is the left margin minus the tick text offset (dx) so the ellipsis math matches the draw area.
  return function CategoryTick(props: {
    x: number;
    y: number;
    formattedValue: string | undefined;
    dx?: string | number;
    dy?: string | number;
  }): React.ReactElement {
    const full = props.formattedValue ?? '';
    const { display, truncated } = fitAxisLabel(full, gutterPx);
    return (
      <text
        x={props.x}
        y={props.y}
        dx={props.dx}
        dy={props.dy}
        fill="var(--scope-color-text-muted)"
        fontSize={11}
        textAnchor="end"
      >
        {truncated ? <title>{full}</title> : null}
        {display}
      </text>
    );
  };
}

/**
 * Maps a taxonomy_id to the --scope-trace-tool-* CSS variable value for the bucket.
 * taxonomy_id values can be full ids (shell.exec, search.content) or short ids (find, shell).
 * This maps to the canonical trace-tool token names.
 * CostLedgerView by-tool-type is in the same semantic category as AccountingPanel —
 * spend broken down by tool type — which is explicitly sanctioned for --scope-trace-tool-* usage.
 */
function toolTypeToTraceToken(taxonomyId: string): string {
  // Map full taxonomy ids -> bucket token name
  const mapping: Record<string, string> = {
    // Full taxonomy ids from ToolTaxonomy
    'shell.exec':     'shell',
    'search.content': 'find',
    'search.files':   'find',
    'fs.list':        'system',
    'file.read':      'system',
    'file.write':     'edit',
    'file.edit':      'edit',
    'web.fetch':      'external',
    'web.search':     'external',
    'agent.dispatch': 'model',
    'agent.message':  'model',
    'user.question':  'operator',
    'code.intel':     'find',
    'task.manage':    'system',
    'skill.invoke':   'system',
    'mcp.other':      'external',
    'other':          'system',
    // Short bucket names (pass-through if already a bucket name)
    'find':     'find',
    'pack':     'pack',
    'hook':     'hook',
    'edit':     'edit',
    'model':    'model',
    'shell':    'shell',
    'external': 'external',
    'operator': 'operator',
    'system':   'system',
  };
  return `var(--scope-trace-tool-${mapping[taxonomyId] ?? 'system'})`;
}

/** Human-readable label for a taxonomy_id bucket. */
function toolTypeLabel(taxonomyId: string): string {
  const labels: Record<string, string> = {
    'shell.exec':     'Shell',
    'search.content': 'Content search',
    'search.files':   'File search',
    'fs.list':        'Directory listing',
    'file.read':      'File read',
    'file.write':     'File write',
    'file.edit':      'File edit',
    'web.fetch':      'Web fetch',
    'web.search':     'Web search',
    'agent.dispatch': 'Agent dispatch',
    'agent.message':  'Agent message',
    'user.question':  'Operator (human-in-loop)',
    'code.intel':     'Code intelligence',
    'task.manage':    'Task management',
    'skill.invoke':   'Skill',
    'mcp.other':      'Other MCP',
    'other':          'Other',
    'find':     'Find / Search',
    'pack':     'Pack',
    'hook':     'Hook',
    'edit':     'Edit',
    'model':    'Model call',
    'shell':    'Shell',
    'external': 'External / MCP',
    'operator': 'Operator',
    'system':   'System',
  };
  return labels[taxonomyId] ?? taxonomyId;
}

/** Build a human-readable coverage sentence from provenance_breakdown. */
// ---------------------------------------------------------------------------
// Two-bucket known/assumed note (T-2026-06-16-06)
// ---------------------------------------------------------------------------

/** Two-bucket known/assumed breakdown note for the cost ledger provenance panel. */
function KnownAssumedNote({
  breakdown,
}: {
  breakdown: CommandBudgetResponse['provenance_breakdown'];
}): React.ReactElement | null {
  const { knownUsd, assumedUsd, pricedTotal } = computeKnownAssumedSplit(breakdown);
  if (pricedTotal === 0) return null;
  const fmt = (v: number) =>
    `$${v.toLocaleString('en-US', { minimumFractionDigits: 6, maximumFractionDigits: 6 })}`;
  return (
    <span className={styles.knownAssumedNote}>
      {knownUsd > 0 && <span className={styles.knownBucket}>model known  {fmt(knownUsd)}</span>}
      {assumedUsd > 0 && <span className={styles.assumedBucket}>model assumed  {fmt(assumedUsd)}</span>}
      {assumedUsd > 0 && (
        <span className={styles.assumedInfoNote}>
          {fmt(assumedUsd)} of spend was priced with an assumed model rate because the model was not
          captured; {fmt(knownUsd)} is priced from the identified model.
        </span>
      )}
    </span>
  );
}

function buildCoverageText(breakdown: CommandBudgetResponse['provenance_breakdown']): string {
  if (breakdown.length === 0) return '';
  const total = breakdown.reduce((s, b) => s + b.spend_usd, 0);
  if (total === 0) return '';

  const parts = breakdown
    .filter((b) => b.spend_usd > 0)
    .map((b) => {
      const pct = total > 0 ? Math.round((b.spend_usd / total) * 100) : 0;
      const label = PROVENANCE_LABEL[toCostProvenance(b.provenance)];
      return `${String(pct)}% ${label.toLowerCase()}`;
    })
    .join(', ');

  return parts ? `Coverage: ${parts}` : '';
}

// ---------------------------------------------------------------------------
// CSV column maps — header text and accessor MUST match the visible table columns exactly.
// Raw numeric values are used so the CSV preserves full precision without rounding drift.
// The table renders formatUsd()/formatTokens() for display; we export the underlying numbers
// so the CSV is audit-grade (full precision), which is strictly more honest than the display.
// ---------------------------------------------------------------------------

/** provenance_breakdown table: Provenance | Spend | Runs */
const PROVENANCE_BREAKDOWN_COLUMNS: CsvColumn<CommandBudgetResponse['provenance_breakdown'][0]>[] = [
  { header: 'Provenance', accessor: (r) => PROVENANCE_LABEL[toCostProvenance(r.provenance)] },
  { header: 'Spend (USD)', accessor: (r) => r.spend_usd },
  { header: 'Runs', accessor: (r) => r.run_count },
];

/** by_day table: Date (UTC) | Spend (USD) | Runs */
const BY_DAY_COLUMNS: CsvColumn<BudgetByDay>[] = [
  { header: 'Date (UTC)', accessor: (r) => r.date },
  { header: 'Spend (USD)', accessor: (r) => r.spend_usd },
  { header: 'Runs', accessor: (r) => r.run_count },
];

/** by_agent table: Agent | Spend (USD) | Runs */
const BY_AGENT_COLUMNS: CsvColumn<BudgetByAgent>[] = [
  { header: 'Agent', accessor: (r) => r.label },
  { header: 'Spend (USD)', accessor: (r) => r.spend_usd },
  { header: 'Runs', accessor: (r) => r.run_count },
];

/** by_model table: Model | Spend (USD) | Input tokens | Output tokens | Runs */
const BY_MODEL_COLUMNS: CsvColumn<BudgetByModel>[] = [
  { header: 'Model', accessor: (r) => r.model },
  { header: 'Spend (USD)', accessor: (r) => r.spend_usd },
  { header: 'Input tokens', accessor: (r) => r.input_tokens },
  { header: 'Output tokens', accessor: (r) => r.output_tokens },
  { header: 'Runs', accessor: (r) => r.run_count },
];

/** by_tool_type summary table: Tool / action type | Taxonomy ID | Spend (USD) | Requests */
const BY_TOOL_TYPE_COLUMNS: CsvColumn<BudgetByToolType>[] = [
  { header: 'Tool / action type', accessor: (r) => toolTypeLabel(r.taxonomy_id) },
  { header: 'Taxonomy ID', accessor: (r) => r.taxonomy_id },
  { header: 'Spend (USD)', accessor: (r) => r.spend_usd },
  { header: 'Requests', accessor: (r) => r.run_count },
];

// ---------------------------------------------------------------------------
// Cost Honesty / Provenance Panel
// ---------------------------------------------------------------------------

interface ProvenancePanelProps {
  data: CommandBudgetResponse;
}

function ProvenancePanel({ data }: ProvenancePanelProps): React.ReactElement {
  const provenance = toCostProvenance(data.cost_provenance);
  const coverageText = buildCoverageText(data.provenance_breakdown);
  const headingId = useId();

  return (
    <section
      className={styles.provenancePanel}
      aria-labelledby={headingId}
      role="region"
    >
      <h2 id={headingId} className={styles.provenancePanelHeading}>
        Cost coverage &amp; provenance
      </h2>
      <p className={styles.provenancePanelSubtitle}>
        How much of the spend shown is wire-exact vs. estimated vs. unavailable.
      </p>

      {/* Total spend with provenance — prominent */}
      <div className={styles.totalSpendRow}>
        <span className={styles.totalSpendLabel}>Total spend ({data.window_days}d):</span>
        {data.total_spend_usd == null ? (
          <>
            <span className={styles.totalSpendNull} aria-label="No cost recorded">&mdash;</span>
            <CostProvenanceChip
              variant="single"
              costUsd={null}
              provenance={provenance}
            />
            <CostBillingClassBadge billingClass={data.cost_billing_class} />
          </>
        ) : (
          <>
            <span className={styles.totalSpendValue}>{formatUsd(data.total_spend_usd)}</span>
            {data.provenance_breakdown.length > 0 ? (
              <KnownAssumedNote breakdown={data.provenance_breakdown} />
            ) : (
              <CostProvenanceChip
                variant="single"
                costUsd={data.total_spend_usd}
                provenance={provenance}
              />
            )}
            <CostBillingClassBadge billingClass={data.cost_billing_class} />
          </>
        )}
      </div>

      {/* Date range */}
      <div className={styles.dateRange}>
        {data.from_utc.slice(0, 10)} &ndash; {data.to_utc.slice(0, 10)} UTC
      </div>

      {/* Coverage text */}
      {coverageText && (
        <p className={styles.coverageText}>{coverageText}</p>
      )}

      {/* Provenance breakdown table */}
      {data.provenance_breakdown.length > 0 && (
        <div className={styles.tableContainer}>
          <div className={styles.tableExportRow}>
            <span data-testid="export-provenance-breakdown">
              <ExportCsvButton<typeof data.provenance_breakdown[0]>
                columns={PROVENANCE_BREAKDOWN_COLUMNS}
                rows={data.provenance_breakdown}
                filename={`provenance-breakdown-${String(data.window_days)}d.csv`}
                label="Export provenance CSV"
              />
            </span>
          </div>
          <table className={styles.provenanceTable}>
            <caption>Provenance breakdown — how spend is classified by data source</caption>
            <thead>
              <tr>
                <th scope="col">Provenance</th>
                <th scope="col">Spend</th>
                <th scope="col">Runs</th>
              </tr>
            </thead>
            <tbody>
              {data.provenance_breakdown.map((row) => (
                <tr key={row.provenance}>
                  <td>{PROVENANCE_LABEL[toCostProvenance(row.provenance)]}</td>
                  <td>{formatUsd(row.spend_usd)}</td>
                  <td>{row.run_count}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {data.provenance_breakdown.length === 0 && (
        <p className={styles.emptySection}>No provenance breakdown available for this window.</p>
      )}
    </section>
  );
}

// ---------------------------------------------------------------------------
// Spend-over-time chart (line) + table fallback
// ---------------------------------------------------------------------------

interface SpendOverTimeProps {
  byDay: BudgetByDay[];
  windowDays: number;
}

function SpendOverTimeSection({ byDay, windowDays }: SpendOverTimeProps): React.ReactElement {
  const headingId = useId();
  const tableHeadingId = useId();

  // Filter out trailing zero rows at both ends only when all-zero (honest zero-fill display)
  const hasAnySpend = byDay.some((d) => d.spend_usd > 0);

  return (
    <section className={styles.section} aria-labelledby={headingId}>
      <h2 id={headingId} className={styles.sectionHeading}>
        Spend over time ({windowDays}d)
      </h2>

      {!hasAnySpend ? (
        <p className={styles.emptySection}>No metered spend in this window. The chart shows a flat baseline.</p>
      ) : null}

      <div className={styles.chartAndTable}>
        {/* VISX chart — descriptive summary for screen readers; data points are keyboard-focusable */}
        <p className={styles.srOnly}>
          {`Line chart: daily spend over ${String(windowDays)} days. ${
            hasAnySpend
              ? `Peak: ${formatUsd(Math.max(...byDay.map((d) => d.spend_usd)))}.`
              : 'No spend recorded.'
          } Use Tab to navigate individual data points.`}
        </p>
        <div
          className={styles.chartContainer}
          style={{ height: 240 }}
        >
          <ParentSize>
            {({ width }) => (
              <SpendLineChart
                data={byDay}
                width={Math.max(width, 120)}
                height={240}
              />
            )}
          </ParentSize>
        </div>

        {/* Table fallback — same data, keyboard accessible */}
        <div className={styles.tableContainer}>
          <div className={styles.tableExportRow}>
            <span data-testid="export-by-day">
              <ExportCsvButton<BudgetByDay>
                columns={BY_DAY_COLUMNS}
                rows={byDay}
                filename={`spend-by-day-${String(windowDays)}d.csv`}
                label="Export daily spend CSV"
              />
            </span>
          </div>
          <table className={styles.dataTable} aria-labelledby={tableHeadingId}>
            <caption id={tableHeadingId}>
              Daily spend — {windowDays}-day window (all rows)
            </caption>
            <thead>
              <tr>
                <th scope="col">Date (UTC)</th>
                <th scope="col">Spend (USD)</th>
                <th scope="col">Runs</th>
              </tr>
            </thead>
            <tbody>
              {byDay.map((row) => (
                <tr key={row.date}>
                  <td>{row.date}</td>
                  <td>{row.spend_usd > 0 ? formatUsd(row.spend_usd) : '—'}</td>
                  <td>{row.run_count}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </section>
  );
}

// ---------------------------------------------------------------------------
// Spend line chart (VISX) + tooltip
// ---------------------------------------------------------------------------

interface SpendLineChartProps {
  data: BudgetByDay[];
  width: number;
  height: number;
}

function SpendLineChart({ data, width, height }: SpendLineChartProps): React.ReactElement | null {
  const tooltipId = useId();
  const [tooltip, setTooltip] = useState<ChartTooltipState>({
    visible: false,
    label: '',
    xPx: 0,
    yPx: 0,
  });
  const hoverTimer = useRef<number | null>(null);

  const clearHoverTimer = useCallback(() => {
    if (hoverTimer.current !== null) {
      window.clearTimeout(hoverTimer.current);
      hoverTimer.current = null;
    }
  }, []);

  const hideTooltip = useCallback(() => {
    clearHoverTimer();
    setTooltip((prev) => ({ ...prev, visible: false }));
  }, [clearHoverTimer]);

  if (data.length === 0) return null;

  const innerWidth = Math.max(0, width - MARGIN.left - MARGIN.right);
  const innerHeight = Math.max(0, height - MARGIN.top - MARGIN.bottom);

  // Parse dates
  const parsed = data.map((d) => ({
    ...d,
    dateObj: new Date(`${d.date}T00:00:00Z`),
  }));

  // Indices are safe: parsed is a non-empty array (called only when data.length > 0).
  const first = parsed[0];
  const last = parsed[parsed.length - 1];
  if (first === undefined || last === undefined) return null;
  const minDate = first.dateObj;
  const maxDate = last.dateObj;
  const maxSpend = Math.max(...parsed.map((d) => d.spend_usd), 0);

  const xScale = scaleTime({
    domain: [minDate, maxDate],
    range: [0, innerWidth],
  });

  const yScale = scaleLinear({
    domain: [0, maxSpend * 1.1 || 0.001],
    range: [innerHeight, 0],
    nice: true,
  });

  // Tick format: abbreviated month/day
  const xTickFormat = (d: Date | { valueOf(): number }): string => {
    const date = d instanceof Date ? d : new Date((d).valueOf());
    return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' });
  };

  const yTickFormat = (v: number | { valueOf(): number }): string => {
    const val = typeof v === 'number' ? v : v.valueOf();
    if (val === 0) return '$0';
    if (val < 0.001) return `$${val.toFixed(4)}`;
    return `$${val.toFixed(3)}`;
  };

  // Tick count — reduce on narrow screens
  const xTickCount = Math.min(7, Math.max(2, Math.floor(innerWidth / 80)));

  // Only non-zero points get interactive data-point targets
  const activePoints = parsed.filter((d) => d.spend_usd > 0);

  return (
    <div style={{ position: 'relative', width, height }}>
      <svg width={width} height={height} aria-hidden="true">
        <Group left={MARGIN.left} top={MARGIN.top}>
          {/* Grid lines */}
          {yScale.ticks(4).map((tick) => (
            <line
              key={tick}
              x1={0}
              x2={innerWidth}
              y1={yScale(tick)}
              y2={yScale(tick)}
              stroke="var(--scope-color-chart-grid)"
              strokeDasharray="4 3"
              strokeWidth={1}
            />
          ))}

          {/* Line path */}
          <LinePath
            data={parsed}
            x={(d) => xScale(d.dateObj)}
            y={(d) => yScale(d.spend_usd)}
            stroke="var(--scope-color-chart-series-1)"
            strokeWidth={2}
            strokeLinecap="round"
            strokeLinejoin="round"
          />

          {/* Data points — focusable, keyboard-accessible, tooltip on hover+focus */}
          {activePoints.map((d) => {
            const cx = xScale(d.dateObj);
            const cy = yScale(d.spend_usd);
            // Tooltip anchor: slightly above the circle, left-aligned to point
            const tipX = MARGIN.left + cx;
            const tipY = MARGIN.top + cy - 32;
            const tooltipLabel = `${formatTooltipDate(d.date)}: ${formatUsd(d.spend_usd)}`;
            return (
              <circle
                key={d.date}
                cx={cx}
                cy={cy}
                r={5}
                fill="var(--scope-color-chart-series-1)"
                tabIndex={0}
                role="graphics-symbol"
                aria-label={tooltipLabel}
                aria-describedby={tooltip.visible && tooltip.label === tooltipLabel ? tooltipId : undefined}
                style={{ cursor: 'default', outline: 'none' }}
                onMouseEnter={() => {
                  clearHoverTimer();
                  hoverTimer.current = window.setTimeout(() => {
                    setTooltip({ visible: true, label: tooltipLabel, xPx: tipX, yPx: tipY });
                  }, CHART_TOOLTIP_HOVER_MS);
                }}
                onMouseLeave={hideTooltip}
                onFocus={() => {
                  clearHoverTimer();
                  setTooltip({ visible: true, label: tooltipLabel, xPx: tipX, yPx: tipY });
                }}
                onBlur={hideTooltip}
              />
            );
          })}

          {/* Axes */}
          <AxisBottom
            top={innerHeight}
            scale={xScale}
            numTicks={xTickCount}
            tickFormat={xTickFormat}
            stroke="var(--scope-color-chart-axis)"
            tickStroke="var(--scope-color-chart-axis)"
            tickLabelProps={{
              fill: 'var(--scope-color-text-muted)',
              fontSize: 11,
              textAnchor: 'middle',
            }}
            label="Date (UTC)"
            labelProps={{
              fill: 'var(--scope-color-text-muted)',
              fontSize: 11,
              textAnchor: 'middle',
            }}
          />
          <AxisLeft
            scale={yScale}
            numTicks={4}
            tickFormat={yTickFormat}
            stroke="var(--scope-color-chart-axis)"
            tickStroke="var(--scope-color-chart-axis)"
            tickLabelProps={{
              fill: 'var(--scope-color-text-muted)',
              fontSize: 11,
              textAnchor: 'end',
              dx: '-0.25em',
            }}
            label="Spend (USD)"
            labelProps={{
              fill: 'var(--scope-color-text-muted)',
              fontSize: 11,
              textAnchor: 'middle',
            }}
            labelOffset={52}
          />
        </Group>
      </svg>
      <ChartTooltipOverlay state={tooltip} id={tooltipId} containerWidth={width} />
    </div>
  );
}

// ---------------------------------------------------------------------------
// By-agent bar chart + table fallback
// ---------------------------------------------------------------------------

interface ByAgentSectionProps {
  byAgent: BudgetByAgent[];
  /** The currently selected agent label (null = none selected). ST-03 view-state. */
  selectedAgent: string | null;
  /** Called when the user selects or deselects an agent row. ST-03 view-state. */
  onSelectAgent: (label: string | null) => void;
}

function ByAgentSection({ byAgent, selectedAgent, onSelectAgent }: ByAgentSectionProps): React.ReactElement {
  const headingId = useId();
  const tableHeadingId = useId();

  const chartData = byAgent.slice(0, TOP_N_CHART);
  const hasMore = byAgent.length > TOP_N_CHART;

  return (
    <section className={styles.section} aria-labelledby={headingId}>
      <h2 id={headingId} className={styles.sectionHeading}>
        Spend by agent
      </h2>

      {/* Active agent filter notice */}
      {selectedAgent !== null && (
        <div className={styles.agentFilterBanner} role="status">
          <span className={styles.agentFilterLabel}>
            Filtered to: <strong>{selectedAgent}</strong>
          </span>
          <button
            type="button"
            className={styles.agentFilterClear}
            onClick={() => { onSelectAgent(null); }}
            aria-label={`Clear agent filter: ${selectedAgent}`}
          >
            Clear
          </button>
        </div>
      )}

      {byAgent.length === 0 ? (
        <p className={styles.emptySection}>No agent spend recorded in this window.</p>
      ) : (
        <div className={styles.chartAndTable}>
          {/* Bar chart — descriptive summary for screen readers; bars are keyboard-focusable */}
          <p className={styles.srOnly}>
            {`Horizontal bar chart: spend by agent. Top agent: ${chartData[0]?.label ?? 'none'} at ${chartData[0] != null ? formatUsd(chartData[0].spend_usd) : '—'}.${hasMore ? ` Showing top ${String(TOP_N_CHART)} of ${String(byAgent.length)}.` : ''} Use Tab to navigate individual bars.`}
          </p>
          <div
            className={styles.chartContainer}
            style={{ height: Math.max(160, chartData.length * 36 + MARGIN.top + MARGIN.bottom) }}
          >
            <ParentSize>
              {({ width }) => (
                <AgentBarChart
                  data={chartData}
                  width={Math.max(width, 120)}
                  height={Math.max(160, chartData.length * 36 + MARGIN.top + MARGIN.bottom)}
                />
              )}
            </ParentSize>
          </div>

          {hasMore && (
            <p className={styles.truncationNotice}>
              Chart shows top {TOP_N_CHART} agents. Full data in table below ({byAgent.length} total).
            </p>
          )}

          {/* Table fallback — FULL data, rows selectable (ST-03 view-state) */}
          <div className={styles.tableContainer}>
            <div className={styles.tableExportRow}>
              <span data-testid="export-by-agent">
                <ExportCsvButton<BudgetByAgent>
                  columns={BY_AGENT_COLUMNS}
                  rows={byAgent}
                  filename="spend-by-agent.csv"
                  label="Export agent spend CSV"
                />
              </span>
            </div>
            <table className={styles.dataTable} aria-labelledby={tableHeadingId}>
              <caption id={tableHeadingId}>
                Spend by agent — all {byAgent.length} {byAgent.length === 1 ? 'agent' : 'agents'}, descending by spend.
                {' '}Click a row to select/filter by that agent.
              </caption>
              <thead>
                <tr>
                  <th scope="col">Agent</th>
                  <th scope="col">Spend (USD)</th>
                  <th scope="col">Runs</th>
                </tr>
              </thead>
              <tbody>
                {byAgent.map((row) => {
                  const isSelected = selectedAgent === row.label;
                  return (
                    <tr
                      key={row.label}
                      className={isSelected ? styles.agentRowSelected : styles.agentRowSelectable}
                      aria-selected={isSelected}
                      role="row"
                      tabIndex={0}
                      onClick={() => { onSelectAgent(isSelected ? null : row.label); }}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          e.preventDefault();
                          onSelectAgent(isSelected ? null : row.label);
                        }
                      }}
                    >
                      <td>{row.label}</td>
                      <td>{formatUsd(row.spend_usd)}</td>
                      <td>{row.run_count}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </section>
  );
}

// ---------------------------------------------------------------------------
// Agent bar chart (VISX horizontal bars) + tooltip
// ---------------------------------------------------------------------------

interface AgentBarChartProps {
  data: BudgetByAgent[];
  width: number;
  height: number;
}

function AgentBarChart({ data, width, height }: AgentBarChartProps): React.ReactElement | null {
  const tooltipId = useId();
  const [tooltip, setTooltip] = useState<ChartTooltipState>({
    visible: false,
    label: '',
    xPx: 0,
    yPx: 0,
  });
  const hoverTimer = useRef<number | null>(null);

  const clearHoverTimer = useCallback(() => {
    if (hoverTimer.current !== null) {
      window.clearTimeout(hoverTimer.current);
      hoverTimer.current = null;
    }
  }, []);

  const hideTooltip = useCallback(() => {
    clearHoverTimer();
    setTooltip((prev) => ({ ...prev, visible: false }));
  }, [clearHoverTimer]);

  if (data.length === 0) return null;

  const innerWidth = Math.max(0, width - CATEGORY_MARGIN.left - CATEGORY_MARGIN.right);
  const innerHeight = Math.max(0, height - CATEGORY_MARGIN.top - CATEGORY_MARGIN.bottom);
  const maxSpend = Math.max(...data.map((d) => d.spend_usd), 0);

  const yScale = scaleBand({
    domain: data.map((d) => d.label),
    range: [0, innerHeight],
    padding: 0.2,
  });

  const xScale = scaleLinear({
    domain: [0, maxSpend * 1.1 || 0.001],
    range: [0, innerWidth],
    nice: true,
  });

  const xTickFormat = (v: number | { valueOf(): number }): string => {
    const val = typeof v === 'number' ? v : v.valueOf();
    if (val === 0) return '$0';
    return `$${val.toFixed(3)}`;
  };

  const barHeight = yScale.bandwidth();

  return (
    <div style={{ position: 'relative', width, height }}>
      <svg width={width} height={height} aria-hidden="true">
        <Group left={CATEGORY_MARGIN.left} top={CATEGORY_MARGIN.top}>
          {/* Grid lines */}
          {xScale.ticks(4).map((tick) => (
            <line
              key={tick}
              x1={xScale(tick)}
              x2={xScale(tick)}
              y1={0}
              y2={innerHeight}
              stroke="var(--scope-color-chart-grid)"
              strokeDasharray="4 3"
              strokeWidth={1}
            />
          ))}

          {/* Bars — focusable, tooltip on hover+focus */}
          {data.map((d) => {
            const barY = yScale(d.label) ?? 0;
            const barW = Math.max(0, xScale(d.spend_usd));
            const tooltipLabel = `${d.label}: ${formatUsd(d.spend_usd)} / ${String(d.run_count)} ${d.run_count === 1 ? 'run' : 'runs'}`;
            const tipX = CATEGORY_MARGIN.left + barW / 2;
            const tipY = CATEGORY_MARGIN.top + barY - 8;
            return (
              <rect
                key={d.label}
                x={0}
                y={barY}
                width={barW}
                height={barHeight}
                fill="var(--scope-color-chart-series-2)"
                rx={2}
                tabIndex={0}
                role="graphics-symbol"
                aria-label={tooltipLabel}
                aria-describedby={tooltip.visible && tooltip.label === tooltipLabel ? tooltipId : undefined}
                style={{ cursor: 'default', outline: 'none' }}
                onMouseEnter={() => {
                  clearHoverTimer();
                  hoverTimer.current = window.setTimeout(() => {
                    setTooltip({ visible: true, label: tooltipLabel, xPx: tipX, yPx: tipY });
                  }, CHART_TOOLTIP_HOVER_MS);
                }}
                onMouseLeave={hideTooltip}
                onFocus={() => {
                  clearHoverTimer();
                  setTooltip({ visible: true, label: tooltipLabel, xPx: tipX, yPx: tipY });
                }}
                onBlur={hideTooltip}
              />
            );
          })}

          {/* Axes */}
          <AxisBottom
            top={innerHeight}
            scale={xScale}
            numTicks={4}
            tickFormat={xTickFormat}
            stroke="var(--scope-color-chart-axis)"
            tickStroke="var(--scope-color-chart-axis)"
            tickLabelProps={{
              fill: 'var(--scope-color-text-muted)',
              fontSize: 11,
              textAnchor: 'middle',
            }}
            label="Spend (USD)"
            labelProps={{
              fill: 'var(--scope-color-text-muted)',
              fontSize: 11,
              textAnchor: 'middle',
            }}
          />
          <AxisLeft
            scale={yScale}
            stroke="var(--scope-color-chart-axis)"
            tickStroke="var(--scope-color-chart-axis)"
            tickComponent={makeCategoryTickComponent(CATEGORY_MARGIN.left - 6)}
            tickLabelProps={{
              fill: 'var(--scope-color-text-muted)',
              fontSize: 11,
              textAnchor: 'end',
              dx: '-0.25em',
              dy: '0.3em',
            }}
          />
        </Group>
      </svg>
      <ChartTooltipOverlay state={tooltip} id={tooltipId} containerWidth={width} />
    </div>
  );
}

// ---------------------------------------------------------------------------
// By-model section
// ---------------------------------------------------------------------------

interface ByModelSectionProps {
  byModel: BudgetByModel[];
}

function ByModelSection({ byModel }: ByModelSectionProps): React.ReactElement {
  const headingId = useId();
  const tableHeadingId = useId();

  const chartData = byModel.slice(0, TOP_N_CHART);
  const hasMore = byModel.length > TOP_N_CHART;

  return (
    <section className={styles.section} aria-labelledby={headingId}>
      <h2 id={headingId} className={styles.sectionHeading}>
        Spend by model
      </h2>

      {byModel.length === 0 ? (
        <p className={styles.emptySection}>No model spend recorded in this window.</p>
      ) : (
        <div className={styles.chartAndTable}>
          {/* Bar chart — descriptive summary for screen readers; bars are keyboard-focusable */}
          <p className={styles.srOnly}>
            {`Horizontal bar chart: spend by model. Top model: ${chartData[0]?.model ?? 'none'} at ${chartData[0] != null ? formatUsd(chartData[0].spend_usd) : '—'}.${hasMore ? ` Showing top ${String(TOP_N_CHART)} of ${String(byModel.length)}.` : ''} Use Tab to navigate individual bars.`}
          </p>
          <div
            className={styles.chartContainer}
            style={{ height: Math.max(160, chartData.length * 36 + MARGIN.top + MARGIN.bottom) }}
          >
            <ParentSize>
              {({ width }) => (
                <ModelBarChart
                  data={chartData}
                  width={Math.max(width, 120)}
                  height={Math.max(160, chartData.length * 36 + MARGIN.top + MARGIN.bottom)}
                />
              )}
            </ParentSize>
          </div>

          {hasMore && (
            <p className={styles.truncationNotice}>
              Chart shows top {TOP_N_CHART} models. Full data in table below ({byModel.length} total).
            </p>
          )}

          {/* Table fallback — FULL data */}
          <div className={styles.tableContainer}>
            <div className={styles.tableExportRow}>
              <span data-testid="export-by-model">
                <ExportCsvButton<BudgetByModel>
                  columns={BY_MODEL_COLUMNS}
                  rows={byModel}
                  filename="spend-by-model.csv"
                  label="Export model spend CSV"
                />
              </span>
            </div>
            <table className={styles.dataTable} aria-labelledby={tableHeadingId}>
              <caption id={tableHeadingId}>
                Spend by model — all {byModel.length} {byModel.length === 1 ? 'model' : 'models'}, descending by spend
              </caption>
              <thead>
                <tr>
                  <th scope="col">Model</th>
                  <th scope="col">Spend (USD)</th>
                  <th scope="col">Input tokens</th>
                  <th scope="col">Output tokens</th>
                  <th scope="col">Runs</th>
                </tr>
              </thead>
              <tbody>
                {byModel.map((row) => (
                  <tr key={row.model}>
                    <td><code>{row.model}</code></td>
                    <td>{formatUsd(row.spend_usd)}</td>
                    <td>{formatTokens(row.input_tokens)}</td>
                    <td>{formatTokens(row.output_tokens)}</td>
                    <td>{row.run_count}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </section>
  );
}

// ---------------------------------------------------------------------------
// Model bar chart (VISX horizontal bars — series-3 color) + tooltip
// ---------------------------------------------------------------------------

interface ModelBarChartProps {
  data: BudgetByModel[];
  width: number;
  height: number;
}

function ModelBarChart({ data, width, height }: ModelBarChartProps): React.ReactElement | null {
  const tooltipId = useId();
  const [tooltip, setTooltip] = useState<ChartTooltipState>({
    visible: false,
    label: '',
    xPx: 0,
    yPx: 0,
  });
  const hoverTimer = useRef<number | null>(null);

  const clearHoverTimer = useCallback(() => {
    if (hoverTimer.current !== null) {
      window.clearTimeout(hoverTimer.current);
      hoverTimer.current = null;
    }
  }, []);

  const hideTooltip = useCallback(() => {
    clearHoverTimer();
    setTooltip((prev) => ({ ...prev, visible: false }));
  }, [clearHoverTimer]);

  if (data.length === 0) return null;

  const innerWidth = Math.max(0, width - CATEGORY_MARGIN.left - CATEGORY_MARGIN.right);
  const innerHeight = Math.max(0, height - CATEGORY_MARGIN.top - CATEGORY_MARGIN.bottom);
  const maxSpend = Math.max(...data.map((d) => d.spend_usd), 0);

  const yScale = scaleBand({
    domain: data.map((d) => d.model),
    range: [0, innerHeight],
    padding: 0.2,
  });

  const xScale = scaleLinear({
    domain: [0, maxSpend * 1.1 || 0.001],
    range: [0, innerWidth],
    nice: true,
  });

  const xTickFormat = (v: number | { valueOf(): number }): string => {
    const val = typeof v === 'number' ? v : v.valueOf();
    if (val === 0) return '$0';
    return `$${val.toFixed(3)}`;
  };

  const barHeight = yScale.bandwidth();

  return (
    <div style={{ position: 'relative', width, height }}>
      <svg width={width} height={height} aria-hidden="true">
        <Group left={CATEGORY_MARGIN.left} top={CATEGORY_MARGIN.top}>
          {/* Grid lines */}
          {xScale.ticks(4).map((tick) => (
            <line
              key={tick}
              x1={xScale(tick)}
              x2={xScale(tick)}
              y1={0}
              y2={innerHeight}
              stroke="var(--scope-color-chart-grid)"
              strokeDasharray="4 3"
              strokeWidth={1}
            />
          ))}

          {/* Bars — focusable, tooltip on hover+focus */}
          {data.map((d) => {
            const barY = yScale(d.model) ?? 0;
            const barW = Math.max(0, xScale(d.spend_usd));
            const tooltipLabel = `${d.model}: ${formatUsd(d.spend_usd)} / ${String(d.run_count)} ${d.run_count === 1 ? 'run' : 'runs'}`;
            const tipX = CATEGORY_MARGIN.left + barW / 2;
            const tipY = CATEGORY_MARGIN.top + barY - 8;
            return (
              <rect
                key={d.model}
                x={0}
                y={barY}
                width={barW}
                height={barHeight}
                fill="var(--scope-color-chart-series-3)"
                rx={2}
                tabIndex={0}
                role="graphics-symbol"
                aria-label={tooltipLabel}
                aria-describedby={tooltip.visible && tooltip.label === tooltipLabel ? tooltipId : undefined}
                style={{ cursor: 'default', outline: 'none' }}
                onMouseEnter={() => {
                  clearHoverTimer();
                  hoverTimer.current = window.setTimeout(() => {
                    setTooltip({ visible: true, label: tooltipLabel, xPx: tipX, yPx: tipY });
                  }, CHART_TOOLTIP_HOVER_MS);
                }}
                onMouseLeave={hideTooltip}
                onFocus={() => {
                  clearHoverTimer();
                  setTooltip({ visible: true, label: tooltipLabel, xPx: tipX, yPx: tipY });
                }}
                onBlur={hideTooltip}
              />
            );
          })}

          {/* Axes */}
          <AxisBottom
            top={innerHeight}
            scale={xScale}
            numTicks={4}
            tickFormat={xTickFormat}
            stroke="var(--scope-color-chart-axis)"
            tickStroke="var(--scope-color-chart-axis)"
            tickLabelProps={{
              fill: 'var(--scope-color-text-muted)',
              fontSize: 11,
              textAnchor: 'middle',
            }}
            label="Spend (USD)"
            labelProps={{
              fill: 'var(--scope-color-text-muted)',
              fontSize: 11,
              textAnchor: 'middle',
            }}
          />
          <AxisLeft
            scale={yScale}
            stroke="var(--scope-color-chart-axis)"
            tickStroke="var(--scope-color-chart-axis)"
            tickComponent={makeCategoryTickComponent(CATEGORY_MARGIN.left - 6)}
            tickLabelProps={{
              fill: 'var(--scope-color-text-muted)',
              fontSize: 11,
              textAnchor: 'end',
              dx: '-0.25em',
              dy: '0.3em',
            }}
          />
        </Group>
      </svg>
      <ChartTooltipOverlay state={tooltip} id={tooltipId} containerWidth={width} />
    </div>
  );
}

// ---------------------------------------------------------------------------
// Spend by tool type — stacked time-series + summary bar + table fallbacks
// ---------------------------------------------------------------------------

interface ByToolTypeSectionProps {
  byToolType: BudgetByToolType[];
  byDayToolType: BudgetDayToolType[];
  windowDays: number;
}

/**
 * A pivoted row for the stacked time-series. The `date` field identifies the row;
 * numeric fields for each taxonomy_id hold the spend for that day+bucket.
 * BarStack requires the datum type to index by string key, so we use a loose
 * interface and keep `date` as a known string property.
 */
interface PivotedDayRow {
  date: string;
  [taxonomyId: string]: number | string; // date = string; all others = number
}

/** Safely extract a numeric spend value from a PivotedDayRow by taxonomyId key. */
function pivotSpend(row: PivotedDayRow, id: string): number {
  const v = row[id];
  return typeof v === 'number' ? v : 0;
}

/**
 * Pivot the flat by_day_tool_type rows into a structure usable by BarStack:
 * an array of day-keyed objects where each taxonomy_id is a property.
 *
 * Returns:
 *   - pivotedRows: one object per day, shape { date: string; [taxonomyId]: number }
 *   - taxonomyIds: ordered list of unique taxonomy IDs (most-spend-first)
 */
function pivotDayToolType(
  rows: BudgetDayToolType[],
  byToolType: BudgetByToolType[],
): {
  pivotedRows: PivotedDayRow[];
  taxonomyIds: string[];
} {
  if (rows.length === 0) return { pivotedRows: [], taxonomyIds: [] };

  // Collect all taxonomy IDs, ordered by total spend descending (matches byToolType order)
  const taxonomyIds = byToolType.map((r) => r.taxonomy_id);
  // Add any ids that appear in byDayToolType but not in byToolType (shouldn't happen, but safe)
  const extraIds = [...new Set(rows.map((r) => r.taxonomy_id))].filter(
    (id) => !taxonomyIds.includes(id),
  );
  const allIds = [...taxonomyIds, ...extraIds];

  // Collect all unique dates in order
  const datesOrdered = [...new Set(rows.map((r) => r.date))].sort();

  // Build pivot: date -> taxonomyId -> spend
  const byDate = new Map<string, Map<string, number>>();
  for (const row of rows) {
    let m = byDate.get(row.date);
    if (m === undefined) {
      m = new Map<string, number>();
      byDate.set(row.date, m);
    }
    m.set(row.taxonomy_id, (m.get(row.taxonomy_id) ?? 0) + row.spend_usd);
  }

  const pivotedRows: PivotedDayRow[] = datesOrdered.map((date) => {
    const m = byDate.get(date) ?? new Map<string, number>();
    const obj: PivotedDayRow = { date };
    for (const id of allIds) {
      obj[id] = m.get(id) ?? 0;
    }
    return obj;
  });

  return { pivotedRows, taxonomyIds: allIds };
}

function ByToolTypeSection({ byToolType, byDayToolType, windowDays }: ByToolTypeSectionProps): React.ReactElement {
  const headingId = useId();
  const summaryTableHeadingId = useId();
  const stackedTableHeadingId = useId();
  const legendId = useId();

  const { pivotedRows, taxonomyIds } = pivotDayToolType(byDayToolType, byToolType);
  const hasStackedData = pivotedRows.length > 0 && pivotedRows.some((r) =>
    taxonomyIds.some((id) => pivotSpend(r, id) > 0),
  );

  // Build aria-label summary for stacked chart
  const stackedAriaLabel = hasStackedData
    ? `Stacked bar chart: daily spend by tool type over ${String(windowDays)} days. ${
        byToolType.length > 0
          ? `Top tool type: ${toolTypeLabel(byToolType[0]?.taxonomy_id ?? '')} at ${byToolType[0] != null ? formatUsd(byToolType[0].spend_usd) : '—'} total.`
          : ''
      } ${String(pivotedRows.length)} days shown.`
    : `No tool-type spend data available for ${String(windowDays)}-day window.`;

  return (
    <section className={styles.section} aria-labelledby={headingId}>
      <h2 id={headingId} className={styles.sectionHeading}>
        Spend by tool / action type ({windowDays}d)
      </h2>
      <p className={styles.toolTypeSectionSubtitle}>
        Stacked daily spend broken down by action type using the tool taxonomy. Each segment
        uses the canonical tool-type hue from the trace visualization color scheme.
      </p>

      {byToolType.length === 0 ? (
        <p className={styles.emptySection}>No tool-type spend recorded in this window.</p>
      ) : (
        <div className={styles.chartAndTable}>
          {/* Text legend — label + swatch + value, never color alone */}
          <div
            className={styles.toolTypeLegend}
            id={legendId}
            role="list"
            aria-label="Tool type color legend"
          >
            {byToolType.map((row) => (
              <div
                key={row.taxonomy_id}
                className={styles.toolTypeLegendItem}
                role="listitem"
              >
                <span
                  className={styles.toolTypeLegendSwatch}
                  style={{ background: toolTypeToTraceToken(row.taxonomy_id) }}
                  aria-hidden="true"
                />
                <span className={styles.toolTypeLegendLabel}>
                  {toolTypeLabel(row.taxonomy_id)}
                </span>
                <span className={styles.toolTypeLegendValue}>
                  {formatUsd(row.spend_usd)}
                </span>
              </div>
            ))}
          </div>

          {/* PRIMARY: Stacked bar time-series — one bar per day, segments by tool type */}
          <p className={styles.srOnly}>{stackedAriaLabel}</p>
          <div
            className={styles.chartContainer}
            style={{ height: 260 }}
            aria-describedby={legendId}
          >
            <ParentSize>
              {({ width }) => (
                <StackedToolTypeTimeSeriesChart
                  pivotedRows={pivotedRows}
                  taxonomyIds={taxonomyIds}
                  width={Math.max(width, 120)}
                  height={260}
                />
              )}
            </ParentSize>
          </div>

          {/* Stacked time-series table fallback — day x tool-type matrix */}
          {hasStackedData && (
            <div className={styles.tableContainer}>
              <table className={styles.dataTable} aria-labelledby={stackedTableHeadingId}>
                <caption id={stackedTableHeadingId}>
                  Daily spend by tool/action type &mdash; {String(pivotedRows.length)}-day breakdown
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Date (UTC)</th>
                    {taxonomyIds.map((id) => (
                      <th key={id} scope="col">{toolTypeLabel(id)}</th>
                    ))}
                    <th scope="col">Day total</th>
                  </tr>
                </thead>
                <tbody>
                  {pivotedRows.map((row) => {
                    const dayTotal = taxonomyIds.reduce((s, id) => s + pivotSpend(row, id), 0);
                    return (
                      <tr key={row.date}>
                        <th scope="row">{row.date}</th>
                        {taxonomyIds.map((id) => {
                          const v = pivotSpend(row, id);
                          return <td key={id}>{v > 0 ? formatUsd(v) : '—'}</td>;
                        })}
                        <td>{dayTotal > 0 ? formatUsd(dayTotal) : '—'}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}

          {/* SECONDARY: Summary horizontal bar chart — per-tool-type totals, bars keyboard-focusable */}
          <p className={styles.srOnly}>
            {`Horizontal bar chart: total spend by tool type over ${String(windowDays)} days. Top type: ${toolTypeLabel(byToolType[0]?.taxonomy_id ?? '')} at ${byToolType[0] != null ? formatUsd(byToolType[0].spend_usd) : '—'}. Use Tab to navigate individual bars.`}
          </p>
          <div
            className={styles.chartContainer}
            style={{ height: Math.max(160, byToolType.length * 40 + MARGIN.top + MARGIN.bottom) }}
            aria-describedby={legendId}
          >
            <ParentSize>
              {({ width }) => (
                <ToolTypeBarChart
                  data={byToolType}
                  width={Math.max(width, 120)}
                  height={Math.max(160, byToolType.length * 40 + MARGIN.top + MARGIN.bottom)}
                />
              )}
            </ParentSize>
          </div>

          {/* Summary table fallback — full data, keyboard accessible */}
          <div className={styles.tableContainer}>
            <div className={styles.tableExportRow}>
              <span data-testid="export-by-tool-type">
                <ExportCsvButton<BudgetByToolType>
                  columns={BY_TOOL_TYPE_COLUMNS}
                  rows={byToolType}
                  filename="spend-by-tool-type.csv"
                  label="Export tool type spend CSV"
                />
              </span>
            </div>
            <table className={styles.dataTable} aria-labelledby={summaryTableHeadingId}>
              <caption id={summaryTableHeadingId}>
                Spend by tool/action type &mdash; all {byToolType.length} {byToolType.length === 1 ? 'type' : 'types'}, descending by spend
              </caption>
              <thead>
                <tr>
                  <th scope="col">Tool / action type</th>
                  <th scope="col">Taxonomy ID</th>
                  <th scope="col">Spend (USD)</th>
                  <th scope="col">Requests</th>
                </tr>
              </thead>
              <tbody>
                {byToolType.map((row) => (
                  <tr key={row.taxonomy_id}>
                    <td>{toolTypeLabel(row.taxonomy_id)}</td>
                    <td><code>{row.taxonomy_id}</code></td>
                    <td>{formatUsd(row.spend_usd)}</td>
                    <td>{row.run_count}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </section>
  );
}

// ---------------------------------------------------------------------------
// Stacked tool-type time-series chart (visx BarStack)
// ---------------------------------------------------------------------------

interface StackedToolTypeTimeSeriesChartProps {
  pivotedRows: PivotedDayRow[];
  taxonomyIds: string[];
  width: number;
  height: number;
}

function StackedToolTypeTimeSeriesChart({
  pivotedRows,
  taxonomyIds,
  width,
  height,
}: StackedToolTypeTimeSeriesChartProps): React.ReactElement | null {
  if (pivotedRows.length === 0 || taxonomyIds.length === 0) return null;

  const innerWidth = Math.max(0, width - MARGIN.left - MARGIN.right);
  const innerHeight = Math.max(0, height - MARGIN.top - MARGIN.bottom);

  // Band scale: x-axis over dates
  const xScale = scaleBand({
    domain: pivotedRows.map((r) => r.date),
    range: [0, innerWidth],
    padding: 0.2,
  });

  // Linear scale: y-axis over stacked totals
  const maxDayTotal = Math.max(
    ...pivotedRows.map((r) => taxonomyIds.reduce((s, id) => s + pivotSpend(r, id), 0)),
    0,
  );

  const yScale = scaleLinear({
    domain: [0, maxDayTotal * 1.1 || 0.001],
    range: [innerHeight, 0],
    nice: true,
  });

  // Reduce tick count for narrow charts
  const xTickCount = Math.min(pivotedRows.length, Math.max(2, Math.floor(innerWidth / 60)));

  // Show only every Nth label to avoid overlap
  const labelStep = pivotedRows.length <= xTickCount ? 1 : Math.ceil(pivotedRows.length / xTickCount);

  const yTickFormat = (v: number | { valueOf(): number }): string => {
    const val = typeof v === 'number' ? v : v.valueOf();
    if (val === 0) return '$0';
    if (val < 0.001) return `$${val.toFixed(4)}`;
    return `$${val.toFixed(3)}`;
  };

  return (
    <svg width={width} height={height} aria-hidden="true">
      <Group left={MARGIN.left} top={MARGIN.top}>
        {/* Grid lines */}
        {yScale.ticks(4).map((tick) => (
          <line
            key={tick}
            x1={0}
            x2={innerWidth}
            y1={yScale(tick)}
            y2={yScale(tick)}
            stroke="var(--scope-color-chart-grid)"
            strokeDasharray="4 3"
            strokeWidth={1}
          />
        ))}

        {/* Stacked bars — each key is a taxonomy_id, colored by --scope-trace-tool-* */}
        <BarStack<PivotedDayRow, string>
          data={pivotedRows}
          keys={taxonomyIds}
          x={(d) => d.date}
          xScale={xScale}
          yScale={yScale}
          color={(key) => toolTypeToTraceToken(key)}
        >
          {(barStacks) =>
            barStacks.map((barStack) =>
              barStack.bars.map((bar) => (
                <rect
                  key={`${barStack.key}-${String(bar.index)}`}
                  x={bar.x}
                  y={bar.y}
                  width={bar.width}
                  height={Math.max(0, bar.height)}
                  fill={toolTypeToTraceToken(barStack.key)}
                />
              )),
            )
          }
        </BarStack>

        {/* Axes */}
        <AxisBottom
          top={innerHeight}
          scale={xScale}
          tickFormat={(date, i) => {
            if (i % labelStep !== 0) return '';
            return date.slice(5); // "MM-DD" from "YYYY-MM-DD"
          }}
          stroke="var(--scope-color-chart-axis)"
          tickStroke="var(--scope-color-chart-axis)"
          tickLabelProps={{
            fill: 'var(--scope-color-text-muted)',
            fontSize: 10,
            textAnchor: 'middle',
          }}
          label="Date (UTC)"
          labelProps={{
            fill: 'var(--scope-color-text-muted)',
            fontSize: 11,
            textAnchor: 'middle',
          }}
        />
        <AxisLeft
          scale={yScale}
          numTicks={4}
          tickFormat={yTickFormat}
          stroke="var(--scope-color-chart-axis)"
          tickStroke="var(--scope-color-chart-axis)"
          tickLabelProps={{
            fill: 'var(--scope-color-text-muted)',
            fontSize: 11,
            textAnchor: 'end',
            dx: '-0.25em',
          }}
          label="Spend (USD)"
          labelProps={{
            fill: 'var(--scope-color-text-muted)',
            fontSize: 11,
            textAnchor: 'middle',
          }}
          labelOffset={52}
        />
      </Group>
    </svg>
  );
}

// ---------------------------------------------------------------------------
// Tool-type bar chart (VISX horizontal bars, each colored by --scope-trace-tool-*) + tooltip
// ---------------------------------------------------------------------------

interface ToolTypeBarChartProps {
  data: BudgetByToolType[];
  width: number;
  height: number;
}

function ToolTypeBarChart({ data, width, height }: ToolTypeBarChartProps): React.ReactElement | null {
  const tooltipId = useId();
  const [tooltip, setTooltip] = useState<ChartTooltipState>({
    visible: false,
    label: '',
    xPx: 0,
    yPx: 0,
  });
  const hoverTimer = useRef<number | null>(null);

  const clearHoverTimer = useCallback(() => {
    if (hoverTimer.current !== null) {
      window.clearTimeout(hoverTimer.current);
      hoverTimer.current = null;
    }
  }, []);

  const hideTooltip = useCallback(() => {
    clearHoverTimer();
    setTooltip((prev) => ({ ...prev, visible: false }));
  }, [clearHoverTimer]);

  if (data.length === 0) return null;

  const innerWidth = Math.max(0, width - CATEGORY_MARGIN.left - CATEGORY_MARGIN.right);
  const innerHeight = Math.max(0, height - CATEGORY_MARGIN.top - CATEGORY_MARGIN.bottom);
  const maxSpend = Math.max(...data.map((d) => d.spend_usd), 0);

  const yScale = scaleBand({
    domain: data.map((d) => d.taxonomy_id),
    range: [0, innerHeight],
    padding: 0.2,
  });

  const xScale = scaleLinear({
    domain: [0, maxSpend * 1.1 || 0.001],
    range: [0, innerWidth],
    nice: true,
  });

  const xTickFormat = (v: number | { valueOf(): number }): string => {
    const val = typeof v === 'number' ? v : v.valueOf();
    if (val === 0) return '$0';
    return `$${val.toFixed(3)}`;
  };

  const barHeight = yScale.bandwidth();

  return (
    <div style={{ position: 'relative', width, height }}>
      <svg width={width} height={height} aria-hidden="true">
        <Group left={CATEGORY_MARGIN.left} top={CATEGORY_MARGIN.top}>
          {/* Grid lines */}
          {xScale.ticks(4).map((tick) => (
            <line
              key={tick}
              x1={xScale(tick)}
              x2={xScale(tick)}
              y1={0}
              y2={innerHeight}
              stroke="var(--scope-color-chart-grid)"
              strokeDasharray="4 3"
              strokeWidth={1}
            />
          ))}

          {/* Bars — each colored by the tool's canonical --scope-trace-tool-* token, focusable */}
          {data.map((d) => {
            const barY = yScale(d.taxonomy_id) ?? 0;
            const barW = Math.max(0, xScale(d.spend_usd));
            const tooltipLabel = `${toolTypeLabel(d.taxonomy_id)}: ${formatUsd(d.spend_usd)} / ${String(d.run_count)} ${d.run_count === 1 ? 'request' : 'requests'}`;
            const tipX = CATEGORY_MARGIN.left + barW / 2;
            const tipY = CATEGORY_MARGIN.top + barY - 8;
            return (
              <rect
                key={d.taxonomy_id}
                x={0}
                y={barY}
                width={barW}
                height={barHeight}
                fill={toolTypeToTraceToken(d.taxonomy_id)}
                rx={2}
                tabIndex={0}
                role="graphics-symbol"
                aria-label={tooltipLabel}
                aria-describedby={tooltip.visible && tooltip.label === tooltipLabel ? tooltipId : undefined}
                style={{ cursor: 'default', outline: 'none' }}
                onMouseEnter={() => {
                  clearHoverTimer();
                  hoverTimer.current = window.setTimeout(() => {
                    setTooltip({ visible: true, label: tooltipLabel, xPx: tipX, yPx: tipY });
                  }, CHART_TOOLTIP_HOVER_MS);
                }}
                onMouseLeave={hideTooltip}
                onFocus={() => {
                  clearHoverTimer();
                  setTooltip({ visible: true, label: tooltipLabel, xPx: tipX, yPx: tipY });
                }}
                onBlur={hideTooltip}
              />
            );
          })}

          {/* Axes */}
          <AxisBottom
            top={innerHeight}
            scale={xScale}
            numTicks={4}
            tickFormat={xTickFormat}
            stroke="var(--scope-color-chart-axis)"
            tickStroke="var(--scope-color-chart-axis)"
            tickLabelProps={{
              fill: 'var(--scope-color-text-muted)',
              fontSize: 11,
              textAnchor: 'middle',
            }}
            label="Spend (USD)"
            labelProps={{
              fill: 'var(--scope-color-text-muted)',
              fontSize: 11,
              textAnchor: 'middle',
            }}
          />
          <AxisLeft
            scale={yScale}
            stroke="var(--scope-color-chart-axis)"
            tickStroke="var(--scope-color-chart-axis)"
            tickFormat={(id) => toolTypeLabel(id)}
            tickComponent={makeCategoryTickComponent(CATEGORY_MARGIN.left - 6)}
            tickLabelProps={{
              fill: 'var(--scope-color-text-muted)',
              fontSize: 11,
              textAnchor: 'end',
              dx: '-0.25em',
              dy: '0.3em',
            }}
          />
        </Group>
      </svg>
      <ChartTooltipOverlay state={tooltip} id={tooltipId} containerWidth={width} />
    </div>
  );
}

// ---------------------------------------------------------------------------
// Loading skeleton
// ---------------------------------------------------------------------------

function LoadingSkeleton(): React.ReactElement {
  return (
    <div className={styles.skeleton} role="status" aria-label="Loading cost ledger data">
      <div className={styles.skeletonBlock} style={{ height: 80 }} />
      <div className={styles.skeletonBlock} style={{ height: 280 }} />
      <div className={styles.skeletonBlock} style={{ height: 220 }} />
      <div className={styles.skeletonBlock} style={{ height: 220 }} />
      <span className={styles.srOnly}>Loading cost ledger data&hellip;</span>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Error state
// ---------------------------------------------------------------------------

interface ErrorStateProps {
  error: Error;
  onRetry: () => void;
}

function ErrorState({ error, onRetry }: ErrorStateProps): React.ReactElement {
  const status = error instanceof ScopeApiError ? error.status : null;
  const isNetwork = error instanceof ScopeApiError && error.status === 0;

  return (
    <div className={styles.errorState} role="alert">
      <p className={styles.errorHeading}>Could not load cost ledger</p>
      <p className={styles.errorMessage}>
        {isNetwork
          ? 'Could not reach Scope.Server. Check that the server is running at 127.0.0.1:5130.'
          : status != null
            ? `Server returned ${String(status)}: ${error.message}`
            : error.message}
      </p>
      <button type="button" className={styles.retryButton} onClick={onRetry}>
        Retry
      </button>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Empty state (zero spend window)
// ---------------------------------------------------------------------------

interface EmptySpendStateProps {
  data: CommandBudgetResponse;
}

function EmptySpendState({ data }: EmptySpendStateProps): React.ReactElement {
  return (
    <div role="status">
      <p className={styles.emptySection}>
        No metered spend recorded in the last {data.window_days} days
        ({data.from_utc.slice(0, 10)} &ndash; {data.to_utc.slice(0, 10)} UTC).
        The charts below show the zero-fill baseline.
      </p>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main view
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// BreakdownTabBar — tablist for the four breakdown sections (ST-03 view-state).
// Design contract: active tab = inverse fill block (no left accent stripe).
// ---------------------------------------------------------------------------

interface BreakdownTabBarProps {
  activeTab: BreakdownTabId;
  tablistId: string;
  onTabChange: (tab: BreakdownTabId) => void;
}

function BreakdownTabBar({ activeTab, tablistId, onTabChange }: BreakdownTabBarProps): React.ReactElement {
  return (
    <div
      role="tablist"
      aria-label="Cost breakdown"
      id={tablistId}
      className={styles.breakdownTabBar}
    >
      {BREAKDOWN_TAB_IDS.map((tabId) => {
        const isActive = activeTab === tabId;
        return (
          <button
            key={tabId}
            role="tab"
            type="button"
            aria-selected={isActive}
            aria-controls={`breakdown-panel-${tabId}`}
            id={`breakdown-tab-${tabId}`}
            tabIndex={isActive ? 0 : -1}
            className={isActive ? styles.breakdownTabActive : styles.breakdownTab}
            onClick={() => { onTabChange(tabId); }}
            onKeyDown={(e) => {
              // Arrow key navigation within tablist (ARIA APG tablist pattern).
              if (e.key === 'ArrowRight') {
                e.preventDefault();
                onTabChange(advanceTab(tabId, 1));
              } else if (e.key === 'ArrowLeft') {
                e.preventDefault();
                onTabChange(advanceTab(tabId, -1));
              } else if (e.key === 'Home') {
                e.preventDefault();
                onTabChange(advanceTab(tabId, -BREAKDOWN_TAB_IDS.indexOf(tabId)));
              } else if (e.key === 'End') {
                e.preventDefault();
                onTabChange(advanceTab(tabId, BREAKDOWN_TAB_IDS.length - 1 - BREAKDOWN_TAB_IDS.indexOf(tabId)));
              }
            }}
          >
            {BREAKDOWN_TAB_LABEL[tabId]}
          </button>
        );
      })}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main view
// ---------------------------------------------------------------------------

export default function CostLedgerView(): React.ReactElement {
  const tablistId = useId();

  // ---------------------------------------------------------------------------
  // Date range state: initialised from URL (from/to params) or default (30d preset).
  // The DateRangePicker is controlled — this component owns persistence.
  // ---------------------------------------------------------------------------
  const initialRange = useMemo((): DateRange => {
    const route = currentRoute();
    const { from, to } = decodeRangeFromRoute(route);
    if (from != null && to != null) return { from, to };
    return DEFAULT_DATE_RANGE;
  }, []);

  const [dateRange, setDateRange] = useState<DateRange>(initialRange);

  // ---------------------------------------------------------------------------
  // View-state: active breakdown tab + selected agent — both read from URL on
  // mount and written to URL on every user interaction (ST-03).
  // CF-1: the range (from/to) is ST-05's territory; we spread currentRoute() so
  // agent/tab writes preserve the range and vice versa.
  // ---------------------------------------------------------------------------
  const initialViewState = useMemo(() => {
    const route = currentRoute();
    const vs = decodeCostLedgerState(route);
    return {
      tab: coerceTab(vs.tab),
      agent: vs.agent,
    };
  }, []);

  const [activeTab, setActiveTab] = useState<BreakdownTabId>(initialViewState.tab);
  const [selectedAgent, setSelectedAgent] = useState<string | null>(initialViewState.agent);

  // Write range to URL on every change so the window is deep-linkable (ST-05 owns this).
  const handleRangeChange = useCallback((range: DateRange) => {
    setDateRange(range);
    const r = currentRoute();
    navigate({ ...r, ...encodeRange({ from: range.from, to: range.to }) });
  }, []);

  // Write breakdown tab to URL, preserving all other params (including range + agent).
  const handleTabChange = useCallback((tab: BreakdownTabId) => {
    setActiveTab(tab);
    const r = currentRoute();
    const encoded = encodeCostLedgerState({
      agent: selectedAgent,
      tab,
      detailTab: null,
      from: r.from ?? null,
      to: r.to ?? null,
    });
    navigate({ ...r, ...encoded });
  }, [selectedAgent]);

  // Write selected agent to URL, preserving all other params (including range + tab).
  const handleSelectAgent = useCallback((label: string | null) => {
    setSelectedAgent(label);
    const r = currentRoute();
    const encoded = encodeCostLedgerState({
      agent: label,
      tab: activeTab,
      detailTab: null,
      from: r.from ?? null,
      to: r.to ?? null,
    });
    navigate({ ...r, ...encoded });
  }, [activeTab]);

  const {
    data,
    isLoading,
    isError,
    error,
    refetch,
    dataUpdatedAt,
  } = useQuery({
    queryKey: queryKeys.commandBudget(0, dateRange.from, dateRange.to),
    queryFn: ({ signal }) => fetchCommandBudget(0, signal, dateRange.from, dateRange.to),
    staleTime: STALE_BUDGET_MS,
  });

  const freshness = dataUpdatedAt
    ? `Updated ${new Date(dataUpdatedAt).toLocaleTimeString()}`
    : undefined;

  // windowDays is still used by sub-sections for display (section headings, filenames).
  // When from/to are present, derive a rough windowDays for display purposes.
  const windowDays = data?.window_days ?? Math.round(
    (new Date(dateRange.to).getTime() - new Date(dateRange.from).getTime()) / (1000 * 60 * 60 * 24),
  );

  const hasZeroSpend = data != null && data.total_spend_usd == null && data.by_day.every((d) => d.spend_usd === 0);

  return (
    <div className={styles.page}>
      <PageHeader
        title="Cost Ledger"
        {...(isLoading ? { freshness: 'Loading...' } : freshness !== undefined ? { freshness } : {})}
        primaryAction={<CopyLinkButton />}
      />

      <DateRangePicker
        value={dateRange}
        onChange={handleRangeChange}
        label="Window"
        {...(styles.dateRangePicker !== undefined ? { className: styles.dateRangePicker } : {})}
      />

      {isLoading && <LoadingSkeleton />}

      {isError && (
        <ErrorState
          error={error instanceof Error ? error : new Error(String(error))}
          onRetry={() => void refetch()}
        />
      )}

      {data != null && (
        <>
          {/* Honesty first: if all zero, say so */}
          {hasZeroSpend && <EmptySpendState data={data} />}

          {/* Cost honesty / provenance panel — always shown, outside the tab scope */}
          <ProvenancePanel data={data} />

          {/* Breakdown tablist (ST-03 view-state) */}
          <BreakdownTabBar
            activeTab={activeTab}
            tablistId={tablistId}
            onTabChange={handleTabChange}
          />

          {/* Spend over time */}
          <div
            role="tabpanel"
            id="breakdown-panel-by_day"
            aria-labelledby="breakdown-tab-by_day"
            hidden={activeTab !== 'by_day'}
          >
            <SpendOverTimeSection byDay={data.by_day} windowDays={windowDays} />
          </div>

          {/* By agent */}
          <div
            role="tabpanel"
            id="breakdown-panel-by_agent"
            aria-labelledby="breakdown-tab-by_agent"
            hidden={activeTab !== 'by_agent'}
          >
            <ByAgentSection
              byAgent={data.by_agent}
              selectedAgent={selectedAgent}
              onSelectAgent={handleSelectAgent}
            />
          </div>

          {/* By model */}
          <div
            role="tabpanel"
            id="breakdown-panel-by_model"
            aria-labelledby="breakdown-tab-by_model"
            hidden={activeTab !== 'by_model'}
          >
            <ByModelSection byModel={data.by_model} />
          </div>

          {/* By tool / action type */}
          <div
            role="tabpanel"
            id="breakdown-panel-by_tool_type"
            aria-labelledby="breakdown-tab-by_tool_type"
            hidden={activeTab !== 'by_tool_type'}
          >
            <ByToolTypeSection
              byToolType={data.by_tool_type ?? []}
              byDayToolType={data.by_day_tool_type ?? []}
              windowDays={windowDays}
            />
          </div>
        </>
      )}
    </div>
  );
}
