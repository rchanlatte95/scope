// =============================================================================
// traceLimits.ts -- helpers for surfacing free-tier observability limit notices
// (T-2026-06-13-34). All logic is pure so it can be unit-tested without DOM.
// =============================================================================

import type { ObservabilityLimits } from '../api/traceApi';

/** Describes an honest, non-destructive notice to surface in the trace dashboard. */
export interface LimitNotice {
  /** Unique key for React rendering. */
  key: string;
  /** Short plain-text message, never claiming data was deleted. */
  message: string;
}

/**
 * Derives the set of limit notices to display from an optional limits object.
 *
 * Rules:
 * - If `limits` is undefined/null (older server or paid tier with no flags set), returns [].
 * - `sessions_capped` true -> notice using `max_dashboard_sessions` (falls back to "some").
 * - `retention_window_applied` true -> notice using `observability_retention_days` (falls back to "some").
 * - `audit_export_allowed` false -> notice that audit export is a paid feature.
 * - Paid/unlimited: no flags set and no non-null cap -> returns [].
 *
 * Wording never claims data was deleted. Always says "retained locally but not shown here."
 */
export function deriveLimitNotices(limits: ObservabilityLimits | undefined | null): LimitNotice[] {
  if (!limits) return [];

  const notices: LimitNotice[] = [];

  if (limits.sessions_capped) {
    const n = limits.max_dashboard_sessions;
    const countStr = n != null ? String(n) : 'some';
    notices.push({
      key: 'sessions-capped',
      message: `Showing the ${countStr} most recent sessions. Older sessions are retained locally but outside the free-tier dashboard view.`,
    });
  }

  if (limits.retention_window_applied) {
    const days = limits.observability_retention_days;
    const daysStr = days != null ? String(days) : 'some';
    notices.push({
      key: 'retention-window',
      message: `Free tier shows the last ${daysStr} days. Older local trace data is retained but not shown here.`,
    });
  }

  if (!limits.audit_export_allowed) {
    notices.push({
      key: 'audit-export',
      message:
        'Audit export and HTML trace artifacts are a paid feature. Console audit remains available via the Scope CLI.',
    });
  }

  return notices;
}
