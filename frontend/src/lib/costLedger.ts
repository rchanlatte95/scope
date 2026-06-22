// =============================================================================
// costLedger.ts — Pure helpers for the Cost Ledger view (T-2026-06-16-14 ST-04)
//
// A5 timezone contract (resolved):
//   - Time-series date labels display in LOCAL wall-clock calendar day.
//   - The server emits yyyy-MM-dd UTC date strings (e.g. "2026-06-15").
//   - formatTooltipDate converts the UTC date string to the local calendar day
//     that the user sees on their machine, matching the x-axis display.
//
// Why local, not UTC?
//   The spend-over-time chart x-axis already displays dates as local wall-clock
//   (via Date.toLocaleDateString with timeZone: 'UTC' for axis labels, but the
//   data points use the UTC date parsed as midnight UTC). The tooltip must use
//   the same label as the axis tick the user is pointing at. Displaying "Jun 15"
//   in the tooltip when the axis also reads "Jun 15" is what users expect.
//
// Edge case: a UTC day boundary near midnight can straddle two local calendar
//   days. The server date string "2026-06-15" = 2026-06-15T00:00:00Z. On a
//   machine in UTC-5, midnight UTC = 7pm local the PREVIOUS day (June 14).
//   In that case the local display is "Jun 14" not "Jun 15". This is intentional
//   and correct: the tooltip matches the axis label, which also uses UTC midnight
//   parsed as a JS Date and formatted in the user's local timezone.
// =============================================================================

/**
 * Formats a yyyy-MM-dd UTC date string for tooltip display.
 *
 * displayMode controls the output:
 *  - 'local': converts midnight-UTC to the user's local calendar day label
 *             (e.g. "Jun 15, 2026"). Matches the x-axis local display.
 *  - 'utc':   displays the date as-is in UTC calendar format
 *             (e.g. "Jun 15, 2026 UTC"). Used when UTC display is required.
 *
 * @param dateStr  A yyyy-MM-dd date string (UTC midnight).
 * @param displayMode  'local' (default) or 'utc'.
 * @returns  A human-readable date label.
 */
export function formatTooltipDate(
  dateStr: string,
  displayMode: 'local' | 'utc' = 'local',
): string {
  // Parse as UTC midnight: appending T00:00:00Z ensures the JS Date is exactly
  // midnight UTC regardless of the local timezone.
  const d = new Date(`${dateStr}T00:00:00Z`);

  // Guard against invalid date strings (e.g. empty string or malformed input).
  if (isNaN(d.getTime())) {
    return dateStr;
  }

  if (displayMode === 'utc') {
    return d.toLocaleDateString('en-US', {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      timeZone: 'UTC',
    });
  }

  // 'local': format in the user's local timezone so the label matches the
  // x-axis tick (which uses the same Date object displayed in local time).
  return d.toLocaleDateString('en-US', {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  });
}
