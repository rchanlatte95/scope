import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  fetchTraceSessions,
  fetchTraceGraph,
  fetchTraceFull,
  type GraphNode,
  type GraphEdge,
  type Money,
  type TraceEntry,
  type TraceSubject,
  type TraceSession,
  type ObservabilityLimits,
} from '../api/traceApi';
import { queryKeys, STALE_COMMANDS_TRACES_MS, STALE_GRAPH_MS } from '../api/queryClient';
import { resolveReference, type Resolvable } from '../lib/displayId';
import { resolveZone, zoneAbbr, formatAbsolute } from '../lib/time';
import { readPrefs } from '../lib/prefs';
import { deriveLimitNotices } from '../lib/traceLimits';
import { traceKindLabel, traceKindTooltip, userSubmitLabel, userSubmitTooltip, isInitiatingSubmit } from '../lib/traceKind';
import { currentRoute, navigate } from '../lib/router';
import { decodeTraceState, encodeTraceState, encodeRange } from '../lib/viewState';
import { usePagination, computePageCount } from '../hooks/usePagination';
import { DEFAULT_PAGE_SIZE } from '../lib/viewState';
import type { DateRange } from '../lib/dateRange';
import SessionGraph from '../components/SessionGraph';
import CopyChip from '../components/CopyChip';
import AccountingPanel from '../components/AccountingPanel';
import { PageHeader } from '../components/PageHeader';
import { CopyLinkButton } from '../components/CopyLinkButton';
import { TraceLegend } from '../components/TraceLegend';
import { EvidenceTable, type EvidenceColumn } from '../components/EvidenceTable';
import { ToolTypeMark } from '../components/ToolTypeMark';
import { StatusBadge } from '../components/StatusBadge';
import { StatusGlyphBlock } from '../components/StatusGlyphBlock';
import { CostProvenanceChip } from '../components/CostProvenanceChip';
import { EmptyState } from '../components/EmptyState';
import { Pagination } from '../components/Pagination';
import Snippet from '../components/Snippet';
import Tooltip from '../components/Tooltip';
import ScrollToTop from '../components/ScrollToTop';
import { toCostProvenance } from '../lib/provenance';
import { SessionPicker, type PickerTab } from '../components/SessionPicker';
import TraceSummaryPanel from '../components/TraceSummaryPanel';
import {
  collapseToToolType,
  TOOL_TYPE_LABEL,
  TOOL_TYPE_TOKEN,
  type TraceToolType,
} from '../lib/traceToolType';
import styles from './TraceView.module.css';

/** localStorage key for per-surface page size persistence (events timeline). */
const LS_EVENTS_PAGE_SIZE = 'scope.events.pageSize';

const TIMELINE_COLUMN_COUNT = 7;

/** Read the persisted events page size from localStorage, falling back to DEFAULT_PAGE_SIZE. */
function readEventsPageSize(): number {
  try {
    const raw = localStorage.getItem(LS_EVENTS_PAGE_SIZE);
    if (raw != null) {
      const n = parseInt(raw, 10);
      if ([10, 25, 50, 100].includes(n)) return n;
    }
  } catch { /* localStorage unavailable */ }
  return DEFAULT_PAGE_SIZE;
}

/** Extract a short target string from an event's content fields. */
function extractEntryTarget(entry: TraceEntry): string | null {
  if (entry.input_summary != null && entry.input_summary !== '') {
    const s = entry.input_summary.trim();
    return s.length <= 40 ? s : s.slice(0, 38) + '...';
  }
  if (entry.operation != null && entry.operation !== '') return entry.operation;
  if (entry.query_text != null && entry.query_text !== '') {
    const s = entry.query_text.trim();
    return s.length <= 40 ? s : s.slice(0, 38) + '...';
  }
  return null;
}

function isUserSubmitEntry(entry: TraceEntry): boolean {
  return entry.kind === 'event' && entry.event_type === 'user_submit';
}

function deriveEntryDescription(entry: TraceEntry): string {
  if (isUserSubmitEntry(entry)) {
    if (entry.input_snippet != null && entry.input_snippet !== '') {
      const s = entry.input_snippet.trim();
      return s.length <= 80 ? s : s.slice(0, 78) + '...';
    }
    return userSubmitLabel(entry.user_submit_subtype);
  }
  if (entry.kind === 'span') {
    return entry.label ?? entry.operation ?? entry.kind;
  }
  if (entry.kind === 'event') {
    const toolName = entry.tool?.name;
    if (toolName != null && toolName !== '') {
      const target = extractEntryTarget(entry);
      return target != null ? `${toolName} ${target}` : toolName;
    }
  }
  return entry.tool?.name ?? entry.label ?? entry.operation ?? entry.kind;
}

function getEntryToolType(entry: TraceEntry): TraceToolType {
  if (isUserSubmitEntry(entry)) return 'operator';
  if (entry.kind === 'event') return collapseToToolType(entry.tool?.taxonomy_id);
  if (entry.kind === 'span') return 'shell';
  return 'find';
}

function getCostProvenanceForMoney(money: Money | null | undefined): ReturnType<typeof toCostProvenance> {
  if (money == null) return toCostProvenance('unavailable');
  if (money.cost_provenance != null && money.cost_provenance !== '') {
    return toCostProvenance(money.cost_provenance);
  }
  if (money.source === 'exact') return toCostProvenance('joined_exact');
  if (money.source === 'wire') return toCostProvenance('wire_exact');
  if (money.source === 'estimate') return toCostProvenance('scope_estimated');
  return toCostProvenance('unavailable');
}

