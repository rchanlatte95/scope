import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import styles from './Tooltip.module.css';

// =============================================================================
// Tooltip (owner feedback item 7; a11y extended T-2026-06-15-11b).
//
// The old static renderer's tooltips got STUCK: they were shown on hover but never
// reliably dismissed, so they piled up over the trace. This implementation fixes the
// three things item 7 calls out:
//  - bounded size: the bubble has a hard max-width/max-height + overflow in CSS.
//  - hover-intent DELAY: a show timer (default 350ms) so a passing cursor does not
//    flash tooltips; the timer is cancelled on early mouse-leave.
//  - NOT sticky: mouse-leave clears any pending show timer AND hides immediately.
//    There is no click-to-pin, no focus-trap, nothing that can wedge it open.
//
// A11y additions (T-2026-06-15-11b / WCAG 1.4.13):
//  - Escape key dismisses the tooltip without moving focus (WCAG 1.4.13 Dismissible).
//  - The bubble id is wired to aria-describedby on the trigger wrapper so screen
//    readers can announce it. Callers supply the `id` prop for stable references.
//
// Pure presentational; no portal (the bubble is absolutely positioned within the
// trigger's relatively-positioned wrapper) so it never escapes its card's stacking
// context and cannot be orphaned on the page.
// =============================================================================

const HOVER_INTENT_MS = 350;

interface TooltipProps {
  /** The hover target. Must include a keyboard-focusable child (the caller's responsibility). */
  children: ReactNode;
  /** Bounded tooltip body. */
  content: ReactNode;
  /**
   * Stable id for the tooltip bubble element. When provided:
   *  - The bubble renders with this id.
   *  - The wrapper renders with aria-describedby pointing at this id.
   * This enables screen readers to associate the tooltip with its trigger.
   */
  id?: string;
}

export default function Tooltip({ children, content, id }: TooltipProps) {
  const [visible, setVisible] = useState(false);
  const timer = useRef<number | null>(null);

  const clearTimer = useCallback(() => {
    if (timer.current !== null) {
      window.clearTimeout(timer.current);
      timer.current = null;
    }
  }, []);

  const show = useCallback(() => {
    clearTimer();
    // hover-intent: only show after the cursor lingers.
    timer.current = window.setTimeout(() => { setVisible(true); }, HOVER_INTENT_MS);
  }, [clearTimer]);

  const hide = useCallback(() => {
    // Not sticky: cancel any pending show AND hide now.
    clearTimer();
    setVisible(false);
  }, [clearTimer]);

  // WCAG 1.4.13 Dismissible: Escape hides the tooltip without moving focus.
  useEffect(() => {
    if (!visible) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        setVisible(false);
        clearTimer();
      }
    };
    window.addEventListener('keydown', onKeyDown, { capture: true });
    return () => { window.removeEventListener('keydown', onKeyDown, { capture: true }); };
  }, [visible, clearTimer]);

  return (
    <span
      className={styles.wrap}
      onMouseEnter={show}
      onMouseLeave={hide}
      onFocus={show}
      onBlur={hide}
      aria-describedby={visible ? id : undefined}
    >
      {children}
      {visible && (
        <span className={styles.bubble} role="tooltip" id={id}>
          {content}
        </span>
      )}
    </span>
  );
}
