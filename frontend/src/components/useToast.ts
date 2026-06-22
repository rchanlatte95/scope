import { useContext } from 'react';
import { ToastContext, type ToastContextValue } from './ToastProvider';

// =============================================================================
// useToast hook (ST-03)
//
// Returns the toast imperative API from the nearest ToastProvider.
// Split into a separate .ts file so ToastProvider.tsx can be a pure
// component-only file (satisfies react-refresh/only-export-components).
//
// Usage:
//   const { show } = useToast();
//   const dismiss = show({ message: 'Done', actionLabel: 'Undo', onAction: handleUndo });
// =============================================================================

/**
 * Returns the toast imperative API. Must be used inside a <ToastProvider>.
 *
 * @example
 *   const { show } = useToast();
 *   const dismiss = show({ message: 'Deleted', actionLabel: 'Undo', onAction: handleUndo });
 *   // dismiss() programmatically removes the toast before its auto-dismiss fires.
 */
export function useToast(): ToastContextValue {
  const ctx = useContext(ToastContext);
  if (ctx === null) {
    throw new Error('useToast must be called inside a <ToastProvider>.');
  }
  return ctx;
}
