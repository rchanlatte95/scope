// =============================================================================
// PageHeader (FE-1)
//
// Standard page header area used by all Scope SPA pages.
//
// Per design-doc + uxContract:
//   - One clear H1 (area name), 24–32px.
//   - Workspace / repo context (mono facts).
//   - Current location / breadcrumb.
//   - Optional data freshness label.
//   - Optional status chips (summary governance state).
//   - At most ONE dominant primary action.
//
// Uses only --scope-color-* tokens. No raw hex.
// =============================================================================

import React from 'react';
import styles from './PageHeader.module.css';

// ---------------------------------------------------------------------------
// Breadcrumb item
// ---------------------------------------------------------------------------

export interface BreadcrumbItem {
  /** The visible label for this breadcrumb segment. */
  label: string;
  /** Optional href/hash link. When omitted, renders as plain text (current page). */
  href?: string;
}

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface PageHeaderProps {
  /**
   * Area name rendered as H1.
   * H1 sizing per design doc: 24–32px (font-size-xl via CSS).
   */
  title: string;

  /**
   * Workspace or repo context. Rendered in monospace as secondary metadata.
   * Example: ["my-workspace", "MyApp.sln"]
   */
  context?: string[];

  /**
   * Breadcrumb chain for hierarchical pages (e.g. Cron Agents > my-agent).
   * When provided, renders a breadcrumb trail above the H1.
   */
  breadcrumb?: BreadcrumbItem[];

  /**
   * Data freshness label (e.g. "Updated 2 minutes ago").
   * Optional; show only when the page has polled/fetched data.
   */
  freshness?: string;

  /**
   * Status chips to show as a summary of current governance state.
   * Each chip is an arbitrary React node (typically a StatusBadge).
   */
  statusChips?: React.ReactNode[];

  /**
   * The single dominant primary action for this page.
   * Must be at most one — use a button or anchor. Do not pass multiple
   * actions here; secondary actions belong in the page body.
   */
  primaryAction?: React.ReactNode;

  /** Additional CSS class for the header element. */
  className?: string;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * PageHeader renders the standard Scope page header: H1, context, breadcrumb,
 * freshness, status chips, and one primary action.
 *
 * Design-doc rule: one clear H1 per page; at most one dominant primary action.
 * UX contract: first viewport exposes page job, highest-risk state, and
 * likely next action.
 */
export function PageHeader({
  title,
  context,
  breadcrumb,
  freshness,
  statusChips,
  primaryAction,
  className,
}: PageHeaderProps): React.ReactElement {
  const hasMeta = (context !== undefined && context.length > 0) || (freshness !== undefined && freshness !== '') || (statusChips !== undefined && statusChips.length > 0);
  const hasBreadcrumb = breadcrumb !== undefined && breadcrumb.length > 0;

  return (
    <header className={[styles.header, className].filter((c): c is string => c !== undefined).join(' ')}>
      {/* Breadcrumb (only for hierarchical detail pages) */}
      {hasBreadcrumb && (
        <nav className={styles.breadcrumb} aria-label="Breadcrumb">
          {breadcrumb.map((item, idx) => (
            <React.Fragment key={idx}>
              {idx > 0 && (
                <span className={styles.breadcrumbSeparator} aria-hidden="true">
                  /
                </span>
              )}
              {item.href != null && item.href !== '' ? (
                <a href={item.href}>{item.label}</a>
              ) : (
                <span aria-current="page">{item.label}</span>
              )}
            </React.Fragment>
          ))}
        </nav>
      )}

      {/* Top row: H1 + primary action */}
      <div className={styles.topRow}>
        <h1 className={styles.title}>{title}</h1>
        {primaryAction != null && (
          <div className={styles.primaryAction}>{primaryAction}</div>
        )}
      </div>

      {/* Meta row: workspace context + freshness + status chips */}
      {hasMeta && (
        <div className={styles.metaRow}>
          {context !== undefined && context.length > 0 && (
            <div className={styles.context}>
              {context.map((segment, idx) => (
                <React.Fragment key={idx}>
                  {idx > 0 && (
                    <span className={styles.contextSeparator} aria-hidden="true">
                      /
                    </span>
                  )}
                  <span>{segment}</span>
                </React.Fragment>
              ))}
            </div>
          )}
          {freshness != null && freshness !== '' && (
            <span className={styles.freshness}>{freshness}</span>
          )}
          {statusChips && statusChips.length > 0 && (
            <div className={styles.statusChips}>
              {statusChips.map((chip, idx) => (
                <React.Fragment key={idx}>{chip}</React.Fragment>
              ))}
            </div>
          )}
        </div>
      )}
    </header>
  );
}
