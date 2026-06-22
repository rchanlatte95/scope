import { sourceBadge } from '../lib/accounting';
import styles from './SourceBadge.module.css';

// =============================================================================
// SourceBadge (owner feedback item 6: "never silently wrong").
//
// A small pill labeling WHERE a token/cost number came from: exact | wire |
// estimate | unknown. Every accounting number is paired with one of these so a user
// can tell a provider-measured value apart from a computed estimate or an unknown.
// The color/label come from lib/accounting.sourceBadge (the single source, unit-tested).
// Status-color routing: each source tag maps to a --scope-color-* CSS class so colors
// respect both light and dark themes without raw hex in inline styles.
// =============================================================================

interface SourceBadgeProps {
  source: string | null | undefined;
  /** Optional title override for the tooltip; defaults to a per-tag explanation. */
  title?: string;
}

const EXPLAIN: Record<string, string> = {
  exact: 'exact: host/provider-reported usage',
  wire: 'wire: provider wire-call usage (metered)',
  estimate: 'estimate: computed from token estimates and configured prices',
  unknown: 'unknown: no value available; not counted as zero',
};

/** Maps source tag to its CSS modifier class (status-color side). */
const SOURCE_CLASS: Record<string, string> = {
  exact: styles.sourceExact ?? '',
  wire: styles.sourceWire ?? '',
  estimate: styles.sourceEstimate ?? '',
  unknown: styles.sourceUnknown ?? '',
};

export default function SourceBadge({ source, title }: SourceBadgeProps) {
  const badge = sourceBadge(source);
  const colorClass = SOURCE_CLASS[badge.tag] ?? styles.sourceUnknown;
  return (
    <span
      className={`${styles.badge ?? ''} ${colorClass ?? ''}`}
      title={title ?? EXPLAIN[badge.tag]}
    >
      {badge.label}
    </span>
  );
}
