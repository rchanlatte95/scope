// =============================================================================
// sessionGeneration.ts (T-2026-06-19-03 Wave 2 / ST-07)
//
// Pure helper functions for session generation key parsing and base-launch
// grouping. Extracted from SessionPicker.tsx to satisfy the fast-refresh
// lint rule (react-refresh/only-export-components): a file mixing component
// exports with utilities must be split.
//
// Key format (centralized here to match ObservabilityWriter.GenerationDelimiter):
//   - Generation 0: cli-{uuid7}           (suffixless; IS the base key)
//   - Generation n: cli-{uuid7}#g{n}      (n >= 1)
//
// The #g delimiter is a RESERVED token: no minter other than the session
// generation binding path should emit it.
// =============================================================================

import type { BudgetBaseLaunchRow, BudgetConversationRow, TraceSession } from '../api/traceApi';

/** The reserved generation delimiter. Must match ObservabilityWriter.GenerationDelimiter. */
export const GENERATION_DELIMITER = '#g';

/**
 * Splits an effective session key into its base key and generation number.
 *
 * Examples:
 *   "cli-abc123"    -> { base: "cli-abc123", generation: 0 }
 *   "cli-abc123#g2" -> { base: "cli-abc123", generation: 2 }
 *
 * Any key without the delimiter returns generation 0 (no-op for cron/MCP/host keys).
 */
export function splitEffectiveKey(key: string): { base: string; generation: number } {
  const idx = key.indexOf(GENERATION_DELIMITER);
  if (idx === -1) return { base: key, generation: 0 };
  const genStr = key.slice(idx + GENERATION_DELIMITER.length);
  const gen = parseInt(genStr, 10);
  return { base: key.slice(0, idx), generation: Number.isFinite(gen) ? gen : 0 };
}

/**
 * Returns true when the session key represents a non-zero generation (has #g{n} suffix).
 * Exposed for the trace header badge and grouping logic.
 */
export function isNonZeroGeneration(key: string): boolean {
  const idx = key.indexOf(GENERATION_DELIMITER);
  if (idx === -1) return false;
  const genStr = key.slice(idx + GENERATION_DELIMITER.length);
  const gen = parseInt(genStr, 10);
  return Number.isFinite(gen) && gen > 0;
}

// ---------------------------------------------------------------------------
// Base-launch grouping
// ---------------------------------------------------------------------------

/** A resolved group: a base launch row paired with its matching child sessions. */
export interface BaseLaunchGroup {
  /** The base_session_key from /api/command/base-launches. */
  base: string;
  /** The total spend and generation count from the rollup. */
  rollup: BudgetBaseLaunchRow;
  /** Sessions belonging to this base (sorted ascending by generation: g0, g1, g2...). */
  sessions: TraceSession[];
}

/**
 * Groups CLI sessions by their base launch, using the base-launches rollup for metadata.
 *
 * Sessions whose base is NOT in the rollup (no spend data in window) are returned as
 * ungrouped so they still render flat. The base key for a generation-0 session (no #g
 * suffix) IS its own key; for generation-n sessions, the base is extracted via
 * splitEffectiveKey.
 *
 * Returns:
 *  - groups: sessions whose base appears in the rollup (with rollup metadata).
 *    Within each group, sessions are sorted ascending by generation.
 *    Groups are ordered by the rollup row order (typically descending by spend).
 *  - ungrouped: sessions whose base is not in the rollup (rendered flat).
 *
 * When rollupRows is empty, returns { groups: [], ungrouped: sessions } (no-op).
 */
export function buildBaseLaunchGroups(
  sessions: TraceSession[],
  rollupRows: BudgetBaseLaunchRow[],
): { groups: BaseLaunchGroup[]; ungrouped: TraceSession[] } {
  if (rollupRows.length === 0) {
    return { groups: [], ungrouped: sessions };
  }

  // Build a quick lookup from base_session_key -> rollup row.
  const rollupMap = new Map<string, BudgetBaseLaunchRow>();
  for (const row of rollupRows) {
    rollupMap.set(row.base_session_key, row);
  }

  // Partition sessions: has-rollup base vs no-rollup base.
  const byBase = new Map<string, TraceSession[]>();
  const ungrouped: TraceSession[] = [];

  for (const s of sessions) {
    const { base } = splitEffectiveKey(s.id);
    if (rollupMap.has(base)) {
      const bucket = byBase.get(base) ?? [];
      bucket.push(s);
      byBase.set(base, bucket);
    } else {
      ungrouped.push(s);
    }
  }

  // Build groups in rollup row order, then sort each group's sessions by generation ascending.
  const groups: BaseLaunchGroup[] = [];
  for (const row of rollupRows) {
    const bucket = byBase.get(row.base_session_key);
    if (bucket == null || bucket.length === 0) continue;
    const sorted = [...bucket].sort((a, b) => {
      const ga = splitEffectiveKey(a.id).generation;
      const gb = splitEffectiveKey(b.id).generation;
      return ga - gb;
    });
    groups.push({ base: row.base_session_key, rollup: row, sessions: sorted });
  }

  return { groups, ungrouped };
}

// ---------------------------------------------------------------------------
// Conversation grouping (T-2026-06-19-05)
// ---------------------------------------------------------------------------

/**
 * Label for a conversation group: "Cron run" when all member sessions are cron
 * agents, "Conversation" otherwise.
 *
 * Cron detection: session.agent_type === 'cron' OR session.host === 'cron'.
 */
export type ConversationLabel = 'Cron run' | 'Conversation';

/**
 * A resolved conversation group: one or more base launches that share the same
 * cc_session_id, paired with the matching BudgetConversationRow for spend/counts.
 */
