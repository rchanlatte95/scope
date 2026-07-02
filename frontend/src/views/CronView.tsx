import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  fetchCronAgents,
  fetchCronAgent,
  fetchCronCapabilities,
  fetchCronRuns,
  fetchCronRun,
  createCronAgent,
  updateCronAgent,
  removeCronAgent,
  runCronAgent,
  enableCronAgent,
  disableCronAgent,
  CronApiError,
  type CronAgent,
  type CronRun,
  type CronRunResult,
} from '../api/cronApi';
import {
  HARNESS_UNCHOSEN,
  SCHEDULE_SUGAR,
  validateForm,
  buildCreateRequest,
  buildUpdateRequest,
  formStateFromAgent,
  deriveHarnessOptions,
  scheduleHint,
  humanSchedule,
  isFailureStatus,
  isSkippedStatus,
  type CronFormState,
  type HarnessOption,
} from '../lib/cron';
import { toCostProvenance } from '../lib/provenance';
import { toRunStatus, type RunStatus } from '../lib/status';
import { readLastHarness, writeLastHarness } from '../lib/lastHarness';
import { readPrefs } from '../lib/prefs';
import { resolveZone, formatAbsolute } from '../lib/time';
import { buildHash, navigate, parseHash, type CronSortKey, type SortOrder } from '../lib/router';
import { PageHeader } from '../components/PageHeader';
import { StatusBadge } from '../components/StatusBadge';
import { StatusGlyphBlock } from '../components/StatusGlyphBlock';
import { CostProvenanceChip } from '../components/CostProvenanceChip';
import { EmptyState } from '../components/EmptyState';
import { ExportCsvButton } from '../components/ExportCsvButton';
import type { CsvColumn } from '../lib/exportCsv';
import {
  queryKeys,
  invalidationKeys,
  STALE_AGENTS_MS,
  STALE_AGENT_MS,
  STALE_RUNS_MS,
  STALE_RUN_STATUS_MS,
} from '../api/queryClient';
import { useSingleFireMutation } from '../hooks/useSingleFireMutation';
import styles from './CronView.module.css';

// ---------------------------------------------------------------------------
// normalizeRunStatus: maps wire aliases to RunStatus values.
// The server (and some mocks) return 'completed' while RunStatus has 'complete'.
// This adapter lives in the view because it is a wire-to-display boundary concern,
// not a lib/status concern (lib/status owns the closed enum; adapters can alias here).
// ---------------------------------------------------------------------------
function normalizeRunStatus(raw: string | null | undefined): RunStatus {
  if (raw === 'completed') return 'complete';
  if (raw === 'succeeded' || raw === 'success') return 'complete';
  return toRunStatus(raw);
}

// ---------------------------------------------------------------------------
// isTerminalStatus: true when a run has reached a terminal state.
// Used to stop polling once the run is complete/failed/skipped.
// ---------------------------------------------------------------------------
function isTerminalStatus(raw: string | null | undefined): boolean {
  if (raw == null || raw === '') return false;
  const s = normalizeRunStatus(raw);
  // 'unknown' = a non-empty but unrecognized server status. Treat it as terminal so polling
  // STOPS rather than loops forever (safety). null/'' ("no status yet") is handled above and
  // stays non-terminal so polling continues. (toRunStatus now narrows unrecognized -> 'unknown'
  // instead of 'failed'; this preserves the poll-stop behavior the badge change would otherwise lose.)
  return s === 'complete' || s === 'failed' || s === 'skipped' || s === 'unknown';
}

// ---------------------------------------------------------------------------
// Cron agent delete review-state copy (T-2026-06-15-08 / G5-05).
//
// Shared between AgentRow and AgentDetailActions: names the specific agent and states
// the concrete consequence, using a REAL run-history count when it is available instead
// of a fabricated one.
//
// IMPORTANT provenance note: DELETE /api/cron/agents/{label} only soft-deletes the agent
// row server-side (CronRepository.RemoveAgentByLabel sets deleted_at; it does NOT cascade
// delete cron_runs). So this copy does not claim run-history rows are destroyed — it says
// they become unreachable from the UI, which IS true (the agent drops out of the list and
// its detail route 404s), without overclaiming a hard data-deletion fact that isn't real.
// ---------------------------------------------------------------------------

/** Max `limit` GET /api/cron/agents/{label}/runs accepts (server clamps to [1,500]). */
const DELETE_RUN_COUNT_FETCH_LIMIT = 500;

/** How long the inline "Cron agent deleted" success status stays visible before the row
 *  disappears from the list / the detail page navigates back (so it is actually seen). */
const REMOVE_SUCCESS_DELAY_MS = 900;

type RemoveRunCountState =
  | { kind: 'loading' }
  | { kind: 'known'; count: number }
  | { kind: 'unknown' };

/**
 * Derives the run-history count state from the delete-confirm count query. 'unknown' covers
 * both a fetch error and hitting the fetch cap (we cannot tell if the cap IS the true total
 * or an undercount, so we do not print a number we cannot stand behind).
 */
function removeRunCountState(query: { data: CronRun[] | undefined; isError: boolean }): RemoveRunCountState {
  if (query.data != null) {
    return query.data.length >= DELETE_RUN_COUNT_FETCH_LIMIT
      ? { kind: 'unknown' }
      : { kind: 'known', count: query.data.length };
  }
  return query.isError ? { kind: 'unknown' } : { kind: 'loading' };
}

/** Builds the delete review-state consequence copy naming the agent and the real count. */
function removeConsequenceCopy(label: string, countState: RemoveRunCountState): string {
  const headline = `Cron agent ${label} will be permanently deleted.`;
  const scheduleClause = 'Its schedule stops immediately and it will not run again.';
  if (countState.kind === 'loading') {
    return `${headline} ${scheduleClause} Checking its run-history record count…`;
  }
  if (countState.kind === 'known') {
    if (countState.count === 0) {
      return `${headline} ${scheduleClause} It has no run history yet, so none will be lost. This cannot be undone.`;
    }
    const noun = countState.count === 1 ? 'record' : 'records';
    return `${headline} ${scheduleClause} Its ${String(countState.count)} run-history ${noun} will no longer be visible on this page. This cannot be undone.`;
  }
  return `${headline} ${scheduleClause} Its run-history records will no longer be visible on this page. This cannot be undone.`;
}

// ---------------------------------------------------------------------------
// Sort state — URL-owned (T-2026-06-13-35).
//
// The URL hash owns the sort key and order. Default view (no ?sort=) uses
// the enabled-first precedence. When a column is clicked, that column becomes
// primary; the tiebreak chain is always status->name->creation.
// ---------------------------------------------------------------------------

/** Reads sort state from the current URL hash. */
function readSortFromHash(): { sort: CronSortKey | null; order: SortOrder | null } {
  const route = parseHash(window.location.hash);
  return { sort: route.sort ?? null, order: route.order ?? null };
}

/** Writes sort state to the URL hash without triggering a navigation event loop. */
function writeSortToHash(sort: CronSortKey | null, order: SortOrder | null): void {
  window.location.hash = buildHash({ name: 'cron', id: null, session: null, tab: null, sort, order });
}

/**
 * RunStatus numeric rank for sorting by last-run status.
 * Lower = more operationally important.
 */
const RUN_STATUS_RANK: Partial<Record<string, number>> = {
  failed: 0,
  blocked: 1,
  running: 2,
  queued: 3,
  paused: 4,
  skipped: 5,
  complete: 6,
  ready: 7,
};

function runStatusRank(raw: string | null | undefined): number {
  if (raw == null || raw === '') return 99; // never-run agents sort last
  const s = normalizeRunStatus(raw);
  return RUN_STATUS_RANK[s] ?? 50;
}

/**
 * Default enabled-first sort: enabled agents first, tiebreak status->name->creation.
 */
function agentRiskOrder(a: CronAgent, b: CronAgent): number {
  // enabled-first
  const enabledDiff = (a.enabled ? 0 : 1) - (b.enabled ? 0 : 1);
  if (enabledDiff !== 0) return enabledDiff;
  // tiebreak: last-run status rank
  const statusDiff = runStatusRank(a.last_run_status) - runStatusRank(b.last_run_status);
  if (statusDiff !== 0) return statusDiff;
  // tiebreak: name alpha
  const nameDiff = a.label.localeCompare(b.label);
  if (nameDiff !== 0) return nameDiff;
  // tiebreak: creation time (oldest first)
  return a.created_at.localeCompare(b.created_at);
}

/**
 * Tiebreak chain when a primary sort key is active: status -> name -> creation.
 */
function tiebreak(a: CronAgent, b: CronAgent): number {
  const statusDiff = runStatusRank(a.last_run_status) - runStatusRank(b.last_run_status);
  if (statusDiff !== 0) return statusDiff;
  const nameDiff = a.label.localeCompare(b.label);
  if (nameDiff !== 0) return nameDiff;
  return a.created_at.localeCompare(b.created_at);
}

/**
 * Sorts agents by the given column key + order, with tiebreak on status->name->creation.
 * When sort is null, falls back to the enabled-first default.
 */
function sortAgents(
  agents: CronAgent[],
  sort: CronSortKey | null,
  order: SortOrder | null,
  ianaZone: string,
): CronAgent[] {
  const sorted = [...agents];
  if (!sort) {
    sorted.sort(agentRiskOrder);
    return sorted;
  }

  const dir = order === 'desc' ? -1 : 1;

  sorted.sort((a, b) => {
    let primary = 0;
    switch (sort) {
      case 'name':
        primary = a.label.localeCompare(b.label);
        break;
      case 'status':
        primary = runStatusRank(a.last_run_status) - runStatusRank(b.last_run_status);
        break;
      case 'schedule':
        // Sort alphabetically on raw expression; this is lexicographic but consistent.
        primary = a.schedule.localeCompare(b.schedule);
        break;
      case 'last_run':
        // Null last_run_at sorts last regardless of direction.
        if ((a.last_run_at == null || a.last_run_at === '') && (b.last_run_at == null || b.last_run_at === '')) primary = 0;
        else if (a.last_run_at == null || a.last_run_at === '') primary = 1;
        else if (b.last_run_at == null || b.last_run_at === '') primary = -1;
        else primary = a.last_run_at.localeCompare(b.last_run_at);
        break;
      case 'cost':
        // Null/absent cost sorts last regardless of direction.
        // Use == null (not === null) because WhenWritingNull can omit the field -> undefined.
        if (a.last_run_cost == null && b.last_run_cost == null) primary = 0;
        else if (a.last_run_cost == null) primary = 1;
        else if (b.last_run_cost == null) primary = -1;
        else primary = a.last_run_cost - b.last_run_cost;
        break;
    }
    if (primary !== 0) return primary * dir;
    return tiebreak(a, b);
  });

  // Suppress unused param warning (ianaZone is passed for future use if schedule sort
  // needs to compare times in a zone-aware way; currently lexicographic is sufficient).
  void ianaZone;

  return sorted;
}

