// =============================================================================
// StatusBadge (FE-1)
//
// Renders a RunStatus as: visible text label + icon/shape + status color token.
// Status is NEVER color alone: the label and shape carry meaning independently.
//
// Designed to REPLACE CronView's inline enabledTag / statusGlyph in FE-3.
// Props cover both the run-status use-case (status="running") and the
// enabled/disabled use-case (status="enabled" | "disabled") that CronView uses.
//
// Boundary: accepts an arbitrary string for `rawStatus` (from the cron API
// status: string) and narrows it safely via toRunStatus().
//
// Constraint: uses only --scope-color-* tokens (via StatusBadge.module.css
// data-status-token selectors). No raw hex. No trace-tool tokens here.
// =============================================================================

import React from 'react';
import { toRunStatus, STATUS_LABEL, STATUS_SHAPE, STATUS_TOKEN, type RunStatus } from '../lib/status';
import styles from './StatusBadge.module.css';

// ---------------------------------------------------------------------------
// Shape/icon inline SVG stubs.
// MVP: inline SVG paths keyed by shape name. FE-3 may swap these for a shared
// icon component or a sprite — the prop contract is the shape NAME, not the SVG.
// Only aria-hidden icons here; visible label always carries the meaning.
// ---------------------------------------------------------------------------

const SHAPE_SVG: Record<string, React.ReactElement> = {
  'circle-dot': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <circle cx="12" cy="12" r="10" />
      <circle cx="12" cy="12" r="3" fill="currentColor" stroke="none" />
    </svg>
  ),
  'clock': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <circle cx="12" cy="12" r="10" />
      <polyline points="12 6 12 12 16 14" />
    </svg>
  ),
  'arrow-path': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <path d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
    </svg>
  ),
  'pause-circle': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <circle cx="12" cy="12" r="10" />
      <line x1="10" y1="15" x2="10" y2="9" />
      <line x1="14" y1="15" x2="14" y2="9" />
    </svg>
  ),
  'forward': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <polygon points="5 4 15 12 5 20 5 4" />
      <line x1="19" y1="5" x2="19" y2="19" />
    </svg>
  ),
  'x-circle': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <circle cx="12" cy="12" r="10" />
      <line x1="15" y1="9" x2="9" y2="15" />
      <line x1="9" y1="9" x2="15" y2="15" />
    </svg>
  ),
  'no-symbol': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <circle cx="12" cy="12" r="10" />
      <line x1="4.93" y1="4.93" x2="19.07" y2="19.07" />
    </svg>
  ),
  'check-circle': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <circle cx="12" cy="12" r="10" />
      <polyline points="9 12 11 14 15 10" />
    </svg>
  ),
  'question-mark-circle': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <circle cx="12" cy="12" r="10" />
      <path d="M9.5 9a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.7v.5" />
      <line x1="12" y1="17" x2="12" y2="17.01" />
    </svg>
  ),
  // Enabled/disabled glyphs for CronView FE-3 migration
  'check': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <polyline points="20 6 9 17 4 12" />
    </svg>
  ),
  'minus': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <line x1="5" y1="12" x2="19" y2="12" />
    </svg>
  ),
};

function ShapeIcon({ name }: { name: string }): React.ReactElement {
  const icon = SHAPE_SVG[name];
  if (!icon) {
    // Unknown shape: render a generic bullet (never crash)
    return (
      <svg aria-hidden="true" viewBox="0 0 24 24" fill="currentColor" className={styles.shape}>
        <circle cx="12" cy="12" r="4" />
      </svg>
    );
  }
  return <span className={styles.shape}>{icon}</span>;
}

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface StatusBadgeProps {
  /**
   * A known RunStatus value. When you already have a typed RunStatus, prefer
   * this over rawStatus.
   */
  status?: RunStatus;

  /**
   * A raw string from the cron/obs API status field. Narrowed via toRunStatus()
   * (alias-aware, e.g. 'completed' -> 'complete'); unrecognized values fall back to
   * 'unknown' (muted), never 'failed'. Takes precedence over `status` if both are provided.
   */
  rawStatus?: string | null;

  /**
   * Override the visible label. Defaults to STATUS_LABEL[resolved status].
   * Use for short display (e.g. "Enabled" / "Disabled" in CronView FE-3).
   */
  label?: string;

  /**
   * Override the shape/icon name. Defaults to STATUS_SHAPE[resolved status].
   * Use 'check' / 'minus' for enabled/disabled display in CronView FE-3.
   */
  shapeName?: string;

  /**
   * Whether the associated agent/entity is enabled (for FE-3 CronView
   * enabled/disabled display). When false, applies reduced opacity.
   */
  enabled?: boolean;

  /** Additional CSS class for the wrapper element. Also accepts undefined for CSS module access. */
  className?: string | undefined;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * StatusBadge renders a RunStatus as text + shape + color token.
 * Status is never color alone: the visible label and icon shape both carry meaning.
 *
 * Accepts either a typed RunStatus or a raw API string (narrowed safely).
 * Designed to replace CronView's inline enabledTag/statusGlyph in FE-3.
 */
export function StatusBadge({
  status,
  rawStatus,
  label: labelOverride,
  shapeName: shapeOverride,
  enabled,
  className,
}: StatusBadgeProps): React.ReactElement {
  // Resolve to a typed RunStatus. Unknown/absent -> 'unknown' (muted), never 'failed',
  // so an unrecognized status is not mislabeled as a failure (T-2026-06-16).
  const resolved: RunStatus =
    rawStatus != null
      ? toRunStatus(rawStatus)
      : (status ?? 'unknown');

  const label = labelOverride ?? STATUS_LABEL[resolved];
  const shape = shapeOverride ?? STATUS_SHAPE[resolved];
  const tokenBucket = STATUS_TOKEN[resolved];

  return (
    <span
      className={[styles.badge, className].filter(Boolean).join(' ')}
      data-status-token={tokenBucket}
      data-enabled={enabled === false ? 'false' : undefined}
    >
      <ShapeIcon name={shape} />
      <span>{label}</span>
    </span>
  );
}
