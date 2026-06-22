import { useDeferredValue, useEffect, useRef, useState } from 'react';
import TraceView from './views/TraceView';
import CronView from './views/CronView';
import { CronAgentDetail } from './views/CronView';
import CommandView from './views/CommandView';
import SettingsView from './views/SettingsView';
import CostLedgerView from './views/CostLedgerView';
import ContextPacksView from './views/ContextPacksView';
import ProofPathsView from './views/ProofPathsView';
import SecurityControlsView from './views/SecurityControlsView';
import AuditLogView from './views/AuditLogView';
import OrgUplinkView from './views/OrgUplinkView';
import BillingView from './views/BillingView';
import { DateRangePickerHarness } from './views/DateRangePickerHarness';
import { currentRoute, navigate, type Route, type RouteName } from './lib/router';
import { isAreaAvailable } from './lib/buildFlags';
import { ScopeLogo } from './components/ScopeLogo';
import { ExportCsvButtonE2eFixture } from './views/ExportCsvButtonE2eFixture';
import styles from './App.module.css';

/**
 * App shell for the Scope governance dashboard.
 *
 * Shell provides:
 *   1. Skip link -> #main-content
 *   2. Semantic nav rail with area links, aria-current on active item, and
 *      the Scope brand mark linking to #/command.
 *      - Desktop (>=769px): persistent left rail.
 *      - Mobile (<=768px): compact disclosure — a bar with brand + "Menu" toggle
 *        button (aria-expanded/aria-controls) that expands/collapses the nav list.
 *   3. <main id="main-content" tabIndex={-1}> — focusable for the skip link.
 *
 * The shell has NO title/subtitle spans. Each routed view owns its PageHeader.
 *
 * Mount contracts (preserved for parallel FE-3/FE-4a):
 *   <CronView />                           (no props)
 *   <TraceView initialSession={session} /> (session from deep-link)
 *
 * PP-15 + T-2026-06-20-10: Five areas are gated in external builds (the default).
 * They are filtered from NAV_ITEMS via isAreaAvailable() and their routes are
 * neutralized in the router (GATED_AREAS in buildFlags.ts).
 * Gated: context-packs, security-controls, audit-log, org-uplink, billing.
 * Task A (CommandView) imports isAreaAvailable/GATED_AREAS to clean up dead links.
 *
 * T-2026-06-21-01: Nav glyph rendering updated to support either a string glyph
 * or an inline SVG ReactNode. Five glyphs swapped to finalized SVG assets:
 * Command, Trace Sessions, Cost Ledger, Proof Paths, Settings.
 * All SVGs use currentColor; aria-hidden="true" / focusable="false" (decorative).
 * The visible nav label continues to supply each route's accessible name.
 */

// ---------------------------------------------------------------------------
// Inline SVG glyphs (decorative, aria-hidden, currentColor).
// These are rendered in the .navGlyph slot at ~18–20px via App.module.css.
// Each SVG is inlined so no external asset fetch is required and currentColor
// adapts automatically to light/dark theme, active inverse-fill, hover, focus.
// ---------------------------------------------------------------------------

/** Command nav glyph — scope-command-center-logo.svg */
const CommandGlyph = (
  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="square" strokeLinejoin="miter" aria-hidden="true" focusable="false" className={styles.navSvgGlyph}>
    <path d="M5 5h14v14H5z"/>
    <path d="M9 9h6v6H9z"/>
    <path d="M12 4v3M12 17v3M4 12h3M17 12h3"/>
  </svg>
);

/** Trace Sessions nav glyph — trace-sessions-symbol-t6-polished.svg */
const TraceSessionsGlyph = (
  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false" className={styles.navSvgGlyph}>
    <path d="M5.75 6.25h6.25v5.75h6.25v5.75" vectorEffect="non-scaling-stroke"/>
    <circle cx="5.75" cy="6.25" r="1.45" fill="currentColor" stroke="none"/>
    <circle cx="12" cy="12" r="1.45" fill="currentColor" stroke="none"/>
    <circle cx="18.25" cy="17.75" r="1.45" fill="currentColor" stroke="none"/>
  </svg>
);

/** Cost Ledger nav glyph — cost-ledger-logo-final.svg */
const CostLedgerGlyph = (
  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false" className={styles.navSvgGlyph}>
    <path d="M7.25 4.25h9.25a2.25 2.25 0 0 1 2.25 2.25v11a2.25 2.25 0 0 1-2.25 2.25H7.25z"/>
    <path d="M7.25 4.25v15.5"/>
    <path d="M4.75 7.25h4.5"/>
    <path d="M4.75 12h4.5"/>
    <path d="M4.75 16.75h4.5"/>
    <path d="M11 8.5h4.5"/>
    <path d="M11 12h5"/>
    <path d="M11 15.5h3.25"/>
    <circle cx="16" cy="15.5" r=".85" fill="currentColor" stroke="none"/>
  </svg>
);