// =============================================================================
// CronView: the Cron Agents page — C1 migration onto F1's TanStack Query + resilience infra.
//
// Changes from FE-3 migration (T-2026-06-13-35):
//  - R2: Manual loaders replaced with useQuery (agents list, capabilities).
//  - R3: Mutations (create, enable, disable, remove) routed through useMutation +
//    useSingleFireMutation; no more ad hoc submitting/toggling/removing state flags.
//  - R7-FE: run-now passes crypto.randomUUID() as the idempotencyKey (3rd arg).
//  - Poll loops for run-now moved into AgentRow/AgentDetailActions via useQuery
//    refetchInterval on the run-status query.
//
// Behavioral contracts preserved:
//  - POST /run returns 202 immediately; poll GET /runs/{id} until terminal.
//  - Double-submit guard on run-now (useSingleFireMutation + isPending disabled state).
//  - Capabilities drive harness option disabling.
//  - Harness pre-fill from localStorage (lastHarness.ts).
//  - Server 400/404 error bodies shown verbatim.
//  - Optimistic enable/disable indicator with server reconciliation.
//  - Confirm-before-remove.
//  - Deep-link session key -> #/trace?session=KEY.
//  - 409 from run-now treated as benign "already running" (no error toast).
//
// Mount contract (FE-2a): default export, no required props.
// =============================================================================

const EMPTY_FORM: CronFormState = {
  label: '',
  repoPath: '',
  schedule: '',
  prompt: '',
  harness: HARNESS_UNCHOSEN,
  model: '',
  tokenBudget: '',
  // Concurrency guard defaults ON (matches the server default), surfaced as an editable toggle.
  blockConcurrentRuns: true,
  maxRuntimeSeconds: '',
};

/**
 * Builds a blank create form, pre-filling the harness with the user's last stored selection
 * when one is present and valid; otherwise the no-default sentinel.
 */
function blankFormWithRememberedHarness(): CronFormState {
  return { ...EMPTY_FORM, harness: readLastHarness() ?? HARNESS_UNCHOSEN };
}

/** Human-readable lead-in for an error, distinguishing transport from server reply. */
function describeError(err: unknown): { message: string; transport: boolean } {
  if (err instanceof CronApiError) {
    if (err.status === 0) {
      return {
        message: `Could not reach Scope.Server: ${err.message}. Is it running on port 5130?`,
        transport: true,
      };
    }
    return { message: err.message, transport: false };
  }
  return { message: err instanceof Error ? err.message : String(err), transport: false };
}

/**
 * Column header config for the agent table.
 */
interface ColumnDef {
  key: CronSortKey;
  label: string;
}

const COLUMNS: ColumnDef[] = [
  { key: 'name', label: 'Name' },
  { key: 'schedule', label: 'Schedule' },
  { key: 'status', label: 'Last-run status' },
  { key: 'last_run', label: 'Last run' },
  { key: 'cost', label: 'Cost' },
];

/**
 * CronView: the Cron Agents page.
 * DEFAULT EXPORT, NO REQUIRED PROPS (mount contract for FE-2a shell).
 */
export default function CronView() {
  const queryClient = useQueryClient();

  // URL-owned sort state (T-2026-06-13-35).
  const [sortState, setSortState] = useState<{ sort: CronSortKey | null; order: SortOrder | null }>(
    readSortFromHash,
  );

  // Sync sort state from URL on hashchange (external nav, back/forward).
  useEffect(() => {
    const onHash = () => { setSortState(readSortFromHash()); };
    window.addEventListener('hashchange', onHash);
    return () => { window.removeEventListener('hashchange', onHash); };
  }, []);

  // Resolved timezone from user prefs (read once on mount; does not need to
  // re-read on every render since timezone changes are rare session-level events).
  const ianaZone = useMemo(() => resolveZone(readPrefs().timezone), []);

  // R2: agents list via useQuery (staleTime 30s; mutations invalidate).
  const agentsQuery = useQuery({
    queryKey: queryKeys.agents(),
    queryFn: () => fetchCronAgents(),
    staleTime: STALE_AGENTS_MS,
  });

  // R2: capabilities via useQuery (stale 30s; near-static for session).
  const capabilitiesQuery = useQuery({
    queryKey: queryKeys.capabilities(),
    queryFn: () => fetchCronCapabilities(),
    staleTime: STALE_AGENTS_MS,
  });

  const capabilitiesLoaded = !capabilitiesQuery.isLoading;
  const harnessOptions = useMemo(
    () => deriveHarnessOptions(capabilitiesQuery.data ?? []),
    [capabilitiesQuery.data],
  );

  const sortedAgents = useMemo(
    () => sortAgents(agentsQuery.data ?? [], sortState.sort, sortState.order, ianaZone),
    [agentsQuery.data, sortState.sort, sortState.order, ianaZone],
  );


  /**
   * Handles clicking a column header: if already the primary key, toggles order;
   * otherwise sets it as primary with default 'asc' order.
   */
  const onColumnSort = useCallback(
    (key: CronSortKey) => {
      const { sort, order } = sortState;
      if (sort === key) {
        // Toggle order
        const newOrder: SortOrder = order === 'asc' ? 'desc' : 'asc';
        setSortState({ sort: key, order: newOrder });
        writeSortToHash(key, newOrder);
      } else {
        setSortState({ sort: key, order: 'asc' });
        writeSortToHash(key, 'asc');
      }
    },
    [sortState],
  );

  // Callback to invalidate agents list (used by create/remove/enable/disable mutations).
  const invalidateAgents = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: invalidationKeys.agents });
  }, [queryClient]);

  // Derive list state from query state (preserves the same loading/error/loaded contract).
  let listState: { status: 'loading' } | { status: 'error'; message: string; transport: boolean } | { status: 'loaded' };
  if (agentsQuery.isLoading) {
    listState = { status: 'loading' };
  } else if (agentsQuery.isError) {
    const described = describeError(agentsQuery.error);
    listState = { status: 'error', message: described.message, transport: described.transport };
  } else {
    listState = { status: 'loaded' };
  }

  return (
    <div className={styles.view}>
      <PageHeader title="Cron Agents" />

      {/* Create form — always visible (behavioral contract preserved). PageHeader h1 anchors the page. */}
      <section className={styles.section} aria-labelledby="create-heading">
        <h2 id="create-heading" className={styles.sectionHeading}>
          Create a cron agent
        </h2>
        <CreateAgentForm
          harnessOptions={harnessOptions}
          capabilitiesLoaded={capabilitiesLoaded}
          onCreated={invalidateAgents}
        />
      </section>

      <section className={styles.section} aria-labelledby="agents-heading">
        <h2 id="agents-heading" className={styles.sectionHeading}>
          Cron agents
        </h2>
        <AgentList
          state={listState}
          agents={sortedAgents}
          onChanged={invalidateAgents}
          sortKey={sortState.sort}
          sortOrder={sortState.order}
          onColumnSort={onColumnSort}
          ianaZone={ianaZone}
          harnessOptions={harnessOptions}
          capabilitiesLoaded={capabilitiesLoaded}
        />
      </section>
    </div>
  );
}

// ---------------------------------------------------------------------------
// CreateAgentForm
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// SharedCronFields — the editable fieldset shared by the create AND edit forms
// (T-2026-06-17-05). Reusing one fieldset guarantees both forms gather the exact
// same inputs (including the block_concurrent_runs toggle + max_runtime_seconds
// field) and validate identically, so create and edit cannot drift.
//
// `idPrefix` namespaces the input ids so a create form and an open edit modal can
// coexist in the DOM without duplicate ids. The label field is read-only in edit
// (the label is the immutable identity/key), controlled via `labelReadOnly`.
// ---------------------------------------------------------------------------

interface SharedCronFieldsProps {
  idPrefix: string;
  form: CronFormState;
  set: (key: keyof CronFormState) => (
    e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>,
  ) => void;
  setToggle: (key: keyof CronFormState, value: boolean) => void;
  harnessOptions: HarnessOption[];
  capabilitiesLoaded: boolean;
  labelReadOnly: boolean;
  harnessHelp: string;
}

function SharedCronFields({
  idPrefix,
  form,
  set,
  setToggle,
  harnessOptions,
  capabilitiesLoaded,
  labelReadOnly,
  harnessHelp,
}: SharedCronFieldsProps) {
  const schedulePreview = scheduleHint(form.schedule);
  const selectedDetail = harnessOptions.find((o) => o.id === form.harness)?.detail ?? null;
  const id = (suffix: string) => `${idPrefix}-${suffix}`;

  return (
    <>
      <div className={styles.field}>
        <label className={styles.label} htmlFor={id('label')}>
          Label{labelReadOnly ? ' (immutable)' : ''}
        </label>
        <input
          id={id('label')}
          className={styles.input}
          value={form.label}
          onChange={set('label')}
          autoComplete="off"
          placeholder="nightly-audit"
          readOnly={labelReadOnly}
          aria-readonly={labelReadOnly}
          aria-describedby={labelReadOnly ? id('label-help') : undefined}
        />
        {labelReadOnly ? (
          <p id={id('label-help')} className={styles.help}>
            The label is the agent&apos;s identity and cannot be changed. Remove and recreate to rename.
          </p>
        ) : null}
      </div>

      <div className={styles.field}>
        <label className={styles.label} htmlFor={id('repo')}>
          Repository path
        </label>
        <input
          id={id('repo')}
          className={styles.input}
          value={form.repoPath}
          onChange={set('repoPath')}
          autoComplete="off"
          placeholder="C:/repos/scope"
        />
      </div>

      <div className={styles.field}>
        <label className={styles.label} htmlFor={id('schedule')}>
          Schedule (5-field cron, UTC)
        </label>
        <input
          id={id('schedule')}
          className={styles.input}
          value={form.schedule}
          onChange={set('schedule')}
          autoComplete="off"
          placeholder="0 3 * * *"
          aria-describedby={id('schedule-help')}
        />
        <p id={id('schedule-help')} className={styles.help}>
          Five fields: minute hour day month weekday, evaluated in UTC. Shorthands:{' '}
          {Object.entries(SCHEDULE_SUGAR).map(([sugar, expansion], i) => (
            <span key={sugar}>
              {i > 0 ? ', ' : ''}
              <code className={styles.code}>{sugar}</code> = <code className={styles.code}>{expansion}</code>
            </span>
          ))}
          .
          {form.schedule.trim() !== '' && schedulePreview != null && schedulePreview !== '' ? (
            <span className={styles.hintWarn}> {schedulePreview}</span>
          ) : null}
        </p>
      </div>

      <div className={styles.field}>
        <label className={styles.label} htmlFor={id('prompt')}>
          Prompt
        </label>
        <textarea
          id={id('prompt')}
          className={styles.textarea}
          value={form.prompt}
          onChange={set('prompt')}
          rows={3}
          placeholder="Review the diff for regressions and summarize risks."
        />
      </div>

      <div className={styles.field}>
        <label className={styles.label} htmlFor={id('harness')}>
          Harness
        </label>
        <select
          id={id('harness')}
          className={styles.input}
          value={form.harness}
          onChange={set('harness')}
          aria-describedby={id('harness-help')}
        >
          <option value={HARNESS_UNCHOSEN}>Select a harness…</option>
          {harnessOptions.map((opt) => (
            <option key={opt.id} value={opt.id} disabled={!opt.available}>
              {opt.id}
              {opt.available ? '' : ' (unavailable)'}
            </option>
          ))}
        </select>
        <p id={id('harness-help')} className={styles.help}>
          {harnessHelp}
        </p>
        <ul className={styles.capabilityList}>
          {harnessOptions.map((opt) => (
            <li key={opt.id} className={styles.capabilityRow}>
              <span className={styles.capabilityGlyph} aria-hidden="true">
                {opt.available ? '●' : '○'}
              </span>
              <span className={styles.capabilityName}>{opt.id}</span>
              <span className={styles.capabilityStatus}>
                {capabilitiesLoaded ? (opt.available ? 'available' : 'unavailable') : 'checking…'}
              </span>
              <span className={styles.capabilityDetail}>{opt.detail}</span>
            </li>
          ))}
        </ul>
      </div>

      <div className={styles.fieldRow}>
        <div className={styles.field}>
          <label className={styles.label} htmlFor={id('model')}>
            Model (optional)
          </label>
          <input
            id={id('model')}
            className={styles.input}
            value={form.model}
            onChange={set('model')}
            autoComplete="off"
            placeholder="leave blank for harness default"
          />
        </div>

        <div className={styles.field}>
          <label className={styles.label} htmlFor={id('budget')}>
            Token budget (optional)
          </label>
          <input
            id={id('budget')}
            className={styles.input}
            value={form.tokenBudget}
            onChange={set('tokenBudget')}
            inputMode="numeric"
            autoComplete="off"
            placeholder="e.g. 20000"
          />
        </div>
      </div>

      <div className={styles.fieldRow}>
        <div className={styles.field}>
          <label className={styles.label} htmlFor={id('max-runtime')}>
            Max runtime, seconds (optional)
          </label>
          <input
            id={id('max-runtime')}
            className={styles.input}
            value={form.maxRuntimeSeconds}
            onChange={set('maxRuntimeSeconds')}
            inputMode="numeric"
            autoComplete="off"
            placeholder="blank = global default"
            aria-describedby={id('max-runtime-help')}
          />
          <p id={id('max-runtime-help')} className={styles.help}>
            Wall-clock cutoff for one run. Blank uses the global supervisor default.
          </p>
        </div>

        <div className={styles.field}>
          <span className={styles.label} id={id('concurrency-label')}>
            Concurrency
          </span>
          <label className={styles.checkboxRow} htmlFor={id('block-concurrent')}>
            <input
              id={id('block-concurrent')}
              type="checkbox"
              className={styles.checkbox}
              checked={form.blockConcurrentRuns}
              onChange={(e) => { setToggle('blockConcurrentRuns', e.target.checked); }}
              aria-describedby={id('block-concurrent-help')}
            />
            <span>Block concurrent runs</span>
          </label>
          <p id={id('block-concurrent-help')} className={styles.help}>
            When on, a second run-now is refused while one is in flight (recommended).
          </p>
        </div>
      </div>

      {selectedDetail != null && selectedDetail !== '' && form.harness !== HARNESS_UNCHOSEN ? (
        <p className={styles.help}>Selected harness: {selectedDetail}</p>
      ) : null}
    </>
  );
}

