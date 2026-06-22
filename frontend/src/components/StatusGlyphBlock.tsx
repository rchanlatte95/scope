// =============================================================================
// StatusGlyphBlock (T-2026-06-14-16b)
//
// A solid status-colored square holding a status glyph. Leads a plain square
// card as the visual status language for alert/status panels.
//
// Design spec (scope_frontend_design_doc.md §4):
//   "event/status cards use a status glyph-block (a solid status-colored square
//    holding the status glyph) on a plain square card."
//
// Reuses STATUS_TOKEN + STATUS_SHAPE from lib/status.ts (same glyph/color
// derivation as StatusBadge). The fg/bg token relationship in StatusBadge is
// text-on-status-bg (AA-verified). Here, the STATUS color IS the background —
// so the glyph must use white (light theme) or the dark bg (dark theme) to
// maintain AA contrast. See StatusGlyphBlock.module.css for the token pairing.
//
// Constraint: uses only --scope-color-* tokens. No raw hex.
// =============================================================================

import React from 'react';
import { STATUS_SHAPE, STATUS_TOKEN } from '../lib/status';
import type { RunStatus } from '../lib/status';
import styles from './StatusGlyphBlock.module.css';

// ---------------------------------------------------------------------------
// Glyph SVGs (same set as StatusBadge — inline for portability)
// ---------------------------------------------------------------------------

const SHAPE_SVG: Record<string, React.ReactElement> = {
  'circle-dot': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.5}>
      <circle cx="12" cy="12" r="9" />
      <circle cx="12" cy="12" r="3" fill="currentColor" stroke="none" />
    </svg>
  ),
  'clock': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.5}>
      <circle cx="12" cy="12" r="9" />
      <polyline points="12 7 12 12 16 14" />
    </svg>
  ),
  'arrow-path': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.5}>
      <path d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
    </svg>
  ),
  'pause-circle': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.5}>
      <circle cx="12" cy="12" r="9" />
      <line x1="10" y1="15" x2="10" y2="9" />
      <line x1="14" y1="15" x2="14" y2="9" />
    </svg>
  ),
  'forward': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.5}>
      <polygon points="5 4 15 12 5 20 5 4" />
      <line x1="19" y1="5" x2="19" y2="19" />
    </svg>
  ),
  'x-circle': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.5}>
      <circle cx="12" cy="12" r="9" />
      <line x1="15" y1="9" x2="9" y2="15" />
      <line x1="9" y1="9" x2="15" y2="15" />
    </svg>
  ),
  'no-symbol': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.5}>
      <circle cx="12" cy="12" r="9" />
      <line x1="4.93" y1="4.93" x2="19.07" y2="19.07" />
    </svg>
  ),
  'check-circle': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.5}>
      <circle cx="12" cy="12" r="9" />
      <polyline points="9 12 11 14 15 10" />
    </svg>
  ),
  // Alert/info glyphs for non-RunStatus panels
  'exclamation-triangle': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.5}>
      <path d="M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
      <line x1="12" y1="9" x2="12" y2="13" />
      <line x1="12" y1="17" x2="12.01" y2="17" />
    </svg>
  ),
  'information-circle': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.5}>
      <circle cx="12" cy="12" r="10" />
      <line x1="12" y1="16" x2="12" y2="12" />
      <line x1="12" y1="8" x2="12.01" y2="8" />
    </svg>
  ),
  'check': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.5}>
      <polyline points="20 6 9 17 4 12" />
    </svg>
  ),
};

function GlyphIcon({ name }: { name: string }): React.ReactElement {
  const icon = SHAPE_SVG[name];
  if (!icon) {
    return (
      <svg aria-hidden="true" viewBox="0 0 24 24" fill="currentColor" className={styles.glyph}>
        <circle cx="12" cy="12" r="4" />
      </svg>
    );
  }
  return <span className={styles.glyph}>{icon}</span>;
}

// ---------------------------------------------------------------------------
// StatusToken type (4 core + neutral)
// ---------------------------------------------------------------------------

export type StatusGlyphToken = 'success' | 'warning' | 'danger' | 'info' | 'neutral';

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface StatusGlyphBlockProps {
  /**
   * The status severity bucket. Controls the solid fill color of the square
   * and maps to a --scope-color-* token.
   */
  token: StatusGlyphToken;

  /**
   * The shape/icon name. Defaults to the STATUS_SHAPE for the given RunStatus,
   * or 'information-circle' for info/neutral, 'exclamation-triangle' for warning,
   * 'x-circle' for danger, 'check-circle' for success.
   */
  shapeName?: string;

  /**
   * Optional: derive token + shapeName from a RunStatus. When provided,
   * `token` prop is still required for fallback typing but can use the derived value.
   * Convenience for RunStatus-driven callers.
   */
  runStatus?: RunStatus;

  /** Additional CSS class name for the block element. */
  className?: string;
}

/**
 * Returns the default shape name for a StatusGlyphToken (when no RunStatus provided).
 */
function defaultShape(token: StatusGlyphToken): string {
  switch (token) {
    case 'success': return 'check-circle';
    case 'warning': return 'exclamation-triangle';
    case 'danger':  return 'x-circle';
    case 'info':    return 'information-circle';
    case 'neutral': return 'information-circle';
  }
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * StatusGlyphBlock renders a solid status-colored square holding a status glyph.
 * It LEADS a plain square card as the status language for alert/status panels.
 *
 * Contrast: the glyph uses white (light theme) or dark-bg (dark theme) so the
 * icon reads at WCAG AA against the solid status fill. See contrast.test.ts
 * section 5 for the underlying token verification (status text >= 4.5:1 on bg).
 *
 * Usage: wrap the panel in a `.statusCard` flex row with the glyph-block first,
 * content second. See StatusGlyphBlock.module.css `.statusCard` layout helper.
 */
export function StatusGlyphBlock({
  token,
  shapeName: shapeOverride,
  runStatus,
  className,
}: StatusGlyphBlockProps): React.ReactElement {
  // When a RunStatus is provided, derive token + shape from the lib mapping.
  const resolvedToken: StatusGlyphToken = runStatus != null
    ? (STATUS_TOKEN[runStatus] as StatusGlyphToken)
    : token;

  const resolvedShape = shapeOverride
    ?? (runStatus != null ? STATUS_SHAPE[runStatus] : defaultShape(resolvedToken));

  return (
    <span
      className={[styles.block, className].filter(Boolean).join(' ')}
      data-status-token={resolvedToken}
      aria-hidden="true"
    >
      <GlyphIcon name={resolvedShape} />
    </span>
  );
}
