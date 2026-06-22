// =============================================================================
// ToolTypeMark (FE-1)
//
// Renders a TraceToolType bucket as a per-event fill swatch + glyph + optional label.
// Uses --scope-trace-tool-* CSS custom properties via inline style (only on card surfaces).
//
// SANCTIONED consumer of --scope-trace-tool-* tokens: ToolTypeMark, TraceLegend,
// TraceGraph, TraceTimeline ONLY.
//
// CONSTRAINT (FE-0b): trace-tool colors clear 3:1 against the card surface only.
// The parent is responsible for placing this mark on a card-surface background.
//
// STATUS NOTE: ToolTypeMark communicates tool TYPE only.
// A failed find event must show "failed" via StatusBadge, not via the find color.
//
// Taxonomy collapse: accepts taxonomyId (tool-taxonomy-v1) and collapses to the
// 9-bucket TraceToolType via collapseToToolType(). Can also accept a pre-collapsed
// TraceToolType directly.
// =============================================================================

import React from 'react';
import {
  TOOL_TYPE_LABEL,
  TOOL_TYPE_GLYPH,
  TOOL_TYPE_TOKEN,
  collapseToToolType,
  type TraceToolType,
} from '../lib/traceToolType';
import styles from './ToolTypeMark.module.css';

// ---------------------------------------------------------------------------
// Glyph SVG registry (subset used by the 9 buckets)
// operator uses the RESERVED glyph 'person-raise-hand' (frozen by FE-0b)
// ---------------------------------------------------------------------------

const GLYPH_SVG: Record<string, React.ReactElement> = {
  'magnifying-glass': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <circle cx="11" cy="11" r="8" />
      <line x1="21" y1="21" x2="16.65" y2="16.65" />
    </svg>
  ),
  'archive-box': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <polyline points="21 8 21 21 3 21 3 8" />
      <rect x="1" y="3" width="22" height="5" />
      <line x1="10" y1="12" x2="14" y2="12" />
    </svg>
  ),
  'bolt': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2" />
    </svg>
  ),
  'pencil-square': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
      <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
    </svg>
  ),
  'cpu-chip': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <rect x="4" y="4" width="16" height="16" rx="2" />
      <rect x="9" y="9" width="6" height="6" />
      <line x1="9" y1="1" x2="9" y2="4" />
      <line x1="15" y1="1" x2="15" y2="4" />
      <line x1="9" y1="20" x2="9" y2="23" />
      <line x1="15" y1="20" x2="15" y2="23" />
      <line x1="20" y1="9" x2="23" y2="9" />
      <line x1="20" y1="14" x2="23" y2="14" />
      <line x1="1" y1="9" x2="4" y2="9" />
      <line x1="1" y1="14" x2="4" y2="14" />
    </svg>
  ),
  'command-line': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <rect x="2" y="3" width="20" height="18" rx="2" />
      <polyline points="8 9 12 12 8 15" />
      <line x1="12" y1="15" x2="16" y2="15" />
    </svg>
  ),
  'arrow-top-right-on-square': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" />
      <polyline points="15 3 21 3 21 9" />
      <line x1="10" y1="14" x2="21" y2="3" />
    </svg>
  ),
  // RESERVED operator glyph (FE-0b frozen): person-raise-hand
  'person-raise-hand': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      {/* Hand raised / person raising hand glyph */}
      <circle cx="12" cy="4" r="2" />
      <path d="M12 6v5" />
      <path d="M9 8.5L12 11l3-2.5" />
      <path d="M12 11v8" />
      <path d="M9 15l-2 4" />
      <path d="M15 15l2 4" />
      <path d="M15 9l3-1" />
      <path d="M9 9l-3-1" />
    </svg>
  ),
  'cog-6-tooth': (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <path d="M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z" />
      <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
    </svg>
  ),
};

function ToolGlyph({ name }: { name: string }): React.ReactElement {
  const icon = GLYPH_SVG[name];
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
// Props
// ---------------------------------------------------------------------------

export interface ToolTypeMarkProps {
  /**
   * A pre-collapsed TraceToolType bucket. Takes precedence over taxonomyId.
   */
  toolType?: TraceToolType;

  /**
   * A raw tool-taxonomy-v1 id (from ToolRef.taxonomy_id on a trace event).
   * Collapsed to a TraceToolType bucket via the 16->9 map.
   * Unknown ids -> 'system' (never throws).
   * Also accepts undefined so callers can pass optional-chained values directly.
   */
  taxonomyId?: string | null | undefined;

  /**
   * When true, hides the text label (compact swatch+glyph only).
   * Accessible name still comes from the parent or an aria-label.
   */
  compact?: boolean;

  /** Additional CSS class. Also accepts undefined so CSS module access can be passed directly. */
  className?: string | undefined;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * ToolTypeMark renders a trace tool type bucket as a colored swatch + glyph + label.
 *
 * Uses --scope-trace-tool-* tokens via inline style.
 * Must be placed on a card-surface background (FE-0b constraint: 3:1 contrast
 * only against the card surface).
 *
 * Communicates tool TYPE only. Status is owned by StatusBadge.
 */
export function ToolTypeMark({
  toolType,
  taxonomyId,
  compact = false,
  className,
}: ToolTypeMarkProps): React.ReactElement {
  // Resolve to a typed TraceToolType bucket
  const resolved: TraceToolType =
    toolType ?? collapseToToolType(taxonomyId);

  const label = TOOL_TYPE_LABEL[resolved];
  const glyph = TOOL_TYPE_GLYPH[resolved];
  const tokenVar = TOOL_TYPE_TOKEN[resolved];

  return (
    <span
      className={[styles.mark, compact ? styles.compact : '', className].filter(Boolean).join(' ')}
      style={{ color: `var(${tokenVar})` }}
      title={label}
    >
      {/* Color swatch: fill using the trace-tool token */}
      <span
        className={styles.swatch}
        style={{ backgroundColor: `var(${tokenVar})` }}
        aria-hidden="true"
      />
      <ToolGlyph name={glyph} />
      <span className={styles.label}>{label}</span>
    </span>
  );
}
