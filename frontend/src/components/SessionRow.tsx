import type { ReactNode } from 'react';
import styles from './SessionPicker.module.css';

// =============================================================================
// SessionRow (T-2026-06-16-02)
//
// A single normalized session row shared by BOTH the Agent and Cron tabs of
// SessionPicker. Owns all of:
//  - The selectable button (displayId as mono primary handle, "no session" when
//    null; an optional subtitle rendered on the same id line).
//  - The meta line (arbitrary ReactNode built by each tab).
//  - An indicator slot (Agent: "Running" badge; Cron: status glyph+label).
//  - Selection (inverse-fill block; no left accent stripe).
//  - The deleted badge.
//  - The delete / restore action button (44px, keyboard-reachable, a11y).
//
// The delete/restore/optimistic-remove/toast-undo logic lives ONCE in the parent
// (SessionPicker) and is passed down as callbacks. Adding a field to this
// component propagates it to BOTH tabs automatically.
//
// Design contract: semantic scope-* tokens only, no raw hex, status = text+shape+color,
// 44px touch targets, visible focus both themes, WCAG 2.2 AA.
// =============================================================================

/** Normalized view-model consumed by SessionRow. Both Agent and Cron tabs map their data into this shape. */
export interface SessionRowViewModel {
  /** Unique key for React list rendering (run_id for cron; session id for agent sessions). */
  rowKey: string;
  /** The session_key or agent session id used for selection and delete/restore actions. Null = no obs session. */
  sessionKey: string | null;
  /** Short BASE36 session handle shown as the primary mono identifier. Null -> renders "no session". */
  displayId: string | null;
  /** Optional secondary label shown after displayId on the id line (e.g. cron job name). */
  subtitle?: string;
  /** Meta line content (host + request count + time for agent sessions; status + time + cost for Cron). */
  meta: ReactNode;
  /**
   * Optional indicator shown below the meta line (e.g. "[>] Running" for active agent sessions;
   * the status glyph+label for Cron). When absent, no indicator is rendered.
   */
  indicator?: ReactNode;
  /** ISO-8601 UTC soft-delete timestamp, or null when not deleted. */
  deletedAt: string | null;
}

export interface SessionRowProps {
  vm: SessionRowViewModel;
  /** Whether this row is currently selected. */
  isSelected: boolean;
  /** Called when the user clicks the row button to select this session. No-op when sessionKey is null. */
  onSelect: (sessionKey: string) => void;
  /** Called when the user clicks the delete button. Receives the sessionKey and displayId. */
  onDelete: (sessionKey: string, displayId: string) => void;
  /** Called when the user clicks the Restore button (trash view). Receives the sessionKey. */
  onRestore: (sessionKey: string) => void;
  /** When true, this row is hidden optimistically (delete pending). Used by parent to filter list. */
  optimisticallyHidden?: boolean;
}

/**
 * Shared session row for Agent and Cron tabs. Renders a selectable button + optional delete/restore
 * action. The parent controls visibility (optimistic remove / trash filter) by filtering the list
 * before rendering; optimisticallyHidden is only used by the parent, not this component directly.
 */
export function SessionRow({ vm, isSelected, onSelect, onDelete, onRestore }: SessionRowProps) {
  const { sessionKey, displayId, subtitle, meta, indicator, deletedAt } = vm;
  const isDeleted = deletedAt != null;

  const rowClass = [
    styles.sessionRow,
    isSelected ? styles.sessionRowSelected : '',
    isDeleted ? styles.sessionRowDeleted : '',
  ].filter(Boolean).join(' ');

  const ariaLabel = displayId != null
    ? `Select session ${displayId}${subtitle != null ? ` from ${subtitle}` : ''}`
    : `Session has no session key`;

  return (
    <li
      role="option"
      aria-selected={isSelected}
      className={rowClass}
    >
      <button
        type="button"
        className={styles.sessionRowBtn}
        onClick={() => {
          if (sessionKey != null && !isDeleted) onSelect(sessionKey);
        }}
        aria-label={ariaLabel}
        disabled={sessionKey == null || isDeleted}
      >
        {/* Id line: displayId (mono primary) + optional subtitle */}
        {subtitle != null ? (
          <span className={styles.sessionRowIdLine}>
            <span className={styles.sessionRowId}>
              {displayId ?? 'no session'}
            </span>
            <span className={styles.sessionRowJob} title="Session source">
              {subtitle}
            </span>
          </span>
        ) : (
          <span className={styles.sessionRowId}>
            {displayId ?? 'no session'}
          </span>
        )}

        {/* Meta line */}
        <span className={styles.sessionRowMeta}>
          {meta}
        </span>

        {/* Indicator slot: only rendered when present */}
        {indicator != null && indicator}

        {/* Deleted badge */}
        {isDeleted && (
          <span className={styles.sessionRowDeletedBadge} aria-label="Deleted">
            <span aria-hidden="true">[x]</span>
            <span>Deleted</span>
          </span>
        )}
      </button>

      {/* Delete / Restore action - 44px touch target */}
      {isDeleted ? (
        <button
          type="button"
          className={styles.restoreBtn}
          aria-label={`Restore session ${displayId ?? 'this session'}`}
          onClick={() => {
            if (sessionKey != null) onRestore(sessionKey);
          }}
          disabled={sessionKey == null}
        >
          Restore
        </button>
      ) : (
        sessionKey != null && (
          <button
            type="button"
            className={styles.deleteBtn}
            aria-label={`Delete session ${displayId ?? 'this session'}`}
            onClick={(e) => {
              e.stopPropagation();
              onDelete(sessionKey, displayId ?? sessionKey);
            }}
            title="Delete session (reversible)"
          >
            <span aria-hidden="true">[x]</span>
          </button>
        )
      )}
    </li>
  );
}
