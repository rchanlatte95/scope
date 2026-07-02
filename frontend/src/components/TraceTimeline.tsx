import { useMemo } from 'react';
import type { Money, TraceEntry } from '../api/traceApi';
import { formatAbsolute } from '../lib/time';
import { traceKindLabel, traceKindTooltip, userSubmitLabel, userSubmitTooltip, isInitiatingSubmit } from '../lib/traceKind';
import { toCostProvenance } from '../lib/provenance';
import {
  collapseToToolType,
  TOOL_TYPE_LABEL,
  TOOL_TYPE_TOKEN,
  type TraceToolType,
} from '../lib/traceToolType';
import type { PaginationState, PaginationActions } from '../hooks/usePagination';
import type { UseAutoRefreshReturn } from '../hooks/useAutoRefresh';
import { RefreshControl } from './RefreshControl';
import CopyChip from './CopyChip';
import { EvidenceTable, type EvidenceColumn } from './EvidenceTable';
import { ToolTypeMark } from './ToolTypeMark';
import { StatusBadge } from './StatusBadge';
import { CostProvenanceChip } from './CostProvenanceChip';
import { Pagination } from './Pagination';
import Snippet from './Snippet';
import Tooltip from './Tooltip';
import styles from './TraceTimeline.module.css';

// =============================================================================
// TraceTimeline (social-trace-export ST-02, T-2026-06-16-20)
//
// Extracted from TraceView.tsx: the trace timeline RENDERER — event/span rows,
// tool-type marks, cost/provenance chips, and the inline per-row detail panel.
// This is a pure, self-contained, props-driven renderer:
//   - Takes the already-fetched entries + display state via props.
//   - Does NOT fetch, poll, or own any data. All state (pagination, highlight,
//     expanded rows, auto-refresh) is owned by the caller (TraceView today;
//     a future standalone .scopetrace viewer is a second, non-fetching caller).
//   - No new features, no behavior change from the pre-extraction TraceView
//     timeline section; this is a REFACTOR ONLY (ST-02).
//
// TraceView keeps: fetching, loading skeleton, error banners for the overall
// trace load, session/graph state, and URL/state wiring. TraceTimeline owns
// only the "Event timeline" block: header + refresh control, pagination,
// bulk open/collapse controls, the EvidenceTable of entries, and the empty
// state for zero entries.
// =============================================================================

/** Number of columns in the timeline table (used for the detail row's colSpan). */
const TIMELINE_COLUMN_COUNT = 7;

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

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface TraceTimelineProps {
  /** Loaded trace entries for the current page. Fetched by the caller. */
  entries: TraceEntry[];
  /** Resolved display timezone (IANA zone name, or 'UTC'). */
  zone: string;
  /** Total entry count across all pages (0 = unknown, e.g. an older server). */
  eventsTotalCount: number;
  /** Trace fetch error message, if any. Rendered inline; the caller owns the fetch. */
  traceError: string | null;
  /** The currently highlighted entry id (shared selection with the session graph). */
  highlightId: string | null;
  /** Ids of entries whose inline detail row is currently expanded. */
  expandedRowIds: ReadonlySet<string>;
  /** Auto-refresh state/actions, forwarded to the RefreshControl in the header. */
  autoRefresh: UseAutoRefreshReturn;
  /** Pagination state for the Event timeline pagination control. */
  pageState: PaginationState;
  /** Pagination actions for the Event timeline pagination control. */
  pageActions: PaginationActions;
  /** Row click handler: selects the row (syncs the graph highlight). */
  onRowClick: (entry: TraceEntry) => void;
  /** Toggle a single row's inline detail open/closed. */
  onToggleDetail: (entry: TraceEntry) => void;
  /** Expand every row's inline detail. */
  onOpenAllDetails: () => void;
  /** Collapse every row's inline detail. */
  onCollapseAllDetails: () => void;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * TraceTimeline renders the "Event timeline" block: header (label, count,
 * refresh control), pagination, bulk open/collapse-all controls, the entries
 * table (tool-type marks, status, cost/provenance chips, inline expandable
 * detail per row), and the empty-entries notice.
 *
 * Pure/controlled: takes entries and display state via props; never fetches
 * or owns data. Intended to be reused by both the /trace page (TraceView)
 * and a future standalone .scopetrace viewer (ST-05).
 */
export function TraceTimeline({
  entries,
  zone,
  eventsTotalCount,
  traceError,
  highlightId,
  expandedRowIds,
  autoRefresh,
  pageState,
  pageActions,
  onRowClick,
  onToggleDetail,
  onOpenAllDetails,
  onCollapseAllDetails,
}: TraceTimelineProps) {
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
            {formatAbsolute(ts, zone)}
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
              onToggleDetail(row);
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
  ], [zone, expandedRowIds, onToggleDetail]);

  return (
    <div className={styles.timelineSection}>
      <div className={styles.timelineHeader}>
        <span className={styles.timelineLabel}>
          Event timeline · {eventsTotalCount > 0 ? `${String(eventsTotalCount)} entries total` : `${String(entries.length)} entries`}
        </span>
        <RefreshControl autoRefresh={autoRefresh} className={styles.timelineRefreshControl ?? ''} />
      </div>
      {traceError != null && traceError !== '' && <p className={styles.error}>Failed to load trace: {traceError}</p>}

      {/* Pagination: shown when server reports total_count (newer server). */}
      {pageState.pageCount > 0 && (
        <Pagination
          page={pageState.page}
          pageCount={pageState.pageCount}
          pageSize={pageState.pageSize}
          totalCount={eventsTotalCount}
          onGoToPage={pageActions.goToPage}
          onPrevPage={pageActions.prevPage}
          onNextPage={pageActions.nextPage}
          onSetPageSize={pageActions.setPageSize}
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
              onClick={onOpenAllDetails}
            >
              <span className={styles.timelineBulkSymbol} aria-hidden="true">v</span>
              <span>Open all</span>
            </button>
            <button
              type="button"
              className={styles.timelineBulkButton}
              onClick={onCollapseAllDetails}
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
          onRowClick={onRowClick}
          useRowButtonRole={false}
          getRowClassName={(row, idx) => [
            styles.timelineEventRow,
            idx % 2 === 1 ? styles.timelineEventRowAlt : '',
            expandedRowIds.has(row.id) ? styles.timelineEventRowExpanded : '',
            row.id === highlightId ? styles.timelineRowSelected : '',
            isUserSubmitEntry(row) && isInitiatingSubmit(row.user_submit_subtype) ? styles.timelineInitiatingRow : '',
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
                  <TraceEntryInlineDetail entry={row} zone={zone} />
                </td>
              </tr>
            );
          }}
        />
      ) : (
        (traceError == null || traceError === '') && (
          <p className={styles.notice}>No entries recorded in this session.</p>
        )
      )}
    </div>
  );
}

export default TraceTimeline;
