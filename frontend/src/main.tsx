import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import App from './App';
import './index.css';
import { readPrefs, applyPrefs } from './lib/prefs';
import { queryClient } from './api/queryClient';
import { ToastProvider } from './components/ToastProvider';

// Apply persisted display preferences at the document root before first paint
// so the initial render uses the correct theme, font scale, and accessibility
// preferences. This runs synchronously before React mounts.
//
// Guard: if document is unavailable (SSR / test env without DOM), skip silently.
if (typeof document !== 'undefined') {
  try {
    const prefs = readPrefs();
    applyPrefs(prefs, document.documentElement);
  } catch {
    // Storage unavailable or prefs corrupted — continue with CSS defaults.
    // :root defaults to light theme; system prefers-color-scheme media query
    // handles dark mode without any JavaScript.
  }
}

const rootEl = document.getElementById('root');
if (rootEl === null) throw new Error('Root element #root not found in DOM');
createRoot(rootEl).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      {/* ToastProvider: mounts the imperative toast stack at the app root.
          Any component can call useToast().show(...) to show a notification.
          The ToastStack is rendered inside the provider, outside <App>, so it
          sits above the page z-stack and is not clipped by overflow:hidden panels. */}
      <ToastProvider>
        <App />
      </ToastProvider>
      {/* ReactQueryDevtools: DEV-only, suppressed when navigator.webdriver is true
          (Playwright sets webdriver=true on the test browser) so the floating button
          does not appear during e2e runs. Tree-shaken in production builds. */}
      {import.meta.env.DEV && typeof navigator !== 'undefined' && !navigator.webdriver && (
        <ReactQueryDevtools initialIsOpen={false} />
      )}
    </QueryClientProvider>
  </StrictMode>,
);
