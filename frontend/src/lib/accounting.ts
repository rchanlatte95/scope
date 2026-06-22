// =============================================================================
// Accounting formatting + source-badge logic (owner feedback items 6 / 6a / 6b / 10).
//
// Pure functions (no DOM, no React) so they are unit-testable in the node-environment
// vitest setup (vitest.config.ts includes only src/**/*.test.ts). The accounting
// PANEL (AccountingPanel.tsx) is presentation over these; the honesty rules ("never
// silently wrong", item 6) live here where they can be asserted.
//
// Contract source: design-scope-trace-v1.xml (Money / TokenBlock / source tags
// exact|wire|estimate|unknown / pricing_version). Server projection:
// src/Scope.Core/Telemetry/Trace/. Mirrored types: ../api/traceApi.ts.
// =============================================================================

import type { CostBreakdownComponent, MoneyAggregate, TokenAggregate, Coverage } from '../api/traceApi';

/** The four contract source tags, strongest (most specific) first. */
export type SourceTag = 'exact' | 'wire' | 'estimate' | 'unknown';

/** A rendered source badge: the tag, a human label, and a CSS var ref accent color. */
export interface SourceBadge {
  tag: SourceTag;
  label: string;
  /**
   * CSS custom property reference resolving to a --scope-color-* token.
   * Maps Money.source 4-value to status semantics (FE-4a):
   *   exact   -> var(--scope-color-success)  (host/provider-measured; certainty = success)
   *   wire    -> var(--scope-color-info)     (provider wire usage; informational)
   *   estimate -> var(--scope-color-warning) (computed estimate; uncertain = warning)
   *   unknown -> var(--scope-color-text-muted) (no value known; dimmed/neutral)
   */
  color: string;
}

// Status-semantic mapping for the 4 source tags.
// Each source tag maps to a --scope-color-* product token (NOT raw hex).
// This aligns with SourceBadge.module.css's .sourceExact/.sourceWire etc. classes.
// Typed as Partial so callers can safely use ?? fallback for unknown runtime tags.
const SOURCE_BADGES: Readonly<Partial<Record<SourceTag, SourceBadge>>> = {
  exact:    { tag: 'exact',    label: 'exact',    color: 'var(--scope-color-success)'    },
  wire:     { tag: 'wire',     label: 'wire',     color: 'var(--scope-color-info)'       },
  estimate: { tag: 'estimate', label: 'estimate', color: 'var(--scope-color-warning)'    },
  unknown:  { tag: 'unknown',  label: 'unknown',  color: 'var(--scope-color-text-muted)' },
};

// eslint-disable-next-line @typescript-eslint/no-non-null-assertion -- 'unknown' is always in the SOURCE_BADGES literal above; Partial typing is for runtime safety of other keys
const UNKNOWN_BADGE = SOURCE_BADGES.unknown!;

/**
 * Resolves a source tag to its render badge. An unrecognized/missing tag resolves to
 * the "unknown" badge rather than erroring (contract: unknown enum value -> fallback).
 */
export function sourceBadge(source: string | null | undefined): SourceBadge {
  if (source == null || source === '') return UNKNOWN_BADGE;
  return SOURCE_BADGES[source as SourceTag] ?? UNKNOWN_BADGE;
}

/**
 * Formats a token count for display, grouped with thousands separators. Null/undefined
 * renders as an em-dash sentinel (never "0", which would imply a measured zero).
 */
export function formatTokens(value: number | null | undefined): string {
  if (value == null) return '—';
  return value.toLocaleString('en-US');
}

/**
 * The smallest magnitude representable at the sub-cent (4dp) branch. A non-zero cost whose
 * magnitude is below this rounds to $0.0000 at 4dp, which reads as a measured zero. Such a value
 * is floored to a "<$0.01" treatment instead so a real sub-cent cost NEVER renders $0.00
 * (T-2026-06-20-20 Piece 1).
 */
const SUB_CENT_FLOOR = 0.00005; // rounds to $0.0000 at 4dp below this

/** Options for {@link formatMoneyUsd}. */
export interface FormatMoneyOpts {
  /**
   * The string returned for a null/undefined (unavailable) value. Defaults to "unavailable".
   * Callers with a different empty-value contract (e.g. an em-dash glyph "—" for a measured-absent
   * cell, or a provenance chip rendered separately) pass their own sentinel. This NEVER returns
   * "$0.00" for a null value (cost honesty: null is not a measured zero).
   */
  unavailable?: string;
}