export interface ConversationGroup {
  /**
   * The shared cc_session_id (or a singleton base key for idless sessions).
   * This is the stable group key.
   */
  conversation_id: string;
  /**
   * The matched BudgetConversationRow for this conversation, or null when
   * no matching row exists in by_conversation (the group was reconstructed
   * from sessions alone in the fallback path).
   */
  conversationRow: BudgetConversationRow | null;
  /** All sessions belonging to this conversation. */
  sessions: TraceSession[];
  /** "Cron run" when all member sessions are cron agents; otherwise "Conversation". */
  label: ConversationLabel;
  /**
   * A short display handle for the conversation header.
   * Prefers the first session's display_id (the g0 / earliest row).
   */
  displayId: string;
}

/**
 * Returns true when a session belongs to a cron agent.
 * Uses agent_type and host, whichever is available.
 */
function isCronSession(s: TraceSession): boolean {
  return (s.agent_type ?? '').toLowerCase() === 'cron'
    || (s.host ?? '').toLowerCase() === 'cron';
}

/**
 * Derives a stable short display handle for a conversation group.
 * Picks the first session's display_id, or falls back to the last 8 chars
 * of the conversation_id when no session display_id is available.
 */
function conversationDisplayId(sessions: TraceSession[], conversationId: string): string {
  if (sessions.length > 0 && sessions[0] != null && sessions[0].display_id.length > 0) {
    return sessions[0].display_id;
  }
  return conversationId.slice(-8);
}

/**
 * Groups CLI sessions by a machine-aware composite key derived from conversation_id
 * (resume-stable transcript filename stem), falling back to cc_session_id then s.id.
 *
 * Behaviour:
 * - conversation_id (if non-empty) is the primary grouping identity. It is stable
 *   across /resume (which assigns a new cc_session_id). Two sessions with the same
 *   conversation_id AND the same machine_id are ONE group (resume continuity).
 * - machine_id is included in the bucket key so that two different machines that
 *   happen to share the same conversation_id are kept in separate groups.
 * - Null/empty machine_id is treated as an empty sentinel: legacy/null-machine
 *   sessions with the same conversation_id still merge into ONE group.
 * - The emitted ConversationGroup.conversation_id field is the conversation component
 *   ALONE (not the composite), so convRowMap.get(conversation_id) still matches
 *   BudgetConversationRow.conversation_id from the backend by_conversation rollup.
 * - Sessions with a null/empty conversation_id fall back to cc_session_id (backward
 *   compat), then to s.id (idless singleton).
 * - Label is "Cron run" when ALL members are cron sessions; otherwise "Conversation".
 * - Groups are ordered by the first session's position in the input array (preserves
 *   server sort order for each conversation's lead session).
 *
 * When byConversationRows is empty (older server without by_conversation), the function
 * still groups correctly (spend/counts will be null). Callers may check whether any
 * conversationRow is present to decide whether to display cost.
 */
export function buildConversationGroups(
  sessions: TraceSession[],
  byConversationRows: BudgetConversationRow[],
): ConversationGroup[] {
  // Build a quick lookup from conversation_id -> BudgetConversationRow.
  // Keyed by the conversation component alone (matches backend by_conversation row key).
  const convRowMap = new Map<string, BudgetConversationRow>();
  for (const row of byConversationRows) {
    convRowMap.set(row.conversation_id, row);
  }

  // Group sessions by a machine-aware composite bucket key.
  // convComponent = conversation_id ?? cc_session_id ?? s.id (backend fallback order).
  // machineComponent = machine_id when non-empty, else '' (null/empty -> no-fragment sentinel).
  // bucketKey = machineComponent + ' ' + convComponent (space cannot appear in uuid/hex ids).
  // The emitted conversation_id is convComponent ALONE (for convRowMap lookup).
  const orderedBucketKeys: string[] = [];           // insertion-order bucket keys
  const buckets = new Map<string, TraceSession[]>();
  const bucketToConvComponent = new Map<string, string>(); // bucketKey -> convComponent

  for (const s of sessions) {
    const convId = s.conversation_id ?? null;
    const ccId = s.cc_session_id ?? null;

    // Mirror backend fallback: conversation_id (if non-empty) else cc_session_id (if non-empty) else s.id.
    const convComponent: string =
      (convId !== null && convId !== '')
        ? convId
        : (ccId !== null && ccId !== '')
          ? ccId
          : s.id;

    // Null/empty machine_id -> empty sentinel so legacy rows don't fragment.
    const machineComponent = s.machine_id ?? '';

    // Idless singleton guard: when convComponent is s.id (fallback), every session gets
    // a unique bucket regardless of machine_id (s.id is already unique per session).
    // For non-singleton paths the composite key provides cross-machine isolation.
    const bucketKey = `${machineComponent} ${convComponent}`;

    let bucket = buckets.get(bucketKey);
    if (bucket === undefined) {
      bucket = [];
      orderedBucketKeys.push(bucketKey);
      buckets.set(bucketKey, bucket);
      bucketToConvComponent.set(bucketKey, convComponent);
    }
    bucket.push(s);
  }

  // Build result in insertion order.
  const result: ConversationGroup[] = [];
  for (const bucketKey of orderedBucketKeys) {
    const members = buckets.get(bucketKey) ?? [];
    const convComponent = bucketToConvComponent.get(bucketKey) ?? bucketKey;
    const allCron = members.length > 0 && members.every(isCronSession);
    const label: ConversationLabel = allCron ? 'Cron run' : 'Conversation';
    // convRowMap is keyed by convComponent alone (matches BudgetConversationRow.conversation_id).
    const convRow = convRowMap.get(convComponent) ?? null;
    const displayId = conversationDisplayId(members, convComponent);
    result.push({
      conversation_id: convComponent,
      conversationRow: convRow,
      sessions: members,
      label,
      displayId,
    });
  }

  return result;
}
