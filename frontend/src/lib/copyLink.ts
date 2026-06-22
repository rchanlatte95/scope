/**
 * copyLink.ts — clipboard helper for sharing the current page URL.
 *
 * copyCurrentUrl() copies window.location.href to the clipboard.
 * Falls back to document.execCommand('copy') on insecure contexts or
 * older browsers where navigator.clipboard is unavailable.
 *
 * Returns true on success, false on failure. Never throws.
 */

/**
 * Copy the given text to the clipboard.
 *
 * Strategy:
 *  1. Try navigator.clipboard.writeText (modern, secure context).
 *  2. Fall back to a hidden textarea + document.execCommand('copy').
 *  3. On any failure, return false.
 *
 * The fallback path handles insecure contexts (http://) and legacy
 * browsers that predate the Clipboard API.
 */
export async function copyText(text: string): Promise<boolean> {
  // Primary: Clipboard API.
  if (
    typeof navigator !== 'undefined' &&
    typeof navigator.clipboard !== 'undefined' &&
    typeof navigator.clipboard.writeText === 'function'
  ) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Fall through to execCommand fallback.
    }
  }

  // Fallback: execCommand('copy') via a temporarily-injected textarea.
  // execCommand is deprecated in the spec but remains the only reliable cross-browser
  // clipboard mechanism for http:// (insecure) contexts where navigator.clipboard is blocked.
  // Suppression is intentional and scoped to this one call site.
  try {
    const textarea = document.createElement('textarea');
    textarea.value = text;
    // Keep the textarea off-screen but reachable for the browser's copy mechanism.
    textarea.style.position = 'fixed';
    textarea.style.top = '-9999px';
    textarea.style.left = '-9999px';
    textarea.setAttribute('aria-hidden', 'true');
    document.body.appendChild(textarea);
    textarea.focus();
    textarea.select();
    // eslint-disable-next-line @typescript-eslint/no-deprecated
    const succeeded = document.execCommand('copy');
    document.body.removeChild(textarea);
    return succeeded;
  } catch {
    return false;
  }
}

/**
 * Copy window.location.href (the full shareable view-state URL) to the clipboard.
 * Returns true on success, false on failure.
 */
export async function copyCurrentUrl(): Promise<boolean> {
  return copyText(window.location.href);
}