function TraceEntryInlineDetail({ entry, zone }: { entry: TraceEntry; zone: string }) {
  const isUserSubmit = isUserSubmitEntry(entry);
  const toolType = getEntryToolType(entry);
  const description = deriveEntryDescription(entry);
  const ts = entry.created_at ?? entry.started_at;
  const kindLabel = isUserSubmit ? userSubmitLabel(entry.user_submit_subtype) : traceKindLabel(entry.kind);
  const kindTooltip = isUserSubmit ? userSubmitTooltip(entry.user_submit_subtype) : traceKindTooltip(entry.kind);

  const metaItems: string[] = [];
  if (ts != null && ts !== '') metaItems.push(formatAbsolute(ts, zone));
  if (entry.duration_ms != null) metaItems.push(`${String(entry.duration_ms)} ms`);
  if (entry.total_elapsed_ms != null) metaItems.push(`${String(entry.total_elapsed_ms)} ms total`);
  if (entry.status != null && entry.status !== '') metaItems.push(entry.status);
  if (entry.time_source != null && entry.time_source !== '') metaItems.push(entry.time_source);
  if (entry.tokens?.total != null) metaItems.push(`${entry.tokens.total.toLocaleString()} tok (${entry.tokens.source})`);
  if (entry.turn_index != null) metaItems.push(`turn ${String(entry.turn_index)}`);
  if (entry.message_recipient != null && entry.message_recipient !== '') metaItems.push(`to ${entry.message_recipient}`);

  return (
    <section
      id={`entry-${entry.id}`}
      className={styles.timelineDetailPanel}
      aria-label={`Details for entry ${entry.display_id}`}
      data-display-id={entry.display_id}
      data-alias={entry.alias ?? ''}
    >
      <header className={styles.timelineDetailHeader}>
        <div className={styles.timelineDetailTitleGroup}>
          <span className={styles.timelineDetailKicker}>
            {kindTooltip != null && kindTooltip !== '' ? (
              <Tooltip content={<span>{kindTooltip}</span>}>
                <span>{kindLabel}</span>
              </Tooltip>
            ) : (
              kindLabel
            )}
          </span>
          <h3 className={styles.timelineDetailTitle}>
            {entry.alias != null && entry.alias !== '' ? (
              <>
                <span className={styles.timelineDetailAlias}>{entry.alias}</span>
                <span aria-hidden="true"> · </span>
              </>
            ) : null}
            {isUserSubmit && entry.input_snippet == null ? 'Prompt not captured' : description}
          </h3>
        </div>
        <div className={styles.timelineDetailAffordances}>
          <ToolTypeMark toolType={toolType} className={styles.timelineDetailToolMark} />
          <CopyChip value={entry.display_id} kind="id" label="id" />
          {entry.alias != null && entry.alias !== '' && (
            <CopyChip value={entry.alias} kind="alias" label="alias" />
          )}
        </div>
      </header>

      {metaItems.length > 0 && (
        <div className={styles.timelineDetailMeta}>
          {metaItems.map((item) => (
            <span key={item}>{item}</span>
          ))}
        </div>
      )}

      {entry.caps != null && entry.caps.length > 0 && (
        <div className={styles.timelineDetailCapsProvenance}>
          <span className={styles.timelineDetailCapsLabel}>
            {entry.caps_universe != null
              ? `${String(entry.caps.length)}/${String(entry.caps_universe.length)} fields captured`
              : `${String(entry.caps.length)} fields captured`}
          </span>
          <span className={styles.timelineDetailCapsSection}>
            <span className={styles.timelineDetailCapsHeading}>present:</span>
            {' '}{entry.caps.join(', ')}
          </span>
          {entry.caps_universe != null && (() => {
            const missing = entry.caps_universe.filter((f) => entry.caps?.includes(f) !== true);
            return missing.length > 0 ? (
              <span className={styles.timelineDetailCapsSection}>
                <span className={styles.timelineDetailCapsHeading}>missing:</span>
                {' '}{missing.join(', ')}
              </span>
            ) : null;
          })()}
        </div>
      )}

      <div className={styles.timelineDetailFacts}>
        <span>Tool type: {TOOL_TYPE_LABEL[toolType]}</span>
        {entry.tool?.name != null && entry.tool.name !== '' && <span>Tool: {entry.tool.name}</span>}
        {entry.tool?.taxonomy_id != null && entry.tool.taxonomy_id !== '' && <span>Taxonomy: {entry.tool.taxonomy_id}</span>}
        <span>Display ref: {entry.display_id}</span>
        {entry.request_id != null && entry.request_id !== '' && <span>Request: {entry.request_id}</span>}
        {entry.tool_use_id != null && entry.tool_use_id !== '' && <span>Tool use: {entry.tool_use_id}</span>}
      </div>

      {isUserSubmit && entry.input_snippet != null && entry.input_snippet !== '' && (
        <Snippet label="prompt" text={entry.input_snippet} />
      )}
      {!isUserSubmit && entry.input_snippet != null && entry.input_snippet !== '' && <Snippet label="input" text={entry.input_snippet} />}
      {entry.output_snippet != null && entry.output_snippet !== '' && <Snippet label="output" text={entry.output_snippet} />}
      {entry.query_text != null && entry.query_text !== '' && <Snippet label="query" text={entry.query_text} />}
      {entry.error_message != null && entry.error_message !== '' && <Snippet label="error" text={entry.error_message} />}
      {!isUserSubmit && entry.input_summary != null && entry.input_summary !== '' && (entry.input_snippet == null || entry.input_snippet === '') && (
        <Snippet label="input summary" text={entry.input_summary} />
      )}
      {entry.output_summary != null && entry.output_summary !== '' && (entry.output_snippet == null || entry.output_snippet === '') && (
        <Snippet label="output summary" text={entry.output_summary} />
      )}

      <div className={styles.timelineDetailCost}>
        <span className={styles.timelineDetailCostLabel}>Cost</span>
        <CostProvenanceChip
          variant="single"
          costUsd={entry.cost?.value_usd}
          provenance={getCostProvenanceForMoney(entry.cost)}
        />
      </div>
    </section>
  );
}

// =============================================================================
// TraceView: the main interactive trace view. FE-4a migration.
//
// Changes from pre-FE-4a:
//  - Added PageHeader (TraceView now owns its page header per mount contract).
//  - Timeline is now EvidenceTable (FE-1 primitive) with absolute timestamps,
//    ToolTypeMark, StatusBadge semantics, and shared selection with the graph.
//  - Graph + timeline SHARE SELECTION: clicking a graph node highlights the
//    timeline row; clicking a timeline row highlights the graph node.
//  - TraceLegend shown in the trace tab toolbar.
//  - Tool colors confined to SessionGraph, ToolTypeMark, TraceLegend per contract.
//  - No raw hex anywhere. All status colors -> --scope-color-*.
//
// R2 (T-2026-06-14-13, Lane T1): sessions, graph, and trace-first-page loads are
//   now driven by useQuery (TanStack Query) using queryKeys + STALE_* constants
//   from queryClient.ts. Manual loading/error useState removed where replaced.
//   Load-more (paginated) is a manual fetchQuery-with-cursor that appends to the
//   entries list; this preserves the existing "append pages" UX without the full
//   data-model rewrite that useInfiniteQuery would require.
//   Deep-link direct load stays as an imperative fetchQuery branch (conditional,
//   one-shot; not a steady-state query).
//
// Mount contract (FE-2a):
//   `export default function TraceView({ initialSession }: { initialSession: string | null })`
//   FE-2a's shell renders <TraceView initialSession={session} />.
//   PRESERVE: prop name `initialSession`, default export, shape unchanged.
// =============================================================================

