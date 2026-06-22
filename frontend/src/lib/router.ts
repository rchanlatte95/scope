// =============================================================================
// Hash router (FE-2a).
//
// 11 nav areas + sub-routes. Pure parse + DOM-nav split so the parse half is
// unit-testable without window. No router dependency.
//
// Hash format: #/<area>[/<id>][?<params>]
//
// Known routes:
//   #/command                   Command (home/landing)
//   #/cron                      Cron Agents list
//   #/cron?sort=KEY&order=asc|desc  Cron list with URL-owned sort (T-2026-06-13-35)
//   #/cron/:id                  Cron detail
//   #/trace                     Trace Sessions list
//   #/trace/:id                 Trace detail
//   #/trace?session=KEY         Trace deep-link from a cron run (e2e-pinned)
//   #/trace?selectedSession=KEY&detailTab=TAB&from=ISO&to=ISO  Trace view state (T-2026-06-16-18)
//   #/cost-ledger               Cost Ledger
//   #/cost-ledger?agent=KEY&tab=TAB&detailTab=TAB&from=ISO&to=ISO  Cost Ledger view state (T-2026-06-16-18)
//   #/context-packs             Context Packs (internal/diagnostic only; neutralized in external builds)
//   #/proof-paths               Proof Paths
//   #/security-controls         Security Controls
//   #/audit-log                 Audit Log
//   #/org-uplink                Org Uplink
//   #/billing                   Billing
//   #/settings                  Settings (tabbed)
//   #/settings?tab=accessibility Settings > Accessibility tab
//
// Unknown routes default to Command (not a crash).
//
// PP-15: In external builds (IS_EXTERNAL_BUILD=true, the default), #/context-packs
// is treated as unknown and routes to Command. The ContextPacksView source is retained
// for internal/diagnostic builds (set VITE_EXTERNAL_BUILD=false).
// =============================================================================

import { IS_EXTERNAL_BUILD, GATED_AREAS } from './buildFlags';

export type RouteName =
  | 'command'
  | 'cron'
  | 'trace'
  | 'cost-ledger'
  | 'context-packs'
  | 'proof-paths'
  | 'security-controls'
  | 'audit-log'
  | 'org-uplink'
  | 'billing'
  | 'settings'
  | 'test-drp';

/**
 * Valid sort keys for the cron agent table (T-2026-06-13-35).
 * 'name' | 'status' | 'schedule' | 'last_run' | 'cost'
 */
export type CronSortKey = 'name' | 'status' | 'schedule' | 'last_run' | 'cost';

/** Sort order for the cron table. */
export type SortOrder = 'asc' | 'desc';

/** Valid cron sort keys as a set for validation. */
const CRON_SORT_KEYS: ReadonlySet<string> = new Set<CronSortKey>([
  'name',
  'status',
  'schedule',
  'last_run',
  'cost',
]);