// ---------------------------------------------------------------------------
// CreateAgentForm
// ---------------------------------------------------------------------------

interface CreateAgentFormProps {
  harnessOptions: HarnessOption[];
  capabilitiesLoaded: boolean;
  onCreated: () => void;
}

function CreateAgentForm({ harnessOptions, capabilitiesLoaded, onCreated }: CreateAgentFormProps) {
  const [form, setForm] = useState<CronFormState>(blankFormWithRememberedHarness);
  const [formError, setFormError] = useState<string | null>(null);
  const [createdLabel, setCreatedLabel] = useState<string | null>(null);
  const [rememberedHarness, setRememberedHarness] = useState<string | null>(() => readLastHarness());
  const prefilled = rememberedHarness !== null && form.harness === rememberedHarness;

  const set = (key: keyof CronFormState) => (
    e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>,
  ) => { setForm((f) => ({ ...f, [key]: e.target.value })); };
  const setToggle = (key: keyof CronFormState, value: boolean) => {
    setForm((f) => ({ ...f, [key]: value }));
  };

  // R3: create mutation via useSingleFireMutation (double-submit guard).
  const createMutation = useSingleFireMutation({
    mutationFn: (body: Parameters<typeof createCronAgent>[0]) => createCronAgent(body),
    onSuccess: (created) => {
      setCreatedLabel(created.label);
      writeLastHarness(form.harness);
      setRememberedHarness(readLastHarness());
      setForm(blankFormWithRememberedHarness());
      onCreated();
    },
    onError: (err: unknown) => {
      setFormError(describeError(err).message);
    },
  });

  const onSubmit = (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setFormError(null);
    setCreatedLabel(null);

    const validation = validateForm(form);
    if (!validation.ok) {
      setFormError(validation.reason);
      return;
    }

    const body = buildCreateRequest(form);
    createMutation.safeMutate(body);
  };

  const harnessHelp = prefilled
    ? `Pre-filled from your last selection (${rememberedHarness}). Change it below if you want a different harness.`
    : 'No default: choose claude-code or codex explicitly.';

  return (
    <form className={styles.form} onSubmit={onSubmit} noValidate>
      <SharedCronFields
        idPrefix="cron-create"
        form={form}
        set={set}
        setToggle={setToggle}
        harnessOptions={harnessOptions}
        capabilitiesLoaded={capabilitiesLoaded}
        labelReadOnly={false}
        harnessHelp={harnessHelp}
      />

      {formError != null && formError !== '' ? (
        <div className={styles.error} role="alert">
          <StatusGlyphBlock token="danger" shapeName="x-circle" />
          <div className={styles.errorContent}>{formError}</div>
        </div>
      ) : null}

      {createdLabel != null && createdLabel !== '' ? (
        <div className={styles.success} role="status">
          <StatusGlyphBlock token="success" shapeName="check-circle" />
          <div className={styles.successContent}>
            Created cron agent <strong>{createdLabel}</strong>.
          </div>
        </div>
      ) : null}

      <div className={styles.formActions}>
        <button type="submit" className={styles.primaryBtn} disabled={createMutation.isPending}>
          {createMutation.isPending ? 'Creating…' : 'Create agent'}
        </button>
      </div>
    </form>
  );
}

// ---------------------------------------------------------------------------
// EditAgentModal — a modal dialog wrapping the SAME SharedCronFields + validation,
// prefilled from the agent's current values (T-2026-06-17-05). On save it PUTs the
// full-replace body and invalidates the cron queries. The label is read-only; the
// enabled flag is untouched (it stays on the row's enable/disable button).
// ---------------------------------------------------------------------------

interface EditAgentModalProps {
  agent: CronAgent;
  harnessOptions: HarnessOption[];
  capabilitiesLoaded: boolean;
  onClose: () => void;
  onSaved: () => void;
}

function EditAgentModal({ agent, harnessOptions, capabilitiesLoaded, onClose, onSaved }: EditAgentModalProps) {
  const [form, setForm] = useState<CronFormState>(() => formStateFromAgent(agent));
  const [formError, setFormError] = useState<string | null>(null);
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const headingId = `edit-agent-heading-${agent.id}`;

  const set = (key: keyof CronFormState) => (
    e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>,
  ) => { setForm((f) => ({ ...f, [key]: e.target.value })); };
  const setToggle = (key: keyof CronFormState, value: boolean) => {
    setForm((f) => ({ ...f, [key]: value }));
  };

  // Move focus into the dialog on open and trap Escape to close (accessibility).
  useLayoutEffect(() => {
    dialogRef.current?.focus();
  }, []);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('keydown', onKey); };
  }, [onClose]);

  const updateMutation = useSingleFireMutation({
    mutationFn: (body: Parameters<typeof updateCronAgent>[1]) => updateCronAgent(agent.label, body),
    onSuccess: () => {
      onSaved();
      onClose();
    },
    onError: (err: unknown) => {
      setFormError(describeError(err).message);
    },
  });

  const onSubmit = (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    setFormError(null);
    const validation = validateForm(form);
    if (!validation.ok) {
      setFormError(validation.reason);
      return;
    }
    updateMutation.safeMutate(buildUpdateRequest(form));
  };

  // Render through a portal to document.body so the overlay is NOT nested inside the agent table's
  // <tbody> (invalid DOM: a <tbody> cannot contain a <div>). The portal keeps the dialog a direct
  // child of body while staying owned by this component for state/focus management.
  return createPortal(
    <div
      className={styles.modalOverlay}
      role="presentation"
      onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div
        ref={dialogRef}
        className={styles.modalDialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        tabIndex={-1}
      >
        <h2 id={headingId} className={styles.modalHeading}>
          Edit cron agent: {agent.label}
        </h2>
        <p className={styles.help}>
          Changes apply to the next scheduled run. A run already in flight is unaffected.
        </p>
        <form className={styles.form} onSubmit={onSubmit} noValidate>
          <SharedCronFields
            idPrefix={`cron-edit-${agent.id}`}
            form={form}
            set={set}
            setToggle={setToggle}
            harnessOptions={harnessOptions}
            capabilitiesLoaded={capabilitiesLoaded}
            labelReadOnly
            harnessHelp="Choose claude-code or codex explicitly."
          />

          {formError != null && formError !== '' ? (
            <div className={styles.error} role="alert">
              <StatusGlyphBlock token="danger" shapeName="x-circle" />
              <div className={styles.errorContent}>{formError}</div>
            </div>
          ) : null}

          <div className={styles.formActions}>
            <button type="submit" className={styles.primaryBtn} disabled={updateMutation.isPending}>
              {updateMutation.isPending ? 'Saving…' : 'Save changes'}
            </button>
            <button type="button" className={styles.secondaryBtn} onClick={onClose} disabled={updateMutation.isPending}>
              Cancel
            </button>
          </div>
        </form>
      </div>
    </div>,
    document.body,
  );
}

// ---------------------------------------------------------------------------
// AgentList — wraps the agent table (desktop) + card list (mobile)
//
// Dual-layout strategy (T-2026-06-20-12):
//   >=769px: desktop <table> with sortable columns (unchanged).
//   <768px:  card list — each agent renders as an <article> with all fields and
//            a wrapped action row exposing ALL five actions without horizontal scroll.
//
// Both layouts share the SAME AgentRow component (via `layout` prop). Only the
// outer presentational shell differs; all state/mutation/poll logic is identical.
// ---------------------------------------------------------------------------

interface AgentListProps {
  state: { status: 'loading' } | { status: 'error'; message: string; transport: boolean } | { status: 'loaded' };
  agents: CronAgent[];
  onChanged: () => void;
  sortKey: CronSortKey | null;
  sortOrder: SortOrder | null;
  onColumnSort: (key: CronSortKey) => void;
  ianaZone: string;
  harnessOptions: HarnessOption[];
  capabilitiesLoaded: boolean;
}

