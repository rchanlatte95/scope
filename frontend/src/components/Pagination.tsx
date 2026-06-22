/**
 * Pagination.tsx — Reusable pagination component (T-2026-06-17-03).
 *
 * Design contract:
 * - Real <button> elements, 44px targets (--scope-target-min).
 * - Visible focus in both themes (outline via :focus-visible in global CSS).
 * - aria-current="page" on the active page button.
 * - role="status" / aria-live="polite" summary line announces page changes to AT.
 * - Semantic tokens only (no raw hex). Sharp geometry (--scope-radius-sm on small controls).
 * - Page-size <select> for 10/25/50/100.
 * - Controls: First, Prev, local five-page window, Next, Last, Per page.
 * - Summary line: "Showing X–Y of N · page P of M".
 * - Reflows at 320px (all controls stack vertically).
 *
 * Both server-side and client-side modes are supported. The caller passes
 * the current page, total pages, and callbacks — this component is stateless.
 */

import styles from './Pagination.module.css';

export interface PaginationProps {
  /** Current 1-based page number. */
  page: number;
  /** Total number of pages (derived by caller from totalCount / pageSize). 0 = unknown. */
  pageCount: number;
  /** Current page size. */
  pageSize: number;
  /** Total record count (used for the summary line). 0 = unknown/loading. */
  totalCount: number;
  /** Called when the user clicks a specific page button. */
  onGoToPage: (page: number) => void;
  /** Called when the user clicks Prev. */
  onPrevPage: () => void;
  /** Called when the user clicks Next. */
  onNextPage: () => void;
  /** Called when the user changes the page-size select. */
  onSetPageSize: (pageSize: number) => void;
  /**
   * Optional CSS class name applied to the root nav element.
   */
  className?: string;
  /**
   * Optional accessible label for this pagination control.
   * Default: "Pagination".
   */
  label?: string;
}

/** Valid page sizes presented in the size selector. */
const PAGE_SIZE_OPTIONS = [10, 25, 50, 100] as const;

/**
 * Maximum number of page buttons rendered in the row (excluding nav buttons).
 * When pageCount exceeds this, show a local window around the current page.
 */
const MAX_PAGE_BUTTONS = 5;

/**
 * Compute a local page window centered on the current page when possible.
 * The first and last pages are reached through dedicated First/Last buttons,
 * so this intentionally does not anchor page 1/pageCount with ellipses.
 */
function computePageWindow(page: number, pageCount: number): number[] {
  if (pageCount <= 0) return [];
  if (pageCount <= MAX_PAGE_BUTTONS) {
    return Array.from({ length: pageCount }, (_, i) => i + 1);
  }
  const halfWindow = Math.floor(MAX_PAGE_BUTTONS / 2);
  const start = Math.min(
    Math.max(1, page - halfWindow),
    pageCount - MAX_PAGE_BUTTONS + 1,
  );
  return Array.from({ length: MAX_PAGE_BUTTONS }, (_, i) => start + i);
}

export function Pagination({
  page,
  pageCount,
  pageSize,
  totalCount,
  onGoToPage,
  onPrevPage,
  onNextPage,
  onSetPageSize,
  className,
  label = 'Pagination',
}: PaginationProps) {
  const hasPrev = page > 1;
  const hasNext = pageCount > 0 && page < pageCount;

  // Summary line: "Showing X–Y of N · page P of M"
  const first = totalCount > 0 ? (page - 1) * pageSize + 1 : 0;
  const last = totalCount > 0 ? Math.min(page * pageSize, totalCount) : 0;
  const summaryText = totalCount > 0
    ? `Showing ${String(first)}–${String(last)} of ${String(totalCount)} · page ${String(page)} of ${String(pageCount)}`
    : 'Loading…';

  const pageWindow = computePageWindow(page, pageCount);

  return (
    <nav
      aria-label={label}
      className={`${styles.pagination ?? ''} ${className ?? ''}`}
    >
      {/* Summary line: announces page changes to AT */}
      <span
        role="status"
        aria-live="polite"
        className={styles.summary}
      >
        {summaryText}
      </span>

      <div className={styles.controls}>
        {/* First button */}
        <button
          type="button"
          className={styles.navBtn}
          onClick={() => { onGoToPage(1); }}
          disabled={!hasPrev}
          aria-label="First page"
        >
          <span className={styles.navBtnText}>First</span>
        </button>

        {/* Previous button */}
        <button
          type="button"
          className={styles.navBtn}
          onClick={onPrevPage}
          disabled={!hasPrev}
          aria-label="Previous page"
        >
          <span aria-hidden="true">‹</span>
          <span className={styles.navBtnText}>Prev</span>
        </button>

        {/* Page buttons */}
        <div className={styles.pageButtons} role="group" aria-label="Page numbers">
          {pageWindow.map((pg) => (
            <button
              key={pg}
              type="button"
              className={`${styles.pageBtn ?? ''} ${pg === page ? (styles.pageBtnActive ?? '') : ''}`}
              onClick={() => { onGoToPage(pg); }}
              aria-label={`Page ${String(pg)}`}
              aria-current={pg === page ? 'page' : undefined}
              disabled={pg === page}
            >
              {String(pg)}
            </button>
          ))}
        </div>

        {/* Next button */}
        <button
          type="button"
          className={styles.navBtn}
          onClick={onNextPage}
          disabled={!hasNext}
          aria-label="Next page"
        >
          <span className={styles.navBtnText}>Next</span>
          <span aria-hidden="true">›</span>
        </button>

        {/* Last button */}
        <button
          type="button"
          className={styles.navBtn}
          onClick={() => { onGoToPage(pageCount); }}
          disabled={!hasNext}
          aria-label="Last page"
        >
          <span className={styles.navBtnText}>Last</span>
        </button>

        {/* Page-size selector */}
        <label className={styles.sizeLabel}>
          <span className={styles.sizeLabelText}>Per page</span>
          <select
            className={styles.sizeSelect}
            value={pageSize}
            onChange={(e) => {
              const n = parseInt(e.target.value, 10);
              if (Number.isFinite(n)) onSetPageSize(n);
            }}
            aria-label="Items per page"
          >
            {PAGE_SIZE_OPTIONS.map((sz) => (
              <option key={sz} value={sz}>
                {String(sz)}
              </option>
            ))}
          </select>
        </label>
      </div>
    </nav>
  );
}