/** Proof Paths nav glyph — scope-proof-paths-logo-rebalanced-1.5.svg */
const ProofPathsGlyph = (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="square" strokeLinejoin="miter" aria-hidden="true" focusable="false" xmlns="http://www.w3.org/2000/svg" className={styles.navSvgGlyph}>
    <rect x="4" y="5" width="5" height="5"/>
    <path d="M9 7.5h4v3.5h2.5"/>
    <path d="M13 11v6.5h2.5"/>
    <rect x="15.5" y="8.75" width="4.5" height="4.5"/>
    <rect x="15.5" y="15.25" width="4.5" height="4.5"/>
  </svg>
);

/** Settings nav glyph — settings-cog-symbol-s4-stroke-175.svg */
const SettingsGlyph = (
  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false" className={styles.navSvgGlyph}>
    <path vectorEffect="non-scaling-stroke" d="M10.85 3.75h2.3l.46 2.1c.61.16 1.19.4 1.72.72l1.81-1.1 1.62 1.62-1.1 1.81c.32.53.57 1.11.73 1.72l2.1.46v2.3l-2.1.46c-.16.61-.41 1.19-.73 1.72l1.1 1.81-1.62 1.62-1.81-1.1c-.53.32-1.11.56-1.72.72l-.46 2.1h-2.3l-.46-2.1a6.3 6.3 0 0 1-1.72-.72l-1.81 1.1-1.62-1.62 1.1-1.81a6.3 6.3 0 0 1-.73-1.72l-2.1-.46v-2.3l2.1-.46c.16-.61.41-1.19.73-1.72l-1.1-1.81 1.62-1.62 1.81 1.1c.53-.32 1.11-.56 1.72-.72l.46-2.1Z"/>
    <circle vectorEffect="non-scaling-stroke" cx="12" cy="12" r="2.9"/>
  </svg>
);

// ---------------------------------------------------------------------------
// NavItem type: glyph is either a string (Unicode/text) or a ReactNode (SVG).
// ---------------------------------------------------------------------------

interface NavItem {
  name: RouteName;
  label: string;
  /** A non-color glyph so the active item is never signalled by color alone.
   *  Either a Unicode/text string or an inline SVG ReactNode (decorative). */
  glyph: string | React.ReactNode;
}

// 11 top-level nav areas total. Order matches the IA in frontend-dev-guide.xml.
// In external builds (default) the five GATED_AREAS are filtered out via
// isAreaAvailable(), leaving 6 visible nav items.
//
// T-2026-06-21-01: Command, Trace Sessions, Cost Ledger, Proof Paths, and Settings
// now use inline SVG ReactNodes. Cron Agents, Context Packs, Security Controls,
// Audit Log, Org Uplink, and Billing retain their existing string glyphs.
const ALL_NAV_ITEMS: NavItem[] = [
  { name: 'command',           label: 'Command',          glyph: CommandGlyph },
  { name: 'cron',              label: 'Cron Agents',      glyph: '◷' },
  { name: 'trace',             label: 'Trace Sessions',   glyph: TraceSessionsGlyph },
  { name: 'cost-ledger',       label: 'Cost Ledger',      glyph: CostLedgerGlyph },
  { name: 'context-packs',     label: 'Context Packs',    glyph: '⊞' },
  { name: 'proof-paths',       label: 'Proof Paths',      glyph: ProofPathsGlyph },
  { name: 'security-controls', label: 'Security Controls',glyph: '⚿' },
  { name: 'audit-log',         label: 'Audit Log',        glyph: '☰' },
  { name: 'org-uplink',        label: 'Org Uplink',       glyph: '↑' },
  { name: 'billing',           label: 'Billing',          glyph: '◎' },
  { name: 'settings',          label: 'Settings',         glyph: SettingsGlyph },
];

// PP-15 + T-2026-06-20-10: filter out gated areas in external builds.
// isAreaAvailable() returns false for gated areas when IS_EXTERNAL_BUILD=true.
const NAV_ITEMS: NavItem[] = ALL_NAV_ITEMS.filter((item) => isAreaAvailable(item.name));

function renderView(route: Route) {
  switch (route.name) {
    case 'command':           return <CommandView />;
    case 'cron':
      // #/cron/:id -> agent detail page; #/cron -> list
      if (route.id != null && route.id !== '') return <CronAgentDetail agentId={route.id} />;
      return <CronView />;
    case 'trace':
      // #/trace/:id?session=KEY -> resolve the entry ref in that session and highlight.
      // #/trace/:id -> resolve entry ref in the default session (best-effort).
      // #/trace?session=KEY -> session deep-link (existing behavior, T-49).
      // #/trace -> list (existing behavior).
      if (route.id != null && route.id !== '') {
        return <TraceView initialSession={route.session ?? null} initialHighlightRef={route.id} />;
      }
      return <TraceView initialSession={route.session ?? null} />;
    case 'cost-ledger':       return <CostLedgerView />;
    case 'context-packs':     return <ContextPacksView />;
    case 'proof-paths':       return <ProofPathsView />;
    case 'security-controls': return <SecurityControlsView />;
    case 'audit-log':         return <AuditLogView />;
    case 'org-uplink':        return <OrgUplinkView />;
    case 'billing':           return <BillingView />;
    case 'settings':          return <SettingsView />;
    case 'test-drp':          return <DateRangePickerHarness />;
    default:                  return <CommandView />;
  }
}

