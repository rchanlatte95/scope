/**
 * RefreshControl.tsx — Portable auto-refresh UI primitive (TRACE-04).
 *
 * Renders an auto-refresh toggle, interval selector, and manual Refresh button.
 * Designed to be wired into any view that uses useAutoRefresh.
 *
 * Accessibility contract:
 * - Real <button> and <select> with programmatic labels.
 * - 44px min touch targets (--scope-target-min).
 * - Visible focus in both themes (global :focus-visible rule).
 * - aria-pressed on the toggle button conveys on/off state.
 * - aria-live="polite" region announces the current refresh status without
 *   spamming AT on every interval tick (only on user action).
 * - Interval-tick auto-refetches are silent (aria-live is on the status text
 *   which only changes when the user interacts, not on each background fetch).
 * - Manual Refresh button has a clear outcome label: "Refresh now".
 * - Sharp geometry: --scope-radius-sm on buttons/select (2px), 4/8px grid.
 * - Semantic tokens only; no raw hex.
 */

import { type UseAutoRefreshReturn, REFRESH_PRESETS } from '../hooks/useAutoRefresh';
import styles from './RefreshControl.module.css';

export interface RefreshControlProps {
  /** State and actions returned from useAutoRefresh(). */
  autoRefresh: UseAutoRefreshReturn;
  /**
   * Optional CSS class applied to the root element.
   */
  className?: string;
  /**
   * Optional label for the group, displayed as a visually-hidden but
   * accessible label on the control group container.
   * Default: "Refresh controls"
   */
  label?: string;
}

/**
 * RefreshControl — compact auto-refresh UI control.
 *
 * Wire it next to any view header:
 *   <RefreshControl autoRefresh={autoRefreshState} />
 *
 * The parent must call autoRefresh.refreshNow() on manual refresh,
 * which itself calls the onRefreshNow callback provided to useAutoRefresh.
 */
export function RefreshControl({
  autoRefresh,
  className,
  label = 'Refresh controls',
}: RefreshControlProps) {
  const { enabled, intervalMs, setEnabled, setIntervalMs, refreshNow } = autoRefresh;

  // Human-readable status text for the aria-live region.
  // Only changes on user interaction; NOT updated on background interval ticks.
  const statusLabel = enabled
    ? `Auto-refresh on, every ${REFRESH_PRESETS.find((p) => p.ms === intervalMs)?.label ?? `${String(Math.round(intervalMs / 1000))} s`}`
    : 'Auto-refresh off';

  return (
    <div
      className={[styles.refreshControl, className].filter(Boolean).join(' ')}
      role="group"
      aria-label={label}
    >
      {/* Auto-refresh status: aria-live region. Polite so it does not interrupt.
          Only announces when the user interacts (enabled/interval change), not on
          each background tick. */}
      <span
        className={styles.statusText}
        aria-live="polite"
        aria-atomic="true"
        data-testid="refresh-status"
      >
        {statusLabel}
      </span>

      {/* Toggle: aria-pressed conveys on/off state to AT. */}
      <button
        type="button"
        className={[styles.toggleBtn, enabled ? styles.toggleBtnOn : null].filter(Boolean).join(' ')}
        aria-pressed={enabled}
        onClick={() => { setEnabled(!enabled); }}
        title={enabled ? 'Auto-refresh is on — click to disable' : 'Auto-refresh is off — click to enable'}
      >
        <span aria-hidden="true">{enabled ? '⏵' : '⏸'}</span>
        <span>{enabled ? 'On' : 'Off'}</span>
      </button>

      {/* Interval selector: only meaningful when auto-refresh is on. */}
      <label className={styles.intervalLabel}>
        <span className={styles.intervalLabelText}>Every</span>
        <select
          className={styles.intervalSelect}
          value={intervalMs}
          disabled={!enabled}
          onChange={(e) => {
            const n = parseInt(e.target.value, 10);
            if (Number.isFinite(n) && n > 0) setIntervalMs(n);
          }}
          aria-label="Auto-refresh interval"
        >
          {REFRESH_PRESETS.map((preset) => (
            <option key={preset.ms} value={preset.ms}>
              {preset.label}
            </option>
          ))}
        </select>
      </label>

      {/* Manual refresh button: always enabled regardless of auto-refresh state. */}
      <button
        type="button"
        className={styles.refreshBtn}
        onClick={refreshNow}
        aria-label="Refresh now"
      >
        <span aria-hidden="true">↻</span>
        <span>Refresh</span>
      </button>
    </div>
  );
}
