// =============================================================================
// CommandView — Command landing page (T-2026-06-13-51).
//
// Consumes GET /api/command/status (single fetch) for all sections:
//   1. Workspace status bar  — real data: index state, entitlement, uplink
//   2. Operating-state cards — real data: active runs, 24h traces, warnings, cron agents
//   3. Recent cron runs      — EvidenceTable, max 5, failed-first from endpoint
//
// T-2026-06-21-01 streamline: removed sections 4–7 (Latest trace sessions,
// Budget & cost, Security & data, Display preferences). Those areas have
// dedicated routes in the nav rail.
//
// AUDIT #7: Timestamps are rendered as ABSOLUTE via the user's tz pref
// (resolveZone + formatAbsolute from lib/time.ts), with relative as supplement.
// prefs.ts and lib/time.ts are NOT modified.
//
// R2 migration (T-2026-06-14-13, Lane Cmd1):
// useEffect/useState loader replaced with useQuery from TanStack Query.
// queryKey: queryKeys.commandStatus() from api/queryClient.ts.
// staleTime: STALE_COMMANDS_TRACES_MS (120 000 ms = 2 min).
// isLoading/isError surface accessible status (text + shape, no raw hex).
// =============================================================================

import { useState, useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  fetchCommandStatus,
  type CommandStatusResponse,
  type CommandRecentRun,
} from '../api/commandApi';
import { queryKeys, STALE_COMMANDS_TRACES_MS } from '../api/queryClient';
import {
  toCostProvenance,
} from '../lib/provenance';
import { toRunStatus } from '../lib/status';
import { readPrefs, type Prefs } from '../lib/prefs';
import { resolveZone, formatAbsolute } from '../lib/time';
import { buildHash } from '../lib/router';
import { isAreaAvailable } from '../lib/buildFlags';
import { PageHeader } from '../components/PageHeader';
import { StatusBadge } from '../components/StatusBadge';
import { CostProvenanceChip } from '../components/CostProvenanceChip';
import { EmptyState } from '../components/EmptyState';
import { EvidenceTable, type EvidenceColumn } from '../components/EvidenceTable';
import styles from './CommandView.module.css';

// ---------------------------------------------------------------------------
// normalizeRunStatus: maps wire aliases to RunStatus values.
// Same adapter as CronView — the cron API may return 'completed'; our enum has 'complete'.
// ---------------------------------------------------------------------------
function normalizeRunStatus(raw: string | null | undefined): import('../lib/status').RunStatus {
  if (raw === 'completed' || raw === 'succeeded' || raw === 'success') return 'complete';
  return toRunStatus(raw);
}

// ---------------------------------------------------------------------------
// Relative time helper (short, supplemental display)
// ---------------------------------------------------------------------------
function relativeTime(ts: string | null | undefined): string {
  if (ts == null || ts === '') return '';
  const d = new Date(ts);
  if (Number.isNaN(d.getTime())) return '';
  const diffMs = Date.now() - d.getTime();
  const diffMin = Math.round(diffMs / 60_000);
  if (diffMin < 2) return 'just now';
  if (diffMin < 60) return `${String(diffMin)}m ago`;
  const diffH = Math.round(diffMin / 60);
  if (diffH < 24) return `${String(diffH)}h ago`;
  const diffD = Math.round(diffH / 24);
  return `${String(diffD)}d ago`;
}

// ---------------------------------------------------------------------------
// Freshness label
// ---------------------------------------------------------------------------
function freshnessLabel(ts: Date | null): string {
  if (ts === null) return '';
  return `Updated ${relativeTime(ts.toISOString())}`;
}