/**
 * THE single shared USD money formatter. All three FE money formatters (accounting `formatCost`,
 * the Cost Ledger `formatUsd`, and the `CostProvenanceChip` `formatCostUsd`) delegate here so the
 * tiered-precision + sub-cent-floor + honesty rules live in ONE place and cannot drift
 * (T-2026-06-20-20).
 *
 * Tiered precision (owner-ratified rule):
 *   - a value > $0.01 (headline / aggregate magnitude) displays at 2 dp ("$4.63");
 *   - a value in (0, $0.01] (routine per-row / per-tool-call magnitude) keeps finer 4 dp precision
 *     so $0.0042 does not collapse to "$0.00";
 *   - a non-zero value too small to show at 4 dp (|v| < SUB_CENT_FLOOR) renders "<$0.01" (or
 *     ">-$0.01" for a tiny negative) rather than the false "$0.0000";
 *   - an exact, measured 0 renders "$0.00";
 *   - a null/undefined (unavailable) value renders the {@link FormatMoneyOpts.unavailable} sentinel
 *     (default "unavailable"), NEVER "$0.00".
 *
 * Round-once-from-raw: callers MUST pass a RAW (full-precision) value and NEVER a value derived by
 * summing already-rounded rows. Formatting is the ONLY place rounding happens, so a displayed total
 * formatted from a raw sum reconciles exactly with a headline formatted from the raw total.
 */
export function formatMoneyUsd(value: number | null | undefined, opts?: FormatMoneyOpts): string {
  if (value == null) return opts?.unavailable ?? 'unavailable';
  if (value === 0) return '$0.00';
  // A real but tiny sub-cent cost must not render as $0.00 / $0.0000 (false zero).
  if (Math.abs(value) < SUB_CENT_FLOOR) return value > 0 ? '<$0.01' : '>-$0.01';
  // Tiered: aggregate-magnitude (>$0.01) at 2dp; sub-cent at 4dp.
  const decimals = Math.abs(value) > 0.01 ? 2 : 4;
  return `$${value.toLocaleString('en-US', { minimumFractionDigits: decimals, maximumFractionDigits: decimals })}`;
}

/**
 * Formats a USD cost. The cost is rendered ONLY when the source carries an actual value
 * AND a value is present; otherwise the cost is honestly "unavailable" (item 6: never a
 * fake number). source "unknown" or a null value both yield the unavailable sentinel.
 *
 * Delegates the numeric formatting (tiered precision + sub-cent floor) to the shared
 * {@link formatMoneyUsd} so the honesty rules cannot drift across money surfaces.
 */
export function formatCost(money: Pick<MoneyAggregate, 'value_usd' | 'source'> | null | undefined): string {
  if (!money || money.source === 'unknown' || money.value_usd == null) return 'unavailable';
  return formatMoneyUsd(money.value_usd);
}

/**
 * Whether a cost block should render a real number at all. False when no pricing was
 * configured / no value is known -- the caller then shows "unavailable" and (if a stamp
 * is absent) explains WHY rather than implying a measured zero.
 */
export function costAvailable(money: Pick<MoneyAggregate, 'value_usd' | 'source'> | null | undefined): boolean {
  return !!money && money.source !== 'unknown' && money.value_usd != null;
}

/**
 * The "prices as of <date>" stamp text for a header. Returns null when pricing_version is
 * absent (no pricing configured): the header then labels cost "unavailable" and omits a
 * fabricated date. The date is formatted in the chosen display zone (item 9 carries over).
 */
export function pricesAsOfLabel(
  pricingVersion: string | null | undefined,
  formatDate: (ts: string) => string,
): string | null {
  if (pricingVersion == null || pricingVersion === '') return null;
  return `prices as of ${formatDate(pricingVersion)}`;
}

/**
 * Formats a duration in milliseconds as a compact human string. Sub-second stays in ms;
 * larger values use s / m / h. Null/undefined -> em-dash. A measured 0 renders "0 ms".
 */
export function formatMs(ms: number | null | undefined): string {
  if (ms == null) return '—';
  if (ms < 1000) return `${String(ms)} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(s < 10 ? 2 : 1)} s`;
  const m = s / 60;
  if (m < 60) return `${m.toFixed(1)} m`;
  return `${(m / 60).toFixed(2)} h`;
}

/**
 * Builds the human coverage caption "k of n measured" for an aggregate block, so the
 * dashboard can render "at least N tokens (1 of 21 tasks measured)" instead of a
 * silently-wrong total (item 6 honesty; design AggregateRules/coverage).
 */
export function coverageCaption(coverage: Coverage | null | undefined, noun = 'measured'): string | null {
  if (!coverage) return null;
  const total = coverage.known + coverage.unknown;
  if (total === 0) return null;
  return `${String(coverage.known)} of ${String(total)} ${noun}`;
}

/**
 * True when a token aggregate's value is a LOWER BOUND rather than a complete total:
 * some constituents are unknown, so the rendered total should read "at least N". This is
 * the visible-honesty signal the contract's coverage counts exist for.
 */
export function isLowerBound(block: Pick<TokenAggregate, 'coverage'> | Pick<MoneyAggregate, 'coverage'> | null | undefined): boolean {
  return !!block && block.coverage.known > 0 && block.coverage.unknown > 0;
}

// ---------------------------------------------------------------------------
// Cost breakdown formatting (T-2026-06-15-11b)
// ---------------------------------------------------------------------------

/** Maps a breakdown component kind to a human label for display. */
const KIND_LABELS: Readonly<Record<CostBreakdownComponent['kind'], string>> = {
  input:       'input tokens',
  output:      'output tokens',
  cache_read:  'cache read tokens',
  cache_write: 'cache write tokens',
};

