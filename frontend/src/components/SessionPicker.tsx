import type { ReactNode } from 'react';
import { useCallback, useEffect, useId, useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  fetchTraceSessions,
  fetchCronSessions,
  fetchBaseLaunches,
  deleteSession,
  restoreSession,
  type TraceSession,
  type CronSession,
  type BudgetConversationRow,
} from '../api/traceApi';
import { queryKeys, invalidationKeys, STALE_COMMANDS_TRACES_MS, STALE_BASE_LAUNCHES_MS } from '../api/queryClient';
import {
  splitEffectiveKey,
  isNonZeroGeneration,
  buildBaseLaunchGroups,
  buildConversationGroups,
  type ConversationGroup,
} from '../lib/sessionGeneration';
import { formatAbsolute } from '../lib/time';
import { useToast } from './useToast';
import { SessionRow, type SessionRowViewModel } from './SessionRow';
import { CostBillingClassBadge } from './CostBillingClassBadge';
import { DateRangePicker } from './DateRangePicker';
import { type DateRange, presetToRange } from '../lib/dateRange';
import { computePageCount } from '../hooks/usePagination';
import { DEFAULT_PAGE_SIZE } from '../lib/viewState';
import { Pagination } from './Pagination';
import styles from './SessionPicker.module.css';

// =============================================================================
// SessionPicker (ST-04 + T-2026-06-16-02)
//
// Two-tab picker (Agent | Cron) with date-range filter, per-row delete+undo, and
// a "Show deleted" trash view. Both tabs render via the shared SessionRow component
// so any feature added to the row propagates to both tabs automatically.
//
// Picker tabs (role=tablist) are structurally distinct from the detail tabs
// (Trace/Accounting) rendered below by TraceView.
//
// Deep-link tab selection: callers pass initialTab to pre-select the correct tab
// (cron- prefix -> 'cron', else 'cli').
// =============================================================================

export type PickerTab = 'cli' | 'cron';

/** localStorage key prefix for per-surface page size persistence. */
const LS_SESSIONS_PAGE_SIZE = 'scope.sessions.pageSize';

export interface SessionPickerProps {
  /** The currently selected session key (id for agent sessions, session_key for cron). */
  selected: string | null;
  /** Called when the user clicks a session row to select it. */
  onSelect: (sessionKey: string) => void;
  /** Zone string (IANA) for timestamp rendering. */
  zone: string;
  /** Pre-select the cron tab when opening via a cron- deep-link. */
  initialTab?: PickerTab;
  /**
   * Optional initial date range to pre-populate the picker on mount.
   * Used to restore the window from a deep-link URL (?from=ISO&to=ISO on /trace).
   * When not provided, defaults to the last 24h (local wall-clock, e529b33 behavior).
   */
  initialRange?: DateRange | null;
  /**
   * Called when the user changes the date range.
   * The caller (TraceView) uses this to write from/to to the URL so the window
   * is deep-linkable.
   */
  onRangeChange?: (range: DateRange) => void;
  /**
   * Initial page for the session list (T-2026-06-17-03). From URL deep-link (sessionsPage).
   * Defaults to 1.
   */
  initialPage?: number;
  /**
   * Called when the user navigates pages (T-2026-06-17-03).
   * The caller (TraceView) writes sessionsPage + sessionsPageSize to the URL.
   */
  onPageChange?: (page: number, pageSize: number) => void;
  /**
   * Called when the user switches the source tab (Agent / Cron) — P2-3.
   * The caller (TraceView) uses this to reset the detail tab and clear stale
   * selection/highlight state when the source changes.
   */
  onTabChange?: (tab: PickerTab) => void;
}

// ---------------------------------------------------------------------------
// Default date range: last 24h (rolling-from-now, LOCAL wall-clock display).
//
// Preserves the e529b33 local-wall-clock default: the DateRangePicker's 24h preset
// calls presetToRange('24h') which emits rolling UTC ISO-8601, and displays local
// wall-clock time in the inputs. The effective window (now - 24h to now) is identical
// to the prior minus24hIso()/nowIso() raw-input behavior -- same start/end points.
// ---------------------------------------------------------------------------

const DEFAULT_PICKER_RANGE: DateRange = presetToRange('24h');

// ---------------------------------------------------------------------------
// Status maps for cron rows (text + glyph, not color alone)
// ---------------------------------------------------------------------------

const CRON_STATUS_GLYPH: Record<string, string> = {
  running: '[>]',
  completed: '[+]',
  failed: '[x]',
  skipped: '[-]',
};