function AgentList({ state, agents, onChanged, sortKey, sortOrder, onColumnSort, ianaZone, harnessOptions, capabilitiesLoaded }: AgentListProps) {
  if (state.status === 'loading') {
    return <p className={styles.notice}>Loading cron agents…</p>;
  }
  if (state.status === 'error') {
    return (
      <div className={styles.error} role="alert">
        <StatusGlyphBlock token="danger" shapeName="x-circle" />
        <div className={styles.errorContent}>
          Failed to load cron agents: {state.message}
        </div>
      </div>
    );
  }
  if (agents.length === 0) {
    return (
      <EmptyState
        variant="neutral"
        glyph="clock"
        heading="No cron agents yet. Create one above."
      />
    );
  }

  const sharedRowProps = { onChanged, ianaZone, harnessOptions, capabilitiesLoaded };

  return (
    <>
      {/* Desktop table: shown at >=769px, hidden on mobile via CSS. */}
      <div className={`${styles.agentTableWrapper ?? ''} ${styles.desktopOnly ?? ''}`}>
        <table className={styles.agentTable}>
          <caption className={styles.visuallyHidden}>Cron agents</caption>
          <thead>
            <tr>
              {COLUMNS.map((col) => {
                const isActive = sortKey === col.key;
                const ariaSort: React.AriaAttributes['aria-sort'] = isActive
                  ? sortOrder === 'desc'
                    ? 'descending'
                    : 'ascending'
                  : 'none';
                return (
                  <th
                    key={col.key}
                    scope="col"
                    aria-sort={ariaSort}
                    className={styles.sortableHeader}
                  >
                    <button
                      type="button"
                      className={`${styles.sortBtn ?? ''} ${isActive ? (styles.sortBtnActive ?? '') : ''}`}
                      onClick={() => { onColumnSort(col.key); }}
                      aria-label={`Sort by ${col.label}${isActive ? `, currently ${ariaSort}` : ''}`}
                    >
                      {col.label}
                      <span className={styles.sortIcon} aria-hidden="true">
                        {isActive
                          ? sortOrder === 'desc'
                            ? ' ↓'
                            : ' ↑'
                          : ' ↕'}
                      </span>
                    </button>
                  </th>
                );
              })}
              <th scope="col">Harness</th>
              <th scope="col">Enabled</th>
              <th scope="col">Actions</th>
            </tr>
          </thead>
          <tbody>
            {agents.map((agent) => (
              <AgentRow
                key={agent.id}
                agent={agent}
                layout="table"
                {...sharedRowProps}
              />
            ))}
          </tbody>
        </table>
      </div>

      {/* Mobile card list: shown below 769px, hidden on desktop via CSS. */}
      <div className={`${styles.agentCardList ?? ''} ${styles.mobileOnly ?? ''}`} role="list" aria-label="Cron agents">
        {agents.map((agent) => (
          <AgentRow
            key={agent.id}
            agent={agent}
            layout="card"
            {...sharedRowProps}
          />
        ))}
      </div>
    </>
  );
}

// ---------------------------------------------------------------------------
// AgentRow — one agent rendered in either a desktop table row or a mobile card.
//
// `layout="table"` (default): renders <tr>/<td> structure for the desktop table.
// `layout="card"`:  renders an <article> card for the mobile list view.
//
// ALL interactive state, mutation, and polling logic is shared between both layouts.
// Only the outer presentational shell differs. EditAgentModal always portals to
// document.body so it is layout-agnostic.
// ---------------------------------------------------------------------------

interface AgentRowProps {
  agent: CronAgent;
  layout?: 'table' | 'card';
  onChanged: () => void;
  ianaZone: string;
  harnessOptions: HarnessOption[];
  capabilitiesLoaded: boolean;
}

