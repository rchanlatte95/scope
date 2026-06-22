// =============================================================================
// TraceSummaryPanel (T-D / T-14)
//
// Consolidated selected-session surface rendered between the SessionPicker and
// detail tabs. Shows session identity once, session facts, and the controls that
// operate on that selected session.
//
// Rendered ONLY when a session is selected (controlled by parent TraceView).
//
// Design contract:
//  - Semantic --scope-* tokens only. No raw hex.
//  - Sharp/geometric: radius 0 default, --scope-radius-sm (2px) for small controls.
//  - 4/8px spacing grid.
//  - Lines over shadows.
//  - Status = text + shape + color (CostProvenanceChip, StatusBadge).
//  - WCAG 2.2 AA compliant.
// =============================================================================

import React from 'react';
import type { TraceSubject, TraceSession } from '../api/traceApi';
import { splitEffectiveKey } from '../lib/sessionGeneration';
import { formatAbsolute } from '../lib/time';
import CopyChip from './CopyChip';
import styles from './TraceSummaryPanel.module.css';

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface TraceSummaryPanelProps {
  /** The trace subject (carries display_id for the session). */
  subject: TraceSubject | null;
  /** The selected TraceSession from the sessions list (metadata). */
  session: TraceSession | null;
  /** The currently selected session key. */
  selectedKey: string | null;
  /** Total event count (from pagination or entries.length). */
  eventCount: number;
  /** Resolver input value. */
  refQuery: string;
  /** Resolver input change handler. */
  onRefQueryChange: (value: string) => void;
  /** Resolver form submit handler. */
  onResolve: (event: React.SyntheticEvent<HTMLFormElement>) => void;
  /** Visible timezone toggle label. */
  zoneLabel: string;
  /** Whether UTC is currently selected. */
  showUtc: boolean;
  /** Toggle timezone display. */
  onToggleZone: () => void;
  /** Tooltip for the timezone toggle. */
  zoneToggleTitle: string;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * TraceSummaryPanel renders the consolidated selected-session surface.
 *
 * It is a read-only information surface: no new API calls, data from props only.
 * Rendered between the SessionPicker and the detail tab bar.
 */
export default function TraceSummaryPanel({
  subject,
  session,
  selectedKey,
  eventCount,
  refQuery,
  onRefQueryChange,
  onResolve,
  zoneLabel,
  showUtc,
  onToggleZone,
  zoneToggleTitle,
}: TraceSummaryPanelProps): React.ReactElement | null {
  if (selectedKey == null) return null;

  // Generation badge: shown when the selected session is a non-zero generation.
  const { generation } = splitEffectiveKey(selectedKey);
  const showGenBadge = generation > 0;

  // Date range: from session metadata if available.
  const firstSeen = session?.first_seen_at ?? null;
  const lastSeen = session?.last_seen_at ?? null;

  // Request count and event count from session metadata.
  const requestCount = session?.request_count ?? null;
  const sessionEventCount = session?.event_count ?? eventCount;

  // Host / agent type label.
  const hostLabel = session?.host ?? session?.agent_type ?? null;

  return (
    <section
      className={styles.panel}
      role="region"
      aria-label="Selected session"
      data-testid="trace-summary-panel"
    >
      <div className={styles.sessionMain}>
        <div className={styles.identityRow}>
          {subject != null && (
            <span className={styles.identityGroup}>
              <span className={styles.fieldLabel}>session</span>
              <CopyChip value={subject.display_id} kind="id" label="session id" />
            </span>
          )}

          {showGenBadge && (
            <span
              className={styles.genBadge}
              aria-label={`Generation ${String(generation)} of this base launch`}
              data-testid="summary-generation-badge"
            >
              <span className={styles.genBadgeLabel} aria-hidden="true">gen</span>
              <span>{String(generation)}</span>
            </span>
          )}

          {hostLabel != null && hostLabel !== '' && (
            <span className={styles.metaChip} aria-label={`Host: ${hostLabel}`}>
              {hostLabel}
            </span>
          )}

          {requestCount != null && (
            <span className={styles.statItem}>
              <span className={styles.statValue}>{String(requestCount)}</span>
              <span className={styles.statLabel}>req</span>
            </span>
          )}
          {sessionEventCount > 0 && (
            <span className={styles.statItem}>
              <span className={styles.statValue}>{String(sessionEventCount)}</span>
              <span className={styles.statLabel}>{sessionEventCount === 1 ? 'event' : 'events'}</span>
            </span>
          )}
        </div>

        {firstSeen != null && (
          <span className={styles.dateRange}>
            <span className={styles.statLabel}>from</span>
            <span className={styles.dateValue}>{formatAbsolute(firstSeen, 'UTC')}</span>
            {lastSeen != null && lastSeen !== firstSeen && (
              <>
                <span className={styles.statLabel} aria-hidden="true">to</span>
                <span className={styles.dateValue}>{formatAbsolute(lastSeen, 'UTC')}</span>
              </>
            )}
          </span>
        )}
      </div>

      <div className={styles.sessionTools} aria-label="Trace controls">
        <form className={styles.resolver} onSubmit={onResolve}>
          <label className={styles.fieldLabel} htmlFor="trace-node-ref">
            go to node
          </label>
          <input
            id="trace-node-ref"
            className={styles.refInput}
            placeholder="e.g. 3.7 or 1F9XCBI"
            value={refQuery}
            onChange={(e) => { onRefQueryChange(e.target.value); }}
          />
          <button type="submit" className={styles.goBtn}>
            go
          </button>
        </form>

        <button
          type="button"
          className={styles.zoneToggle}
          onClick={onToggleZone}
          aria-pressed={showUtc}
          title={zoneToggleTitle}
        >
          {zoneLabel}
        </button>
      </div>
    </section>
  );
}
