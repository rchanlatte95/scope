// =============================================================================
// RisksPanel (PP-10, Proof Paths)
//
// Renders heuristic risk flags from ImpactAnalyzer.DetectRisks as a list of
// callouts. Each callout shows:
//   - A kind label (text + shape glyph-block; never color alone)
//   - The analyzer message VERBATIM (no rewording, no added interpretation)
//
// Panel framing: "Heuristic risk flags" (EDGE-HON-5). The SQL risk flag is
// a naive substring match (SourceText.Contains("sql") && Contains("\""),
// ImpactAnalyzer.cs ~286) and can false-positive. This panel MUST remain
// visibly distinct from the compiler-proven proof paths shown elsewhere.
// Never frame these as "Proven risks" or add certainty language.
//
// Design contract:
//   - --scope-* tokens only (no raw hex).
//   - Sharp corners (radius 0), 4/8px grid.
//   - WCAG 2.2 AA; status = text + shape + color (never color alone).
//   - Hidden entirely when risks is empty.
// =============================================================================

import React from 'react';
import type { ImpactRisk } from '../../api/impactApi';
import { kindLabel, kindShape, PANEL_TITLE } from './riskKind';
import styles from './RisksPanel.module.css';

// ---------------------------------------------------------------------------
// Inline SVG shapes (aria-hidden; text label always carries the meaning)
// ---------------------------------------------------------------------------

function TriangleIcon(): React.ReactElement {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      className={styles.shapeIcon}
    >
      <path d="M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
      <line x1="12" y1="9" x2="12" y2="13" />
      <line x1="12" y1="17" x2="12.01" y2="17" />
    </svg>
  );
}

function DiamondIcon(): React.ReactElement {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      className={styles.shapeIcon}
    >
      <polygon points="12 2 22 12 12 22 2 12" />
      <line x1="12" y1="8" x2="12" y2="13" />
      <line x1="12" y1="16" x2="12.01" y2="16" />
    </svg>
  );
}

function ShapeGlyph({ shape }: { shape: string }): React.ReactElement {
  if (shape === 'diamond') {
    return <DiamondIcon />;
  }
  return <TriangleIcon />;
}

// ---------------------------------------------------------------------------
// RiskCallout — one risk entry
// ---------------------------------------------------------------------------

interface RiskCalloutProps {
  risk: ImpactRisk;
}

function RiskCallout({ risk }: RiskCalloutProps): React.ReactElement {
  const label = kindLabel(risk.kind);
  const shape = kindShape(risk.kind);

  return (
    <li className={styles.callout}>
      {/* Glyph-block: solid status-colored square + glyph (WCAG: text+shape+color) */}
      <span
        className={styles.glyphBlock}
        aria-hidden="true"
        data-risk-shape={shape}
      >
        <ShapeGlyph shape={shape} />
      </span>

      {/* Content: kind label + verbatim message */}
      <div className={styles.calloutContent}>
        <span className={styles.kindLabel}>{label}</span>
        <span className={styles.message}>{risk.message}</span>
      </div>
    </li>
  );
}

// ---------------------------------------------------------------------------
// RisksPanel — public component
// ---------------------------------------------------------------------------

export interface RisksPanelProps {
  /** Heuristic risk flags from ImpactAnalyzer.DetectRisks. Hidden when empty. */
  risks: ImpactRisk[];
}

/**
 * RisksPanel renders heuristic risk flags from the impact analyzer.
 *
 * Framing is intentionally "Heuristic risk flags" (EDGE-HON-5): these flags are
 * produced by pattern matching (e.g. SQL substring match), not compiler-proven
 * analysis. They must stay visually distinct from proof paths.
 *
 * Hidden when risks is empty.
 */
export function RisksPanel({ risks }: RisksPanelProps): React.ReactElement | null {
  if (risks.length === 0) {
    return null;
  }

  return (
    <section className={styles.panel} aria-labelledby="risks-panel-heading">
      <header className={styles.panelHeader}>
        <h2 id="risks-panel-heading" className={styles.panelTitle}>
          {PANEL_TITLE}
        </h2>
        <p className={styles.panelNote}>
          Flags are heuristic, not compiler-proven. Some may be false positives
          (for example, SQL detection uses source-text pattern matching).
        </p>
      </header>

      <ul className={styles.list} aria-label="Risk flags">
        {risks.map((risk, index) => (
          <RiskCallout
            key={`${risk.kind}-${index.toString()}`}
            risk={risk}
          />
        ))}
      </ul>
    </section>
  );
}
