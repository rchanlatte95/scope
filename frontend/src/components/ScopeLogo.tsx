/**
 * ScopeLogo — renders the correct static brand SVG from /brand/ via <img>.
 *
 * Variant selection is driven by the active theme, NOT prefers-color-scheme, because
 * an <img>-loaded SVG's internal @media (prefers-color-scheme) only fires in Chromium;
 * Firefox and WebKit render the light defaults regardless of OS theme. To guarantee
 * cross-engine fidelity we read the explicit data-theme attribute from <html> (the same
 * seam that prefs.ts / applyPrefs() writes) and serve the matching -light / -dark SVG.
 * The ScopeThemeableLogo (inline/SVGR variant) is deferred per README sec 17 — no
 * surface currently requires runtime color control.
 *
 * Props:
 *   size        — width in px (height is auto to preserve the SVG aspect ratio;
 *                  padded/app-icon variants have a square viewBox so height = size)
 *   background  — 'light' | 'dark'; defaults to the active theme when omitted
 *   padded      — use the padded variant (square container safe)
 *   decorative  — if true: alt="" + aria-hidden (no accessible name)
 *   className   — forwarded to the <img> element
 */

import { useEffect, useState } from 'react';
import styles from './ScopeLogo.module.css';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export type LogoBackground = 'light' | 'dark';

export interface ScopeLogoProps {
  size?: number;
  /** Override the background variant. Defaults to the active app theme. */
  background?: LogoBackground;
  /** Use the padded variant (square safe for card/avatar/tile contexts). */
  padded?: boolean;
  /** Render as decorative: alt="" + aria-hidden. Use when surrounding text says "Scope". */
  decorative?: boolean;
  className?: string;
}

// ---------------------------------------------------------------------------
// Asset map (paths are relative to the public root; Vite serves public/ as /)
// ---------------------------------------------------------------------------

const LOGO_SRC = {
  light: {
    mark:   './brand/scope-mark-polished-light.svg',
    padded: './brand/scope-mark-polished-light-padded.svg',
  },
  dark: {
    mark:   './brand/scope-mark-polished-dark.svg',
    padded: './brand/scope-mark-polished-dark-padded.svg',
  },
} as const;

// ---------------------------------------------------------------------------
// Active-theme hook — reads data-theme from <html> (the prefs seam)
// ---------------------------------------------------------------------------

function resolveEffectiveTheme(): LogoBackground {
  if (typeof document === 'undefined') return 'light'; // SSR / test env
  const dataTheme = document.documentElement.getAttribute('data-theme');
  if (dataTheme === 'dark') return 'dark';
  if (dataTheme === 'light') return 'light';
  // 'system' or absent: fall back to OS preference
  if (window.matchMedia('(prefers-color-scheme: dark)').matches) return 'dark';
  return 'light';
}

function useActiveTheme(): LogoBackground {
  const [theme, setTheme] = useState<LogoBackground>(resolveEffectiveTheme);

  useEffect(() => {
    // Watch data-theme mutations on <html>
    const observer = new MutationObserver(() => {
      setTheme(resolveEffectiveTheme());
    });
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-theme'],
    });

    // Also watch OS preference changes (affects 'system' / absent)
    const mq = window.matchMedia('(prefers-color-scheme: dark)');
    const onMqChange = () => { setTheme(resolveEffectiveTheme()); };
    mq.addEventListener('change', onMqChange);

    return () => {
      observer.disconnect();
      mq.removeEventListener('change', onMqChange);
    };
  }, []);

  return theme;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export function ScopeLogo({
  size = 32,
  background,
  padded = false,
  decorative = false,
  className,
}: ScopeLogoProps) {
  const activeTheme = useActiveTheme();
  const resolvedBg: LogoBackground = background ?? activeTheme;
  const src = padded ? LOGO_SRC[resolvedBg].padded : LOGO_SRC[resolvedBg].mark;

  return (
    <img
      src={src}
      width={size}
      // height="auto" on <img> elements is a CSS concern; set the attribute
      // only for square padded/app-icon variants to avoid distorting the
      // 196x160 unpadded viewBox. For unpadded marks, let the browser infer
      // natural height from the SVG viewBox (width-driven, aspect-ratio preserved).
      height={padded ? size : undefined}
      alt={decorative ? '' : 'Scope'}
      aria-hidden={decorative ? true : undefined}
      className={[styles.logo, className].filter(Boolean).join(' ')}
      decoding="async"
    />
  );
}