/**
 * Formats a single CostBreakdownComponent as a human-readable line.
 *
 * Pattern: "172 output tokens x $5.00 / 1M = $0.000860"
 *
 * The rate is shown as a dollar amount per million tokens. The cost is shown
 * with 6 significant decimals so sub-cent values remain readable. This is a
 * PURE function (no DOM / no React) and is unit-tested in accounting.test.ts.
 */
export function formatBreakdownLine(component: CostBreakdownComponent): string {
  const label = KIND_LABELS[component.kind];
  const rateDollars = component.rate_usd_per_mtok.toLocaleString('en-US', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
  // Keep 6 decimal places for the cost so sub-cent values are legible.
  const costStr = `$${component.cost_usd.toLocaleString('en-US', {
    minimumFractionDigits: 6,
    maximumFractionDigits: 6,
  })}`;
  return `${component.tokens.toLocaleString('en-US')} ${label} x $${rateDollars} / 1M = ${costStr}`;
}

/**
 * Sums cost_usd across all components in a breakdown. Returns 0 when the
 * components array is empty. Used to verify total_usd == sum(components).
 */
export function sumBreakdownComponents(components: readonly CostBreakdownComponent[]): number {
  return components.reduce((acc, c) => acc + c.cost_usd, 0);
}

// ---------------------------------------------------------------------------
// Token breakdown formatting (T-2026-06-15-12)
// ---------------------------------------------------------------------------

/**
 * True when a TokenAggregate has enough components to show a breakdown tooltip.
 * Requires at least one of: input, output, cache_creation, cache_read to be non-null.
 * When only total is present (exact-only with no split), returns false -> plain number, no hover.
 */
export function hasTokenBreakdown(tokens: Pick<TokenAggregate, 'input' | 'output' | 'cache_creation' | 'cache_read'> | null | undefined): boolean {
  if (tokens == null) return false;
  return (
    tokens.input != null ||
    tokens.output != null ||
    tokens.cache_creation != null ||
    tokens.cache_read != null
  );
}

/** Maps a token breakdown kind to a human label for display. */
const TOKEN_KIND_LABELS: Readonly<Record<string, string>> = {
  input:       'input tokens',
  output:      'output tokens',
  cache_read:  'cache_read tokens',
  cache_write: 'cache_write tokens',
};

/**
 * Formats a single token breakdown line for display in the tooltip.
 *
 * Pattern: "47,864 cache_write tokens"
 *
 * This is a PURE function (no DOM / no React) and is unit-tested in accounting.test.ts.
 * Kind is one of: input | output | cache_read | cache_write.
 */
export function formatTokenLine(kind: string, count: number): string {
  const label = TOKEN_KIND_LABELS[kind] ?? `${kind} tokens`;
  return `${count.toLocaleString('en-US')} ${label}`;
}

/**
 * Sums the token components that should be included in the total.
 * Returns null when no components are available (no split to sum).
 * Used to assert that summed components match the displayed total.
 */
export function sumTokenComponents(
  tokens: Pick<TokenAggregate, 'input' | 'output' | 'cache_creation' | 'cache_read'>,
): number {
  return (tokens.input ?? 0) + (tokens.output ?? 0) + (tokens.cache_creation ?? 0) + (tokens.cache_read ?? 0);
}

/**
 * Auditable coherence check the panel can SURFACE (item 6a/6b): per-task sums + the
 * unattributed remainder must equal the header totals. Returns the two sides and whether
 * they match, so the UI can show a green "balanced" check or a red mismatch a user can
 * see rather than trust blindly. Null values are treated as 0 for the arithmetic (an
 * unknown token/busy contributes nothing), exactly as the server partition does.
 */
export interface CoherenceCheck {
  totalTokens: number;
  sumTokens: number;
  totalBusyMs: number;
  sumBusyMs: number;
  tokensBalanced: boolean;
  busyBalanced: boolean;
  balanced: boolean;
}

export function checkCoherence(
  totals: { tokens: { total?: number | null }; busy_ms_sum: number },
  byTask: readonly { tokens: { total?: number | null }; busy_ms: number }[],
  unattributed: { tokens: { total?: number | null }; busy_ms: number },
): CoherenceCheck {
  const totalTokens = totals.tokens.total ?? 0;
  const sumTokens =
    byTask.reduce((acc, r) => acc + (r.tokens.total ?? 0), 0) + (unattributed.tokens.total ?? 0);
  const totalBusyMs = totals.busy_ms_sum;
  const sumBusyMs = byTask.reduce((acc, r) => acc + r.busy_ms, 0) + unattributed.busy_ms;
  const tokensBalanced = totalTokens === sumTokens;
  const busyBalanced = totalBusyMs === sumBusyMs;
  return {
    totalTokens,
    sumTokens,
    totalBusyMs,
    sumBusyMs,
    tokensBalanced,
    busyBalanced,
    balanced: tokensBalanced && busyBalanced,
  };
}