export interface Route {
  name: RouteName;
  /**
   * For `cron` and `trace`: the detail ID segment (e.g. cron/:id or trace/:id).
   * Null when on the list view. Defaults to null if omitted.
   */
  id?: string | null;
  /**
   * For `trace`: the `session` query param for deep-linking a cron run's trace.
   * Preserved byte-compatible with the original buildHash({name:'trace',session}).
   * Defaults to null if omitted.
   */
  session?: string | null;
  /**
   * For `settings`: the `tab` query param (e.g. "accessibility").
   * For `cost-ledger`: the active breakdown tab name.
   * Defaults to null if omitted.
   */
  tab?: string | null;
  /**
   * For `cron`: the column sort key from ?sort=KEY (T-2026-06-13-35).
   * Null when using the default enabled-first sort.
   */
  sort?: CronSortKey | null;
  /**
   * For `cron`: the sort order from ?order=asc|desc (T-2026-06-13-35).
   * Null = use default (asc).
   */
  order?: SortOrder | null;
  // ---------------------------------------------------------------------------
  // View-state params added by T-2026-06-16-18 (ST-01: shared URL view-state
  // codec). These slots are defined here so ST-02 (trace wiring), ST-03
  // (cost-ledger wiring), and the T-16-14 date-picker task can all consume them
  // without another Route-breaking change (CF-1).
  // ---------------------------------------------------------------------------
  /**
   * For `cost-ledger`: the selected agent name/key used as a row filter.
   * Null means no agent is selected (show all). ST-03 writes this.
   */
  agent?: string | null;
  /**
   * For `cost-ledger` and `trace`: the active detail/breakdown tab name.
   * Distinct from `tab` (which is owned by settings). ST-02/ST-03 write this.
   */
  detailTab?: string | null;
  /**
   * For `cost-ledger` and `trace`: the date-range start as an ISO-8601 UTC
   * string (e.g. "2026-06-01T00:00:00Z"). Null means unbounded start.
   * The T-16-14 date-picker task fills this; ST-01 defines the slot.
   */
  from?: string | null;
  /**
   * For `cost-ledger` and `trace`: the date-range end as an ISO-8601 UTC
   * string (e.g. "2026-06-30T23:59:59Z"). Null means unbounded end.
   * The T-16-14 date-picker task fills this; ST-01 defines the slot.
   */
  to?: string | null;
  /**
   * For `trace`: the selected session key in the trace list view.
   * Distinct from the legacy `session` param (which is the cron-run deep-link).
   * ST-02 writes this; consumers must NOT confuse it with `session`.
   */
  selectedSession?: string | null;
  /**
   * For `proof-paths`: the fully-qualified symbol name from `?symbol=<FQN>`.
   * URL is the single source of truth (PP-01). FQN special chars are
   * encodeURIComponent-encoded in the URL and decoded back on parse.
   * Null when no symbol is selected (landing state).
   */
  symbol?: string | null;
  /**
   * For `proof-paths`: the sort column key for the PRIMARY (affected-tests)
   * table, namespaced as `tsort` so it never collides with the cron `sort`
   * slot or the dependents table's local sort (PP-12b two-table sort decision).
   * Null = default server order (confidence desc).
   */
  tsort?: string | null;
  /**
   * For `proof-paths`: the sort order for the affected-tests table, from
   * `torder=asc|desc`. Null = default (desc, strongest-first).
   */
  torder?: SortOrder | null;
  // ---------------------------------------------------------------------------
  // Pagination params (T-2026-06-17-03). Numeric, carried as decimal strings in
  // the URL (e.g. sessionsPage=2). Page 1 and default pageSize (10) are omitted
  // from the URL to keep deep-links clean.
  // ---------------------------------------------------------------------------
  /**
   * For `trace`: current page (1-based) for the session LIST.
   * Omitted when on page 1 (the default, no clutter in URL).
   */
  sessionsPage?: number | null;
  /**
   * For `trace`: page size for the session LIST (10|25|50|100).
   * Omitted when using the default (10).
   */
  sessionsPageSize?: number | null;
  /**
   * For `trace`: current page (1-based) for the event-card TIMELINE.
   * Omitted when on page 1.
   */
  eventsPage?: number | null;
  /**
   * For `trace`: page size for the event-card TIMELINE (10|25|50|100).
   * Omitted when using the default (10).
   */
  eventsPageSize?: number | null;
}

const DEFAULT_ROUTE: Route = { name: 'command', id: null, session: null, tab: null, sort: null, order: null } as const;

// PP-15 + T-2026-06-20-10: In external builds, all GATED_AREAS are excluded
// from KNOWN_AREAS so that deep-links to gated routes are treated as unknown
// and fall through to Command (the default). This is the single source of
// truth for route-level gating; the nav rail filtering in App.tsx uses
// isAreaAvailable() from buildFlags.ts.
//
// ALL_AREAS are the full set; in external builds we subtract GATED_AREAS.
// The ContextPacksView source (and other gated views) are retained for use
// in internal/diagnostic builds (VITE_EXTERNAL_BUILD=false).
const ALL_AREAS: readonly RouteName[] = [
  'command',
  'cron',
  'trace',
  'cost-ledger',
  'context-packs',
  'proof-paths',
  'security-controls',
  'audit-log',
  'org-uplink',
  'billing',
  'settings',
  'test-drp',
];

const KNOWN_AREAS: ReadonlySet<RouteName> = new Set<RouteName>(
  IS_EXTERNAL_BUILD
    ? ALL_AREAS.filter((area) => !GATED_AREAS.has(area))
    : ALL_AREAS,
);

