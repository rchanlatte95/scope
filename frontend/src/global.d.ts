// global.d.ts
// Window type augmentations for the Scope frontend.
// This file is included in both tsconfig.json (src) and tsconfig.e2e.json (e2e)
// so Playwright specs can access dev/test seams set on window.

interface Window {
  /**
   * Dev/test seam set by ToastProvider in non-production builds.
   * Playwright specs use this to trigger toasts without a UI control.
   * Undefined in production (import.meta.env.PROD guard in ToastProvider).
   */
  __scopeToast?: {
    show: (opts: { message: string; actionLabel?: string; durationMs?: number }) => void;
  };
}
