// =============================================================================
// ExportCsvButton.tsx — Accessible CSV export button
//
// Design contract:
// - Real <button> element (not a link, div, or span).
// - type="button" (never type="submit").
// - Text label visible at all times (not icon-only). Default: "Export CSV".
// - 44px minimum touch/click target (--scope-target-min via CSS module).
// - Visible focus ring in both themes (global :focus-visible from index.css).
// - scope-* tokens only; no raw hex.
// - corner radius: --scope-radius-sm (2px) — small interactive control.
// - Disabled state communicated by text (disabledReason), NOT color alone.
//   disabledReason renders as a visible inline notice via aria-describedby.
//
// Usage:
//   <ExportCsvButton
//     columns={myColumns}
//     rows={myRows}
//     filename="cost-by-agent-2026-06-17.csv"
//   />
// =============================================================================

import React, { useId } from 'react';
import { exportToCsv, type CsvColumn } from '../lib/exportCsv';
import styles from './ExportCsvButton.module.css';

// Download icon (aria-hidden; label carries meaning per UX contract)
function DownloadIcon(): React.ReactElement {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M12 15V3" />
      <path d="M7 10l5 5 5-5" />
      <path d="M20 20H4" />
    </svg>
  );
}

export interface ExportCsvButtonProps<T> {
  /** Ordered column definitions passed directly to exportToCsv. */
  columns: CsvColumn<T>[];
  /** Data rows passed directly to exportToCsv. */
  rows: T[];
  /** Download filename (e.g. "cost-by-agent-2026-06-17.csv"). */
  filename: string;
  /**
   * Optional visible label override.
   * Default: "Export CSV"
   */
  label?: string;
  /**
   * Optional CSS class for external positioning (e.g. float/flex alignment).
   * Does NOT affect internal design tokens.
   */
  className?: string;
  /**
   * Set true when export is not available (e.g. entitlement gate in future tiers).
   * Renders the button with the HTML disabled attribute.
   */
  disabled?: boolean;
  /**
   * Optional human-readable reason shown as inline text when disabled=true.
   * Per UX contract: disabled state must be communicated by text, not color alone.
   * Example: "Available on paid plans"
   */
  disabledReason?: string;
}

/**
 * ExportCsvButton — accessible button that triggers a CSV download.
 *
 * Calls exportToCsv(columns, rows, filename) on click.
 * Renders a visible text label (default: "Export CSV") plus a download icon.
 * The disabled state renders an optional inline disabledReason notice.
 */
export function ExportCsvButton<T>({
  columns,
  rows,
  filename,
  label = 'Export CSV',
  className,
  disabled = false,
  disabledReason,
}: ExportCsvButtonProps<T>): React.ReactElement {
  const noticeId = useId();

  function handleClick(): void {
    if (!disabled) {
      exportToCsv(columns, rows, filename);
    }
  }

  const hasNotice = disabled && disabledReason != null && disabledReason !== '';

  return (
    <span>
      <button
        type="button"
        className={[styles.button, className].filter(Boolean).join(' ')}
        disabled={disabled}
        aria-describedby={hasNotice ? noticeId : undefined}
        onClick={handleClick}
      >
        <span className={styles.icon}>
          <DownloadIcon />
        </span>
        {label}
      </button>
      {hasNotice && (
        <span id={noticeId} className={styles.disabledNotice}>
          {disabledReason}
        </span>
      )}
    </span>
  );
}