// Unique id for the nav list disclosure panel (aria-controls / id pairing).
const NAV_LIST_ID = 'app-nav-list';

export default function App() {
  const [hash, setHash] = useState<string>(() => window.location.hash);
  const [route, setRoute] = useState<Route>(() => currentRoute());
  // Nav highlight uses the urgent `route` so the active inverse-fill paints
  // immediately on click; the (potentially heavy) destination view renders from
  // the deferred value so its synchronous mount never blocks that paint.
  const deferredRoute = useDeferredValue(route);

  // Mobile nav disclosure state. The nav list is hidden by default on mobile and
  // toggled by the "Menu" button. Desktop (>=769px) always shows the list via CSS
  // (navListExpanded class is not used at desktop width; the CSS handles this).
  const [mobileNavOpen, setMobileNavOpen] = useState(false);
  const menuButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    const onHashChange = () => {
      setHash(window.location.hash);
      setRoute(currentRoute());
    };
    window.addEventListener('hashchange', onHashChange);
    return () => { window.removeEventListener('hashchange', onHashChange); };
  }, []);

  // E2e test fixture route — only active in dev (not visible in nav).
  // ST-01 Playwright spec navigates to /#/__e2e/export-csv to render ExportCsvButton
  // in isolation with predictable data. The hash does NOT conflict with the typed
  // RouteName union; this guard fires before the router switch.
  if (hash === '#/__e2e/export-csv') {
    return <ExportCsvButtonE2eFixture />;
  }

  function handleNavLinkClick(e: React.MouseEvent, item: NavItem) {
    e.preventDefault();
    navigate({ name: item.name, id: null, session: null, tab: null });
    // Close mobile menu after navigation and return focus to the menu button
    // so the user has a clear return point (WCAG 2.1 SC 3.2.2).
    setMobileNavOpen(false);
    // Focus the menu button after a tick so the nav list has collapsed and
    // the button is visible again.
    setTimeout(() => { menuButtonRef.current?.focus(); }, 0);
  }

  return (
    <div className={styles.shell}>
      <a className={styles.skipLink} href="#main-content">
        Skip to main content
      </a>

      <nav className={styles.rail} aria-label="Dashboard areas">
        {/* Mobile header row: brand mark + "Menu" toggle button.
            The toggle button is only interactive on mobile (<=768px); on desktop it
            is visually hidden via CSS and the nav list is always visible.
            aria-expanded on the button reports disclosure state; aria-controls
            points at the nav list by id so AT can locate the controlled region. */}
        <div className={styles.railHeader}>
          {/* Scope brand mark: returns to Command (the home area).
              The link has aria-label="Scope" so the accessible name is 'Scope'
              regardless of whether the logo img alt is announced (the img alt=""
              with aria-hidden suppresses announcement here since the link itself
              carries the name). The visible focus ring is on this <a> container
              (not on SVG paths) via :focus-visible in index.css. */}
          <a
            className={styles.brand}
            href="#/command"
            aria-label="Scope"
            onClick={(e) => { e.preventDefault(); navigate({ name: 'command', id: null, session: null, tab: null }); }}
          >
            <ScopeLogo size={40} decorative />
            <span className={styles.wordmark}>Scope</span>
          </a>

          {/* Menu toggle — mobile only. Hidden on desktop via CSS (.menuToggle). */}
          <button
            ref={menuButtonRef}
            className={styles.menuToggle}
            type="button"
            aria-expanded={mobileNavOpen}
            aria-controls={NAV_LIST_ID}
            onClick={() => { setMobileNavOpen((prev) => !prev); }}
          >
            <span aria-hidden="true">{mobileNavOpen ? '✕' : '☰'}</span>
            {mobileNavOpen ? 'Close' : 'Menu'}
          </button>
        </div>

        <ul
          id={NAV_LIST_ID}
          className={[styles.navList, mobileNavOpen ? styles.navListOpen : ''].filter(Boolean).join(' ')}
        >
          {NAV_ITEMS.map((item) => {
            const active = route.name === item.name;
            return (
              <li key={item.name}>
                <a
                  className={`${styles.navLink ?? ''} ${active ? (styles.navLinkActive ?? '') : ''}`}
                  href={`#/${item.name}`}
                  aria-current={active ? 'page' : undefined}
                  onClick={(e) => { handleNavLinkClick(e, item); }}
                >
                  {/* Glyph slot: renders either an inline SVG ReactNode or a string
                      Unicode/text glyph. Both paths are aria-hidden so the visible
                      nav label supplies the route's accessible name (announced once). */}
                  {typeof item.glyph === 'string' ? (
                    <span className={styles.navGlyph} aria-hidden="true">
                      {item.glyph}
                    </span>
                  ) : (
                    <span className={styles.navGlyph}>
                      {item.glyph}
                    </span>
                  )}
                  {item.label}
                </a>
              </li>
            );
          })}
        </ul>
      </nav>

      <div className={styles.content}>
        <main id="main-content" className={styles.main} tabIndex={-1}>
          {renderView(deferredRoute)}
        </main>
      </div>
    </div>
  );
}