function AgentRow({ agent, layout = 'table', onChanged, ianaZone, harnessOptions, capabilitiesLoaded }: AgentRowProps) {
  const queryClient = useQueryClient();
  const [localEnabled, setLocalEnabled] = useState(agent.enabled);
  const [toggleError, setToggleError] = useState<string | null>(null);
  const [confirmingRemove, setConfirmingRemove] = useState(false);
  const [removeError, setRemoveError] = useState<string | null>(null);
  const [removeSuccess, setRemoveSuccess] = useState(false);
  const [editing, setEditing] = useState(false);
  const editBtnRef = useRef<HTMLButtonElement | null>(null);

  // Refs for focus management in the remove-confirm group (mirrors the run-confirm refs below).
  const removeBtnRef = useRef<HTMLButtonElement | null>(null);
  const removeConfirmBtnRef = useRef<HTMLButtonElement | null>(null);
  // Timeout id for the delayed invalidation after a successful delete, so the inline
  // "Cron agent deleted" status has time to render before the row disappears from the list.
  const removeSuccessTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const [confirmingRun, setConfirmingRun] = useState(false);
  // runId tracks the in-flight run for the polling query.
  const [runId, setRunId] = useState<string | null>(null);
  const [runResult, setRunResult] = useState<CronRunResult | null>(null);
  const [runError, setRunError] = useState<string | null>(null);
  // inflightNotice: set to true when a 409 is received (already running — benign).
  const [inflightNotice, setInflightNotice] = useState(false);

  // Refs for focus management in the run-confirm group.
  const runNowBtnRef = useRef<HTMLButtonElement | null>(null);
  const runConfirmBtnRef = useRef<HTMLButtonElement | null>(null);

  const [historyOpen, setHistoryOpen] = useState(false);

  // Reconcile the optimistic local state with reloaded server truth.
  // eslint-disable-next-line react-hooks/set-state-in-effect -- intentional optimistic-state reconciliation; not a cascading render bug
  useEffect(() => { setLocalEnabled(agent.enabled); }, [agent.enabled]);

  // When the run-confirm panel opens, move focus to the "Run agent" confirm button.
  // useLayoutEffect (synchronous, post-commit pre-paint) so the focus lands deterministically
  // right after the panel mounts, with no async gap where a concurrent re-render (e.g. a
  // settling query) could interleave between the "Run now" button blurring (it becomes
  // disabled) and the confirm button being focused.
  useLayoutEffect(() => {
    if (confirmingRun) {
      runConfirmBtnRef.current?.focus();
    }
  }, [confirmingRun]);

  // Escape closes the run-confirm panel without running and returns focus to "Run now".
  useEffect(() => {
    if (!confirmingRun) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setConfirmingRun(false);
        runNowBtnRef.current?.focus();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('keydown', onKey); };
  }, [confirmingRun]);

  // When the remove-confirm panel opens, move focus to the "Delete cron agent" button
  // (mirrors the run-confirm focus effect above).
  useLayoutEffect(() => {
    if (confirmingRemove) {
      removeConfirmBtnRef.current?.focus();
    }
  }, [confirmingRemove]);

  // When the remove-confirm panel closes via cancel/Escape (NOT via a successful delete, which
  // shows a success status and unmounts the row shortly after), return focus to "Remove". This
  // is a useLayoutEffect (not a same-tick .focus() call in the cancel/Escape handlers) because
  // the "Remove" button is disabled WHILE confirmingRemove is true; calling .focus() on it in the
  // same synchronous tick as setConfirmingRemove(false) targets a still-disabled element (React
  // has not yet committed the re-render), so the focus call silently no-ops. Deferring to a
  // layout effect keyed on confirmingRemove guarantees it runs after commit, when the button is
  // enabled again.
  const wasConfirmingRemoveRef = useRef(false);
  useLayoutEffect(() => {
    if (!confirmingRemove && wasConfirmingRemoveRef.current && !removeSuccess) {
      removeBtnRef.current?.focus();
    }
    wasConfirmingRemoveRef.current = confirmingRemove;
  }, [confirmingRemove, removeSuccess]);

  // Escape closes the remove-confirm panel without deleting (focus return handled above).
  useEffect(() => {
    if (!confirmingRemove) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setConfirmingRemove(false);
      }
    };
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('keydown', onKey); };
  }, [confirmingRemove]);

  // Clear any pending delayed-invalidation timeout on unmount.
  useEffect(() => {
    return () => {
      if (removeSuccessTimeoutRef.current !== null) clearTimeout(removeSuccessTimeoutRef.current);
    };
  }, []);

  // R2: run history via useQuery (staleTime 15s; only fetches when historyOpen is true).
  const historyQuery = useQuery({
    queryKey: queryKeys.runs(agent.label),
    queryFn: () => fetchCronRuns(agent.label, 25),
    staleTime: STALE_RUNS_MS,
    enabled: historyOpen,
  });

  // R3 / R2: run-status polling via useQuery (staleTime 0; refetchInterval 1s while non-terminal).
  // Stops polling once run reaches a terminal state.
  // Capture runId in a local const so the queryFn can use it without a non-null assertion.
  const activeRunId = runId ?? '';
  const runStatusQuery = useQuery({
    queryKey: queryKeys.runStatus(agent.label, activeRunId),
    queryFn: () => fetchCronRun(agent.label, activeRunId),
    staleTime: STALE_RUN_STATUS_MS,
    enabled: runId !== null,
    refetchInterval: (query) => {
      const data = query.state.data;
      if (data == null) return 1000;
      return isTerminalStatus(data.status) ? false : 1000;
    },
  });

  // When run-status query settles to a terminal state, capture the result.
  useEffect(() => {
    const run = runStatusQuery.data;
    if (run == null || !isTerminalStatus(run.status)) return;
    const result: CronRunResult = {
      run_id: run.id,
      cron_agent_id: run.cron_agent_id,
      dispatch_id: run.dispatch_id,
      session_key: run.session_key,
      status: run.status,
      exit_code: run.exit_code,
      total_cost_usd: run.total_cost_usd,
      launched: run.status !== 'skipped',
      skip_reason: run.skip_reason,
      error_message: run.error_message,
      capability_detail: null,
      cost_provenance: run.cost_provenance,
    };
    // eslint-disable-next-line react-hooks/set-state-in-effect -- polling effect captures terminal run result; cannot restructure without changing polling architecture
    setRunResult(result);
    setRunId(null);
    // Refresh the agents list (last_run_* columns) now that the run is terminal.
    void queryClient.invalidateQueries({ queryKey: invalidationKeys.agents });
    // Invalidate run history so the history panel refreshes after a completed run.
    if (historyOpen) {
      void queryClient.invalidateQueries({ queryKey: queryKeys.runs(agent.label) });
    } else {
      queryClient.removeQueries({ queryKey: queryKeys.runs(agent.label) });
    }
  }, [runStatusQuery.data, historyOpen, agent.label, queryClient]);

  // When run-status query errors (network failure during poll), surface it.
  useEffect(() => {
    if (!runStatusQuery.isError || runId === null) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- polling error handler; cannot be restructured without changing polling architecture
    setRunError(describeError(runStatusQuery.error).message);
    setRunId(null);
  }, [runStatusQuery.isError, runStatusQuery.error, runId]);

  // R3: run-now mutation via useSingleFireMutation (double-submit guard + idempotency key).
  // R7-FE: passes crypto.randomUUID() as the 3rd argument (idempotencyKey).
  const runMutation = useSingleFireMutation({
    mutationFn: ({ label, idempotencyKey }: { label: string; idempotencyKey: string }) =>
      runCronAgent(label, undefined, idempotencyKey),
    onSuccess: (initial) => {
      setRunError(null);
      setInflightNotice(false);
      const isTerminal = isTerminalStatus(initial.status);
      if (isTerminal) {
        // Terminal immediately (skipped, failed, etc.) — no polling needed.
        setRunResult(initial);
        setRunId(null);
        // Refresh the agents list (last_run_* columns) and, if open, the run history.
        void queryClient.invalidateQueries({ queryKey: invalidationKeys.agents });
        if (historyOpen) {
          void queryClient.invalidateQueries({ queryKey: queryKeys.runs(agent.label) });
        }
      } else {
        // Non-terminal (running) — start polling via the run-status query. The agents
        // list and history are invalidated when the run reaches a terminal state (see
        // the run-status terminal effect above), not on the initial 202 dispatch.
        setRunResult(null);
        setRunId(initial.run_id);
      }
    },
    onError: (err: unknown) => {
      // R7-FE: 409 = already running (benign). Show an accessible inline notice, not an error.
      if (err instanceof CronApiError && err.status === 409) {
        setInflightNotice(true);
        setRunError(null);
        return;
      }
      setRunError(describeError(err).message);
      setInflightNotice(false);
    },
  });

  const isRunning = runId !== null;

  const onRunConfirmed = () => {
    setConfirmingRun(false);
    setRunError(null);
    setRunResult(null);
    setInflightNotice(false);
    // R7-FE: generate a per-click idempotency key (UUID v4 via crypto.randomUUID).
    runMutation.safeMutate({ label: agent.label, idempotencyKey: crypto.randomUUID() });
  };

  // R3: enable/disable mutation via useSingleFireMutation.
  const toggleMutation = useSingleFireMutation({
    mutationFn: (vars: { label: string; enable: boolean }) =>
      vars.enable ? enableCronAgent(vars.label) : disableCronAgent(vars.label),
    onSuccess: (updated) => {
      setLocalEnabled(updated.enabled);
      setToggleError(null);
      // Invalidate agents list + single-agent query.
      void queryClient.invalidateQueries({ queryKey: invalidationKeys.agents });
      void queryClient.invalidateQueries({ queryKey: queryKeys.agent(agent.label) });
      onChanged();
    },
    onError: (err: unknown) => {
      setToggleError(describeError(err).message);
    },
  });

  const onToggle = () => {
    setToggleError(null);
    toggleMutation.safeMutate({ label: agent.label, enable: !localEnabled });
  };

  // T-2026-06-15-08: run-history count for the delete review-state copy. Only fetched while
  // the remove-confirm panel is open, so the consequence copy can name a REAL count.
  const removeCountQuery = useQuery({
    queryKey: ['cron', 'runs', agent.label, 'remove-count'] as const,
    queryFn: () => fetchCronRuns(agent.label, DELETE_RUN_COUNT_FETCH_LIMIT),
    staleTime: STALE_RUNS_MS,
    enabled: confirmingRemove,
  });
  const removeCountState = removeRunCountState(removeCountQuery);

  // R3: remove mutation via useSingleFireMutation.
  const removeMutation = useSingleFireMutation({
    mutationFn: (label: string) => removeCronAgent(label),
    onSuccess: () => {
      setRemoveError(null);
      setConfirmingRemove(false);
      // Show the inline success status immediately; delay the actual list invalidation
      // (which removes this row) so the status is visible for a moment.
      setRemoveSuccess(true);
      removeSuccessTimeoutRef.current = setTimeout(() => {
        void queryClient.invalidateQueries({ queryKey: invalidationKeys.agents });
        void queryClient.invalidateQueries({ queryKey: queryKeys.agent(agent.label) });
        onChanged();
      }, REMOVE_SUCCESS_DELAY_MS);
    },
    onError: (err: unknown) => {
      setRemoveError(describeError(err).message);
      setConfirmingRemove(false);
    },
  });

  const onRemove = () => {
    setRemoveError(null);
    setRemoveSuccess(false);
    removeMutation.safeMutate(agent.label);
  };

  const toggleHistory = () => {
    setHistoryOpen((prev) => !prev);
  };

  const hasDetail = confirmingRun || isRunning || runMutation.isPending || runResult !== null || runError !== null || inflightNotice ||
    historyOpen || toggleError !== null || removeError !== null || confirmingRemove || removeMutation.isPending || removeSuccess;

  // Human-readable schedule (T-2026-06-13-35).
  const scheduleHuman = useMemo(() => humanSchedule(agent.schedule, ianaZone), [agent.schedule, ianaZone]);

  // Total column count = COLUMNS.length + 3 (Harness, Enabled, Actions).
  const totalCols = COLUMNS.length + 3;

  // Last-run cost provenance for the inline column.
  const lastRunProvenance = toCostProvenance(agent.last_run_cost_provenance);

  // ---------------------------------------------------------------------------
  // Shared action buttons and detail panel used by BOTH table and card layouts.
  // ---------------------------------------------------------------------------

  const actionButtons = (
    <div className={styles.agentActions}>
      <button
        ref={runNowBtnRef}
        type="button"
        className={styles.primaryBtn}
        onClick={() => { setConfirmingRun(true); }}
        disabled={isRunning || runMutation.isPending || confirmingRun}
        aria-describedby={isRunning ? `running-${layout}-${agent.id}` : undefined}
      >
        {isRunning ? 'Running…' : 'Run now'}
      </button>

      <button
        type="button"
        className={styles.secondaryBtn}
        onClick={onToggle}
        disabled={toggleMutation.isPending}
        aria-label={localEnabled ? `Disable ${agent.label}` : `Enable ${agent.label}`}
      >
        {toggleMutation.isPending ? (localEnabled ? 'Disabling…' : 'Enabling…') : (localEnabled ? 'Disable' : 'Enable')}
      </button>

      <button
        ref={editBtnRef}
        type="button"
        className={styles.secondaryBtn}
        onClick={() => { setEditing(true); }}
        aria-haspopup="dialog"
        aria-label={`Edit ${agent.label}`}
      >
        Edit
      </button>

      <button
        type="button"
        className={styles.secondaryBtn}
        onClick={toggleHistory}
        aria-expanded={historyOpen}
      >
        {historyOpen ? 'Hide run history' : 'Run history'}
      </button>

      <button
        ref={removeBtnRef}
        type="button"
        className={styles.secondaryBtn}
        onClick={() => { setConfirmingRemove(true); }}
        disabled={confirmingRemove || removeMutation.isPending || removeSuccess}
      >
        Remove
      </button>
    </div>
  );

  const detailContent = hasDetail ? (
    <div className={styles.agentDetailCell}>
      {confirmingRun ? (
        <div
          className={styles.runConfirmPanel}
          role="group"
          aria-label={`Confirm running ${agent.label}`}
          data-testid="run-confirm-panel"
        >
          <StatusGlyphBlock token="warning" shapeName="exclamation-triangle" />
          <div className={styles.runConfirmContent}>
            <p className={styles.runConfirmWarning}>
              <strong>This starts a real agent run and incurs cost.</strong>
            </p>
            <dl className={styles.runConfirmDetails}>
              <div className={styles.runConfirmRow}>
                <dt>Agent</dt>
                <dd><code className={styles.code}>{agent.label}</code></dd>
              </div>
              <div className={styles.runConfirmRow}>
                <dt>Harness</dt>
                <dd><code className={styles.code}>{agent.harness}</code></dd>
              </div>
              <div className={styles.runConfirmRow}>
                <dt>Model</dt>
                <dd><code className={styles.code}>{agent.model ?? 'harness default'}</code></dd>
              </div>
              <div className={styles.runConfirmRow}>
                <dt>Token budget</dt>
                <dd><code className={styles.code}>{agent.token_budget != null ? String(agent.token_budget) : 'unbounded/default'}</code></dd>
              </div>
              <div className={styles.runConfirmRow}>
                <dt>Repository</dt>
                <dd><code className={styles.code}>{agent.repo_path}</code></dd>
              </div>
            </dl>
            <div className={styles.runConfirmActions}>
              <button
                ref={runConfirmBtnRef}
                type="button"
                className={styles.primaryBtn}
                onClick={() => { onRunConfirmed(); }}
              >
                Run agent
              </button>
              <button
                type="button"
                className={styles.secondaryBtn}
                onClick={() => {
                  setConfirmingRun(false);
                  runNowBtnRef.current?.focus();
                }}
              >
                Cancel
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {confirmingRemove ? (
        <div
          className={styles.removeConfirmPanel}
          role="group"
          aria-label={`Confirm deleting ${agent.label}`}
          data-testid="remove-confirm-panel"
        >
          <StatusGlyphBlock token="danger" shapeName="exclamation-triangle" />
          <div className={styles.runConfirmContent}>
            <p className={styles.runConfirmWarning}>{removeConsequenceCopy(agent.label, removeCountState)}</p>
            <div className={styles.runConfirmActions}>
              <button
                ref={removeConfirmBtnRef}
                type="button"
                className={styles.dangerBtn}
                onClick={onRemove}
                disabled={removeMutation.isPending}
              >
                {removeMutation.isPending ? 'Deleting…' : 'Delete cron agent'}
              </button>
              <button
                type="button"
                className={styles.secondaryBtn}
                onClick={() => { setConfirmingRemove(false); }}
                disabled={removeMutation.isPending}
              >
                Cancel
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {isRunning ? (
        <span id={`running-${layout}-${agent.id}`} className={styles.pending} role="status">
          <span aria-hidden="true">⏳</span> Run in progress — polling for result…
        </span>
      ) : null}

      {inflightNotice ? (
        <p className={styles.notice} role="status" aria-live="polite">
          A run is already in progress for this agent.
        </p>
      ) : null}

      {toggleError != null && toggleError !== '' ? (
        <div className={styles.error} role="alert">
          <StatusGlyphBlock token="danger" shapeName="x-circle" />
          <div className={styles.errorContent}>{toggleError}</div>
        </div>
      ) : null}

      {removeSuccess ? (
        <div className={styles.success} role="status">
          <StatusGlyphBlock token="success" shapeName="check-circle" />
          <div className={styles.successContent}>Cron agent {agent.label} deleted.</div>
        </div>
      ) : null}

      {removeError != null && removeError !== '' ? (
        <div className={styles.error} role="alert">
          <StatusGlyphBlock token="danger" shapeName="x-circle" />
          <div className={styles.errorContent}>Failed to delete: {removeError}</div>
        </div>
      ) : null}

      {runError != null && runError !== '' ? (
        <div className={styles.error} role="alert">
          <StatusGlyphBlock token="danger" shapeName="x-circle" />
          <div className={styles.errorContent}>Run failed: {runError}</div>
        </div>
      ) : null}

      {runResult != null ? <RunResultPanel result={runResult} /> : null}

      {historyOpen ? (
        <RunHistory
          loading={historyQuery.isLoading}
          error={historyQuery.isError ? describeError(historyQuery.error).message : null}
          runs={historyQuery.data ?? null}
          onReload={() => {
            void queryClient.invalidateQueries({ queryKey: queryKeys.runs(agent.label) });
          }}
        />
      ) : null}
    </div>
  ) : null;

  const editModal = editing ? (
    <EditAgentModal
      agent={agent}
      harnessOptions={harnessOptions}
      capabilitiesLoaded={capabilitiesLoaded}
      onClose={() => {
        setEditing(false);
        // Return focus to the Edit button that opened the modal (accessibility).
        editBtnRef.current?.focus();
      }}
      onSaved={() => {
        void queryClient.invalidateQueries({ queryKey: invalidationKeys.agents });
        void queryClient.invalidateQueries({ queryKey: queryKeys.agent(agent.label) });
        onChanged();
      }}
    />
  ) : null;

  // ---------------------------------------------------------------------------
  // Shared schedule + metadata fragments
  // ---------------------------------------------------------------------------

  const scheduleFragment = (
    <span className={styles.scheduleCell}>
      <span className={styles.scheduleHuman} title={agent.schedule}>
        {scheduleHuman.error ? (
          <code className={styles.code}>{agent.schedule}</code>
        ) : (
          <>
            {scheduleHuman.text}
            {scheduleHuman.tzNote != null && scheduleHuman.tzNote !== '' ? (
              <span className={styles.scheduleTzNote}> {scheduleHuman.tzNote}</span>
            ) : null}
          </>
        )}
      </span>
      <code className={styles.scheduleRaw}>{agent.schedule}</code>
    </span>
  );

  const lastRunStatusFragment = agent.last_run_status != null && agent.last_run_status !== '' ? (
    <StatusBadge status={normalizeRunStatus(agent.last_run_status)} />
  ) : (
    <span className={styles.metaMuted}>{'—'}</span>
  );

  const lastRunAtFragment = agent.last_run_at != null && agent.last_run_at !== '' ? (
    <span className={styles.lastRunAt}>{formatAbsolute(agent.last_run_at, ianaZone)}</span>
  ) : (
    <span className={styles.metaMuted}>{'—'}</span>
  );

  const costFragment = (
    <CostProvenanceChip
      variant="single"
      costUsd={agent.last_run_cost ?? null}
      provenance={lastRunProvenance}
    />
  );

  const enabledBadge = (
    <StatusBadge
      label={localEnabled ? 'Enabled' : 'Disabled'}
      shapeName={localEnabled ? 'check' : 'minus'}
      status={localEnabled ? 'complete' : 'paused'}
      enabled={localEnabled}
    />
  );

  const agentNameLink = (
    <a
      className={styles.agentNameLink}
      href={buildHash({ name: 'cron', id: agent.label, session: null, tab: null })}
      onClick={(e) => {
        e.preventDefault();
        navigate({ name: 'cron', id: agent.label, session: null, tab: null });
      }}
    >
      {agent.label}
    </a>
  );

  // ---------------------------------------------------------------------------
  // TABLE layout (desktop >=769px)
  // ---------------------------------------------------------------------------

  if (layout === 'table') {
    return (
      <>
        {/* Data row */}
        <tr data-agent={agent.label}>
          <td>{agentNameLink}</td>
          <td>{scheduleFragment}</td>
          <td>{lastRunStatusFragment}</td>
          <td>{lastRunAtFragment}</td>
          <td>{costFragment}</td>
          <td>{agent.harness}</td>
          <td>{enabledBadge}</td>
          <td>{actionButtons}</td>
        </tr>

        {/* Expanded detail row — only rendered when there is active content.
            Spans all columns so content is not constrained by column widths. */}
        {hasDetail ? (
          <tr data-agent={agent.label} data-agent-detail="true">
            <td colSpan={totalCols} className={styles.agentDetailCellTable}>
              {detailContent}
            </td>
          </tr>
        ) : null}

        {editModal}
      </>
    );
  }

  // ---------------------------------------------------------------------------
  // CARD layout (mobile <=768px)
  // Each card is a <article role="listitem"> showing all fields and a wrapped
  // action row so no action clips off screen. The detail panel renders below the
  // actions inside the same card.
  // ---------------------------------------------------------------------------

  return (
    <>
      <article
        className={styles.agentCard}
        role="listitem"
        data-agent-card={agent.label}
        aria-label={`Cron agent: ${agent.label}`}
      >
        {/* Card header: agent name link */}
        <div className={styles.cardHeader}>
          {agentNameLink}
        </div>

        {/* Card body: key fields */}
        <dl className={styles.cardFields}>
          <div className={styles.cardField}>
            <dt className={styles.cardFieldLabel}>Schedule</dt>
            <dd className={styles.cardFieldValue}>{scheduleFragment}</dd>
          </div>
          <div className={styles.cardField}>
            <dt className={styles.cardFieldLabel}>Last-run status</dt>
            <dd className={styles.cardFieldValue}>{lastRunStatusFragment}</dd>
          </div>
          <div className={styles.cardField}>
            <dt className={styles.cardFieldLabel}>Last run</dt>
            <dd className={styles.cardFieldValue}>{lastRunAtFragment}</dd>
          </div>
          <div className={styles.cardField}>
            <dt className={styles.cardFieldLabel}>Cost</dt>
            <dd className={styles.cardFieldValue}>{costFragment}</dd>
          </div>
          <div className={styles.cardField}>
            <dt className={styles.cardFieldLabel}>Harness</dt>
            <dd className={styles.cardFieldValue}>
              <code className={styles.code}>{agent.harness}</code>
            </dd>
          </div>
          <div className={styles.cardField}>
            <dt className={styles.cardFieldLabel}>Enabled</dt>
            <dd className={styles.cardFieldValue}>{enabledBadge}</dd>
          </div>
        </dl>

        {/* Action row: wrapped so all 5 actions are visible without horizontal scroll */}
        {actionButtons}

        {/* Detail panel: confirm/run/history/errors */}
        {detailContent}
      </article>

      {editModal}
    </>
  );
}

// ---------------------------------------------------------------------------
// RunResultPanel
// ---------------------------------------------------------------------------

function RunResultPanel({ result }: { result: CronRunResult }) {
  const failed = isFailureStatus(result.status);
  const skipped = isSkippedStatus(result.status);
  const detail = result.error_message ?? result.skip_reason ?? result.capability_detail ?? null;
  const resolvedStatus = normalizeRunStatus(result.status);
  // Read the real provenance from the backend field. toCostProvenance narrows to 'unavailable'
  // for unknown/absent values, so a null cost without a provenance shows "Unavailable" rather
  // than the misleading "Unmetered".
  const provenance = toCostProvenance(result.cost_provenance);

  return (
    <section className={styles.runResult} aria-label="Latest run result" role="status">
      <div className={styles.runStatusLine}>
        <StatusBadge status={resolvedStatus} />
        <span className={styles.runCost}>
          <span className={styles.runCostLabel}>cost:</span>
          <CostProvenanceChip
            variant="single"
            costUsd={result.total_cost_usd}
            provenance={provenance}
          />
        </span>
      </div>

      <dl className={styles.runMeta}>
        <div className={styles.metaPair}>
          <dt className={styles.metaKey}>dispatch id</dt>
          <dd className={styles.metaVal}>
            <code className={styles.code}>{result.dispatch_id ?? 'not recorded'}</code>
          </dd>
        </div>
        <div className={styles.metaPair}>
          <dt className={styles.metaKey}>session key</dt>
          <dd className={styles.metaVal}>
            {result.session_key != null && result.session_key !== '' ? (
              <a className={styles.traceLink} href={buildHash({ name: 'trace', session: result.session_key })}>
                <code className={styles.code}>{result.session_key}</code>
                <span aria-hidden="true"> →</span>{' '}
                <span className={styles.traceLinkText}>open in trace view</span>
              </a>
            ) : (
              <code className={styles.code}>not recorded</code>
            )}
          </dd>
        </div>
        {result.exit_code != null ? (
          <div className={styles.metaPair}>
            <dt className={styles.metaKey}>exit code</dt>
            <dd className={styles.metaVal}>{result.exit_code}</dd>
          </div>
        ) : null}
      </dl>

      {detail != null && detail !== '' ? (
        failed || skipped ? (
          <div className={styles.runDetailWarn}>
            <StatusGlyphBlock token="warning" shapeName="exclamation-triangle" />
            <p className={`${styles.runDetail ?? ''} ${styles.runDetailWarnContent ?? ''}`}>{detail}</p>
          </div>
        ) : (
          <p className={styles.runDetail ?? ''}>{detail}</p>
        )
      ) : null}
    </section>
  );
}

// ---------------------------------------------------------------------------
// RunHistory — uses a real table (EvidenceTable under the hood) + StatusBadge + CostProvenanceChip
// ---------------------------------------------------------------------------

interface RunHistoryProps {
  loading: boolean;
  error: string | null;
  runs: CronRun[] | null;
  onReload: () => void;
}

// ---------------------------------------------------------------------------
// CSV column map for run history — headers match the visible table column headers exactly.
// Raw values are exported for audit precision (status string, raw timestamps, raw cost).
// ---------------------------------------------------------------------------

const RUN_HISTORY_COLUMNS: CsvColumn<CronRun>[] = [
  { header: 'Status', accessor: (r) => normalizeRunStatus(r.status) },
  { header: 'Started', accessor: (r) => r.started_at },
  {
    header: 'Cost (USD)',
    accessor: (r) => r.total_cost_usd ?? null,
  },
  {
    header: 'Exit / Detail',
    accessor: (r) => {
      const parts = [
        r.exit_code != null ? `exit ${String(r.exit_code)}` : null,
        r.error_message ?? r.skip_reason ?? null,
      ].filter(Boolean);
      return parts.length > 0 ? parts.join(' -- ') : null;
    },
  },
  { header: 'Session', accessor: (r) => r.session_key ?? null },
];

function RunHistory({ loading, error, runs, onReload }: RunHistoryProps) {
  return (
    <section className={styles.history} aria-label="Run history">
      <div className={styles.historyHeader}>
        <h4 className={styles.historyHeading}>Run history</h4>
        <button type="button" className={styles.secondaryBtn} onClick={() => { onReload(); }} disabled={loading}>
          {loading ? 'Refreshing…' : 'Refresh'}
        </button>
        {runs != null && runs.length > 0 && (
          <span data-testid="export-run-history">
            <ExportCsvButton<CronRun>
              columns={RUN_HISTORY_COLUMNS}
              rows={runs}
              filename="run-history.csv"
              label="Export run history CSV"
            />
          </span>
        )}
      </div>

      {error != null && error !== '' ? (
        <div className={styles.error} role="alert">
          <StatusGlyphBlock token="danger" shapeName="x-circle" />
          <div className={styles.errorContent}>{error}</div>
        </div>
      ) : loading && runs === null ? (
        <p className={styles.notice}>Loading run history…</p>
      ) : runs != null && runs.length > 0 ? (
        <table className={styles.historyTable}>
          <thead>
            <tr>
              <th scope="col">Status</th>
              <th scope="col">Started</th>
              <th scope="col">Cost</th>
              <th scope="col">Exit / Detail</th>
              <th scope="col">Session</th>
            </tr>
          </thead>
          <tbody>
            {runs.map((run) => {
              const exitDetail = [
                run.exit_code != null ? `exit ${String(run.exit_code)}` : null,
                run.error_message ?? run.skip_reason ?? null,
              ]
                .filter(Boolean)
                .join(' — ');
              return (
                <tr key={run.id}>
                  <td>
                    <StatusBadge status={normalizeRunStatus(run.status)} />
                  </td>
                  <td>
                    <code className={styles.code}>{run.started_at}</code>
                  </td>
                  <td>
                    <CostProvenanceChip
                      variant="single"
                      costUsd={run.total_cost_usd}
                      provenance={toCostProvenance(run.cost_provenance)}
                    />
                  </td>
                  <td>
                    {exitDetail ? (
                      <span className={styles.historyDetail}>{exitDetail}</span>
                    ) : (
                      <span className={styles.metaMuted}>{'—'}</span>
                    )}
                  </td>
                  <td>
                    {run.session_key != null && run.session_key !== '' ? (
                      <a
                        className={styles.traceLink}
                        href={buildHash({ name: 'trace', session: run.session_key })}
                      >
                        <code className={styles.code}>{run.session_key}</code>
                      </a>
                    ) : (
                      <span className={styles.metaMuted}>{'—'}</span>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      ) : (
        <p className={styles.notice}>No runs recorded yet for this agent.</p>
      )}
    </section>
  );
}

// =============================================================================
// CronAgentDetail — the #/cron/:id detail page.
//
// Reuses AgentRow's interactive content (run-now-with-confirm, enable/disable,
// remove, run history) but promotes it to a full page with config section,
// back-navigation, and a not-found EmptyState for bad ids.
// =============================================================================

interface CronAgentDetailProps {
  /** The agent label (decoded from the :id URL segment). */
  agentId: string;
}

/**
 * CronAgentDetail: the #/cron/:id detail page.
 * Fetches the agent by label, shows full config + run history + cost + actions.
 * Non-existent id -> honest EmptyState, never a silent list fallback.
 */
export function CronAgentDetail({ agentId }: CronAgentDetailProps) {
  const queryClient = useQueryClient();
  const ianaZone = useMemo(() => resolveZone(readPrefs().timezone), []);

  // R2: single agent via useQuery (staleTime 30s).
  const agentQuery = useQuery({
    queryKey: queryKeys.agent(agentId),
    queryFn: () => fetchCronAgent(agentId),
    staleTime: STALE_AGENT_MS,
    retry: (failureCount, err) => {
      // Do not retry on 404 (agent not found).
      if (err instanceof CronApiError && err.status === 404) return false;
      return failureCount < 3;
    },
  });

  const onBack = () => { navigate({ name: 'cron', id: null, session: null, tab: null }); };

  // Invalidate the agent on onChanged (e.g. after enable/disable).
  const onChanged = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: queryKeys.agent(agentId) });
  }, [queryClient, agentId]);

  if (agentQuery.isLoading) {
    return (
      <div className={styles.view}>
        <PageHeader title="Cron Agent" />
        <p className={styles.notice}>Loading…</p>
      </div>
    );
  }

  if (agentQuery.isError) {
    if (agentQuery.error instanceof CronApiError && agentQuery.error.status === 404) {
      return (
        <div className={styles.view}>
          <PageHeader title="Cron Agent" />
          <EmptyState
            variant="governance"
            glyph="x-circle"
            heading="Agent not found"
            description={`No cron agent with id "${agentId}" exists. It may have been removed.`}
            action={
              <button type="button" className={styles.secondaryBtn} onClick={onBack}>
                ← Back to agents
              </button>
            }
          />
        </div>
      );
    }
    return (
      <div className={styles.view}>
        <PageHeader title="Cron Agent" />
        <div className={styles.error} role="alert">
          <StatusGlyphBlock token="danger" shapeName="x-circle" />
          <div className={styles.errorContent}>{describeError(agentQuery.error).message}</div>
        </div>
        <button type="button" className={styles.secondaryBtn} onClick={onBack}>
          ← Back to agents
        </button>
      </div>
    );
  }

  // At this point isLoading and isError are both false, so data is defined.
  const agent = agentQuery.data ?? null;
  if (agent === null) return null;
  const scheduleHuman_ = humanSchedule(agent.schedule, ianaZone);

  return (
    <div className={styles.view}>
      <div className={styles.detailNav}>
        <button
          type="button"
          className={styles.backBtn}
          onClick={onBack}
          aria-label="Back to cron agents list"
        >
          ← Cron Agents
        </button>
      </div>

      <PageHeader title={agent.label} />

      {/* Config section */}
      <section className={styles.section} aria-labelledby="detail-config-heading">
        <h2 id="detail-config-heading" className={styles.sectionHeading}>Configuration</h2>
        <dl className={styles.detailConfig}>
          <div className={styles.detailConfigRow}>
            <dt>Label</dt>
            <dd><code className={styles.code}>{agent.label}</code></dd>
          </div>
          <div className={styles.detailConfigRow}>
            <dt>Schedule</dt>
            <dd>
              <span className={styles.scheduleCell}>
                <span className={styles.scheduleHuman}>
                  {scheduleHuman_.error ? (
                    <code className={styles.code}>{agent.schedule}</code>
                  ) : (
                    <>
                      {scheduleHuman_.text}
                      {scheduleHuman_.tzNote != null && scheduleHuman_.tzNote !== '' ? (
                        <span className={styles.scheduleTzNote}> {scheduleHuman_.tzNote}</span>
                      ) : null}
                    </>
                  )}
                </span>
                <code className={styles.scheduleRaw}>{agent.schedule}</code>
              </span>
            </dd>
          </div>
          <div className={styles.detailConfigRow}>
            <dt>Harness</dt>
            <dd><code className={styles.code}>{agent.harness}</code></dd>
          </div>
          <div className={styles.detailConfigRow}>
            <dt>Model</dt>
            <dd><code className={styles.code}>{agent.model ?? 'harness default'}</code></dd>
          </div>
          <div className={styles.detailConfigRow}>
            <dt>Token budget</dt>
            <dd><code className={styles.code}>{agent.token_budget != null ? String(agent.token_budget) : 'unbounded/default'}</code></dd>
          </div>
          <div className={styles.detailConfigRow}>
            <dt>Repository</dt>
            <dd><code className={styles.code}>{agent.repo_path}</code></dd>
          </div>
          <div className={styles.detailConfigRow}>
            <dt>Enabled</dt>
            <dd>
              <StatusBadge
                label={agent.enabled ? 'Enabled' : 'Disabled'}
                shapeName={agent.enabled ? 'check' : 'minus'}
                status={agent.enabled ? 'complete' : 'paused'}
                enabled={agent.enabled}
              />
            </dd>
          </div>
          <div className={styles.detailConfigRow}>
            <dt>Created</dt>
            <dd><code className={styles.code}>{formatAbsolute(agent.created_at, ianaZone)}</code></dd>
          </div>
          <div className={styles.detailConfigRow}>
            <dt>Updated</dt>
            <dd><code className={styles.code}>{formatAbsolute(agent.updated_at, ianaZone)}</code></dd>
          </div>
        </dl>
      </section>

      {/* Actions + run-now-with-confirm: reuse AgentRow's interactive section */}
      <section className={styles.section} aria-labelledby="detail-actions-heading">
        <h2 id="detail-actions-heading" className={styles.sectionHeading}>Actions</h2>
        <AgentDetailActions
          agent={agent}
          ianaZone={ianaZone}
          onChanged={onChanged}
          onRemoved={onBack}
        />
      </section>

      {/* Run history */}
      <section className={styles.section} aria-labelledby="detail-history-heading">
        <h2 id="detail-history-heading" className={styles.sectionHeading}>Run history</h2>
        <AgentDetailHistory agentLabel={agent.label} />
      </section>
    </div>
  );
}

// ---------------------------------------------------------------------------
// AgentDetailActions — actions panel for the detail page (run-now, enable/disable, remove).
// Mirrors AgentRow's action logic but rendered as a standalone block (not inside a table).
// ---------------------------------------------------------------------------

interface AgentDetailActionsProps {
  agent: CronAgent;
  ianaZone: string;
  onChanged: () => void;
  onRemoved: () => void;
}

function AgentDetailActions({ agent, onChanged, onRemoved }: AgentDetailActionsProps) {
  const queryClient = useQueryClient();
  const [localEnabled, setLocalEnabled] = useState(agent.enabled);
  const [toggleError, setToggleError] = useState<string | null>(null);
  const [confirmingRemove, setConfirmingRemove] = useState(false);
  const [removeError, setRemoveError] = useState<string | null>(null);
  const [removeSuccess, setRemoveSuccess] = useState(false);

  const [confirmingRun, setConfirmingRun] = useState(false);
  // runId tracks the in-flight run for the polling query.
  const [runId, setRunId] = useState<string | null>(null);
  const [runResult, setRunResult] = useState<CronRunResult | null>(null);
  const [runError, setRunError] = useState<string | null>(null);
  // inflightNotice: set to true when a 409 is received (already running — benign).
  const [inflightNotice, setInflightNotice] = useState(false);

  const runNowBtnRef = useRef<HTMLButtonElement | null>(null);
  const runConfirmBtnRef = useRef<HTMLButtonElement | null>(null);

  // Refs for focus management in the remove-confirm group (mirrors the run-confirm refs above).
  const removeBtnRef = useRef<HTMLButtonElement | null>(null);
  const removeConfirmBtnRef = useRef<HTMLButtonElement | null>(null);
  // Timeout id for the delayed onRemoved() navigation after a successful delete, so the
  // inline "Cron agent deleted" status has time to render before navigating back.
  const removeSuccessTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Reconcile the optimistic local state with reloaded server truth.
  // eslint-disable-next-line react-hooks/set-state-in-effect -- intentional optimistic-state reconciliation; not a cascading render bug
  useEffect(() => { setLocalEnabled(agent.enabled); }, [agent.enabled]);

  // useLayoutEffect: focus the confirm button synchronously after the panel mounts (no async
  // gap where a concurrent re-render could interleave). Mirrors AgentRow above.
  useLayoutEffect(() => {
    if (confirmingRun) {
      runConfirmBtnRef.current?.focus();
    }
  }, [confirmingRun]);

  useEffect(() => {
    if (!confirmingRun) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setConfirmingRun(false);
        runNowBtnRef.current?.focus();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('keydown', onKey); };
  }, [confirmingRun]);

  // When the remove-confirm panel opens, move focus to the "Delete cron agent" button.
  useLayoutEffect(() => {
    if (confirmingRemove) {
      removeConfirmBtnRef.current?.focus();
    }
  }, [confirmingRemove]);

  // When the remove-confirm panel closes via cancel/Escape (NOT via a successful delete, which
  // shows a success status and navigates back shortly after), return focus to "Remove". See the
  // matching comment in AgentRow above for why this is a layout effect rather than a same-tick
  // .focus() call in the cancel/Escape handlers (the button is disabled pre-commit).
  const wasConfirmingRemoveRef = useRef(false);
  useLayoutEffect(() => {
    if (!confirmingRemove && wasConfirmingRemoveRef.current && !removeSuccess) {
      removeBtnRef.current?.focus();
    }
    wasConfirmingRemoveRef.current = confirmingRemove;
  }, [confirmingRemove, removeSuccess]);

  // Escape closes the remove-confirm panel without deleting (focus return handled above).
  useEffect(() => {
    if (!confirmingRemove) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setConfirmingRemove(false);
      }
    };
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('keydown', onKey); };
  }, [confirmingRemove]);

  // Clear any pending delayed-navigation timeout on unmount.
  useEffect(() => {
    return () => {
      if (removeSuccessTimeoutRef.current !== null) clearTimeout(removeSuccessTimeoutRef.current);
    };
  }, []);

  // R3 / R2: run-status polling via useQuery (staleTime 0; refetchInterval 1s while non-terminal).
  // Capture runId in a local const so the queryFn can use it without a non-null assertion.
  const activeRunId2 = runId ?? '';
  const runStatusQuery = useQuery({
    queryKey: queryKeys.runStatus(agent.label, activeRunId2),
    queryFn: () => fetchCronRun(agent.label, activeRunId2),
    staleTime: STALE_RUN_STATUS_MS,
    enabled: runId !== null,
    refetchInterval: (query) => {
      const data = query.state.data;
      if (data == null) return 1000;
      return isTerminalStatus(data.status) ? false : 1000;
    },
  });

  // When run-status query settles to a terminal state, capture the result.
  useEffect(() => {
    const run = runStatusQuery.data;
    if (run == null || !isTerminalStatus(run.status)) return;
    const result: CronRunResult = {
      run_id: run.id,
      cron_agent_id: run.cron_agent_id,
      dispatch_id: run.dispatch_id,
      session_key: run.session_key,
      status: run.status,
      exit_code: run.exit_code,
      total_cost_usd: run.total_cost_usd,
      launched: run.status !== 'skipped',
      skip_reason: run.skip_reason,
      error_message: run.error_message,
      capability_detail: null,
      cost_provenance: run.cost_provenance,
    };
    // eslint-disable-next-line react-hooks/set-state-in-effect -- polling effect captures terminal run result; cannot restructure without changing polling architecture
    setRunResult(result);
    setRunId(null);
    // Refresh this agent (last_run_* on the detail page), the agents list, and run history.
    void queryClient.invalidateQueries({ queryKey: invalidationKeys.agents });
    void queryClient.invalidateQueries({ queryKey: queryKeys.agent(agent.label) });
    void queryClient.invalidateQueries({ queryKey: queryKeys.runs(agent.label) });
  }, [runStatusQuery.data, agent.label, queryClient]);

  // When run-status query errors, surface it.
  useEffect(() => {
    if (!runStatusQuery.isError || runId === null) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- polling error handler; cannot restructure without changing polling architecture
    setRunError(describeError(runStatusQuery.error).message);
    setRunId(null);
  }, [runStatusQuery.isError, runStatusQuery.error, runId]);

  // R3: run-now mutation via useSingleFireMutation.
  // R7-FE: passes crypto.randomUUID() as the 3rd argument (idempotencyKey).
  const runMutation = useSingleFireMutation({
    mutationFn: ({ label, idempotencyKey }: { label: string; idempotencyKey: string }) =>
      runCronAgent(label, undefined, idempotencyKey),
    onSuccess: (initial) => {
      setRunError(null);
      setInflightNotice(false);
      const isTerminal = isTerminalStatus(initial.status);
      if (isTerminal) {
        setRunResult(initial);
        setRunId(null);
        // Refresh this agent + the agents list + run history now that it is terminal.
        void queryClient.invalidateQueries({ queryKey: invalidationKeys.agents });
        void queryClient.invalidateQueries({ queryKey: queryKeys.agent(agent.label) });
        void queryClient.invalidateQueries({ queryKey: queryKeys.runs(agent.label) });
      } else {
        // Non-terminal (running) — invalidations happen on terminal completion
        // (see the run-status terminal effect above), not on the initial 202 dispatch.
        setRunResult(null);
        setRunId(initial.run_id);
      }
    },
    onError: (err: unknown) => {
      // R7-FE: 409 = already running (benign).
      if (err instanceof CronApiError && err.status === 409) {
        setInflightNotice(true);
        setRunError(null);
        return;
      }
      setRunError(describeError(err).message);
      setInflightNotice(false);
    },
  });

  const isRunning = runId !== null;

  const onRunConfirmed = () => {
    setConfirmingRun(false);
    setRunError(null);
    setRunResult(null);
    setInflightNotice(false);
    // R7-FE: generate a per-click idempotency key (UUID v4 via crypto.randomUUID).
    runMutation.safeMutate({ label: agent.label, idempotencyKey: crypto.randomUUID() });
  };

  // R3: enable/disable mutation via useSingleFireMutation.
  const toggleMutation = useSingleFireMutation({
    mutationFn: (vars: { label: string; enable: boolean }) =>
      vars.enable ? enableCronAgent(vars.label) : disableCronAgent(vars.label),
    onSuccess: (updated) => {
      setLocalEnabled(updated.enabled);
      setToggleError(null);
      void queryClient.invalidateQueries({ queryKey: invalidationKeys.agents });
      void queryClient.invalidateQueries({ queryKey: queryKeys.agent(agent.label) });
      onChanged();
    },
    onError: (err: unknown) => {
      setToggleError(describeError(err).message);
    },
  });

  const onToggle = () => {
    setToggleError(null);
    toggleMutation.safeMutate({ label: agent.label, enable: !localEnabled });
  };

  // T-2026-06-15-08: run-history count for the delete review-state copy. Only fetched while
  // the remove-confirm panel is open, so the consequence copy can name a REAL count.
  const removeCountQuery = useQuery({
    queryKey: ['cron', 'runs', agent.label, 'remove-count'] as const,
    queryFn: () => fetchCronRuns(agent.label, DELETE_RUN_COUNT_FETCH_LIMIT),
    staleTime: STALE_RUNS_MS,
    enabled: confirmingRemove,
  });
  const removeCountState = removeRunCountState(removeCountQuery);

  // R3: remove mutation via useSingleFireMutation.
  const removeMutation = useSingleFireMutation({
    mutationFn: (label: string) => removeCronAgent(label),
    onSuccess: () => {
      setRemoveError(null);
      setConfirmingRemove(false);
      void queryClient.invalidateQueries({ queryKey: invalidationKeys.agents });
      // Show the inline success status immediately; delay navigating back so the status
      // is visible for a moment before the detail page unmounts.
      setRemoveSuccess(true);
      removeSuccessTimeoutRef.current = setTimeout(() => {
        onRemoved();
      }, REMOVE_SUCCESS_DELAY_MS);
    },
    onError: (err: unknown) => {
      setRemoveError(describeError(err).message);
      setConfirmingRemove(false);
    },
  });

  const onRemove = () => {
    setRemoveError(null);
    setRemoveSuccess(false);
    removeMutation.safeMutate(agent.label);
  };

  return (
    <div className={styles.detailActions}>
      <div className={styles.agentActions}>
        <button
          ref={runNowBtnRef}
          type="button"
          className={styles.primaryBtn}
          onClick={() => { setConfirmingRun(true); }}
          disabled={isRunning || runMutation.isPending || confirmingRun}
          aria-describedby={isRunning ? `detail-running-${agent.id}` : undefined}
        >
          {isRunning ? 'Running…' : 'Run now'}
        </button>

        <button
          type="button"
          className={styles.secondaryBtn}
          onClick={onToggle}
          disabled={toggleMutation.isPending}
          aria-label={localEnabled ? `Disable ${agent.label}` : `Enable ${agent.label}`}
        >
          {toggleMutation.isPending ? (localEnabled ? 'Disabling…' : 'Enabling…') : (localEnabled ? 'Disable' : 'Enable')}
        </button>

        <button
          ref={removeBtnRef}
          type="button"
          className={styles.secondaryBtn}
          onClick={() => { setConfirmingRemove(true); }}
          disabled={confirmingRemove || removeMutation.isPending || removeSuccess}
        >
          Remove
        </button>
      </div>

      {confirmingRun ? (
        <div
          className={styles.runConfirmPanel}
          role="group"
          aria-label={`Confirm running ${agent.label}`}
          data-testid="run-confirm-panel"
        >
          <StatusGlyphBlock token="warning" shapeName="exclamation-triangle" />
          <div className={styles.runConfirmContent}>
            <p className={styles.runConfirmWarning}>
              <strong>This starts a real agent run and incurs cost.</strong>
            </p>
            <dl className={styles.runConfirmDetails}>
              <div className={styles.runConfirmRow}>
                <dt>Agent</dt>
                <dd><code className={styles.code}>{agent.label}</code></dd>
              </div>
              <div className={styles.runConfirmRow}>
                <dt>Harness</dt>
                <dd><code className={styles.code}>{agent.harness}</code></dd>
              </div>
              <div className={styles.runConfirmRow}>
                <dt>Model</dt>
                <dd><code className={styles.code}>{agent.model ?? 'harness default'}</code></dd>
              </div>
              <div className={styles.runConfirmRow}>
                <dt>Token budget</dt>
                <dd><code className={styles.code}>{agent.token_budget != null ? String(agent.token_budget) : 'unbounded/default'}</code></dd>
              </div>
              <div className={styles.runConfirmRow}>
                <dt>Repository</dt>
                <dd><code className={styles.code}>{agent.repo_path}</code></dd>
              </div>
            </dl>
            <div className={styles.runConfirmActions}>
              <button
                ref={runConfirmBtnRef}
                type="button"
                className={styles.primaryBtn}
                onClick={() => { onRunConfirmed(); }}
              >
                Run agent
              </button>
              <button
                type="button"
                className={styles.secondaryBtn}
                onClick={() => {
                  setConfirmingRun(false);
                  runNowBtnRef.current?.focus();
                }}
              >
                Cancel
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {confirmingRemove ? (
        <div
          className={styles.removeConfirmPanel}
          role="group"
          aria-label={`Confirm deleting ${agent.label}`}
          data-testid="remove-confirm-panel"
        >
          <StatusGlyphBlock token="danger" shapeName="exclamation-triangle" />
          <div className={styles.runConfirmContent}>
            <p className={styles.runConfirmWarning}>{removeConsequenceCopy(agent.label, removeCountState)}</p>
            <div className={styles.runConfirmActions}>
              <button
                ref={removeConfirmBtnRef}
                type="button"
                className={styles.dangerBtn}
                onClick={onRemove}
                disabled={removeMutation.isPending}
              >
                {removeMutation.isPending ? 'Deleting…' : 'Delete cron agent'}
              </button>
              <button
                type="button"
                className={styles.secondaryBtn}
                onClick={() => { setConfirmingRemove(false); }}
                disabled={removeMutation.isPending}
              >
                Cancel
              </button>
            </div>
          </div>
        </div>
      ) : null}

      {isRunning ? (
        <span id={`detail-running-${agent.id}`} className={styles.pending} role="status">
          <span aria-hidden="true">⏳</span> Run in progress — polling for result…
        </span>
      ) : null}

      {inflightNotice ? (
        <p className={styles.notice} role="status" aria-live="polite">
          A run is already in progress for this agent.
        </p>
      ) : null}

      {toggleError != null && toggleError !== '' ? (
        <div className={styles.error} role="alert">
          <StatusGlyphBlock token="danger" shapeName="x-circle" />
          <div className={styles.errorContent}>{toggleError}</div>
        </div>
      ) : null}

      {removeSuccess ? (
        <div className={styles.success} role="status">
          <StatusGlyphBlock token="success" shapeName="check-circle" />
          <div className={styles.successContent}>Cron agent {agent.label} deleted.</div>
        </div>
      ) : null}

      {removeError != null && removeError !== '' ? (
        <div className={styles.error} role="alert">
          <StatusGlyphBlock token="danger" shapeName="x-circle" />
          <div className={styles.errorContent}>Failed to delete: {removeError}</div>
        </div>
      ) : null}

      {runError != null && runError !== '' ? (
        <div className={styles.error} role="alert">
          <StatusGlyphBlock token="danger" shapeName="x-circle" />
          <div className={styles.errorContent}>Run failed: {runError}</div>
        </div>
      ) : null}

      {runResult ? <RunResultPanel result={runResult} /> : null}
    </div>
  );
}

// ---------------------------------------------------------------------------
// AgentDetailHistory — run history panel for the detail page.
// ---------------------------------------------------------------------------

function AgentDetailHistory({ agentLabel }: { agentLabel: string }) {
  const queryClient = useQueryClient();

  // R2: run history via useQuery (staleTime 15s).
  const historyQuery = useQuery({
    queryKey: queryKeys.runs(agentLabel),
    queryFn: () => fetchCronRuns(agentLabel, 25),
    staleTime: STALE_RUNS_MS,
  });

  return (
    <RunHistory
      loading={historyQuery.isLoading}
      error={historyQuery.isError ? describeError(historyQuery.error).message : null}
      runs={historyQuery.data ?? null}
      onReload={() => {
        void queryClient.invalidateQueries({ queryKey: queryKeys.runs(agentLabel) });
      }}
    />
  );
}
