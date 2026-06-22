// =============================================================================
// EmptyState (FE-1)
//
// variant: 'governance' | 'neutral'
//
// Governance variant: text + glyph ONLY.
//   - No decorative img/svg (images are explicitly disallowed).
//   - Used for security/cost/failed states where cheerful illustrations are
//     inappropriate ("Do not use cheerful illustrations for security, cost, or
//     failed agent states." — design doc section 9).
//   - Vitest asserts the governance variant emits NO decorative image element.
//
// Neutral variant: text + glyph (optional decorative visual still no img/svg).
//   - For benign empty states (e.g. no cron agents created yet).
//
// Uses only --scope-color-* tokens. No raw hex.
// =============================================================================

import React from 'react';
import styles from './EmptyState.module.css';

// ---------------------------------------------------------------------------
// Glyph definitions (functional icons, aria-hidden — label carries meaning)
// ---------------------------------------------------------------------------

/** Available glyph names for EmptyState. All are functional (not decorative). */
export type EmptyStateGlyph =
  | 'shield-exclamation'  // governance: security alert
  | 'currency-dollar-off' // governance: no cost data / unmetered
  | 'x-circle'            // governance: failure / error
  | 'no-symbol'           // governance: blocked / denied
  | 'inbox'               // neutral: no items
  | 'table-cells'         // neutral: no table data
  | 'clock'               // neutral: no runs yet
  | 'magnifying-glass';   // neutral: no results found

const GLYPH_SVG: Record<EmptyStateGlyph, React.ReactElement> = {
  'shield-exclamation': (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
      <path strokeLinecap="round" strokeLinejoin="round" d="M12 2L3 6.5V11c0 5.25 3.75 9.75 9 11 5.25-1.25 9-5.75 9-11V6.5L12 2z" />
      <line x1="12" y1="8" x2="12" y2="12" />
      <circle cx="12" cy="16" r="0.5" fill="currentColor" />
    </svg>
  ),
  'currency-dollar-off': (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
      <line x1="2" y1="2" x2="22" y2="22" />
      <path d="M12 2v2M12 20v2M8 8H6a2 2 0 0 0 0 4h2M16 16h2a2 2 0 0 0 0-4h-2" />
    </svg>
  ),
  'x-circle': (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
      <circle cx="12" cy="12" r="10" />
      <line x1="15" y1="9" x2="9" y2="15" />
      <line x1="9" y1="9" x2="15" y2="15" />
    </svg>
  ),
  'no-symbol': (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
      <circle cx="12" cy="12" r="10" />
      <line x1="4.93" y1="4.93" x2="19.07" y2="19.07" />
    </svg>
  ),
  'inbox': (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
      <polyline points="22 12 16 12 14 15 10 15 8 12 2 12" />
      <path d="M5.45 5.11L2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z" />
    </svg>
  ),
  'table-cells': (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
      <rect x="3" y="3" width="18" height="18" rx="2" />
      <line x1="3" y1="9" x2="21" y2="9" />
      <line x1="3" y1="15" x2="21" y2="15" />
      <line x1="9" y1="3" x2="9" y2="21" />
      <line x1="15" y1="3" x2="15" y2="21" />
    </svg>
  ),
  'clock': (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
      <circle cx="12" cy="12" r="10" />
      <polyline points="12 6 12 12 16 14" />
    </svg>
  ),
  'magnifying-glass': (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
      <circle cx="11" cy="11" r="8" />
      <line x1="21" y1="21" x2="16.65" y2="16.65" />
    </svg>
  ),
};

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface EmptyStateProps {
  /**
   * Variant:
   * - 'governance': security/cost/failed states. Text + glyph ONLY.
   *   No decorative image. No cheerful illustration.
   * - 'neutral': benign empty states. Text + glyph. No img/svg decorations.
   */
  variant: 'governance' | 'neutral';

  /**
   * The functional glyph to display. Required — even in empty states,
   * a shape communicates state without relying on color alone.
   */
  glyph: EmptyStateGlyph;

  /** The heading text explaining the empty state. */
  heading: string;

  /**
   * Optional description providing context and what to do next.
   * Per UX contract: "Empty states explain why and give the next safe action."
   */
  description?: string;

  /**
   * Optional next action (one button or anchor).
   * Governance variant: use only for genuinely safe next steps.
   */
  action?: React.ReactNode;

  /** Additional CSS class. */
  className?: string;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * EmptyState renders a governed empty state.
 *
 * Governance variant: text + glyph ONLY. No decorative img/svg.
 * Neutral variant: text + glyph. No heavy illustrations.
 *
 * Vitest asserting governance variant emits no <img> or role="img" decorative
 * elements is in EmptyState.test.ts (validates pure contract, not DOM render).
 */
export function EmptyState({
  variant,
  glyph,
  heading,
  description,
  action,
  className,
}: EmptyStateProps): React.ReactElement {
  const glyphElement = GLYPH_SVG[glyph];

  return (
    <div
      className={[styles.emptyState, className].filter(Boolean).join(' ')}
      data-variant={variant}
      // Use role="status" for live-region semantics in governance contexts
      role={variant === 'governance' ? 'status' : undefined}
    >
      {/* Functional glyph — NOT decorative (aria-hidden on the SVG; heading carries meaning) */}
      <div className={styles.glyph}>{glyphElement}</div>

      {/* Heading */}
      <p className={styles.heading}>{heading}</p>

      {/* Description */}
      {description != null && description !== '' && <p className={styles.description}>{description}</p>}

      {/* Optional action */}
      {action != null ? <div className={styles.action}>{action}</div> : null}
    </div>
  );
}