const CRON_STATUS_LABEL: Record<string, string> = {
  running: 'Running',
  completed: 'Completed',
  failed: 'Failed',
  skipped: 'Skipped',
};

function cronStatusGlyph(status: string): string {
  return CRON_STATUS_GLYPH[status] ?? '[?]';
}

function cronStatusLabel(status: string): string {
  return CRON_STATUS_LABEL[status] ?? status;
}

function cronStatusClass(status: string, s: typeof styles): string {
  if (status === 'running') return s.statusRunning ?? '';
  if (status === 'completed') return s.statusSuccess ?? '';
  if (status === 'failed') return s.statusDanger ?? '';
  if (status === 'skipped') return s.statusMuted ?? '';
  return '';
}

// ---------------------------------------------------------------------------
// View-model builders: map raw API data to SessionRowViewModel
// ---------------------------------------------------------------------------

function buildCliVm(s: TraceSession, zone: string, generationLabel?: string): SessionRowViewModel {
  const isActive = s.is_active === true;
  const host = s.host ?? s.agent_type ?? 'unknown';
  const indicator: ReactNode = isActive ? (
    <span className={styles.sessionRowRunning} aria-label="Running">
      <span aria-hidden="true">[&gt;]</span>
      <span>Running</span>
    </span>
  ) : undefined;

  const meta: ReactNode = (
    <>
      <span className={styles.sessionRowAgent}>{host}</span>
      <span className={styles.sessionRowDot} aria-hidden="true">·</span>
      <span className={styles.sessionRowReqs}>{s.request_count} req</span>
      {s.first_seen_at != null && (
        <>
          <span className={styles.sessionRowDot} aria-hidden="true">·</span>
          <span className={styles.sessionRowTime}>
            {formatAbsolute(s.first_seen_at, zone)}
          </span>
        </>
      )}
      {/* Generation badge: shown when this session is a non-zero generation (#g{n}).
          Text+shape chip per design contract; never color alone. */}
      {generationLabel != null && (
        <span className={styles.generationBadge} aria-label={`generation ${generationLabel}`}>
          {generationLabel}
        </span>
      )}
    </>
  );

  return {
    rowKey: s.id,
    sessionKey: s.id,
    displayId: s.display_id,
    meta,
    indicator,
    deletedAt: s.deleted_at ?? null,
  };
}

function buildCronVm(c: CronSession, zone: string): SessionRowViewModel {
  const statusGlyph = cronStatusGlyph(c.status);
  const statusLbl = cronStatusLabel(c.status);
  const statusClass = cronStatusClass(c.status, styles);
  const costLabel = c.total_cost_usd != null
    ? `$${c.total_cost_usd.toFixed(4)}`
    : 'Cost unavailable';

  const indicator: ReactNode = (
    <span className={`${styles.sessionRowStatus ?? ''} ${statusClass}`}>
      <span aria-hidden="true">{statusGlyph}</span>
      <span>{statusLbl}</span>
    </span>
  );

  const meta: ReactNode = (
    <>
      <span className={styles.sessionRowTime}>
        {formatAbsolute(c.started_at, zone)}
      </span>
      <span className={styles.sessionRowDot} aria-hidden="true">·</span>
      <span className={styles.sessionRowSchedule} title="Schedule">{c.schedule}</span>
      <span className={styles.sessionRowDot} aria-hidden="true">·</span>
      <span className={styles.sessionRowCost}>{costLabel}</span>
      {c.cost_billing_class != null && (
        <CostBillingClassBadge billingClass={c.cost_billing_class} />
      )}
    </>
  );

  return {
    rowKey: c.run_id,
    sessionKey: c.session_key,
    displayId: c.display_id,
    subtitle: c.agent_label,
    meta,
    indicator,
    deletedAt: c.deleted_at,
  };
}

// ---------------------------------------------------------------------------
// SessionPicker component
// ---------------------------------------------------------------------------

/** Read the persisted page size from localStorage, falling back to DEFAULT_PAGE_SIZE. */
function readPersistedPageSize(): number {
  try {
    const raw = localStorage.getItem(LS_SESSIONS_PAGE_SIZE);
    if (raw != null) {
      const n = parseInt(raw, 10);
      if ([10, 25, 50, 100].includes(n)) return n;
    }
  } catch { /* localStorage unavailable */ }
  return DEFAULT_PAGE_SIZE;
}