/** Parses a location hash (e.g. "#/trace?session=abc") into a Route. Unknown -> Command. */
export function parseHash(hash: string): Route {
  const raw = hash.replace(/^#\/?/, '');
  if (!raw) return DEFAULT_ROUTE;

  const splitResult = raw.split('?');
  const pathPart = splitResult[0] ?? '';
  const query = splitResult[1] ?? '';
  const segments = pathPart.split('/').filter(Boolean);
  const area = segments[0] as RouteName | undefined;
  const id = segments[1] ?? null;

  if (!area || !KNOWN_AREAS.has(area)) return DEFAULT_ROUTE;

  const params = new URLSearchParams(query);

  if (area === 'trace') {
    const session = params.get('session');
    const selectedSession = params.get('selectedSession');
    const detailTab = params.get('detailTab');
    const from = params.get('from');
    const to = params.get('to');
    // Pagination params (T-2026-06-17-03): parse as integers; invalid values become null.
    const rawSessionsPage = params.get('sessionsPage');
    const rawSessionsPageSize = params.get('sessionsPageSize');
    const rawEventsPage = params.get('eventsPage');
    const rawEventsPageSize = params.get('eventsPageSize');
    const parsePositiveInt = (s: string | null): number | null => {
      if (s == null || s === '') return null;
      const n = parseInt(s, 10);
      return Number.isFinite(n) && n > 0 ? n : null;
    };
    return {
      name: 'trace',
      id: id ?? null,
      session: session != null && session.length > 0 ? session : null,
      tab: null,
      sort: null,
      order: null,
      selectedSession: selectedSession != null && selectedSession.length > 0 ? selectedSession : null,
      detailTab: detailTab != null && detailTab.length > 0 ? detailTab : null,
      from: from != null && from.length > 0 ? from : null,
      to: to != null && to.length > 0 ? to : null,
      sessionsPage: parsePositiveInt(rawSessionsPage),
      sessionsPageSize: parsePositiveInt(rawSessionsPageSize),
      eventsPage: parsePositiveInt(rawEventsPage),
      eventsPageSize: parsePositiveInt(rawEventsPageSize),
    };
  }

  if (area === 'settings') {
    const tab = params.get('tab');
    return {
      name: 'settings',
      id: null,
      session: null,
      tab: tab != null && tab.length > 0 ? tab : null,
      sort: null,
      order: null,
    };
  }

  if (area === 'cron' && (id == null || id === '')) {
    const rawSort = params.get('sort');
    const rawOrder = params.get('order');
    const sort = rawSort != null && CRON_SORT_KEYS.has(rawSort) ? (rawSort as CronSortKey) : null;
    const order = rawOrder === 'asc' || rawOrder === 'desc' ? rawOrder : null;
    return { name: 'cron', id: null, session: null, tab: null, sort, order };
  }

  if (area === 'cost-ledger') {
    const agent = params.get('agent');
    const tab = params.get('tab');
    const from = params.get('from');
    const to = params.get('to');
    const detailTab = params.get('detailTab');
    return {
      name: 'cost-ledger',
      id: null,
      session: null,
      tab: tab != null && tab.length > 0 ? tab : null,
      sort: null,
      order: null,
      agent: agent != null && agent.length > 0 ? agent : null,
      detailTab: detailTab != null && detailTab.length > 0 ? detailTab : null,
      from: from != null && from.length > 0 ? from : null,
      to: to != null && to.length > 0 ? to : null,
    };
  }

  // PP-01: URL-owned ?symbol= for the proof-paths route. The FQN may contain
  // special chars (.  <  >  +  (  )  ,) which are encodeURIComponent-encoded in
  // the URL; URLSearchParams decodes them transparently on parse.
  if (area === 'proof-paths') {
    const symbol = params.get('symbol');
    // PP-12b: the affected-tests (primary) table's sort is URL-owned under the
    // `tsort`/`torder` namespace so it cannot collide with the cron `sort` slot
    // or the dependents table (which owns its sort as local component state).
    const rawTsort = params.get('tsort');
    const rawTorder = params.get('torder');
    const torder = rawTorder === 'asc' || rawTorder === 'desc' ? rawTorder : null;
    return {
      name: 'proof-paths',
      id: null,
      session: null,
      tab: null,
      sort: null,
      order: null,
      symbol: symbol != null && symbol.length > 0 ? symbol : null,
      tsort: rawTsort != null && rawTsort.length > 0 ? rawTsort : null,
      torder,
    };
  }

  return { name: area, id: id ?? null, session: null, tab: null, sort: null, order: null };
}

/** Builds a hash string for a route (the inverse of parseHash). */
export function buildHash(route: Route): string {
  const session = route.session ?? null;
  const id = route.id ?? null;
  const tab = route.tab ?? null;
  const sort = route.sort ?? null;
  const order = route.order ?? null;
  // View-state params (T-2026-06-16-18 / ST-01).
  const agent = route.agent ?? null;
  const detailTab = route.detailTab ?? null;
  const from = route.from ?? null;
  const to = route.to ?? null;
  const selectedSession = route.selectedSession ?? null;
  // Pagination params (T-2026-06-17-03). Null = omit from URL (clean deep-links).
  const sessionsPage = route.sessionsPage ?? null;
  const sessionsPageSize = route.sessionsPageSize ?? null;
  const eventsPage = route.eventsPage ?? null;
  const eventsPageSize = route.eventsPageSize ?? null;

  // ---------------------------------------------------------------------------
  // Trace routes (list and detail). All params are encoded with encodeURIComponent
  // to match the original byte-compatible format (spaces -> %20, not +).
  // ---------------------------------------------------------------------------
  if (route.name === 'trace') {
    // Build ordered param list: session first (legacy compat), then new view-state.
    const parts: string[] = [];
    if (session != null && session !== '') parts.push(`session=${encodeURIComponent(session)}`);
    if (selectedSession != null && selectedSession !== '') parts.push(`selectedSession=${encodeURIComponent(selectedSession)}`);
    if (detailTab != null && detailTab !== '') parts.push(`detailTab=${encodeURIComponent(detailTab)}`);
    if (from != null && from !== '') parts.push(`from=${encodeURIComponent(from)}`);
    if (to != null && to !== '') parts.push(`to=${encodeURIComponent(to)}`);
    // Pagination: only emit when non-default (page > 1 or pageSize != 10).
    if (sessionsPage != null && sessionsPage > 1) parts.push(`sessionsPage=${String(sessionsPage)}`);
    if (sessionsPageSize != null && sessionsPageSize !== 10) parts.push(`sessionsPageSize=${String(sessionsPageSize)}`);
    if (eventsPage != null && eventsPage > 1) parts.push(`eventsPage=${String(eventsPage)}`);
    if (eventsPageSize != null && eventsPageSize !== 10) parts.push(`eventsPageSize=${String(eventsPageSize)}`);
    const qs = parts.join('&');
    // Trace detail by id: #/trace/:id[?params]
    if (id != null && id !== '') {
      return qs ? `#/trace/${encodeURIComponent(id)}?${qs}` : `#/trace/${encodeURIComponent(id)}`;
    }
    // Trace list: #/trace[?params]
    return qs ? `#/trace?${qs}` : '#/trace';
  }
  // Cost Ledger with view-state params (encodeURIComponent for consistency).
  if (route.name === 'cost-ledger') {
    const parts: string[] = [];
    if (agent != null && agent !== '') parts.push(`agent=${encodeURIComponent(agent)}`);
    if (tab != null && tab !== '') parts.push(`tab=${encodeURIComponent(tab)}`);
    if (detailTab != null && detailTab !== '') parts.push(`detailTab=${encodeURIComponent(detailTab)}`);
    if (from != null && from !== '') parts.push(`from=${encodeURIComponent(from)}`);
    if (to != null && to !== '') parts.push(`to=${encodeURIComponent(to)}`);
    const qs = parts.join('&');
    return qs ? `#/cost-ledger?${qs}` : '#/cost-ledger';
  }
  // PP-01: Proof Paths — emit ?symbol=<encoded FQN> when set; clean URL when null.
  // FQN special chars (.  <  >  +  (  )  ,) are encodeURIComponent-encoded so they
  // survive round-trips through window.location.hash unchanged.
  if (route.name === 'proof-paths') {
    const symbol = route.symbol ?? null;
    const tsort = route.tsort ?? null;
    const torder = route.torder ?? null;
    const parts: string[] = [];
    if (symbol != null && symbol !== '') parts.push(`symbol=${encodeURIComponent(symbol)}`);
    // Tests-table sort (namespaced; only emitted when a column sort is active).
    if (tsort != null && tsort !== '') {
      parts.push(`tsort=${encodeURIComponent(tsort)}`);
      if (torder != null) parts.push(`torder=${torder}`);
    }
    const qs = parts.join('&');
    return qs ? `#/proof-paths?${qs}` : '#/proof-paths';
  }
  // Cron detail by id.
  if (route.name === 'cron' && id != null && id !== '') {
    return `#/cron/${encodeURIComponent(id)}`;
  }
  // Cron list with sort/order (T-2026-06-13-35).
  if (route.name === 'cron' && sort != null) {
    const params = new URLSearchParams({ sort });
    if (order != null) params.set('order', order);
    return `#/cron?${params.toString()}`;
  }
  // Settings with tab.
  if (route.name === 'settings' && tab != null && tab !== '') {
    return `#/settings?tab=${encodeURIComponent(tab)}`;
  }
  return `#/${route.name}`;
}

/** Reads the current route from window.location. */
export function currentRoute(): Route {
  return parseHash(window.location.hash);
}

/** Navigates by setting the hash; the popstate/hashchange listener picks it up. */
export function navigate(route: Route): void {
  window.location.hash = buildHash(route);
}
