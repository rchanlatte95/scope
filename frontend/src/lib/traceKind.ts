// =============================================================================
// traceKind.ts — human-readable labels for trace entry kinds (T-2026-06-13-48).
//
// Maps the raw discriminator ('span' | 'request' | 'event') to a title-case
// human label shown in the timeline Type column and the TraceCard kind chip.
// Events keep their ToolTypeMark rendering unchanged; this helper is for
// span and request rows only, but is declared for all three kinds for completeness.
//
// Decision (owner 2026-06-13, applied):
//   span    -> "Task"     (a span groups an agent/dispatch work unit)
//   request -> "Request"  (title-case; self-explanatory)
//   event   -> "Event"    (title-case; used as fallback only — events use ToolTypeMark)
//
// ST-05 (T-2026-06-16-19): user_submit events use subtype-aware labels and tooltips.
//   These are exposed via userSubmitLabel() and userSubmitTooltip() so TraceCard
//   can render them as a distinct, elevated card kind without changing traceKindLabel().
// =============================================================================

import type { TraceEntry } from '../api/traceApi';

/** Human-readable label for each trace entry kind. */
export const TRACE_KIND_LABEL: Readonly<Record<TraceEntry['kind'], string>> = {
  span:    'Task',
  request: 'Request',
  event:   'Event',
};

/**
 * Tooltip definition text for non-event trace kinds.
 * Explains the internal term in plain language for governance-console users.
 */
export const TRACE_KIND_TOOLTIP: Readonly<Partial<Record<TraceEntry['kind'], string>>> = {
  span:    'A Task groups an agent or sub-agent dispatch and all its events into a single work unit.',
  request: 'A Request is one model call or top-level invocation recorded by the agent harness.',
};

/**
 * Returns the human-readable label for a trace entry kind.
 * Never returns the raw discriminator string.
 */
export function traceKindLabel(kind: TraceEntry['kind']): string {
  return TRACE_KIND_LABEL[kind];
}

/**
 * Returns the tooltip text for a trace entry kind, or undefined if no tooltip is defined.
 * (Events rely on ToolTypeMark for their type explanation.)
 */
export function traceKindTooltip(kind: TraceEntry['kind']): string | undefined {
  return TRACE_KIND_TOOLTIP[kind];
}

// ---------------------------------------------------------------------------
// user_submit subtype labels and tooltips (ST-05)
// ---------------------------------------------------------------------------

/**
 * Subtype-aware label for a user_submit event.
 * Returns a concise human label for the kind chip.
 * Falls back to 'User input' for unknown or absent subtypes.
 */
export function userSubmitLabel(subtype: string | null | undefined): string {
  switch (subtype) {
    case 'initiating_prompt': return 'Initiating prompt';
    case 'cron_prompt':       return 'Initiating prompt';
    case 'follow_up':         return 'Follow-up';
    case 'interrupt':         return 'Interrupt';
    case 'tool_approval':     return 'Approved';
    case 'tool_denial':       return 'Denied';
    case 'redirection':       return 'Redirection';
    case null:
    case undefined:
    default:                  return 'User input';
  }
}

/**
 * Returns true when the subtype indicates an initiating submit
 * (the strictly-first human input that opens the session).
 */
export function isInitiatingSubmit(subtype: string | null | undefined): boolean {
  return subtype === 'initiating_prompt' || subtype === 'cron_prompt';
}

/**
 * Descriptive tooltip text for a user_submit subtype.
 * Explains the event in plain language for governance-console users.
 */
export function userSubmitTooltip(subtype: string | null | undefined): string {
  switch (subtype) {
    case 'initiating_prompt':
      return 'The first human prompt that initiated this session.';
    case 'cron_prompt':
      return 'The cron agent prompt that initiated this scheduled session.';
    case 'follow_up':
      return 'A follow-up human message continuing this session.';
    case 'interrupt':
      return 'A human interrupt that stopped the agent mid-run.';
    case 'tool_approval':
      return 'A human approval granted for a gated tool call.';
    case 'tool_denial':
      return 'A human denial that blocked a gated tool call.';
    case 'redirection':
      return 'A human course correction that redirected the agent without stopping it.';
    case null:
    case undefined:
    default:
      return 'A human input event captured during this session.';
  }
}