// ---------------------------------------------------------------------------
// AbsoluteTime: renders a timestamp as ABSOLUTE (primary) + relative (supplement).
// Consumes the timezone pref via resolveZone + formatAbsolute from lib/time.ts.
// ---------------------------------------------------------------------------
function AbsoluteTime({
  ts,
  ianaZone,
  className,
}: {
  ts: string | null | undefined;
  ianaZone: string;
  className?: string;
}) {
  if (ts == null || ts === '') return <span className={className}>&mdash;</span>;
  const absolute = formatAbsolute(ts, ianaZone);
  const rel = relativeTime(ts);
  return (
    <time dateTime={ts} title={rel !== '' ? rel : undefined} className={className}>
      {absolute}
      {rel !== '' && <span className={styles.timeRelative}> ({rel})</span>}
    </time>
  );
}

// ---------------------------------------------------------------------------
// CommandView
// ---------------------------------------------------------------------------

export default function CommandView() {
  // -- TanStack Query: /api/command/status -----------------------------------
  // R2 migration: replaces the manual useEffect/useState loader.
  // staleTime = 120 000 ms (STALE_COMMANDS_TRACES_MS). Background refetches on
  // window-focus are handled by TQ's global refetchOnWindowFocus default.
  const {
    data: commandData,
    isLoading,
    isError,
    error,
    dataUpdatedAt,
  } = useQuery({
    queryKey: queryKeys.commandStatus(),
    queryFn: fetchCommandStatus,
    staleTime: STALE_COMMANDS_TRACES_MS,
  });

  // -- Display prefs (reads localStorage once on mount) ----------------------
  const [prefs, setPrefs] = useState<Prefs>(() => readPrefs());
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setPrefs(readPrefs());
  }, []);

  // Resolved IANA zone from pref
  const ianaZone = resolveZone(prefs.timezone);

  // -- Derived values --------------------------------------------------------
  const status: CommandStatusResponse | null = commandData ?? null;
  const fetchedAt: Date | null = dataUpdatedAt > 0 ? new Date(dataUpdatedAt) : null;

  // Error message: ScopeApiError has .message, otherwise stringify
  const errorMessage: string = isError
    ? (error instanceof Error ? error.message : String(error))
    : '';

  // Section 3: sort recent_runs failed-first, then by started_at desc, take max 5
  const sortedRuns: CommandRecentRun[] = status
    ? [...status.recent_runs]
        .sort((a, b) => {
          const aFailed = normalizeRunStatus(a.status) === 'failed' ? 0 : 1;
          const bFailed = normalizeRunStatus(b.status) === 'failed' ? 0 : 1;
          if (aFailed !== bFailed) return aFailed - bFailed;
          return b.started_at.localeCompare(a.started_at);
        })
        .slice(0, 5)
    : [];

  // -- Table columns: recent cron runs (section 3) ---------------------------
  const cronRunColumns: EvidenceColumn<CommandRecentRun>[] = [
    {
      key: 'agent',
      header: 'Agent',
      render: (row) => (
        <a href="#/cron" className={styles.tableLink}>
          {row.agent_label}
        </a>
      ),
    },
    {
      key: 'status',
      header: 'Status',
      render: (row) => (
        <StatusBadge rawStatus={normalizeRunStatus(row.status)} />
      ),
    },
    {
      key: 'started',
      header: 'Started',
      render: (row) => (
        <AbsoluteTime ts={row.started_at} ianaZone={ianaZone} {...(styles.mono != null ? { className: styles.mono } : {})} />
      ),
    },
    {
      key: 'cost',
      header: 'Cost',
      render: (row) => (
        <CostProvenanceChip
          variant="single"
          costUsd={row.total_cost_usd ?? null}
          provenance={toCostProvenance(row.cost_provenance)}
        />
      ),
    },
    {
      key: 'trace',
      header: 'Trace',
      render: (row) => {
        if (row.session_key != null && row.session_key !== '') {
          return (
            <a
              href={buildHash({ name: 'trace', session: row.session_key })}
              className={styles.tableLink}
              aria-label={`View trace for ${row.agent_label}`}
            >
              Trace
            </a>
          );
        }
        // No session key: run was skipped or pre-dates session recording.
        // Fall back to the cron agents list.
        return (
          <a
            href={buildHash({ name: 'cron' })}
            className={styles.tableLink}
            aria-label={`View run for ${row.agent_label}`}
          >
            View run
          </a>
        );
      },
    },
  ];

  return (
    <div className={styles.view}>
      <PageHeader
        title="Command"
        {...(fetchedAt ? { freshness: freshnessLabel(fetchedAt) } : {})}
      />

      {/* ================================================================ */}
      {/* SECTION 1 — Workspace status bar                                  */}
      {/* Real data from /api/command/status workspace section.             */}
      {/* ================================================================ */}
      <section aria-labelledby="cmd-workspace-heading" className={styles.section}>
        <h2 id="cmd-workspace-heading" className={styles.sectionHeading}>
          Workspace
        </h2>
        <div className={styles.statusBar} role="status" aria-label="Workspace status">
          {isLoading && (
            <span className={styles.statusItem}>
              <span className={styles.statusLabel}>Status</span>
              <span className={styles.skeleton} aria-label="Loading workspace status" />
            </span>
          )}
          {isError && (
            <span className={styles.statusItem}>
              <span className={styles.statusLabel}>Status</span>
              <span className={styles.cardError}>Unavailable — {errorMessage}</span>
            </span>
          )}
          {status !== null && (
            <>
              <StatusItem
                label="Index"
                value={status.workspace.index_state}
              />
              <StatusItem
                label="Entitlement"
                value={`${status.entitlement.tier} / ${status.entitlement.status}`}
              />
              <StatusItem
                label="Uplink"
                value={
                  status.uplink.available
                    ? 'On'
                    : 'Off — no trace packets leave this machine'
                }
              />
              <StatusItem
                label="Server"
                value={status.workspace.server_reachable ? 'Reachable' : 'Local'}
              />
            </>
          )}
        </div>
      </section>

      {/* ================================================================ */}
      {/* SECTION 2 — Operating-state metric cards (scalar summaries)       */}
      {/* Cards pull from operating + budget sections of the endpoint.      */}
      {/* ================================================================ */}
      <section aria-labelledby="cmd-ops-heading" className={styles.section}>
        <h2 id="cmd-ops-heading" className={styles.sectionHeading}>
          Operating state
        </h2>
        <div className={styles.cardRow}>
          {/* Active runs */}
          <a href="#/cron" className={styles.card} aria-label="Active runs — go to Cron Agents page">
            <span className={styles.cardValue}>
              {isLoading
                ? <span className={styles.skeleton} aria-label="Loading cron data" />
                : isError
                  ? <span className={styles.cardError}>Unavailable</span>
                  : String(status?.operating.active_runs ?? '—')}
            </span>
            <span className={styles.cardLabel}>Active runs</span>
          </a>

          {/* 7d budget spend */}
          <a href="#/cost-ledger" className={styles.card} aria-label="7-day budget spend — go to Cost Ledger">
            <span className={styles.cardValue}>
              {isLoading
                ? <span className={styles.skeleton} aria-label="Loading budget data" />
                : isError
                  ? <span className={styles.cardError}>Unavailable</span>
                  : (
                    <CostProvenanceChip
                      variant="single"
                      costUsd={status?.budget.total_spend_usd ?? null}
                      provenance={toCostProvenance(status?.budget.cost_provenance)}
                    />
                  )}
            </span>
            <span className={styles.cardLabel}>7-day spend</span>
          </a>

          {/* 24h trace count */}
          <a href="#/trace" className={styles.card} aria-label="Trace sessions last 24h — go to Trace Sessions">
            <span className={styles.cardValue}>
              {isLoading
                ? <span className={styles.skeleton} aria-label="Loading trace data" />
                : isError
                  ? <span className={styles.cardError}>Unavailable</span>
                  : String(status?.operating.trace_count_24h ?? '—')}
            </span>
            <span className={styles.cardLabel}>Trace sessions (24h)</span>
          </a>

          {/* Open warnings — link to audit-log only when that area is available in this build */}
          {isAreaAvailable('audit-log') ? (
            <a href="#/audit-log" className={styles.card} aria-label="Open warnings — go to Audit Log">
              <span className={styles.cardValue}>
                {isLoading
                  ? <span className={styles.skeleton} aria-label="Loading warning count" />
                  : isError
                    ? <span className={styles.cardError}>Unavailable</span>
                    : status?.operating.warning_count === 0
                      ? <span className={styles.muted}>&mdash;</span>
                      : String(status?.operating.warning_count ?? '—')}
              </span>
              <span className={styles.cardLabel}>Open warnings</span>
              {status !== null && status.operating.warning_count === 0 && (
                <span className={styles.cardSub}>None</span>
              )}
            </a>
          ) : (
            <div className={styles.card}>
              <span className={styles.cardValue}>
                {isLoading
                  ? <span className={styles.skeleton} aria-label="Loading warning count" />
                  : isError
                    ? <span className={styles.cardError}>Unavailable</span>
                    : status?.operating.warning_count === 0
                      ? <span className={styles.muted}>&mdash;</span>
                      : String(status?.operating.warning_count ?? '—')}
              </span>
              <span className={styles.cardLabel}>Open warnings</span>
              {status !== null && status.operating.warning_count === 0 && (
                <span className={styles.cardSub}>None</span>
              )}
            </div>
          )}
        </div>
      </section>

      {/* ================================================================ */}
      {/* SECTION 3 — Recent cron runs (EvidenceTable, max 5, failed-first) */}
      {/* Data from recent_runs in /api/command/status.                     */}
      {/* ================================================================ */}
      <section aria-labelledby="cmd-cron-heading" className={styles.section}>
        <div className={styles.sectionHeaderRow}>
          <h2 id="cmd-cron-heading" className={styles.sectionHeading}>
            Recent cron runs
          </h2>
          <a href="#/cron" className={styles.sectionAction}>
            All cron agents
          </a>
        </div>
        {isLoading && (
          <p className={styles.loadingMsg} aria-live="polite">Loading recent cron runs&hellip;</p>
        )}
        {isError && (
          <EmptyState
            variant="governance"
            glyph="x-circle"
            heading="Could not load cron runs"
            description={errorMessage}
            action={
              <a href="#/cron" className={styles.btn}>
                Go to Cron Agents
              </a>
            }
          />
        )}
        {!isLoading && !isError && sortedRuns.length === 0 && (
          <EmptyState
            variant="neutral"
            glyph="clock"
            heading="No cron runs yet"
            description="Create a cron agent and run it to see results here."
            action={
              <a href="#/cron" className={styles.btn}>
                Go to Cron Agents
              </a>
            }
          />
        )}
        {!isLoading && !isError && sortedRuns.length > 0 && (
          <>
            <EvidenceTable
              caption="Recent cron runs (up to 5, failed first)"
              columns={cronRunColumns}
              rows={sortedRuns}
              getRowKey={(row) => row.run_id}
              mobileCards={true}
            />
            <p className={styles.freshness}>
              {freshnessLabel(fetchedAt)}
            </p>
          </>
        )}
      </section>
    </div>
  );
}

// ---------------------------------------------------------------------------
// StatusItem — inline key/value for the workspace status bar (section 1)
// ---------------------------------------------------------------------------
function StatusItem({
  label,
  value,
  muted = false,
}: {
  label: string;
  value: string;
  muted?: boolean;
}) {
  return (
    <div className={styles.statusItem}>
      <span className={styles.statusLabel}>{label}</span>
      <span className={muted ? styles.statusValueMuted : styles.statusValue}>{value}</span>
    </div>
  );
}
