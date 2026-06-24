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
 * Groups CLI sessions by conversation identity, using a three-tier keying strategy:
 *
 * Tier 1 (PRIMARY — ST-09): scope_conversation_id when non-null and non-empty.
 *   This is the Scope-minted durable id (schema v18). It is already machine-aware
 *   (minted per (machine_id, stem) binding), so no machine component is prepended.
 *   The bucket key IS the scope_conversation_id; the emitted conversation_id is also
 *   the scope_conversation_id (so convRowMap lookups work when the backend keys
 *   by_conversation on scope_conversation_id).
 *
 * Tier 2 (FALLBACK — legacy/pre-v18): machine-aware (machine_id, conversation_id) composite.
 *   Used when scope_conversation_id is absent (null/empty). conversation_id is the
 *   transcript filename stem (stable across /resume). machine_id is prepended to the bucket
 *   key for cross-machine isolation. The emitted conversation_id is the conv component ALONE
 *   (not the composite) so convRowMap.get(conversation_id) matches BudgetConversationRow.
 *
 * Tier 3 (IDLESS SINGLETON): when both scope_conversation_id and conversation_id are absent,
 *   falls back to cc_session_id (groups sessions sharing a host conversation), then to s.id
 *   (unique singleton per session).
 *
 * Behaviour:
 * - Label is "Cron run" when ALL members are cron sessions; otherwise "Conversation".
 * - Groups are ordered by the first session's position in the input array (preserves
 *   server sort order for each conversation's lead session).
 * - Null/empty machine_id in the legacy path -> empty sentinel so legacy rows merge.
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

  // Group sessions by a bucket key derived via the three-tier strategy above.
  // bucketKey:     the key used for bucketing (unique per logical group)
  // convComponent: the value emitted as ConversationGroup.conversation_id
  //                (used for convRowMap lookup; must match BudgetConversationRow.conversation_id)
  const orderedBucketKeys: string[] = [];           // insertion-order bucket keys
  const buckets = new Map<string, TraceSession[]>();
  const bucketToConvComponent = new Map<string, string>(); // bucketKey -> convComponent

  for (const s of sessions) {
    const scopeConvId = s.scope_conversation_id ?? null;
    const convId = s.conversation_id ?? null;
    const ccId = s.cc_session_id ?? null;

    let bucketKey: string;
    let convComponent: string;

    if (scopeConvId !== null && scopeConvId !== '') {
      // Tier 1 (PRIMARY — ST-09): scope_conversation_id is machine-aware; use it directly.
      // The bucket key and the emitted conversation_id are both the scope_conversation_id.
      bucketKey = scopeConvId;
      convComponent = scopeConvId;
    } else if (convId !== null && convId !== '') {
      // Tier 2 (FALLBACK — pre-v18): machine-aware (machine_id, conversation_id) composite.
      // Prepend machine_id to the bucket key for cross-machine isolation.
      // Null/empty machine_id -> empty sentinel so legacy rows don't fragment.
      const machineComponent = s.machine_id ?? '';
      bucketKey = `${machineComponent} ${convId}`;
      convComponent = convId;  // emitted alone (not composite) for convRowMap lookup
    } else {
      // Tier 3 (IDLESS): no scope_conversation_id, no conversation_id.
      // Fall back to cc_session_id (groups sessions sharing a host conversation) else s.id.
      const ccComponent = (ccId !== null && ccId !== '') ? ccId : s.id;
      // For idless singleton (s.id fallback) each session gets a unique bucket (s.id is unique).
      // For cc_session_id fallback the bucket may merge multiple sessions.
      bucketKey = ccComponent;
      convComponent = ccComponent;
    }

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