/** Which view tab is active: the chronological trace, or the accounting rollup. */
type TraceTab = 'trace' | 'accounting';

/** Valid TraceTab values as a set for URL decoding. */
const VALID_TRACE_TABS: ReadonlySet<string> = new Set<TraceTab>(['trace', 'accounting']);

/**
 * When a deep-link session key was not found in the sessions list AND the
 * direct fetch returned no data, we show an explicit not-found state rather
 * than silently swapping to a different session.
 */
type DeepLinkState =
  | { kind: 'none' }
  | { kind: 'outside-list'; sessionKey: string }
  | { kind: 'not-found'; sessionKey: string };

interface GraphState {
  root: string;
  nodes: GraphNode[];
  edges: GraphEdge[];
}

interface TraceViewProps {
  /**
   * Optional session key to preselect, used when arriving from a cron run's deep-link
   * (#/trace?session=KEY). When the key is not among the loaded sessions, the first
   * session is selected as before and the deep-link is a no-op rather than an error.
   */
  initialSession?: string | null;
  /**
   * Optional trace-entry id (display_id or alias) to highlight and scroll to.
   * Used by the #/trace/:id route: the URL encodes a specific trace entry's id,
   * which is resolved against the loaded entries and then highlighted + scrolled.
   * TraceView resolves this internally via resolveReference once entries are loaded.
   * This is a SEAM, not a fork: TraceView's normal behavior is unchanged when null.
   */
  initialHighlightRef?: string | null;
}

