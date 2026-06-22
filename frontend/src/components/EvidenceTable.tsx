// =============================================================================
// EvidenceTable (FE-1)
//
// A real semantic <table> for tabular run/trace/cost/audit data.
//
// Sorting is CONTROLLED: sort column and order are passed by the parent (which
// owns URL state). This component NEVER reads or writes URL params, NEVER
// maintains internal sort state. This avoids racing with FE-2's router.
//
// The parent calls onSortChange(key, order) when a header is clicked and
// updates its own URL state, which flows back as props on the next render.
//
// Accessibility: caption, th scope, sortable button role with aria-sort,
// keyboard-operable sort headers, visible focus in both themes.
//
// mobileCards (opt-in): when true, hides the <table> below ~768px and renders
// each row as a definition-list card using the SAME column defs.
// Default is false — all existing callers that do not pass the prop are
// byte-for-byte unchanged.
// =============================================================================

import React from 'react';
import styles from './EvidenceTable.module.css';

// ---------------------------------------------------------------------------
// Column definition
// ---------------------------------------------------------------------------

export interface EvidenceColumn<TRow> {
  /** Stable key used to identify this column (also the default sort key). */
  key: string;
  /** Visible header label. */
  header: string;
  /** Whether this column supports sorting. Defaults to false. */
  sortable?: boolean;
  /** Renders the cell content for a row. */
  render: (row: TRow, rowIndex: number) => React.ReactNode;
  /** Optional header className. */
  headerClassName?: string;
  /** Optional cell className. */
  cellClassName?: string;
}

// ---------------------------------------------------------------------------
// Sort direction
// ---------------------------------------------------------------------------

export type SortOrder = 'asc' | 'desc';

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface EvidenceTableProps<TRow> {
  /** Accessible caption for the table (visible above the table). */
  caption: string;

  /** Column definitions. */
  columns: EvidenceColumn<TRow>[];

  /** The row data. */
  rows: TRow[];

  /** Key extractor for React list identity. */
  getRowKey: (row: TRow, index: number) => string | number;

  /**
   * Currently active sort column key.
   * The parent owns URL state; this is read-only to the table.
   */
  sortKey?: string | null;

  /**
   * Current sort order.
   * The parent owns URL state; this is read-only to the table.
   */
  sortOrder?: SortOrder;

  /**
   * Called when the user clicks a sortable column header.
   * The parent updates its URL state (e.g. router pushes ?sort=key&order=asc).
   * This component does NOT update state or URL directly.
   */
  onSortChange?: (key: string, order: SortOrder) => void;

  /**
   * Message to display when rows is empty.
   * Defaults to "No data available."
   */
  emptyMessage?: string;

  /**
   * When true, hides the semantic <table> below ~768px and renders each row
   * as a definition-list card instead, using the existing column defs
   * (column.header becomes the term; column.render(row) becomes the value).
   *
   * DEFAULT: false. All callers that omit this prop get the original table
   * layout on ALL breakpoints — byte-for-byte unchanged behavior.
   *
   * Intended for dense multi-column tables (e.g. recent cron runs, latest
   * trace sessions on CommandView) where timestamps and status cells become
   * too cramped to read at 390–768px.
   */
  mobileCards?: boolean;

  /**
   * Whether rows are clickable. When true, applies pointer cursor + focus styling.
   */
  onRowClick?: (row: TRow, index: number) => void;

  /**
   * Whether clickable rows should expose role="button" and row-level keyboard
   * handling. Disable when a row contains its own explicit controls.
   */
  useRowButtonRole?: boolean;

  /**
   * Optional per-row class resolver. Return a class name (or null) for each row.
   * Used to apply highlighted/selected state without coupling the table to specific state.
   */
  getRowClassName?: (row: TRow, index: number) => string | null | undefined;

  /**
   * Optional per-row inline style resolver. Intended for CSS custom properties
   * supplied by the parent, not broad table styling.
   */
  getRowStyle?: (row: TRow, index: number) => React.CSSProperties | undefined;

  /**
   * Optional per-row data attribute resolver. Return a plain object of
   * data-* attribute key/value pairs to spread onto the <tr> element.
   * Keys must be valid HTML attribute names (e.g. "data-user-submit").
   * Returning undefined or an empty object adds no attributes.
   */
  getRowDataAttrs?: (row: TRow, index: number) => Record<string, string> | undefined;

  /** Optional aria-expanded state for rows that control inline detail content. */
  getRowAriaExpanded?: (row: TRow, index: number) => boolean | undefined;

  /**
   * Optional companion row renderer. Returned nodes should be <tr> elements with
   * their own keys and an appropriate colSpan.
   */
  renderRowAfter?: (row: TRow, index: number) => React.ReactNode;

  /** Additional CSS class for the wrapper element. */
  className?: string;
}

// ---------------------------------------------------------------------------
// Sort indicator icon
// ---------------------------------------------------------------------------

function SortAscIcon(): React.ReactElement {
  return (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <polyline points="18 15 12 9 6 15" />
    </svg>
  );
}

function SortDescIcon(): React.ReactElement {
  return (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <polyline points="6 9 12 15 18 9" />
    </svg>
  );
}

