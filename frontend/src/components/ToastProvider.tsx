import {
  createContext,
  useCallback,
  useEffect,
  useId,
  useState,
  type ReactNode,
} from 'react';
import ToastStack from './Toast';

// =============================================================================
// ToastProvider (ST-03)
//
// Provides the ToastContext to the app. Wrap at the app root so any component
// can call useToast() (imported from ./useToast) to show notifications.
//
// Imperative API: show({ message, actionLabel?, onAction?, durationMs? })
// Returns a dismiss function so callers can programmatically remove the toast.
//
// Design contract:
//   - Generic: no session/delete language baked in
//   - Accessible: role="status" aria-live="polite", real <button> for actions
//   - Auto-dismiss after durationMs (default 9000ms)
//   - Auto-dismiss PAUSED while toast is focused or hovered
//   - Escape dismisses the toast
//   - Stacking: multiple toasts displayed simultaneously
//
// Dev/test seam: window.__scopeToast is set in non-production environments so
// Playwright specs can trigger toasts without a UI control. Tree-shaken in
// production (import.meta.env.PROD guard). Type declared in src/global.d.ts.
// =============================================================================

// ---------------------------------------------------------------------------
// Public types
// ---------------------------------------------------------------------------

export interface ToastOptions {
  /** The human-readable message to display. */
  message: string;
  /** Optional label for the single action button. */
  actionLabel?: string;
  /** Called when the user activates the action button. */
  onAction?: () => void;
  /**
   * How long (in ms) before the toast auto-dismisses.
   * Default: 9000ms. Set to 0 to disable auto-dismiss.
   */
  durationMs?: number;
}

export interface ToastEntry extends Required<Omit<ToastOptions, 'onAction'>> {
  id: string;
  onAction: (() => void) | undefined;
}

export interface ToastContextValue {
  show: (opts: ToastOptions) => () => void;
}

// ---------------------------------------------------------------------------
// Context (exported so ./useToast.ts can access it without a circular import)
// ---------------------------------------------------------------------------

// eslint-disable-next-line react-refresh/only-export-components
export const ToastContext = createContext<ToastContextValue | null>(null);

// ---------------------------------------------------------------------------
// Module-level counter for unique toast IDs.
// ---------------------------------------------------------------------------

const DEFAULT_DURATION_MS = 9000;
let toastCounter = 0;

// ---------------------------------------------------------------------------
// ToastProvider component
// ---------------------------------------------------------------------------

export function ToastProvider({ children }: { children: ReactNode }) {
  // useId gives a stable React-managed prefix; toastCounter ensures uniqueness
  // across multiple show() calls even within the same render tree.
  const idPrefix = useId();
  const [toasts, setToasts] = useState<ToastEntry[]>([]);

  const dismiss = useCallback((id: string) => {
    setToasts((prev) => prev.filter((t) => t.id !== id));
  }, []);

  const show = useCallback(
    (opts: ToastOptions): (() => void) => {
      toastCounter += 1;
      const id = `${idPrefix}-toast-${String(toastCounter)}`;
      const entry: ToastEntry = {
        id,
        message: opts.message,
        actionLabel: opts.actionLabel ?? '',
        onAction: opts.onAction,
        durationMs: opts.durationMs ?? DEFAULT_DURATION_MS,
      };
      setToasts((prev) => [...prev, entry]);
      return () => { dismiss(id); };
    },
    [idPrefix, dismiss],
  );

  // Dev/test seam: expose show() on window so Playwright specs can trigger
  // toasts without a real UI action. Cleaned up on unmount.
  useEffect(() => {
    if (import.meta.env.PROD) return;
    window.__scopeToast = { show };
    return () => { delete window.__scopeToast; };
  }, [show]);

  return (
    <ToastContext.Provider value={{ show }}>
      {children}
      <ToastStack toasts={toasts} onDismiss={dismiss} />
    </ToastContext.Provider>
  );
}
