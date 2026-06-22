// =============================================================================
// CostBillingClassBadge (T-2026-06-19-01 Phase 4)
//
// Renders a billing-class badge for a cost_billing_class wire value:
//   "billed"          -> label "Billed"         (a real per-token charge)
//   "shadow_would_be" -> label "Would-be"        (reference cost, NOT an invoice)
//   "unclassified"    -> renders NOTHING          (backward-compatible default)
//   absent / null     -> renders NOTHING          (same as unclassified)
//   unknown future    -> renders NOTHING          (forward-compatible; never crash)
//
// Design contract (binding):
//   - Status is TEXT + SHAPE + COLOR, never color alone (WCAG 1.4.1).
//   - --scope-color-* tokens only; NO raw hex.
//   - Corner radius: --scope-radius-sm (2px) on the chip — it is a small control.
//   - No left accent stripe.
//   - "Would-be" badge carries accessible title/aria clarifying it is a reference
//     cost, NOT an invoice, per the task spec.
//   - Non-interactive: no keyboard target needed (display-only chip).
// =============================================================================

import React from 'react';
import type { CostBillingClass } from '../api/traceApi';
import styles from './CostBillingClassBadge.module.css';

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface CostBillingClassBadgeProps {
  /**
   * The billing-class value from the wire. Absent/null/undefined -> renders nothing.
   * Unknown future values also render nothing (forward-compatible).
   */
  billingClass: CostBillingClass | null | undefined;
}

// ---------------------------------------------------------------------------
// Label and aria text maps
// ---------------------------------------------------------------------------

/** Visible label for the badge. Maps the canonical values. */
const BILLING_CLASS_LABEL: Record<string, string> = {
  billed: 'Billed',
  shadow_would_be: 'Would-be',
};

/**
 * Full accessible description for the badge.
 * The "Would-be" value requires an honest tooltip/aria clarification that it is
 * a reference cost at API rates, NOT an actual invoice (per task spec: "make that
 * distinction honest and clear").
 */
const BILLING_CLASS_ARIA: Record<string, string> = {
  billed: 'Billed: this cost is an actual per-token charge to the API account',
  shadow_would_be:
    'Would-be cost: a reference figure at API rates for a subscription session. This is NOT an invoice and may not translate 1:1 to actual charges.',
};

/** data-billing attribute value for CSS/test targeting. */
const BILLING_CLASS_DATA: Record<string, string> = {
  billed: 'billed',
  shadow_would_be: 'shadow',
};

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * CostBillingClassBadge renders a small chip conveying whether a cost is a real
 * charge ("Billed") or a reference figure ("Would-be"). Renders nothing for
 * "unclassified", absent, or unknown values (backward-compatible default).
 *
 * The badge is display-only (not interactive). "Would-be" carries an accessible
 * title attribute and aria-label so screen readers can read the full honest
 * distinction, not just the abbreviated chip text.
 */
export function CostBillingClassBadge({ billingClass }: CostBillingClassBadgeProps): React.ReactElement | null {
  // Absent, null, undefined, "unclassified", or any unknown future value -> render nothing.
  if (billingClass == null) return null;
  const label = BILLING_CLASS_LABEL[billingClass];
  if (label === undefined) return null; // "unclassified" or unknown future value

  const ariaLabel = BILLING_CLASS_ARIA[billingClass] ?? label;
  const dataAttr = BILLING_CLASS_DATA[billingClass] ?? 'unknown';

  return (
    <span
      className={styles.badge}
      data-billing={dataAttr}
      aria-label={ariaLabel}
      title={ariaLabel}
      data-testid="cost-billing-class-badge"
    >
      {label}
    </span>
  );
}