export default function TraceView({ initialSession = null, initialHighlightRef = null }: TraceViewProps) {
  const queryClient = useQueryClient();

  // ---------------------------------------------------------------------------
  // ST-02 (T-2026-06-16-18): read URL view state on mount.
  //
  // Two separate "initial session" sources:
  //   - `initialSession` prop  = legacy `session` URL param (#/trace?session=KEY).
  //     Passed from App.tsx for backward compatibility (e2e-pinned).
  //   - `route.selectedSession` = new URL param written by interactive selection.
  //     Read once at mount from currentRoute(); updated by user interaction.
  //
  // Priority: selectedSession > initialSession (if both are present, prefer
  // selectedSession since it was set by user interaction on this page).
  //
  // `route.detailTab` = active detail tab (trace|accounting), encoded as a URL
  // param. Decoded once at mount; written back on tab switch.
  // ---------------------------------------------------------------------------
  const mountRouteState = useMemo(() => {
    const r = currentRoute();
    const ts = decodeTraceState(r);
    const validTab = ts.detailTab != null && VALID_TRACE_TABS.has(ts.detailTab)
      ? (ts.detailTab as TraceTab)
      : null;
    // Extract range from URL: present when the user previously changed the window
    // (or arrived via a deep-link with ?from=...&to=...). Null = use picker default (24h).
    const rangeFromUrl = ts.from != null && ts.to != null
      ? { from: ts.from, to: ts.to }
      : null;
    return {
      selectedSession: ts.selectedSession,
      detailTab: validTab,
      range: rangeFromUrl,
      // Pagination params from URL (T-2026-06-17-03):
      sessionsPage: ts.sessionsPage ?? 1,
      eventsPage: ts.eventsPage ?? 1,
      eventsPageSize: ts.eventsPageSize ?? null,
    };
  }, []);

  // The effective initial session: URL selectedSession takes priority over the legacy prop.
  const effectiveInitialSession = mountRouteState.selectedSession ?? initialSession;

  const [selected, setSelected] = useState<string | null>(null);
  // Toggle: true = show UTC; false = show the user-selected zone from prefs.
  // Default to false (show user's zone) since it is more locally meaningful.
  const [showUtc, setShowUtc] = useState(false);
  const [tab, setTab] = useState<TraceTab>(mountRouteState.detailTab ?? 'trace');
  // Deep-link resolution state (T-49).
  const [deepLinkState, setDeepLinkState] = useState<DeepLinkState>({ kind: 'none' });
  // Picker tab: determined from effectiveInitialSession prefix (cron- -> 'cron', else 'cli').
  // Computed once at mount via useState initializer — not re-derived on subsequent renders.
  // This drives the SessionPicker's initialTab prop for deep-link tab auto-selection.
  const pickerTab: PickerTab = effectiveInitialSession?.startsWith('cron-') === true ? 'cron' : 'cli';

  // Re-read the timezone pref from localStorage on each render so that a Settings change
  // made while the Trace page is visible takes effect without a full remount.
  // readPrefs() is fast (one localStorage.getItem per key) — no memoization needed.
  const timezonePref = readPrefs().timezone;
  const resolvedZone = showUtc ? 'UTC' : resolveZone(timezonePref);
  const userZoneAbbr = zoneAbbr(resolveZone(timezonePref));

  // Event-card timeline pagination (T-2026-06-17-03).
  // totalCount comes from body.total_count on each page fetch; 0 = unknown (older server).
  const [eventsTotalCount, setEventsTotalCount] = useState(0);
  const [eventsPageState, eventsPageActions] = usePagination(
    eventsTotalCount,
    mountRouteState.eventsPage,
    mountRouteState.eventsPageSize ?? readEventsPageSize(),
  );

  const [refQuery, setRefQuery] = useState('');
  const [highlightId, setHighlightId] = useState<string | null>(null);
  const [refNotice, setRefNotice] = useState<string | null>(null);
  const [expandedRowIds, setExpandedRowIds] = useState<ReadonlySet<string>>(() => new Set<string>());

  const [limitNoticesOpen, setLimitNoticesOpen] = useState(false);
  // Static string id — does not change across renders, so a ref is not needed.
  const limitPanelId = 'trace-limit-notices-panel';

  // ---- R2: sessions list via useQuery ----
  // Used only for the initial session-resolution logic (deep-link handling and
  // auto-selecting the first session). The picker manages its own filtered queries.
  // T-49: when a deep-link session key is NOT in the loaded list, attempt a
  // direct load via fetchTraceFull (this endpoint serves by id even when the
  // session is outside the capped /sessions list). If the direct load returns
  // data, show that session and label it "outside current view". If the direct
  // load returns no data (404 / empty), show an explicit not-found state.
  // Never silently swap to list[0] when a deep-link key was given.
  const sessionsQuery = useQuery({
    queryKey: queryKeys.traceSessions(),
    queryFn: () => fetchTraceSessions(),
    staleTime: STALE_COMMANDS_TRACES_MS,
  });

  // When the sessions query first succeeds, resolve the initial selection
  // (including deep-link logic). We track whether we've done this with a ref so
  // it only runs once per mount (not on background refetch).
  const didResolveInitialSelection = useRef(false);
  useEffect(() => {
    if (sessionsQuery.status !== 'success' || didResolveInitialSelection.current) return;
    // sessionsQuery.data should always be defined when status === 'success',
    // but guard defensively since the mock in tests may return a partial shape.
    const list = sessionsQuery.data.body.sessions;
    if (list.length === 0) return;

    didResolveInitialSelection.current = true;

    if (effectiveInitialSession == null || effectiveInitialSession === '') {
      // Prefer the first session with data; fall back to list[0] if all are empty.
      // has_data absent (older server) is treated as true (conservative).
      const firstWithData = list.find((s) => s.has_data !== false) ?? list[0];
      if (firstWithData !== undefined) {
        // eslint-disable-next-line react-hooks/set-state-in-effect
        setSelected(firstWithData.id);
      }
      return;
    }

    const inList = list.some((s) => s.id === effectiveInitialSession);
    if (inList) {
      setDeepLinkState({ kind: 'none' });
      setSelected(effectiveInitialSession);
      return;
    }

    // Cron session keys (cron-{uuid7}) are served by a separate endpoint and will never
    // appear in the agent sessions list. Accept them directly without a trace-full fetch —
    // the picker's cron query handles their validation and display.
    if (effectiveInitialSession.startsWith('cron-')) {
      setDeepLinkState({ kind: 'none' });
      setSelected(effectiveInitialSession);
      return;
    }

    // Deep-link key is NOT in the loaded list: attempt direct load.
    void queryClient.fetchQuery({
      queryKey: queryKeys.traceFull(effectiveInitialSession, null),
      queryFn: () => fetchTraceFull(effectiveInitialSession, 1, null),
      staleTime: STALE_COMMANDS_TRACES_MS,
    }).then((traceData) => {
      // TraceBody.subject is always present (non-optional); use entries to detect a real payload.
      const hasEntries = traceData.body.entries.length > 0;
      if (hasEntries) {
        setDeepLinkState({ kind: 'outside-list', sessionKey: effectiveInitialSession });
        setSelected(effectiveInitialSession);
      } else {
        setDeepLinkState({ kind: 'not-found', sessionKey: effectiveInitialSession });
      }
    }).catch(() => {
      setDeepLinkState({ kind: 'not-found', sessionKey: effectiveInitialSession });
    });
  }, [sessionsQuery.status, sessionsQuery.data, effectiveInitialSession, queryClient]);

  // ---- R2: session graph via useQuery ----
  // Capture selected so the queryFn can use it safely (enabled guard ensures non-null).
  const activeSelected = selected ?? '';
  const graphQuery = useQuery({
    queryKey: queryKeys.traceGraph(activeSelected),
    queryFn: () => fetchTraceGraph(activeSelected),
    staleTime: STALE_GRAPH_MS,
    enabled: selected != null,
  });

  // ---- R2: trace page via useQuery (page-based pagination, T-2026-06-17-03) ----
  // Switched from cursor/load-more to server-side page/pageSize. Each page is a
  // separate cache entry (queryKey includes page + pageSize) for TQ deduplication.
  const traceFirstQuery = useQuery({
    queryKey: queryKeys.traceFull(activeSelected, null, eventsPageState.page, eventsPageState.pageSize),
    queryFn: () => fetchTraceFull(activeSelected, eventsPageState.pageSize, null, eventsPageState.page, eventsPageState.pageSize),
    staleTime: STALE_COMMANDS_TRACES_MS,
    enabled: selected != null,
  });

  // When the selected session changes, reset the events page to 1 and clear stale state.
  const prevSelected = useRef<string | null>(null);
  useEffect(() => {
    if (selected !== prevSelected.current) {
      prevSelected.current = selected;
      eventsPageActions.resetToFirstPage();
      setEventsTotalCount(0);
      setHighlightId(null);
      setRefNotice(null);
      setExpandedRowIds(new Set());
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selected]);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setExpandedRowIds(new Set());
  }, [eventsPageState.page, eventsPageState.pageSize]);

  // Update eventsTotalCount when the trace page query succeeds.
  useEffect(() => {
    if (traceFirstQuery.status === 'success') {
      const tc = traceFirstQuery.data.body.total_count ?? 0;
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setEventsTotalCount(tc);
    }
  }, [traceFirstQuery.status, traceFirstQuery.data]);

  // Persist events page size and sync events pagination to URL.
  useEffect(() => {
    try { localStorage.setItem(LS_EVENTS_PAGE_SIZE, String(eventsPageState.pageSize)); } catch { /* ignore */ }
    const r = currentRoute();
    const ts = decodeTraceState(r);
    const encoded = encodeTraceState({
      ...ts,
      eventsPage: eventsPageState.page,
      eventsPageSize: eventsPageState.pageSize !== DEFAULT_PAGE_SIZE ? eventsPageState.pageSize : null,
    });
    navigate({ ...r, ...encoded });
  }, [eventsPageState.page, eventsPageState.pageSize]);

  // Derived convenience bindings from query results.
  const sessionsData = sessionsQuery.data;
  // Memoize sessionsList to prevent it from creating a new array reference on every render
  // (sessionsData?.body.sessions ?? [] would produce a new [] each render when data is absent,
  // causing spurious useMemo dependency changes downstream).
  const sessionsList: TraceSession[] = useMemo(
    () => sessionsData?.body.sessions ?? [],
    [sessionsData],
  );
  const limitsData: ObservabilityLimits | undefined = sessionsData?.body.limits;

  const graph: GraphState | null =
    graphQuery.status === 'success'
      ? { root: graphQuery.data.body.root, nodes: graphQuery.data.body.nodes, edges: graphQuery.data.body.edges }
      : null;

  // Page-based: each query returns exactly one page; no extra entries to append.
  const entries: TraceEntry[] = useMemo(
    () => traceFirstQuery.status === 'success' ? traceFirstQuery.data.body.entries : [],
    [traceFirstQuery.status, traceFirstQuery.data],
  );

  const subject: TraceSubject | null =
    traceFirstQuery.status === 'success' ? traceFirstQuery.data.body.subject : null;

  // Derive the selected TraceSession from the sessions list for the TraceSummaryPanel.
  // The sessions list is loaded by the bootstrap query; absent when not yet loaded or not in list.
  const selectedSession: TraceSession | null = useMemo(
    () => (selected != null ? (sessionsList.find((s) => s.id === selected) ?? null) : null),
    [selected, sessionsList],
  );

  // Combined loading/error state for the trace detail area.
  const loadingTrace =
    (graphQuery.isFetching && !graphQuery.data) ||
    (traceFirstQuery.isFetching && !traceFirstQuery.data);
  const traceError: string | null =
    graphQuery.isError
      ? (graphQuery.error instanceof Error ? graphQuery.error.message : String(graphQuery.error))
      : traceFirstQuery.isError
        ? (traceFirstQuery.error instanceof Error ? traceFirstQuery.error.message : String(traceFirstQuery.error))
        : null;

  // ---- auto-highlight seam: when initialHighlightRef is set + entries are loaded, resolve it ----
  // This drives the #/trace/:id route: the URL entry id is resolved against loaded entries,
  // then highlighted + scrolled. This is a seam, not a fork — normal TraceView behavior unchanged.
  const didAutoHighlightRef = useRef(false);
  useEffect(() => {
    // Only fire once per mount (the ref prevents re-firing on subsequent entry loads).
    if (initialHighlightRef == null || initialHighlightRef === '' || didAutoHighlightRef.current || entries.length === 0) return;
    const resolvables: Resolvable[] = entries.map((e) => ({ id: e.id, display_id: e.display_id, alias: e.alias }));
    const result = resolveReference(initialHighlightRef, resolvables);
    if (result.kind === 'one') {
      didAutoHighlightRef.current = true;
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setHighlightId(result.match.id);
      setExpandedRowIds((prev) => {
        const next = new Set(prev);
        next.add(result.match.id);
        return next;
      });
      // Scroll after paint so the row element is in the DOM.
      requestAnimationFrame(() => {
        document.getElementById(`entry-${result.match.id}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' });
      });
    }
  }, [initialHighlightRef, entries]);

  // Page-based timeline pagination: each page is a separate TQ cache entry.
  // No "load more" needed; the Pagination component handles navigation.

  // ---- reference resolver: alias or display id -> scroll + highlight ----
  const resolvables: Resolvable[] = useMemo(
    () => entries.map((e) => ({ id: e.id, display_id: e.display_id, alias: e.alias })),
    [entries],
  );

  const onResolve = useCallback(
    (e: React.SyntheticEvent<HTMLFormElement>) => {
      e.preventDefault();
      const q = refQuery.trim();
      if (q === '') return;
      const result = resolveReference(q, resolvables);
      if (result.kind === 'one') {
        setHighlightId(result.match.id);
        setRefNotice(null);
        setExpandedRowIds((prev) => {
          const next = new Set(prev);
          next.add(result.match.id);
          return next;
        });
        // Wrap in rAF so the scroll runs after the re-render that opens the
        // inline detail row.
        requestAnimationFrame(() => {
          document.getElementById(`entry-${result.match.id}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' });
        });
      } else if (result.kind === 'ambiguous') {
        setHighlightId(null);
        setRefNotice(`${String(result.candidates.length)} matches in this trace for "${q}". Use the full display id.`);
      } else {
        setHighlightId(null);
        setRefNotice(`No node "${q}" in the loaded pages. Load more or check the id/alias.`);
      }
    },
    [refQuery, resolvables],
  );

  // ---- ST-02: URL-aware session selection handler ----
  // Called when the user selects a session in the picker (interactive selection).
  // Writes selectedSession to the URL so reloading restores the selection.
  // Toggle behavior: clicking the currently-selected session clears the selection
  // (removes selectedSession and detailTab from the URL).
  const handleSelectSession = useCallback((sessionKey: string) => {
    if (sessionKey === selected) {
      // Toggle: deselect the current session.
      setSelected(null);
      const r = currentRoute();
      navigate({ ...r, selectedSession: null, detailTab: null, eventsPage: null, eventsPageSize: null });
      return;
    }
    setSelected(sessionKey);
    // Reset events to page 1 when switching sessions.
    eventsPageActions.resetToFirstPage();
    // Write the new selectedSession to the URL; preserve other params (from/to etc.)
    // but reset detailTab to the current tab state (do not carry over old tab).
    const r = currentRoute();
    navigate({ ...r, selectedSession: sessionKey, detailTab: tab !== 'trace' ? tab : null, eventsPage: null, eventsPageSize: null });
  }, [selected, tab, eventsPageActions]);

  // ---- ST-02: URL-aware tab switch handler ----
  // Called when the user switches the detail tab (Trace / Accounting).
  // Writes detailTab to the URL so reloading restores the active tab.
  const handleSetTab = useCallback((newTab: TraceTab) => {
    setTab(newTab);
    const r = currentRoute();
    // Write detailTab to URL; null for 'trace' (the default) keeps the URL clean.
    navigate({ ...r, detailTab: newTab !== 'trace' ? newTab : null });
  }, []);

  // ---- ST-05 (T-2026-06-16-14): URL-aware date range handler for SessionPicker ----
  // Called when the user changes the date range in the picker.
  // Writes from/to to the URL so the window is deep-linkable and round-trips.
  // Also resets events pagination to page 1 (filter change resets pagination).
  const handlePickerRangeChange = useCallback((range: DateRange) => {
    eventsPageActions.resetToFirstPage();
    const r = currentRoute();
    navigate({ ...r, ...encodeRange({ from: range.from, to: range.to }) });
  }, [eventsPageActions]);

  // ---- shared selection: graph node click -> highlight timeline row + scroll ----
  const onGraphSelect = useCallback((node: GraphNode) => {
    setHighlightId(node.id);
    setExpandedRowIds((prev) => {
      const next = new Set(prev);
      next.add(node.id);
      return next;
    });
    // Wrap in rAF so the scroll runs after the re-render that opens the
    // inline detail row.
    requestAnimationFrame(() => {
      document.getElementById(`entry-${node.id}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    });
  }, []);

  // ---- shared selection: timeline row click -> highlight graph node ----
  // Selection is URL-keyed: clicking a row selects it (local highlight) and opens
  // the inline row detail attached to that event.
  const onTimelineRowClick = useCallback((entry: TraceEntry) => {
    setHighlightId(entry.id);
    setExpandedRowIds((prev) => {
      const next = new Set(prev);
      next.add(entry.id);
      return next;
    });
    // Graph nodes that match will be highlighted via the highlightId prop.
    // Wrap in rAF so the scroll runs after the re-render that opens the
    // inline detail row.
    requestAnimationFrame(() => {
      document.getElementById(`entry-${entry.id}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    });
  }, []);

  const toggleTimelineDetail = useCallback((entry: TraceEntry) => {
    setHighlightId(entry.id);
    setExpandedRowIds((prev) => {
      const next = new Set(prev);
      if (next.has(entry.id)) {
        next.delete(entry.id);
      } else {
        next.add(entry.id);
      }
      return next;
    });
  }, []);

  const openAllTimelineDetails = useCallback(() => {
    setExpandedRowIds((prev) => {
      const next = new Set(prev);
      entries.forEach((entry) => { next.add(entry.id); });
      return next;
    });
  }, [entries]);

  const collapseAllTimelineDetails = useCallback(() => {
    setExpandedRowIds((prev) => {
      const next = new Set(prev);
      entries.forEach((entry) => { next.delete(entry.id); });
      return next;
    });
  }, [entries]);

  // ---- EvidenceTable columns for the timeline ----
  const timelineColumns = useMemo((): EvidenceColumn<TraceEntry>[] => [
    {
      key: 'toolType',
      header: 'Type',
      render: (row) => {
        const toolType = getEntryToolType(row);
        const kind = isUserSubmitEntry(row) ? userSubmitLabel(row.user_submit_subtype) : traceKindLabel(row.kind);
        const tip = isUserSubmitEntry(row) ? userSubmitTooltip(row.user_submit_subtype) : traceKindTooltip(row.kind);
        // A11y: for user_submit rows, include a visually-hidden description so screen
        // readers announce the full context alongside the abbreviated visible label.
        // clip-rect keeps it in the AT tree (unlike display:none which removes it).
        const srOnlyText = isUserSubmitEntry(row)
          ? isInitiatingSubmit(row.user_submit_subtype)
            ? `${userSubmitLabel(row.user_submit_subtype)} — human prompt captured at session start`
            : `${userSubmitLabel(row.user_submit_subtype)} — human intervention`
          : null;
        return (
          <span className={styles.timelineTypeCell}>
            <ToolTypeMark
              toolType={toolType}
              className={styles.timelineToolMark}
            />
            {tip != null && tip !== '' ? (
              <Tooltip content={<span>{tip}</span>}>
                <span className={styles.timelineKind}>{kind}</span>
              </Tooltip>
            ) : (
              <span className={styles.timelineKind}>{kind}</span>
            )}
            {srOnlyText != null && (
              <span className={styles.srOnly}>{srOnlyText}</span>
            )}
          </span>
        );
      },
    },
    {
      key: 'timestamp',
      header: 'Time',
      render: (row) => {
        const ts = row.created_at ?? row.started_at;
        return ts != null && ts !== '' ? (
          <span className={styles.timelineMono}>
            {formatAbsolute(ts, resolvedZone)}
          </span>
        ) : (
          <span className={styles.timelineMuted}>—</span>
        );
      },
    },
    {
      key: 'actor',
      header: 'Actor / Tool',
      render: (row) => {
        const title = row.tool?.name ?? row.label ?? row.operation ?? row.kind;
        const sub = row.caller_surface ?? row.agent_type ?? null;
        return (
          <span>
            <span className={styles.timelineActorTitle}>{title}</span>
            {sub != null && sub !== '' && (
              <span className={styles.timelineActorSub}>
                {sub}
              </span>
            )}
          </span>
        );
      },
    },
    {
      key: 'status',
      header: 'Status',
      render: (row) => {
        // OBS-12: event rows have a dedicated `status` field ("success"/"failed"/null).
        // For events: render StatusBadge when status is present; render null when null
        // (do NOT fall through to the exit_code path — events don't have exit codes).
        if (row.kind === 'event') {
          if (row.status == null || row.status === '') return null;
          return (
            <StatusBadge
              rawStatus={row.status}
              label={row.status}
              className={styles.timelineStatusBadge}
            />
          );
        }
        // Non-event rows: use status if present, otherwise derive from exit_code.
        const st = row.status ?? (row.exit_code != null ? `exit ${String(row.exit_code)}` : null);
        if (st == null || st === '') return null;
        // Route through StatusBadge so status = text + shape + color (not color alone).
        // toRunStatus() is alias-aware (e.g. 'completed' -> 'complete'); unrecognized
        // strings narrow to 'unknown' (muted), never 'failed', so a non-failure is never
        // shown as a red X. We pass rawStatus for color/shape and label for the verbatim text.
        return (
          <StatusBadge
            rawStatus={st}
            label={st}
            className={styles.timelineStatusBadge}
          />
        );
      },
    },
    {
      key: 'cost',
      header: 'Cost',
      render: (row) => {
        // CRITICAL: use ONLY row.cost (per-row cost), never row.rollup_cost.
        // A span row can carry BOTH rollup_cost (subtree total) AND a per-row cost.
        // Summing rollup_cost into any per-row total would double-count subtrees.
        const money = row.cost;
        if (money == null) {
          return (
            <CostProvenanceChip
              variant="single"
              costUsd={null}
              provenance="unavailable"
            />
          );
        }
        return (
          <CostProvenanceChip
            variant="single"
            costUsd={money.value_usd}
            provenance={getCostProvenanceForMoney(money)}
          />
        );
      },
    },
    {
      key: 'ref',
      header: 'Ref',
      render: (row) =>
        row.alias != null && row.alias !== '' ? (
          <span className={styles.timelineRefAlias}>{row.alias}</span>
        ) : (
          <span className={styles.timelineMuted}>
            {row.display_id !== '' ? row.display_id : '—'}
          </span>
        ),
    },
    {
      key: 'detail',
      header: 'Details',
      render: (row) => {
        const expanded = expandedRowIds.has(row.id);
        return (
          <button
            type="button"
            className={styles.timelineDetailsButton}
            aria-expanded={expanded}
            aria-controls={`entry-${row.id}`}
            onClick={(event) => {
              event.stopPropagation();
              toggleTimelineDetail(row);
            }}
            aria-label={`${expanded ? 'Collapse' : 'Open'} details for entry ${row.display_id}`}
          >
            <span className={styles.timelineDetailsSymbol} aria-hidden="true">
              {expanded ? '-' : 'v'}
            </span>
            <span>Details</span>
          </button>
        );
      },
    },
  ], [resolvedZone, expandedRowIds, toggleTimelineDetail]);

  // ---- Sessions initial-load error state ----
  // The sessionsQuery is a bootstrap query used only to seed the initial session selection
  // (deep-link handling + auto-select). The picker manages its own filtered queries.
  // isPending covers both the initial fetch AND retry-delay gaps (when isFetching=false
  // but status is still 'pending' between retries). isLoading alone misses the retry-delay.
  // We still surface a hard error here since the initial auto-selection can't proceed without it.
  if (sessionsQuery.isError) {
    const msg = sessionsQuery.error instanceof Error
      ? sessionsQuery.error.message
      : String(sessionsQuery.error);
    return (
      <div className={styles.view}>
        <PageHeader title="Trace Sessions" />
        <p className={styles.error}>
          Failed to load sessions: {msg}. Is Scope.Server running on port 5130?
        </p>
      </div>
    );
  }

  // T-49: explicit not-found state for deep-links that couldn't be resolved.
  // Never silently swap to a different session.
  if (deepLinkState.kind === 'not-found') {
    return (
      <div className={styles.view}>
        <PageHeader title="Trace Sessions" />
        <EmptyState
          variant="governance"
          glyph="x-circle"
          heading={`Session not found`}
          description={`Session "${deepLinkState.sessionKey}" isn't in the current view and couldn't be loaded. It may be outside the free-tier retention window.`}
          action={
            <button
              type="button"
              className={styles.loadMore}
              onClick={() => {
                setDeepLinkState({ kind: 'none' });
                // sessionsList is non-empty when this button renders (deep-link not-found state).
                const first = sessionsList[0];
                if (first !== undefined) setSelected(first.id);
              }}
            >
              View recent sessions
            </button>
          }
        />
      </div>
    );
  }

  // Derive limit notices once from the loaded limits object. Empty array when paid/absent.
  const limitNotices = deriveLimitNotices(limitsData);

  return (
    <div className={styles.view}>
      {/* PageHeader: TraceView owns its own header per mount contract (FE-2a removes App.tsx title). */}
      <PageHeader title="Trace Sessions" primaryAction={<CopyLinkButton />} />

      {limitNotices.length > 0 && (
        <section
          className={styles.limitNotices}
          aria-label="Free-tier observability notices"
        >
          {/* Status glyph-block leads the collapsible header row. */}
          <div className={styles.limitNoticesHeader}>
            <StatusGlyphBlock token="warning" shapeName="exclamation-triangle" />
            <button
              type="button"
              className={styles.limitNoticesSummary}
              aria-expanded={limitNoticesOpen}
              aria-controls={limitPanelId}
              onClick={() => { setLimitNoticesOpen((prev) => !prev); }}
            >
              <span>Free tier limited</span>
              <span className={styles.limitNoticesBadge}>{limitNotices.length}</span>
              <span className={styles.limitNoticesChevron} aria-hidden="true">
                {limitNoticesOpen ? '▲' : '▼'}
              </span>
            </button>
          </div>
          <div
            id={limitPanelId}
            className={styles.limitNoticesPanel}
            hidden={!limitNoticesOpen}
          >
            {limitNotices.map((notice) => (
              <p
                key={notice.key}
                role="note"
                className={styles.limitNotice}
                data-testid={`limit-notice-${notice.key}`}
              >
                <span className={styles.limitNoticeGlyph} aria-hidden="true">
                  ℹ
                </span>
                <span className={styles.limitNoticeText}>{notice.message}</span>
              </p>
            ))}
          </div>
        </section>
      )}

      {/* ST-04: Session picker — two tabs (Agent / Cron), date-range filter, delete+undo, trash view.
          Visually distinct from the detail tabs below (Trace/Accounting):
          - Picker tabs: surface-2 background, inverse-fill selected state.
          - Detail tabs below: transparent background, underline selected state.
          Two tab-bars are separated by the picker panel itself so they do not read as one. */}
      {/* P2-4: "Session source" label above the picker group. */}
      <p className={styles.pickerGroupLabel}>Session source</p>
      <SessionPicker
        selected={selected}
        onSelect={handleSelectSession}
        zone={resolvedZone}
        initialTab={pickerTab}
        initialRange={mountRouteState.range}
        onRangeChange={handlePickerRangeChange}
        initialPage={mountRouteState.sessionsPage}
        onPageChange={(pg, pgSz) => {
          const r = currentRoute();
          const ts = decodeTraceState(r);
          const encoded = encodeTraceState({
            ...ts,
            sessionsPage: pg,
            sessionsPageSize: pgSz !== DEFAULT_PAGE_SIZE ? pgSz : null,
          });
          navigate({ ...r, ...encoded });
        }}
        onTabChange={(newPickerTab) => {
          // P2-3: Reset detail tab to 'trace' and clear stale selection/highlight
          // when the source tab changes. Prevents showing stale Accounting data
          // for a session from a different source (e.g. cron vs cli).
          void newPickerTab; // consumed; reset is unconditional on source tab change
          handleSetTab('trace');
          setHighlightId(null);
          setRefNotice(null);
          setSelected(null);
          setExpandedRowIds(new Set());
          const r = currentRoute();
          navigate({ ...r, selectedSession: null, detailTab: null });
        }}
      />

      {/* Selected-session surface: metadata plus trace controls. */}
      {selected != null && (
        <TraceSummaryPanel
          subject={subject}
          session={selectedSession}
          selectedKey={selected}
          eventCount={eventsTotalCount > 0 ? eventsTotalCount : entries.length}
          refQuery={refQuery}
          onRefQueryChange={setRefQuery}
          onResolve={onResolve}
          zoneLabel={showUtc ? 'UTC' : userZoneAbbr}
          showUtc={showUtc}
          onToggleZone={() => { setShowUtc((v) => !v); }}
          zoneToggleTitle={
            showUtc
              ? `Showing UTC — click to switch to ${userZoneAbbr}. Storage stays UTC; this is display-only.`
              : `Showing ${userZoneAbbr} — click to switch to UTC. Storage stays UTC; this is display-only.`
          }
        />
      )}

      {/* P2-4: "Detail view" label above the detail tab group. */}
      <p className={styles.detailGroupLabel}>Detail view</p>
      {/* Detail tab bar: Trace vs Costs (ST-15).
          Structurally and visually distinct from the picker tabs above:
          - No surface-2 background; sits on the page background.
          - Uses a bottom-underline active indicator (not inverse fill).
          - Labelled "Session detail view" so screen readers distinguish it from the picker tablist. */}
      <div className={styles.tabs} role="tablist" aria-label="Session detail view">
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'trace'}
          className={`${styles.tab ?? ''} ${tab === 'trace' ? (styles.tabActive ?? '') : ''}`}
          onClick={() => { handleSetTab('trace'); }}
        >
          Trace
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'accounting'}
          className={`${styles.tab ?? ''} ${tab === 'accounting' ? (styles.tabActive ?? '') : ''}`}
          onClick={() => { handleSetTab('accounting'); }}
        >
          Costs
        </button>
      </div>

      {/* T-49: outside-list notice — shown when the selected session is a deep-link
          that resolved directly but wasn't in the capped sessions list. */}
      {deepLinkState.kind === 'outside-list' && (
        <div
          className={styles.deepLinkNotice}
          role="note"
          data-testid="deep-link-outside-list"
        >
          <StatusGlyphBlock token="info" shapeName="information-circle" />
          <span className={styles.deepLinkNoticeContent}>
            This session is outside the current view (it may exceed the free-tier session cap or retention window) but was loaded directly from your local data.
          </span>
        </div>
      )}

      {tab === 'accounting' && selected != null && selected !== '' ? (
        <AccountingPanel sessionKey={selected} zone={resolvedZone} />
      ) : (
        <>
          {refNotice != null && refNotice !== '' && <p className={styles.refNotice}>{refNotice}</p>}

          {/* TraceLegend: sanctioned trace-tool token consumer; renders on card surface. */}
          <div className={styles.legendSection}>
            <TraceLegend showCaption />
          </div>

          {/* Trace layout: graph ABOVE timeline on all widths (owner, 2026-06-13). */}
          <div className={styles.traceLayout}>
            {/* Graph section (left / top): overview of who calls what. */}
            {graph != null && graph.nodes.length > 0 && (
              <div className={styles.graphSection}>
                <span className={styles.graphLabel}>Session graph</span>
                <SessionGraph
                  nodes={graph.nodes}
                  edges={graph.edges}
                  highlightId={highlightId}
                  onSelect={onGraphSelect}
                />
              </div>
            )}

            {/* Timeline section: authoritative chronological evidence (non-graph equivalent). */}
            <div className={styles.timelineSection}>
              <span className={styles.timelineLabel}>
                Event timeline · {eventsTotalCount > 0 ? `${String(eventsTotalCount)} entries total` : `${String(entries.length)} entries`}
              </span>
              {traceError != null && traceError !== '' && <p className={styles.error}>Failed to load trace: {traceError}</p>}

              {/* Pagination: shown when server reports total_count (newer server). */}
              {computePageCount(eventsTotalCount, eventsPageState.pageSize) > 0 && (
                <Pagination
                  page={eventsPageState.page}
                  pageCount={eventsPageState.pageCount}
                  pageSize={eventsPageState.pageSize}
                  totalCount={eventsTotalCount}
                  onGoToPage={eventsPageActions.goToPage}
                  onPrevPage={eventsPageActions.prevPage}
                  onNextPage={eventsPageActions.nextPage}
                  onSetPageSize={eventsPageActions.setPageSize}
                  label="Event timeline pagination"
                  className={styles.timelinePagination ?? ''}
                />
              )}

              {entries.length > 0 && (
                <div className={styles.timelineUtility}>
                  <span className={styles.timelineUtilityLabel}>Row details</span>
                  <div className={styles.timelineBulkControls} role="group" aria-label="Event timeline row detail controls">
                    <button
                      type="button"
                      className={styles.timelineBulkButton}
                      onClick={openAllTimelineDetails}
                    >
                      <span className={styles.timelineBulkSymbol} aria-hidden="true">v</span>
                      <span>Open all</span>
                    </button>
                    <button
                      type="button"
                      className={styles.timelineBulkButton}
                      onClick={collapseAllTimelineDetails}
                    >
                      <span className={styles.timelineBulkSymbol} aria-hidden="true">-</span>
                      <span>Collapse all</span>
                    </button>
                  </div>
                </div>
              )}

              {entries.length > 0 ? (
                <EvidenceTable<TraceEntry>
                  caption="Event timeline"
                  columns={timelineColumns}
                  rows={entries}
                  getRowKey={(row) => row.id}
                  onRowClick={onTimelineRowClick}
                  useRowButtonRole={false}
                  getRowClassName={(row, idx) => [
                    styles.timelineEventRow,
                    idx % 2 === 1 ? styles.timelineEventRowAlt : '',
                    expandedRowIds.has(row.id) ? styles.timelineEventRowExpanded : '',
                    row.id === highlightId ? styles.timelineRowSelected : '',
                  ].filter(Boolean).join(' ')}
                  getRowStyle={(row) => ({
                    '--event-color': `var(${TOOL_TYPE_TOKEN[getEntryToolType(row)]})`,
                  } as React.CSSProperties)}
                  getRowAriaExpanded={(row) => expandedRowIds.has(row.id)}
                  getRowDataAttrs={(row) => {
                    const attrs: Record<string, string> = {};
                    if (isUserSubmitEntry(row)) {
                      attrs['data-user-submit'] = 'true';
                      const isInit = isInitiatingSubmit(row.user_submit_subtype);
                      if (isInit) attrs['data-initiating'] = 'true';
                      // Note: descriptive text for screen readers is provided by the
                      // sr-only span inside the Type cell (toolType column render),
                      // not by aria-label on the <tr>. aria-label on role=row has
                      // inconsistent AT support across browsers/screen-reader combos.
                    }
                    return Object.keys(attrs).length > 0 ? attrs : undefined;
                  }}
                  renderRowAfter={(row, idx) => {
                    if (!expandedRowIds.has(row.id)) return null;
                    return (
                      <tr
                        key={`${row.id}-detail`}
                        className={[
                          styles.timelineDetailRow,
                          idx % 2 === 1 ? styles.timelineDetailRowAlt : '',
                        ].filter(Boolean).join(' ')}
                        style={{
                          '--event-color': `var(${TOOL_TYPE_TOKEN[getEntryToolType(row)]})`,
                        } as React.CSSProperties}
                      >
                        <td colSpan={TIMELINE_COLUMN_COUNT}>
                          <TraceEntryInlineDetail entry={row} zone={resolvedZone} />
                        </td>
                      </tr>
                    );
                  }}
                />
              ) : (
                !loadingTrace && (traceError == null || traceError === '') && (
                  <p className={styles.notice}>No entries recorded in this session.</p>
                )
              )}
            </div>
          </div>
        </>
      )}

      {/* T-50: ScrollToTop — window-scroll based, self-contained, eligible only on long pages. */}
      <ScrollToTop />
    </div>
  );
}
