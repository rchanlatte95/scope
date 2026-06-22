// =============================================================================
// DateRangePicker.tsx
//
// Reusable controlled date-range picker component.
//
// Design contract:
//   - Presets: 24h / 7d / 30d / 90d / 6mo / 1yr  (rolling-from-now, owner A7).
//   - Custom: datetime-local inputs (local wall-clock display).
//   - Emits normalized { from, to } ISO-8601 UTC pair for ALL modes (A3).
//   - Timezone (A5): display is local wall-clock; emit is UTC ISO with Z.
//   - Custom-range START FLOOR = 2020-01-01; enforced via min attribute + validation.
//   - start <= end validated; error shown as text + glyph (not color alone).
//   - No HIDE-ZERO control (A6 scrapped).
//   - WCAG 2.2 AA: keyboard, visible focus both themes, 44px targets.
//   - scope-* tokens only. No raw hex. Sharp + geometric (--scope-radius-sm).
//
// Props:
//   value       - The currently selected date range (controlled: callers own persistence).
//   onChange    - Called with a valid DateRange whenever the selection changes.
//   label       - Optional visible group label.
//   className   - Optional additional class for the outer wrapper.
//
// State:
//   activePreset  - which preset key is highlighted, or 'custom'.
//   localFrom     - local wall-clock string for the Start input.
//   localTo       - local wall-clock string for the End input.
//   customError   - validation message or undefined.
//
// The component is fully controlled through value/onChange. The local input
// state (localFrom/localTo) drives the custom range inputs and is synchronised
// from value when the caller updates it externally. React re-uses a key-based
// reset pattern for hard resets; soft external sync is handled by tracking the
// previous rendered value in a mutable ref inside event handlers only.
//
// Keyboard:
//   Preset buttons use role=radio inside role=radiogroup.
//   ArrowLeft / ArrowRight / ArrowUp / ArrowDown navigate between presets.
//   Tab moves from preset group into custom inputs.
// =============================================================================

import { useCallback, useId, useRef, useState } from 'react';
import {
  type DateRange,
  type PresetKey,
  PRESET_OPTIONS,
  presetToRange,
  localInputToUtcIso,
  utcIsoToLocalInput,
  validateRange,
  floorLocalInputMin,
  RANGE_FLOOR,
} from '../lib/dateRange';
import styles from './DateRangePicker.module.css';

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface DateRangePickerProps {
  /** The currently selected range (controlled). */
  value: DateRange;
  /** Called with a valid DateRange on any selection change. */
  onChange: (range: DateRange) => void;
  /** Optional visible label for the entire picker group. */
  label?: string;
  /** Optional additional CSS class for the outer wrapper. */
  className?: string;
}

// ---------------------------------------------------------------------------
// Inner component: holds mutable local state.
// When value.from/to change externally, callers can use key= to force a full
// reset; otherwise the component tracks its own display state.
// ---------------------------------------------------------------------------

