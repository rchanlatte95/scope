// =============================================================================
// ProofPathChain (PP-07) — Text-first proof-path display with copyable ID.
//
// Renders proof_path: string[] (from AffectedSymbol or AffectedTest) as a
// variable-length text sequence joined by an arrow glyph. Does NOT assume
// exactly 3 hops; chains can be rooted at a member (ground-truth #1) and
// may lengthen. No canvas, no SVG graph (design contract forbids them).
//
// Key correctness mandates (PP-07):
//   - Empty array (proof_path.length === 0) -> "(no path recorded)", length 0.
//     Indexing proof_path[0] or proof_path[last] on empty MUST NOT throw.
//   - proofPathId(row): pure function returning row.symbol_id (OQ-4).
//     Lives in proofPathChainHelpers.ts (react-refresh rule).
//   - chainHash(proof_path): pure deterministic hash, no randomness.
//     Lives in proofPathChainHelpers.ts (react-refresh rule).
//   - Arrow glyph is aria-hidden; screen-reader order uses <ol> + aria-label.
//   - Copy button: real <button>, aria-label, 44px target, focus ring both themes.
//
// Design contract: --scope-* tokens only, sharp (radius 0 or --scope-radius-sm
// for small controls), status never color-only.
// =============================================================================

import { useCallback, useRef, useState } from 'react';
import type React from 'react';
import { proofPathId } from './proofPathChainHelpers';
import styles from './ProofPathChain.module.css';

// ---------------------------------------------------------------------------
// Arrow glyph — aria-hidden, separates hops visually only (EDGE-A11Y-6)
// ---------------------------------------------------------------------------

const ARROW_GLYPH = '→'; // → U+2192 RIGHTWARDS ARROW

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface ProofPathChainProps {
  /**
   * The proof path hops. Each element is a short display name (server .Name).
   * Variable length; 0 elements renders "(no path recorded)".
   */
  proofPath: string[];

  /**
   * The row's fully-qualified symbol_id. Used as the primary copyable ID
   * (OQ-4). Displayed as the copy button's target.
   */
  symbolId: string;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * ProofPathChain renders a variable-length proof-path sequence as a text chain
 * joined by an arrow glyph, with a numeric path-length count and a copy button
 * for the full FQN (symbol_id).
 *
 * Accessibility:
 *   - Chain is wrapped in an <ol> where each hop is an <li>, so screen-reader
 *     order conveys the proof path correctly (EDGE-A11Y-6).
 *   - Arrow glyphs between items are aria-hidden (EDGE-A11Y-6).
 *   - The <ol> has aria-label="Proof path: A then B then C" for AT users.
 *   - Copy button has a descriptive aria-label (EDGE-A11Y-5).
 *   - 44px minimum touch target on the copy button (EDGE-A11Y-5).
 *   - Visible focus ring in both light and dark themes (--scope-focus-ring).
 *
 * Design contract:
 *   - --scope-* tokens only; no raw hex.
 *   - Sharp: radius 0 by default, --scope-radius-sm (2px) for the button only.
 *   - Status never color-only.
 *
 * Pure helpers: proofPathId and chainHash live in proofPathChainHelpers.ts to
 * satisfy react-refresh/only-export-components (no mixed exports).
 */
export default function ProofPathChain({
  proofPath,
  symbolId,
}: ProofPathChainProps): React.ReactElement {
  const [copied, setCopied] = useState(false);
  const timerRef = useRef<number | null>(null);

  const handleCopy = useCallback(() => {
    const id = proofPathId({ symbol_id: symbolId });
    navigator.clipboard.writeText(id).then(() => {
      setCopied(true);
      if (timerRef.current !== null) window.clearTimeout(timerRef.current);
      timerRef.current = window.setTimeout(() => { setCopied(false); }, 1500);
    }).catch(() => {
      // Clipboard unavailable (insecure context): fail silently.
      // The FQN remains visible/selectable via the title attribute.
    });
  }, [symbolId]);

  const pathLength = proofPath.length;
  const isEmpty = pathLength === 0;

  return (
    <div className={styles.root}>
      {/* Path length count — always shown so a wrapped chain is countable
          (EDGE-DATA-3, EDGE-A11Y-4) */}
      <span className={styles.lengthBadge} aria-label={`Path length: ${String(pathLength)}`}>
        {pathLength}
      </span>

      {isEmpty ? (
        /* Empty array: render "(no path recorded)"; never index [0] or [last]
           on an empty array (PP-07 correctness mandate, EDGE-DATA-11) */
        <span className={styles.emptyLabel}>(no path recorded)</span>
      ) : (
        /* Non-empty: ordered list for screen-reader ordering (EDGE-A11Y-6).
           The aria-label on the <ol> conveys the full chain for AT users in
           natural-language form: "Proof path: A then B then C".
           Arrow glyphs between items are aria-hidden separators. */
        <ol
          className={styles.chain}
          aria-label={`Proof path: ${proofPath.join(' then ')}`}
        >
          {proofPath.map((hop, index) => (
            <li key={index} className={styles.hop}>
              {/* Arrow separator: rendered before each hop AFTER the first.
                  aria-hidden so screen readers skip it (EDGE-A11Y-6). */}
              {index > 0 && (
                <span className={styles.arrow} aria-hidden="true">
                  {ARROW_GLYPH}
                </span>
              )}
              <span className={styles.hopName}>{hop}</span>
            </li>
          ))}
        </ol>
      )}

      {/* Copy button: copies full FQN (symbol_id), not the short chain display.
          Real <button> with aria-label, 44px target, focus ring both themes,
          visible "copied" feedback (EDGE-A11Y-5, EDGE-SEC-1). */}
      <button
        type="button"
        className={styles.copyBtn}
        onClick={handleCopy}
        aria-label={`Copy symbol ID: ${symbolId}`}
        title={`Copy FQN: ${symbolId}`}
      >
        <span className={styles.copyBtnText}>
          {copied ? 'copied' : 'copy ID'}
        </span>
      </button>
    </div>
  );
}
