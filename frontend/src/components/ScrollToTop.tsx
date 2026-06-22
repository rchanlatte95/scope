import { useEffect, useState } from 'react';
import { useThrottledCallback } from 'use-debounce';
import styles from './ScrollToTop.module.css';

// =============================================================================
// ScrollToTop (T-2026-06-13-50; R4 throttle — T-2026-06-14-13)
//
// A self-contained, window-scroll-based "back to top" button.
// Mounts in TraceView; designed to be promotable to other long views later.
//
// Eligibility: document.scrollHeight > 2 * window.innerHeight
//   (page must be taller than 2 viewport heights; short pages never show it).
//
// Visibility: appears only after scrolling past SHOW_THRESHOLD_VH viewport
//   heights from the top. Hides when scrolled back above the threshold.
//
// R4 (T-2026-06-14-13): the scroll + resize handlers are throttled to
//   SCROLL_THROTTLE_MS (150 ms) via useThrottledCallback from use-debounce,
//   so the handler is not called on every scroll tick.
//
// A11y:
//   - Real <button> with aria-label "Back to top".
//   - Not a tab stop when hidden (tabIndex=-1 + aria-hidden).
//   - Keyboard: click/Enter/Space triggers scroll.
//   - 44px minimum touch/click target (--scope-target-min).
//   - Visible focus ring in both themes.
//   - Smooth scroll normally; instant under prefers-reduced-motion.
//   - After scroll, focus moves to the top skip-nav target (#skip-nav).
// =============================================================================

/** Scroll must be past this many viewport heights before the button appears. */
const SHOW_THRESHOLD_VH = 1;

/** Page must be taller than this many viewport heights for the button to be eligible. */
const ELIGIBLE_HEIGHT_VH = 2;

/**
 * Throttle interval for the scroll/resize handlers (R4, T-2026-06-14-13).
 * 150 ms matches the research doc recommendation for scroll + resize listeners.
 */
export const SCROLL_THROTTLE_MS = 150;

function prefersReducedMotion(): boolean {
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

export default function ScrollToTop() {
  const [visible, setVisible] = useState(false);

  // R4: throttle at SCROLL_THROTTLE_MS so the handler is not called on every tick.
  // useThrottledCallback is stable across renders (memoised internally by use-debounce).
  const updateVisibility = useThrottledCallback(() => {
    const eligible = document.documentElement.scrollHeight > ELIGIBLE_HEIGHT_VH * window.innerHeight;
    if (!eligible) {
      setVisible(false);
      return;
    }
    const pastThreshold = window.scrollY >= SHOW_THRESHOLD_VH * window.innerHeight;
    setVisible(pastThreshold);
  }, SCROLL_THROTTLE_MS);

  useEffect(() => {
    // Evaluate once on mount (e.g. restored scroll position).
    updateVisibility();

    window.addEventListener('scroll', updateVisibility, { passive: true });
    window.addEventListener('resize', updateVisibility, { passive: true });
    return () => {
      window.removeEventListener('scroll', updateVisibility);
      window.removeEventListener('resize', updateVisibility);
    };
  }, [updateVisibility]);

  function handleClick() {
    const behavior: ScrollBehavior = prefersReducedMotion() ? 'instant' : 'smooth';
    window.scrollTo({ top: 0, behavior });

    // Move focus to the top skip-nav target so keyboard users land at the top.
    // If the skip-nav anchor is absent, fall back to the page root (document.body).
    const topTarget =
      document.getElementById('skip-nav') ??
      (document.querySelector('main')) ??
      document.body;
    // Use a brief rAF so focus lands after the scroll begins and the element
    // is at the correct position in the visual viewport.
    requestAnimationFrame(() => {
      topTarget.focus({ preventScroll: true });
    });
  }

  return (
    <button
      type="button"
      className={styles.btn}
      aria-label="Back to top"
      aria-hidden={!visible}
      tabIndex={visible ? 0 : -1}
      onClick={handleClick}
    >
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth={2.5}
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
        className={styles.icon}
      >
        <polyline points="18 15 12 9 6 15" />
      </svg>
      <span className={styles.label}>Top</span>
    </button>
  );
}