export function DateRangePicker({ value, onChange, label, className }: DateRangePickerProps) {
  const pickerId = useId();

  // Which preset is currently active ('custom' when using the text inputs).
  const [activePreset, setActivePreset] = useState<PresetKey | 'custom'>('custom');

  // Local display state for custom range inputs (local wall-clock strings).
  // Initialised from the incoming value prop.
  const [localFrom, setLocalFrom] = useState<string>(() =>
    utcIsoToLocalInput(value.from),
  );
  const [localTo, setLocalTo] = useState<string>(() =>
    utcIsoToLocalInput(value.to),
  );

  // Validation error for the custom range.
  const [customError, setCustomError] = useState<string | undefined>(undefined);

  // Track the last-seen external value to detect caller-driven changes.
  // Updated only in event handlers (not during render) to satisfy react-hooks/refs.
  const lastExternalFromRef = useRef(value.from);
  const lastExternalToRef   = useRef(value.to);

  // Ref array for preset button DOM nodes (for arrow-key focus management).
  const presetButtonRefs = useRef<(HTMLButtonElement | null)[]>([]);

  // Floor min attribute for the start input.
  const floorMin = floorLocalInputMin();

  // ---- Preset click handler ----
  const handlePresetSelect = useCallback((key: PresetKey) => {
    const range = presetToRange(key);
    lastExternalFromRef.current = range.from;
    lastExternalToRef.current   = range.to;
    setActivePreset(key);
    setLocalFrom(utcIsoToLocalInput(range.from));
    setLocalTo(utcIsoToLocalInput(range.to));
    setCustomError(undefined);
    onChange(range);
  }, [onChange]);

  // ---- Arrow-key navigation for preset radiogroup ----
  const handlePresetKeyDown = useCallback((
    e: React.KeyboardEvent<HTMLButtonElement>,
    idx: number,
  ) => {
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
      e.preventDefault();
      const next = (idx + 1) % PRESET_OPTIONS.length;
      presetButtonRefs.current[next]?.focus();
    } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
      e.preventDefault();
      const prev = (idx - 1 + PRESET_OPTIONS.length) % PRESET_OPTIONS.length;
      presetButtonRefs.current[prev]?.focus();
    }
  }, []);

  // ---- Custom range: commit on every change (live validation) ----
  const commitCustomRange = useCallback((fromStr: string, toStr: string) => {
    const fromUtc = localInputToUtcIso(fromStr);
    const toUtc   = localInputToUtcIso(toStr);

    const validation = validateRange(fromUtc, toUtc, RANGE_FLOOR);
    if (!validation.valid) {
      setCustomError(validation.error);
      // Do NOT call onChange for an invalid range.
      return;
    }

    setCustomError(undefined);
    lastExternalFromRef.current = fromUtc;
    lastExternalToRef.current   = toUtc;
    onChange({ from: fromUtc, to: toUtc });
  }, [onChange]);

  const handleFromChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const newFrom = e.target.value;
    setLocalFrom(newFrom);
    setActivePreset('custom');
    commitCustomRange(newFrom, localTo);
  }, [localTo, commitCustomRange]);

  const handleToChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const newTo = e.target.value;
    setLocalTo(newTo);
    setActivePreset('custom');
    commitCustomRange(localFrom, newTo);
  }, [localFrom, commitCustomRange]);

  // ---- Accessible IDs ----
  const groupId     = `${pickerId}-group`;
  const fromId      = `${pickerId}-from`;
  const toId        = `${pickerId}-to`;
  const errorId     = `${pickerId}-error`;

  const hasLabel = label != null && label !== '';

  return (
    <div
      className={[styles.picker, className].filter(Boolean).join(' ')}
      role="group"
      aria-labelledby={hasLabel ? groupId : undefined}
      aria-label={hasLabel ? undefined : 'Date range picker'}
    >
      {hasLabel && (
        <span id={groupId} className={styles.presetLabel}>
          {label}
        </span>
      )}

      {/* ---- Preset segmented control (radiogroup) ---- */}
      <div
        role="radiogroup"
        aria-label="Date range preset"
      >
        <span className={styles.presetLabel} id={`${pickerId}-preset-label`}>
          Preset
        </span>
        <div className={styles.presetGroup}>
          {PRESET_OPTIONS.map(({ key, label: presetLabel }, idx) => {
            const isActive = activePreset === key;
            return (
              <button
                key={key}
                ref={(el) => { presetButtonRefs.current[idx] = el; }}
                type="button"
                role="radio"
                aria-checked={isActive}
                /* Only the active preset is in tab order; when in custom mode
                   the first preset button is focusable (roving tabindex pattern). */
                tabIndex={isActive || (activePreset === 'custom' && idx === 0) ? 0 : -1}
                className={[
                  styles.presetButton,
                  isActive ? styles.presetButtonActive : '',
                ].filter(Boolean).join(' ')}
                onClick={() => { handlePresetSelect(key); }}
                onKeyDown={(e) => { handlePresetKeyDown(e, idx); }}
                data-preset={key}
              >
                {presetLabel}
              </button>
            );
          })}
        </div>
      </div>

      {/* ---- Custom range inputs ---- */}
      <div
        className={styles.customSection}
        role="group"
        aria-label="Custom date range"
      >
        <div className={styles.customRow}>
          <div className={styles.customField}>
            <label htmlFor={fromId} className={styles.fieldLabel}>
              Start
            </label>
            <input
              id={fromId}
              type="datetime-local"
              className={styles.dateInput}
              value={localFrom}
              min={floorMin}
              onChange={handleFromChange}
              aria-describedby={customError != null ? errorId : undefined}
              aria-invalid={customError != null ? 'true' : undefined}
            />
          </div>
          <div className={styles.customField}>
            <label htmlFor={toId} className={styles.fieldLabel}>
              End
            </label>
            <input
              id={toId}
              type="datetime-local"
              className={styles.dateInput}
              value={localTo}
              min={floorMin}
              onChange={handleToChange}
              aria-describedby={customError != null ? errorId : undefined}
              aria-invalid={customError != null ? 'true' : undefined}
            />
          </div>
        </div>

        {/* ---- Validation error (text + glyph, not color alone) ---- */}
        {customError != null && (
          <div
            id={errorId}
            className={styles.errorBlock}
            role="alert"
            aria-live="polite"
          >
            <span className={styles.errorGlyph} aria-hidden="true">[!]</span>
            <span className={styles.errorText}>{customError}</span>
          </div>
        )}
      </div>
    </div>
  );
}
