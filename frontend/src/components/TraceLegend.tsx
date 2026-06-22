// =============================================================================
// TraceLegend (FE-1)
//
// The color key for all 9 TraceToolType buckets.
// Renders each bucket's swatch, glyph, and label.
//
// SANCTIONED consumer of --scope-trace-tool-* tokens: ToolTypeMark, TraceLegend,
// TraceGraph, TraceTimeline ONLY.
//
// CONSTRAINT (FE-0b): Must render on a card-surface background.
// STATUS NOTE: Legend communicates tool TYPE only. Status = StatusBadge.
// =============================================================================

import React from 'react';
import {
  TRACE_TOOL_TYPES,
  TOOL_TYPE_LABEL,
  TOOL_TYPE_TOKEN,
  type TraceToolType,
} from '../lib/traceToolType';
import { ToolTypeMark } from './ToolTypeMark';
import styles from './TraceLegend.module.css';

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface TraceLegendProps {
  /**
   * Subset of TraceToolType buckets to show in this legend instance.
   * Defaults to all 9 buckets.
   */
  buckets?: readonly TraceToolType[];

  /**
   * Whether to show the "Tool types" caption above the legend entries.
   * Defaults to true.
   */
  showCaption?: boolean;

  /** Additional CSS class. */
  className?: string;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * TraceLegend renders the color key for TraceToolType buckets.
 * Shows swatch + glyph + label for each bucket.
 *
 * Sanctioned consumer of --scope-trace-tool-* tokens.
 * Must be placed on a card-surface background (FE-0b 3:1 contrast constraint).
 */
export function TraceLegend({
  buckets = TRACE_TOOL_TYPES,
  showCaption = true,
  className,
}: TraceLegendProps): React.ReactElement {
  return (
    <div
      className={[styles.legend, className].filter(Boolean).join(' ')}
      role="region"
      aria-label="Trace tool type legend"
    >
      {showCaption && (
        <div className={styles.caption} aria-hidden="true">
          Tool types
        </div>
      )}
      {buckets.map((bucket) => {
        const label = TOOL_TYPE_LABEL[bucket];
        const tokenVar = TOOL_TYPE_TOKEN[bucket];
        return (
          <span key={bucket} className={styles.entry}>
            <span
              className={styles.swatch}
              style={{ backgroundColor: `var(${tokenVar})` }}
              aria-hidden="true"
            />
            <span className={styles.glyph}>
              <ToolTypeMark toolType={bucket} compact />
            </span>
            <span>{label}</span>
          </span>
        );
      })}
    </div>
  );
}
