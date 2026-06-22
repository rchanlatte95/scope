// =============================================================================
// exportCsv.ts — Reusable CSV export utility
//
// Library: papaparse (Papa.unparse) — MIT, de-facto standard, handles
// quoting/escaping/unicode/BOM, well-tested, ~28 KB minified (~10 KB gzip).
// Bundle delta from npm run build: papaparse adds ~10 KB to the gzip bundle.
//
// Decision: papaparse (D2-library-choice, confirmed: owner A2).
// Gating: export is FREE-tier (D1-free-tier-gating, confirmed: no entitlement gate).
// =============================================================================

import Papa from 'papaparse';

/**
 * Column definition for exportToCsv / generateCsvString.
 * Column order in the CSV matches the array order (stable, never implicit sort).
 */
export interface CsvColumn<T> {
  /** Column header text — appears in row 1 of the CSV file. */
  header: string;
  /**
   * Extract the value for this column from a data row.
   * Return string | number | null | undefined.
   * null and undefined render as an empty cell (not the literal string "null").
   */
  accessor: (row: T) => string | number | null | undefined;
}

// UTF-8 BOM prepended so Excel opens the file correctly without re-encoding.
const UTF8_BOM = '﻿';

/**
 * Pure function: serializes columns + rows into a CSV string (no DOM side-effects).
 * Testable in node-env Vitest.
 *
 * Guarantees:
 * - Row 1 is the header row (column.header values in definition order).
 * - The library (papaparse) handles quoting for commas, quotes, and newlines.
 * - null/undefined accessors produce an empty string cell.
 * - Number values are serialized as numeric strings (no scientific notation for
 *   reasonable magnitudes — papaparse serializes JS numbers via String()).
 * - Column order is stable: matches the `columns` array order exactly.
 * - The output starts with a UTF-8 BOM (U+FEFF) for Excel compatibility.
 */
export function generateCsvString<T>(columns: CsvColumn<T>[], rows: T[]): string {
  const headerRow = columns.map((col) => col.header);
  const dataRows = rows.map((row) =>
    columns.map((col) => {
      const value = col.accessor(row);
      // null and undefined -> empty string (never "null" / "undefined")
      if (value === null || value === undefined) return '';
      return value;
    }),
  );

  const csvBody = Papa.unparse([headerRow, ...dataRows], {
    // Do NOT pass header: true — we manually prepend the header row so the
    // column order is definitively controlled by the columns array order.
    header: false,
    // newline: \r\n for maximum spreadsheet compatibility (RFC 4180)
    newline: '\r\n',
    // skipEmptyLines: false — an empty rows array should still emit the header
    skipEmptyLines: false,
  });

  return UTF8_BOM + csvBody;
}

/**
 * Serializes rows to CSV using papaparse, prepends a UTF-8 BOM, and triggers a
 * client-side Blob download.
 *
 * @param columns  Ordered column definitions. Header row is derived from these.
 * @param rows     Data rows. Each row is passed to each column's accessor.
 * @param filename Download filename (e.g. "cost-by-agent-2026-06-16.csv").
 *
 * The DOM-side download (Blob, createObjectURL, anchor.click) is not covered by
 * node-env Vitest tests. Use Playwright e2e for that path.
 */
export function exportToCsv<T>(
  columns: CsvColumn<T>[],
  rows: T[],
  filename: string,
): void {
  const csvString = generateCsvString(columns, rows);

  const blob = new Blob([csvString], { type: 'text/csv;charset=utf-8;' });
  const url = URL.createObjectURL(blob);

  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  anchor.style.display = 'none';

  document.body.appendChild(anchor);
  anchor.click();
  document.body.removeChild(anchor);

  // Revoke after a tick so the browser has time to initiate the download
  // before the object URL is freed.
  setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 100);
}
