/**
 * contrast.ts — WCAG contrast utilities (pure functions, node-safe)
 *
 * Implements:
 *   sRGB relative luminance per WCAG 2.x spec (IEC 61966-2-1).
 *   WCAG contrast ratio = (L1 + 0.05) / (L2 + 0.05) where L1 >= L2.
 *
 * These are pure math helpers. No DOM, no CSS variable resolution.
 * Tests use hardcoded hex values from the frozen token tables in index.css.
 */

/** Parse a CSS hex color string to [r, g, b] in [0, 255]. */
export function parseHex(hex: string): [number, number, number] {
  const h = hex.replace('#', '');
  if (h.length === 3) {
    const c0 = h[0] ?? '';
    const c1 = h[1] ?? '';
    const c2 = h[2] ?? '';
    return [
      parseInt(c0 + c0, 16),
      parseInt(c1 + c1, 16),
      parseInt(c2 + c2, 16),
    ];
  }
  if (h.length === 6 || h.length === 8) {
    return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
  }
  throw new Error(`Unsupported hex format: ${hex}`);
}

/**
 * sRGB linearization of a single 0–255 channel value.
 * Per WCAG 2.x / IEC 61966-2-1: piecewise linear + gamma.
 */
export function linearize(channel: number): number {
  const c = channel / 255;
  return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
}

/**
 * Relative luminance of an sRGB color (0 = black, 1 = white).
 * WCAG formula: L = 0.2126*R + 0.7152*G + 0.0722*B (linearized channels).
 */
export function relativeLuminance(hex: string): number {
  const [r, g, b] = parseHex(hex);
  return 0.2126 * linearize(r) + 0.7152 * linearize(g) + 0.0722 * linearize(b);
}

/**
 * WCAG contrast ratio between two colors.
 * Returns (lighter + 0.05) / (darker + 0.05), range [1, 21].
 */
export function contrastRatio(hexA: string, hexB: string): number {
  const la = relativeLuminance(hexA);
  const lb = relativeLuminance(hexB);
  const lighter = Math.max(la, lb);
  const darker = Math.min(la, lb);
  return (lighter + 0.05) / (darker + 0.05);
}