function SortNoneIcon(): React.ReactElement {
  return (
    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}>
      <polyline points="18 9 12 3 6 9" opacity="0.4" />
      <polyline points="6 15 12 21 18 15" opacity="0.4" />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * EvidenceTable renders a semantic <table> for run/trace/cost/audit data.
 *
 * Sort is fully controlled: the parent owns sort state (typically URL params).
 * This component fires onSortChange but never writes state or URL itself.
 *
 * Pass mobileCards={true} to enable a card/definition-list view below ~768px.
 * Omitting the prop (or passing false) leaves behavior completely unchanged.
 */
export function EvidenceTable<TRow>({
  caption,
  columns,
  rows,
  getRowKey,
  sortKey,
  sortOrder,
  onSortChange,
  emptyMessage = 'No data available.',
  mobileCards = false,
  onRowClick,
  useRowButtonRole = true,
  getRowClassName,
  getRowStyle,
  getRowAriaExpanded,
  getRowDataAttrs,
  renderRowAfter,
  className,
}: EvidenceTableProps<TRow>): React.ReactElement {
  function handleSortClick(col: EvidenceColumn<TRow>): void {
    if (col.sortable !== true || onSortChange === undefined) return;
    if (col.key === sortKey) {
      // Toggle order when clicking the active column
      onSortChange(col.key, sortOrder === 'asc' ? 'desc' : 'asc');
    } else {
      // Default to ascending for a new column
      onSortChange(col.key, 'asc');
    }
  }

  function getSortIndicator(col: EvidenceColumn<TRow>): React.ReactElement {
    if (col.key !== sortKey) return <SortNoneIcon />;
    return sortOrder === 'asc' ? <SortAscIcon /> : <SortDescIcon />;
  }

  function getAriaSortAttr(col: EvidenceColumn<TRow>): 'ascending' | 'descending' | 'none' | undefined {
    if (col.sortable !== true) return undefined;
    if (col.key !== sortKey) return 'none';
    return sortOrder === 'asc' ? 'ascending' : 'descending';
  }

  // ---------------------------------------------------------------------------
  // Mobile card list (opt-in, mobileCards=true only)
  // Rendered alongside the table; CSS hides one at each breakpoint.
  // ---------------------------------------------------------------------------
  const cardList: React.ReactElement | null = mobileCards ? (
    <div className={styles.cardList} aria-label={caption}>
      {rows.length === 0 ? (
        <p className={styles.cardListEmpty}>{emptyMessage}</p>
      ) : (
        rows.map((row, idx) => {
          const key = getRowKey(row, idx);
          const extraClass = getRowClassName ? (getRowClassName(row, idx) ?? '') : '';
          return (
            <dl
              key={key}
              className={[styles.card, extraClass].filter(Boolean).join(' ')}
            >
              {columns.map((col) => (
                <div key={col.key} className={styles.cardField}>
                  <dt className={styles.cardTerm}>{col.header}</dt>
                  <dd className={styles.cardValue}>{col.render(row, idx)}</dd>
                </div>
              ))}
            </dl>
          );
        })
      )}
    </div>
  ) : null;

  return (
    <div className={[styles.tableWrapper, mobileCards ? styles.hasMobileCards : undefined, className].filter(Boolean).join(' ')}>
      {cardList}
      <table className={styles.table}>
        <caption>{caption}</caption>
        <thead>
          <tr>
            {columns.map((col) => (
              <th
                key={col.key}
                scope="col"
                aria-sort={getAriaSortAttr(col)}
                className={col.headerClassName}
              >
                {col.sortable === true && onSortChange !== undefined ? (
                  <button
                    type="button"
                    className={styles.sortButton}
                    onClick={() => { handleSortClick(col); }}
                    aria-label={`Sort by ${col.header}${
                      col.key === sortKey
                        ? `, currently ${sortOrder === 'asc' ? 'ascending' : 'descending'}`
                        : ''
                    }`}
                  >
                    {col.header}
                    <span className={styles.sortIndicator}>
                      {getSortIndicator(col)}
                    </span>
                  </button>
                ) : (
                  col.header
                )}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr className={styles.emptyRow}>
              <td colSpan={columns.length}>{emptyMessage}</td>
            </tr>
          ) : (
            rows.map((row, idx) => {
              const key = getRowKey(row, idx);
              const isClickable = !!onRowClick;
              const useButtonRole = isClickable && useRowButtonRole;
              const extraClass = getRowClassName ? (getRowClassName(row, idx) ?? '') : '';
              const rowClass = [isClickable ? styles.clickableRow : '', extraClass].filter(Boolean).join(' ') || undefined;
              const rowStyle = getRowStyle ? getRowStyle(row, idx) : undefined;
              const ariaExpanded = getRowAriaExpanded ? getRowAriaExpanded(row, idx) : undefined;
              const dataAttrs = getRowDataAttrs ? (getRowDataAttrs(row, idx) ?? {}) : {};
              return (
                <React.Fragment key={key}>
                  <tr
                    className={rowClass}
                    style={rowStyle}
                    aria-expanded={ariaExpanded}
                    {...dataAttrs}
                    onClick={isClickable ? () => { onRowClick(row, idx); } : undefined}
                    // Keyboard support for clickable rows
                    tabIndex={useButtonRole ? 0 : undefined}
                    role={useButtonRole ? 'button' : undefined}
                    onKeyDown={
                      useButtonRole
                        ? (e) => {
                            if (e.key === 'Enter' || e.key === ' ') {
                              e.preventDefault();
                              onRowClick(row, idx);
                            }
                          }
                        : undefined
                    }
                  >
                    {columns.map((col, colIdx) => (
                      <td
                        key={col.key}
                        // First cell in a row is a row header for narrow table contexts
                        {...(colIdx === 0 ? {} : {})}
                        className={col.cellClassName}
                      >
                        {col.render(row, idx)}
                      </td>
                    ))}
                  </tr>
                  {renderRowAfter ? renderRowAfter(row, idx) : null}
                </React.Fragment>
              );
            })
          )}
        </tbody>
      </table>
    </div>
  );
}
