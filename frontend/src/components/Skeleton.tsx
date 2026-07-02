// =============================================================================
// Skeleton (T-2026-06-15-09)
//
// Shared layout-reserving loading skeleton, extracted from the CostLedgerView
// LoadingSkeleton pattern (CostLedgerView.tsx / CostLedgerView.module.css) so
// Command, Cron, and Trace can reuse it instead of falling back to full-text
// "Loading..." messages.
//
// - SkeletonBlock: one rectangular placeholder (a table row, a card, a graph
//   pane, ...). Respects prefers-reduced-motion (the pulse animation is
//   dropped, never required — see Skeleton.module.css).
// - SkeletonPanel: groups blocks under a single accessible loading
//   announcement (role="status" + aria-label, plus a redundant sr-only text
//   node so assistive tech announces the loading state even where role=status
//   support is inconsistent — mirrors the CostLedgerView convention).
// - SkeletonRows: convenience wrapper for the common "N equal-height rows"
//   shape (tables, lists).
//
// Callers choose block heights/counts that approximate the loaded layout for
// their surface (table header + N rows, a graph pane + a timeline pane, a
// detail page's header/config/history sections, ...) so the skeleton alone
// identifies the page area and its sections per the UX contract's loading
// clause (G5-03): "loading reserves the loaded layout; prefer panel/table/
// section skeletons over full-page spinners."
// =============================================================================

import type { ReactNode } from 'react';
import styles from './Skeleton.module.css';

export interface SkeletonBlockProps {
  /** Block height in px (or any valid CSS length string). Defaults to 20px. */
  height?: number | string;
  /** Optional block width. Defaults to 100% (fills the panel). */
  width?: number | string;
  /** Additional class name. */
  className?: string;
}

/** One rectangular loading placeholder. Purely decorative — aria-hidden. */
export function SkeletonBlock({ height = 20, width, className }: SkeletonBlockProps) {
  return (
    <div
      className={[styles.block, className].filter(Boolean).join(' ')}
      style={{ height, ...(width != null ? { width } : {}) }}
      aria-hidden="true"
    />
  );
}

export interface SkeletonPanelProps {
  /** Accessible label announced to assistive tech, e.g. "Loading cron agents". */
  label: string;
  /** SkeletonBlock children (or any placeholder content) reserving the loaded layout. */
  children: ReactNode;
  /** Additional class name. */
  className?: string | undefined;
}

/**
 * Groups skeleton blocks under one accessible loading announcement.
 * role="status" + aria-label matches the CostLedgerView reference; the
 * sr-only text duplicate hedges against role=status announcement gaps.
 */
export function SkeletonPanel({ label, children, className }: SkeletonPanelProps) {
  return (
    <div className={[styles.panel, className].filter(Boolean).join(' ')} role="status" aria-label={label}>
      {children}
      <span className={styles.srOnly}>{label}&hellip;</span>
    </div>
  );
}

export interface SkeletonRowsProps {
  /** Accessible label announced to assistive tech. */
  label: string;
  /** Number of row blocks to render. Defaults to 4. */
  rows?: number;
  /** Height of each row block in px. Defaults to 36px (table-row scale). */
  rowHeight?: number;
  /** When set, renders one extra (slightly shorter) block first, standing in for a header/caption row. */
  withHeader?: boolean;
  /** Additional class name. */
  className?: string | undefined;
}

/** Convenience: a stack of equal-height row placeholders (tables, lists). */
export function SkeletonRows({ label, rows = 4, rowHeight = 36, withHeader = false, className }: SkeletonRowsProps) {
  return (
    <SkeletonPanel label={label} className={className}>
      {withHeader && <SkeletonBlock height={Math.round(rowHeight * 0.7)} />}
      {Array.from({ length: rows }, (_, i) => (
        <SkeletonBlock key={i} height={rowHeight} />
      ))}
    </SkeletonPanel>
  );
}
