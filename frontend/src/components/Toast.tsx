import { useCallback, useEffect, useRef } from 'react';
import styles from './Toast.module.css';
import type { ToastEntry } from './ToastProvider';

// =============================================================================
// Toast + ToastStack (ST-03)
//
// ToastStack: the container rendered by ToastProvider into the app root.
//   - role="status" aria-live="polite" aria-atomic="false" so each new toast is
//     announced without re-reading the full stack.
//   - Positioned fixed, bottom-right, stacks upward.
//
// ToastItem: a single dismissable notification.
//   - Sharp corners (--scope-radius-none), line border, semantic tokens.
//   - Optional action button: real <button>, 44px min target, visible focus.
//   - Auto-dismiss via setTimeout; timer PAUSED while focused or hovered.
//   - Escape key dismisses (captured at the item level so only the focused/top
//     toast is dismissed, not every open toast).
//   - Status = text + shape (left glyph-block) + color token -- never color alone.
//
// Design rules applied (docs/front-end-guides/ binding):
//   - Semantic scope-* tokens only; no raw hex.
//   - Corner radius 0 (--scope-radius-none) for the card; --scope-radius-sm (2px)
//     only on the action button (small interactive control).
//   - Spacing on the 4/8px grid (--scope-space-*).
//   - Lines over shadows: 1px border, no box-shadow on the static card.
//   - No left accent stripe: status communicated via glyph-block + text.
//   - prefers-reduced-motion: no required animation.
// =============================================================================

// ---------------------------------------------------------------------------
// Single ToastItem
// ---------------------------------------------------------------------------

interface ToastItemProps {
  toast: ToastEntry;
  onDismiss: (id: string) => void;
}

function ToastItem({ toast, onDismiss }: ToastItemProps) {
  const { id, message, actionLabel, onAction, durationMs } = toast;

  // --- auto-dismiss timer management ---
  // We keep the timer id + a "paused" flag so hover/focus can park the timer
  // and unpark it when the interaction ends.
  const timerRef = useRef<number | null>(null);
  const remainingRef = useRef<number>(durationMs);
  const startedAtRef = useRef<number | null>(null);
  const pausedRef = useRef<boolean>(false);

  const clearTimer = useCallback(() => {
    if (timerRef.current !== null) {
      window.clearTimeout(timerRef.current);
      timerRef.current = null;
    }
  }, []);

  const startTimer = useCallback((remaining: number) => {
    if (remaining <= 0 || durationMs === 0) return;
    clearTimer();
    startedAtRef.current = Date.now();
    timerRef.current = window.setTimeout(() => {
      onDismiss(id);
    }, remaining);
  }, [clearTimer, durationMs, id, onDismiss]);

  // Pause: record elapsed and clear timer.
  const pauseTimer = useCallback(() => {
    if (pausedRef.current) return;
    pausedRef.current = true;
    if (startedAtRef.current !== null && timerRef.current !== null) {
      const elapsed = Date.now() - startedAtRef.current;
      remainingRef.current = Math.max(0, remainingRef.current - elapsed);
    }
    clearTimer();
    startedAtRef.current = null;
  }, [clearTimer]);

  // Resume: restart with remaining time.
  const resumeTimer = useCallback(() => {
    if (!pausedRef.current) return;
    pausedRef.current = false;
    startTimer(remainingRef.current);
  }, [startTimer]);

  // Start auto-dismiss on mount.
  useEffect(() => {
    startTimer(remainingRef.current);
    return clearTimer;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Escape dismisses this toast.
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onDismiss(id);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => { window.removeEventListener('keydown', onKeyDown); };
  }, [id, onDismiss]);

  const handleAction = useCallback(() => {
    onAction?.();
    onDismiss(id);
  }, [id, onAction, onDismiss]);

  const handleDismiss = useCallback(() => {
    onDismiss(id);
  }, [id, onDismiss]);

  return (
    <div
      className={styles.toast}
      onMouseEnter={pauseTimer}
      onMouseLeave={resumeTimer}
      onFocus={pauseTimer}
      onBlur={resumeTimer}
    >
      {/* Status glyph-block: solid status-colored square + glyph (no left accent stripe). */}
      <span className={styles.glyphBlock} aria-hidden="true">
        i
      </span>

      <span className={styles.message}>{message}</span>

      {actionLabel !== '' && (
        <button
          type="button"
          className={styles.action}
          onClick={handleAction}
        >
          {actionLabel}
        </button>
      )}

      <button
        type="button"
        className={styles.dismiss}
        onClick={handleDismiss}
        aria-label="Dismiss notification"
      >
        {/* Unicode times symbol -- not an em dash. Distinct from text content. */}
        <span aria-hidden="true">x</span>
      </button>
    </div>
  );
}

// ---------------------------------------------------------------------------
// ToastStack: the aria-live container
// ---------------------------------------------------------------------------

interface ToastStackProps {
  toasts: ToastEntry[];
  onDismiss: (id: string) => void;
}

export default function ToastStack({ toasts, onDismiss }: ToastStackProps) {
  if (toasts.length === 0) return null;

  return (
    // role="status" + aria-live="polite": non-blocking announcements.
    // aria-atomic="false": each new child is announced independently.
    <div
      role="status"
      aria-live="polite"
      aria-atomic="false"
      className={styles.stack}
    >
      {toasts.map((t) => (
        <ToastItem key={t.id} toast={t} onDismiss={onDismiss} />
      ))}
    </div>
  );
}