export function SessionPicker({
  selected,
  onSelect,
  zone,
  initialTab = 'cli',
  initialRange = null,
  onRangeChange,
  initialPage = 1,
  onPageChange,
  onTabChange,
}: SessionPickerProps) {
  const queryClient = useQueryClient();
  const { show } = useToast();

  const [activeTab, setActiveTab] = useState<PickerTab>(initialTab);
  // Date range: initialized from URL (initialRange) or default 24h preset.
  // The DateRangePicker is controlled; this component owns the local persistence.
  const [dateRange, setDateRange] = useState<DateRange>(initialRange ?? DEFAULT_PICKER_RANGE);
  const [showDeleted, setShowDeleted] = useState(false);
  // Default-ON conversation lens: groups sessions by cc_session_id.
  // OFF reverts to the per-base (buildBaseLaunchGroups) view.
  const [groupByConversation, setGroupByConversation] = useState(true);
  // Collapsed conversation group keys (insertion-order; default: all expanded).
  const [collapsedConversations, setCollapsedConversations] = useState<Set<string>>(new Set());
  // Optimistically removed keys: these rows are hidden immediately after delete,
  // restored when the undo fires or the trash view is opened.
  const [optimisticDeleted, setOptimisticDeleted] = useState<Set<string>>(new Set());

  // ---- Base-launch group expand/collapse state (ST-07) ----
  // Keys that have been explicitly collapsed by the user. By default all groups
  // are EXPANDED so sessions are immediately visible. Collapsing hides children.
  const [collapsedGroups, setCollapsedGroups] = useState<Set<string>>(new Set());

  const fromIso = dateRange.from;
  const toIso = dateRange.to;

  // ---- Session list pagination (T-2026-06-17-03) ----
  // Page and pageSize are independent of totalCount: declared early so they can
  // be passed to the query key. totalCount is derived from the query result after
  // the query is declared (avoids circular dep and set-state-in-effect anti-pattern).
  const [sessionsPage, setSessionsPage] = useState<number>(initialPage);
  const [sessionsPageSize, setSessionsPageSizeState] = useState<number>(readPersistedPageSize);

  const handlePickerRangeChange = useCallback((range: DateRange) => {
    setDateRange(range);
    setSessionsPage(1);
    onRangeChange?.(range);
  }, [onRangeChange]);

  // Note: showDeleted toggle calls setSessionsPage(1) directly in the onClick handler below.
  // This avoids the react-hooks/set-state-in-effect anti-pattern.

  // Persist page size choice and notify parent of page changes.
  useEffect(() => {
    try { localStorage.setItem(LS_SESSIONS_PAGE_SIZE, String(sessionsPageSize)); } catch { /* ignore */ }
    onPageChange?.(sessionsPage, sessionsPageSize);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionsPage, sessionsPageSize]);

  // ---- Agent sessions query ----
  const cliQuery = useQuery({
    queryKey: queryKeys.traceSessions(fromIso || null, toIso || null, showDeleted || undefined, sessionsPage, sessionsPageSize),
    queryFn: () => fetchTraceSessions(fromIso || null, toIso || null, showDeleted || undefined, sessionsPage, sessionsPageSize),
    staleTime: STALE_COMMANDS_TRACES_MS,
  });

  // ---- Cron sessions query ----
  const cronQuery = useQuery({
    queryKey: queryKeys.cronSessions(fromIso || null, toIso || null, showDeleted || undefined),
    queryFn: () => fetchCronSessions(fromIso || null, toIso || null, showDeleted || undefined),
    staleTime: STALE_COMMANDS_TRACES_MS,
    enabled: activeTab === 'cron',
  });

  // ---- Base-launch cost rollup (ST-07) ----
  // Fetched on the Agent tab to enable generation grouping. The from/to range mirrors the
  // sessions window so grouped spend is from the same window as the listed sessions.
  // Failure is non-fatal: an error or missing data falls back to the flat (ungrouped) list.
  const baseLaunchesQuery = useQuery({
    queryKey: queryKeys.baseLaunches(30, fromIso || null, toIso || null),
    queryFn: () => fetchBaseLaunches(30, fromIso || null, toIso || null),
    staleTime: STALE_BASE_LAUNCHES_MS,
    enabled: activeTab === 'cli' && !showDeleted,
  });

  const cliSessions: TraceSession[] = useMemo(
    () => cliQuery.data?.body.sessions ?? [],
    [cliQuery.data],
  );
  const cronSessions: CronSession[] = useMemo(
    () => cronQuery.data ?? [],
    [cronQuery.data],
  );

  // Derive totalCount from the query (no extra state needed).
  // total_count = 0 on older servers → pageCount = 0 → Pagination hides itself.
  const sessionsTotalCount = cliQuery.data?.body.total_count ?? 0;
  const sessionsPageCount = computePageCount(sessionsTotalCount, sessionsPageSize);

  // Derived pagination actions (plain callbacks, not usePagination hook — avoids circular dep).
  const sessionsPageActions = useMemo(() => ({
    goToPage: (p: number) => { setSessionsPage(Math.max(1, Math.min(p, sessionsPageCount > 0 ? sessionsPageCount : p))); },
    prevPage: () => { setSessionsPage((prev) => Math.max(1, prev - 1)); },
    nextPage: () => { setSessionsPage((prev) => sessionsPageCount > 0 ? Math.min(prev + 1, sessionsPageCount) : prev); },
    setPageSize: (ps: number) => {
      if (![10, 25, 50, 100].includes(ps)) return;
      setSessionsPageSizeState(ps);
      // Clamp current page to the new page count.
      setSessionsPage((prev) => {
        const newCount = computePageCount(sessionsTotalCount, ps);
        return newCount > 0 ? Math.max(1, Math.min(prev, newCount)) : 1;
      });
    },
    resetToFirstPage: () => { setSessionsPage(1); },
  }), [sessionsPageCount, sessionsTotalCount]);

  // ---- Shared delete handler (used by both Agent and Cron tabs) ----
  const handleDelete = useCallback((sessionKey: string, displayId: string) => {
    // Optimistic remove: hide the row immediately.
    setOptimisticDeleted((prev) => new Set([...prev, sessionKey]));

    // If this session was selected, select the next available one (agent tab only; cron deep-link remains).
    if (selected === sessionKey) {
      const nextSession = cliSessions.find(
        (s) => s.id !== sessionKey && !optimisticDeleted.has(s.id),
      );
      if (nextSession !== undefined) onSelect(nextSession.id);
    }

    void deleteSession(sessionKey).then(() => {
      // Confirm the optimistic remove by invalidating (so trash view refreshes).
      void queryClient.invalidateQueries({ queryKey: invalidationKeys.traceSessions });
      void queryClient.invalidateQueries({ queryKey: invalidationKeys.cronSessions });

      show({
        message: `Session ${displayId} deleted`,
        actionLabel: 'Undo',
        onAction: () => {
          void restoreSession(sessionKey).then(() => {
            setOptimisticDeleted((prev) => {
              const next = new Set(prev);
              next.delete(sessionKey);
              return next;
            });
            void queryClient.invalidateQueries({ queryKey: invalidationKeys.traceSessions });
            void queryClient.invalidateQueries({ queryKey: invalidationKeys.cronSessions });
          }).catch((err: unknown) => {
            console.error('Restore failed:', err);
          });
        },
        durationMs: 9000,
      });
    }).catch((err: unknown) => {
      // Roll back the optimistic remove on failure.
      setOptimisticDeleted((prev) => {
        const next = new Set(prev);
        next.delete(sessionKey);
        return next;
      });
      console.error('Delete failed:', err);
    });
  }, [selected, cliSessions, optimisticDeleted, onSelect, queryClient, show]);

  // ---- Shared restore handler (trash view) ----
  const handleRestore = useCallback((sessionKey: string) => {
    void restoreSession(sessionKey).then(() => {
      setOptimisticDeleted((prev) => {
        const next = new Set(prev);
        next.delete(sessionKey);
        return next;
      });
      void queryClient.invalidateQueries({ queryKey: invalidationKeys.traceSessions });
      void queryClient.invalidateQueries({ queryKey: invalidationKeys.cronSessions });
    }).catch((err: unknown) => {
      console.error('Restore failed:', err);
    });
  }, [queryClient]);

  // ---- Accessible tab IDs ----
  const tabBaseId = useId();
  const cliTabId = `${tabBaseId}-tab-cli`;
  const cronTabId = `${tabBaseId}-tab-cron`;
  const cliPanelId = `${tabBaseId}-panel-cli`;
  const cronPanelId = `${tabBaseId}-panel-cron`;

  // ---- Keyboard arrow navigation for tablist ----
  const handleTabKeyDown = useCallback((e: React.KeyboardEvent<HTMLButtonElement>, current: PickerTab) => {
    if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
      e.preventDefault();
      const next: PickerTab = current === 'cli' ? 'cron' : 'cli';
      setActiveTab(next);
      onTabChange?.(next);
      const nextId = next === 'cli' ? cliTabId : cronTabId;
      document.getElementById(nextId)?.focus();
    }
  }, [cliTabId, cronTabId, onTabChange]);

  // ---- Build view-models ----

  // Toggle a base-launch group expand/collapse.
  const handleToggleGroup = useCallback((baseKey: string) => {
    setCollapsedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(baseKey)) {
        next.delete(baseKey);
      } else {
        next.add(baseKey);
      }
      return next;
    });
  }, []);

  // Toggle a conversation group expand/collapse.
  const handleToggleConversation = useCallback((convId: string) => {
    setCollapsedConversations((prev) => {
      const next = new Set(prev);
      if (next.has(convId)) {
        next.delete(convId);
      } else {
        next.add(convId);
      }
      return next;
    });
  }, []);

  // Filtered cli sessions (shared by both flat and grouped paths).
  const filteredCliSessions = useMemo(
    () => cliSessions.filter((s) => showDeleted ? (s.deleted_at != null) : !optimisticDeleted.has(s.id)),
    [cliSessions, showDeleted, optimisticDeleted],
  );

  // Base-launch groups (ST-07): only computed when we have rollup data AND not in trash view.
  // In trash view the grouping is disabled (tombstoned sessions may not have matching rollup data).
  const baseLaunchGroups = useMemo(() => {
    if (showDeleted) return null;
    const rows = baseLaunchesQuery.data?.by_base;
    if (rows == null || rows.length === 0) return null;
    const { groups, ungrouped } = buildBaseLaunchGroups(filteredCliSessions, rows);
    // If there are no multi-generation groups (all groups have 1 session), skip grouping overhead.
    const hasAnyMultiGen = groups.some((g) => g.sessions.length > 1);
    if (!hasAnyMultiGen) return null;
    return { groups, ungrouped };
  }, [showDeleted, baseLaunchesQuery.data, filteredCliSessions]);

  // Conversation groups (T-2026-06-19-05): computed when the conversation lens is ON and
  // not in the trash view. Always returns at least one group per session (idless sessions
  // become standalone). Falls back gracefully when by_conversation is absent (older backend).
  const conversationGroups = useMemo((): ConversationGroup[] | null => {
    if (!groupByConversation || showDeleted) return null;
    const byConvRows: BudgetConversationRow[] = baseLaunchesQuery.data?.by_conversation ?? [];
    // Build groups from all filtered sessions using cc_session_id partitioning.
    return buildConversationGroups(filteredCliSessions, byConvRows);
  }, [groupByConversation, showDeleted, baseLaunchesQuery.data, filteredCliSessions]);

  // Flat view-models: used when grouping is absent (trash view, no rollup data, all singletons).
  const cliVms = useMemo(
    () => filteredCliSessions.map((s) => {
      const { generation } = splitEffectiveKey(s.id);
      const genLabel = generation > 0 ? `gen ${String(generation)}` : undefined;
      return buildCliVm(s, zone, genLabel);
    }),
    [filteredCliSessions, zone],
  );

  const cronVms = useMemo(
    () => cronSessions
      .filter((c) => {
        if (showDeleted) return c.deleted_at != null;
        // Null-session runs are never deleted: always show.
        if (c.session_key == null) return true;
        return !optimisticDeleted.has(c.session_key) && c.deleted_at == null;
      })
      .map((c) => buildCronVm(c, zone)),
    [cronSessions, showDeleted, optimisticDeleted, zone],
  );

  return (
    <section className={styles.picker} aria-label="Session picker">
      {/* ---- Date-range filter: DateRangePicker replaces raw datetime-local inputs.
            Presets 24h/7d/30d/90d/6mo/1yr + custom. Default = 24h (e529b33 behavior).
            Displays local wall-clock; emits UTC ISO-8601 to the API. ---- */}
      <DateRangePicker
        value={dateRange}
        onChange={handlePickerRangeChange}
        label="Date range filter"
        {...(styles.pickerDateRange !== undefined ? { className: styles.pickerDateRange } : {})}
      />

      {/* ---- Picker tabs (Agent / Cron) ---- */}
      <div className={styles.pickerTabBar} role="tablist" aria-label="Session type">
        <button
          id={cliTabId}
          type="button"
          role="tab"
          aria-selected={activeTab === 'cli'}
          aria-controls={cliPanelId}
          tabIndex={activeTab === 'cli' ? 0 : -1}
          className={`${styles.pickerTab ?? ''} ${activeTab === 'cli' ? (styles.pickerTabActive ?? '') : ''}`}
          onClick={() => { setActiveTab('cli'); onTabChange?.('cli'); }}
          onKeyDown={(e) => { handleTabKeyDown(e, 'cli'); }}
        >
          Agent Sessions
        </button>
        <button
          id={cronTabId}
          type="button"
          role="tab"
          aria-selected={activeTab === 'cron'}
          aria-controls={cronPanelId}
          tabIndex={activeTab === 'cron' ? 0 : -1}
          className={`${styles.pickerTab ?? ''} ${activeTab === 'cron' ? (styles.pickerTabActive ?? '') : ''}`}
          onClick={() => { setActiveTab('cron'); onTabChange?.('cron'); }}
          onKeyDown={(e) => { handleTabKeyDown(e, 'cron'); }}
        >
          Cron
        </button>
        <div className={styles.pickerTabSpacer} />
        {/* Conversation lens toggle -- only shown on the Agent tab.
            Default-ON: groups sessions by cc_session_id (conversation identity).
            OFF: reverts to the per-base (buildBaseLaunchGroups) view. */}
        {activeTab === 'cli' && !showDeleted && (
          <button
            type="button"
            className={`${styles.convLensToggle ?? ''} ${groupByConversation ? (styles.convLensToggleActive ?? '') : ''}`}
            aria-pressed={groupByConversation}
            onClick={() => { setGroupByConversation((v) => !v); }}
            title={groupByConversation ? 'Switch to per-base view' : 'Switch to conversation view'}
          >
            <span aria-hidden="true">{groupByConversation ? '[~]' : '[=]'}</span>
            <span>Conversations</span>
          </button>
        )}
        {/* Trash toggle -- applies to the ACTIVE tab (Agent and Cron). */}
        <button
          type="button"
          className={`${styles.trashToggle ?? ''} ${showDeleted ? (styles.trashToggleActive ?? '') : ''}`}
          aria-pressed={showDeleted}
          onClick={() => { setShowDeleted((v) => !v); setSessionsPage(1); }}
          title={showDeleted ? 'Hide deleted sessions' : 'Show deleted sessions'}
        >
          <span aria-hidden="true">{showDeleted ? '[x]' : '[_]'}</span>
          <span>{showDeleted ? 'Hide deleted' : 'Show deleted'}</span>
        </button>
      </div>

      {/* ---- Agent panel ---- */}
      <div
        id={cliPanelId}
        role="tabpanel"
        aria-labelledby={cliTabId}
        hidden={activeTab !== 'cli'}
        className={styles.pickerPanel}
      >
        {cliQuery.isPending && <p className={styles.pickerNotice}>Loading sessions...</p>}
        {cliQuery.isError && (
          <p className={styles.pickerError}>
            Failed to load sessions. Is Scope.Server running?
          </p>
        )}
        {cliQuery.isSuccess && cliVms.length === 0 && (
          <p className={styles.pickerNotice}>
            {showDeleted ? 'No deleted sessions in this range.' : 'No sessions in this range.'}
          </p>
        )}
        {/* Conversation lens view (T-2026-06-19-05): default-ON.
            Renders one collapsible group per cc_session_id. Singletons (idless sessions,
            cron runs, fork bases) render as standalone groups with a single child row.
            Total spend/count come from the by_conversation rollup row.
            Toggle OFF to see the per-base (ST-07) or flat view. */}
        {cliQuery.isSuccess && cliVms.length > 0 && conversationGroups != null ? (
          <ul className={styles.sessionList} role="listbox" aria-label="Agent sessions">
            {conversationGroups.map((group) => {
              const isExpanded = !collapsedConversations.has(group.conversation_id);
              const convRow = group.conversationRow;
              const spendLabel = convRow != null && convRow.spend_usd > 0
                ? `$${convRow.spend_usd.toFixed(4)}`
                : null;
              const launchCount = convRow?.launch_count ?? group.sessions.length;
              const launchCountLabel = launchCount === 1
                ? '1 launch'
                : `${String(launchCount)} launches`;
              // Singleton groups (1 session, no conversationRow spanning bases) render
              // as a non-collapsible group with a compact label strip above the session row.
              // This surfaces the "Cron run" / "Conversation" label on every singleton --
              // the COMMON case for cron runs -- consistent with the multi-launch header.
              const isSingleton = group.sessions.length === 1 && (convRow == null || convRow.launch_count <= 1);
              if (isSingleton && group.sessions[0] != null) {
                const s = group.sessions[0];
                const { generation } = splitEffectiveKey(s.id);
                const genLabel = generation > 0 ? `gen ${String(generation)}` : undefined;
                const vm = buildCliVm(s, zone, genLabel);
                return (
                  <li key={group.conversation_id} className={styles.baseLaunchGroup} role="presentation">
                    {/* Non-collapsible label strip: surfaces the conversation label chip
                        (e.g. "Cron run" or "Conversation") so every singleton is labelled,
                        consistent with the multi-launch group header. No chevron/toggle since
                        there is nothing to collapse. */}
                    {/* Non-collapsible label strip: shows the conversation label chip
                        ("Cron run" / "Conversation") and optional spend. Omits the display
                        id since the session row directly below already shows it.
                        aria-hidden: the session row provides the accessible representation. */}
                    <div className={styles.singletonLabelStrip} aria-hidden="true">
                      <span className={styles.baseLaunchMeta}>
                        <span className={styles.convLensLabel}>{group.label}</span>
                        {spendLabel != null && (
                          <>
                            <span aria-hidden="true">·</span>
                            <span className={styles.baseLaunchSpend}>{spendLabel}</span>
                          </>
                        )}
                      </span>
                    </div>
                    <ul
                      className={styles.baseLaunchChildren}
                      role="group"
                      aria-label={`Session in ${group.label} ${group.displayId}`}
                    >
                      <SessionRow
                        key={vm.rowKey}
                        vm={vm}
                        isSelected={selected === vm.sessionKey}
                        onSelect={onSelect}
                        onDelete={handleDelete}
                        onRestore={handleRestore}
                      />
                    </ul>
                  </li>
                );
              }
              // Multi-session (or multi-launch) conversation: render with collapsible header.
              return (
                <li key={group.conversation_id} className={styles.baseLaunchGroup} role="presentation">
                  {/* Conversation header: collapsible; shows label, launch count, total spend. */}
                  <button
                    type="button"
                    className={styles.baseLaunchHeader}
                    aria-expanded={isExpanded}
                    aria-label={`${group.label} ${group.displayId}: ${launchCountLabel}${spendLabel != null ? `, ${spendLabel} total` : ''}`}
                    onClick={() => { handleToggleConversation(group.conversation_id); }}
                  >
                    <span className={styles.baseLaunchChevron} aria-hidden="true">
                      {isExpanded ? '[-]' : '[+]'}
                    </span>
                    <span className={styles.baseLaunchId}>{group.displayId}</span>
                    <span className={styles.baseLaunchMeta}>
                      <span className={styles.convLensLabel}>{group.label}</span>
                      <span aria-hidden="true">·</span>
                      <span className={styles.baseLaunchGenCount}>{launchCountLabel}</span>
                      {spendLabel != null && (
                        <>
                          <span aria-hidden="true">·</span>
                          <span className={styles.baseLaunchSpend}>{spendLabel}</span>
                        </>
                      )}
                    </span>
                  </button>
                  {/* Child session rows: shown when expanded. */}
                  {isExpanded && (
                    <ul
                      className={styles.baseLaunchChildren}
                      role="group"
                      aria-label={`Sessions in ${group.label} ${group.displayId}`}
                    >
                      {group.sessions.map((s) => {
                        const { generation } = splitEffectiveKey(s.id);
                        const genLabel = generation > 0 ? `gen ${String(generation)}` : `gen 0`;
                        const vm = buildCliVm(s, zone, genLabel);
                        return (
                          <SessionRow
                            key={vm.rowKey}
                            vm={vm}
                            isSelected={selected === vm.sessionKey}
                            onSelect={onSelect}
                            onDelete={handleDelete}
                            onRestore={handleRestore}
                          />
                        );
                      })}
                    </ul>
                  )}
                </li>
              );
            })}
          </ul>
        ) : cliQuery.isSuccess && cliVms.length > 0 && baseLaunchGroups != null ? (
          /* Grouped view (ST-07): per-base grouping when conversation lens is OFF and
             base-launches rollup data is available with multi-generation groups. */
          <ul className={styles.sessionList} role="listbox" aria-label="Agent sessions">
            {baseLaunchGroups.groups.map((group) => {
              const isExpanded = !collapsedGroups.has(group.base);
              const spendLabel = group.rollup.spend_usd > 0
                ? `$${group.rollup.spend_usd.toFixed(4)}`
                : null;
              const genCountLabel = group.rollup.generation_count === 1
                ? '1 generation'
                : `${String(group.rollup.generation_count)} generations`;
              // Short base id: use the first session's display_id (g0 row) if available,
              // else truncate the base key.
              const g0Session = group.sessions.find((s) => !isNonZeroGeneration(s.id));
              const baseDisplayId = g0Session?.display_id ?? group.base.slice(-8);
              return (
                <li key={group.base} className={styles.baseLaunchGroup} role="presentation">
                  {/* Group header: collapsible; shows base handle, generation count, total spend. */}
                  <button
                    type="button"
                    className={styles.baseLaunchHeader}
                    aria-expanded={isExpanded}
                    aria-label={`Base launch ${baseDisplayId}: ${genCountLabel}${spendLabel != null ? `, ${spendLabel} total` : ''}`}
                    onClick={() => { handleToggleGroup(group.base); }}
                  >
                    <span className={styles.baseLaunchChevron} aria-hidden="true">
                      {isExpanded ? '[-]' : '[+]'}
                    </span>
                    <span className={styles.baseLaunchId}>{baseDisplayId}</span>
                    <span className={styles.baseLaunchMeta}>
                      <span className={styles.baseLaunchGenCount}>{genCountLabel}</span>
                      {spendLabel != null && (
                        <>
                          <span aria-hidden="true">·</span>
                          <span className={styles.baseLaunchSpend}>{spendLabel}</span>
                        </>
                      )}
                    </span>
                  </button>
                  {/* Child generation rows: shown when expanded. */}
                  {isExpanded && (
                    <ul className={styles.baseLaunchChildren} role="group" aria-label={`Generations of base launch ${baseDisplayId}`}>
                      {group.sessions.map((s) => {
                        const { generation } = splitEffectiveKey(s.id);
                        const genLabel = generation > 0 ? `gen ${String(generation)}` : `gen 0`;
                        const vm = buildCliVm(s, zone, genLabel);
                        return (
                          <SessionRow
                            key={vm.rowKey}
                            vm={vm}
                            isSelected={selected === vm.sessionKey}
                            onSelect={onSelect}
                            onDelete={handleDelete}
                            onRestore={handleRestore}
                          />
                        );
                      })}
                    </ul>
                  )}
                </li>
              );
            })}
            {/* Ungrouped sessions: flat rows after the grouped section. */}
            {baseLaunchGroups.ungrouped.map((s) => {
              const { generation } = splitEffectiveKey(s.id);
              const genLabel = generation > 0 ? `gen ${String(generation)}` : undefined;
              const vm = buildCliVm(s, zone, genLabel);
              return (
                <SessionRow
                  key={vm.rowKey}
                  vm={vm}
                  isSelected={selected === vm.sessionKey}
                  onSelect={onSelect}
                  onDelete={handleDelete}
                  onRestore={handleRestore}
                />
              );
            })}
          </ul>
        ) : (
          cliQuery.isSuccess && cliVms.length > 0 && (
            <ul className={styles.sessionList} role="listbox" aria-label="Agent sessions">
              {cliVms.map((vm) => (
                <SessionRow
                  key={vm.rowKey}
                  vm={vm}
                  isSelected={selected === vm.sessionKey}
                  onSelect={onSelect}
                  onDelete={handleDelete}
                  onRestore={handleRestore}
                />
              ))}
            </ul>
          )
        )}
        {/* Pagination: only shown when server reports a total_count (newer server).
            pageCount = 0 means unknown/not yet loaded → hide the control. */}
        {sessionsPageCount > 0 && (
          <Pagination
            page={sessionsPage}
            pageCount={sessionsPageCount}
            pageSize={sessionsPageSize}
            totalCount={sessionsTotalCount}
            onGoToPage={sessionsPageActions.goToPage}
            onPrevPage={sessionsPageActions.prevPage}
            onNextPage={sessionsPageActions.nextPage}
            onSetPageSize={sessionsPageActions.setPageSize}
            label="Sessions pagination"
          />
        )}
      </div>

      {/* ---- Cron panel ---- */}
      <div
        id={cronPanelId}
        role="tabpanel"
        aria-labelledby={cronTabId}
        hidden={activeTab !== 'cron'}
        className={styles.pickerPanel}
      >
        {cronQuery.isPending && <p className={styles.pickerNotice}>Loading cron sessions...</p>}
        {cronQuery.isError && (
          <p className={styles.pickerError}>
            Failed to load cron sessions.
          </p>
        )}
        {cronQuery.isSuccess && cronVms.length === 0 && (
          <p className={styles.pickerNotice}>
            {showDeleted ? 'No deleted cron sessions in this range.' : 'No cron sessions in this range.'}
          </p>
        )}
        {cronQuery.isSuccess && cronVms.length > 0 && (
          <ul className={styles.sessionList} role="listbox" aria-label="Cron sessions">
            {cronVms.map((vm) => (
              <SessionRow
                key={vm.rowKey}
                vm={vm}
                isSelected={vm.sessionKey != null && selected === vm.sessionKey}
                onSelect={onSelect}
                onDelete={handleDelete}
                onRestore={handleRestore}
              />
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}
