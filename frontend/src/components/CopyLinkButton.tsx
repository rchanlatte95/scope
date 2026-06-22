// =============================================================================
// CopyLinkButton (ST-04 / T-2026-06-16-18)
//
// A reusable "Copy link" button that copies window.location.href (the full
// current view-state URL) to the clipboard. Used on the /cost-ledger and
// /trace page headers.
//
// Design contract:
//   - Real <button type="button">, 44px minimum tap target.
//   - Text label (not icon-only): "Copy link" / "Copied" confirmation.
//   - Inline status confirmation: the button text briefly shows "Copied"
//     and an aria-live region announces it to screen readers.
//   - Visible focus indicator in both themes (inherits --scope-focus-ring
//     from the global :focus-visible rule).
//   - Semantic scope-* tokens only. No raw hex.
//   - WCAG 2.2 AA: keyboard-operable, color-independent confirmation.
//
// Inline confirmation approach (a11y):
//   The confirmation lives in the button label itself, so screen readers
//   announce "Copied" naturally on re-read. A sibling role="status"
//   aria-live="polite" region also announces the change so readers that
//   do not re-read the button label will still catch it.
// =============================================================================

import React, { useCallback, useRef, useState } from 'react';
import { copyCurrentUrl } from '../lib/copyLink';
import styles from './CopyLinkButton.module.css';

/** How long (ms) to show the "Copied" confirmation before reverting. */
const RESET_MS = 1800;

export interface CopyLinkButtonProps {
  /** Additional CSS class for the outer wrapper (optional). */
  className?: string;
}

/**
 * CopyLinkButton copies window.location.href when clicked, shows an inline
 * "Copied" confirmation for 1.8s, then reverts. Works on /trace and
 * /cost-ledger page headers.
 */
export function CopyLinkButton({ className }: CopyLinkButtonProps): React.ReactElement {
  const [copied, setCopied] = useState(false);
  const timer = useRef<number | null>(null);

  const handleClick = useCallback(() => {
    void copyCurrentUrl().then((success) => {
      if (!success) return;
      if (timer.current !== null) window.clearTimeout(timer.current);
      setCopied(true);
      timer.current = window.setTimeout(() => {
        setCopied(false);
        timer.current = null;
      }, RESET_MS);
    });
  }, []);

  const label = copied ? 'Copied' : 'Copy link';

  return (
    <span
      className={[styles.wrapper, className].filter((c): c is string => c !== undefined).join(' ')}
    >
      <button
        type="button"
        className={styles.button}
        onClick={handleClick}
        aria-pressed={copied}
        data-testid="copy-link-button"
      >
        {label}
      </button>
      {/*
        aria-live announcement: polite so it does not interrupt ongoing speech.
        The content changes from empty to "Link copied" when the user clicks.
        This ensures screen readers that do not re-read the button label still
        announce the confirmation.
      */}
      <span
        role="status"
        aria-live="polite"
        className={styles.liveRegion}
        aria-atomic="true"
      >
        {copied ? 'Link copied' : ''}
      </span>
    </span>
  );
}
