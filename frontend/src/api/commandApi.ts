// =============================================================================
// Command Status API client (T-2026-06-13-51).
//
// Mirrors the served JSON shapes from src/Scope.Core/Telemetry/CommandContract.cs
// (snake_case wire names) exactly. This is the single place the SPA imports
// command status contract types and fetchers from.
//
// The endpoint is GET /api/command/status and always returns all sections.
// Null/empty sections reflect honestly-unavailable data, not a server error.
// =============================================================================

/** Workspace identity and index freshness. */
export interface CommandWorkspaceStatus {
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  index_path?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  workspace_id?: string | null;
  /** One of: fresh | stale | missing | unavailable */
  index_state: string;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  indexed_at?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  solution_path?: string | null;
  server_reachable: boolean;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  proxy_reachable?: boolean | null;
}

/** Resolved entitlement state. */
export interface CommandEntitlementStatus {
  tier: string;
  /** One of: free | trial | paid | grace | expired | unchecked */
  status: string;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  last_refresh_at?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  expires_at?: string | null;
  /** Null means not in grace window. WhenWritingNull: omitted when null; arrives as `undefined`. */
  grace_remaining_seconds?: number | null;
}

/** Uplink state. */
export interface CommandUplinkStatus {
  available: boolean;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  last_success_at?: string | null;
}

/** One row in the budget top-drivers list. */
export interface CommandBudgetTopDriver {
  label: string;
  spend_usd: number;
  run_count: number;
}

/** Compact 7-day budget aggregate. */
export interface CommandBudgetSummary {
  window_days: number;
  /** Null means no metered spend in the window. WhenWritingNull: omitted when null; arrives as `undefined`. */
  total_spend_usd?: number | null;
  /** CostProvenance vocabulary string. */
  cost_provenance: string;
  /** Top drivers by cron agent, descending by spend. */
  top_drivers: CommandBudgetTopDriver[];
}

/** Security / telemetry capture mode. */
export interface CommandSecurityStatus {
  /** One of: local_only | uplink */
  capture_mode: string;
  payload_capture: boolean;
}

/** Operational counters (content-free counts only). */
export interface CommandOperatingStatus {
  active_runs: number;
  trace_count_24h: number;
  warning_count: number;
}

/** Compact recent-run summary row (content-free). */
export interface CommandRecentRun {
  run_id: string;
  agent_label: string;
  /** Run status string. Narrow to RunStatus via toRunStatus() before display. */
  status: string;
  started_at: string;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  completed_at?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. Use `?? null` before CostProvenanceChip. */
  total_cost_usd?: number | null;
  /** CostProvenance vocabulary string. */
  cost_provenance: string;
  /**
   * Observability session key for this run (e.g. "cron-<uuid7>").
   * Present for runs that launched a harness; null for skipped runs or
   * rows that pre-date session recording.
   * Use to deep-link to #/trace?session=KEY.
   * WhenWritingNull: omitted when null; arrives as `undefined`.
   */
  session_key?: string | null;
}

/** Compact recent-session summary row. */
export interface CommandRecentSession {
  session_key: string;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  agent_type?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  first_seen_at?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  last_seen_at?: string | null;
  request_count: number;
}

/** Root DTO for GET /api/command/status. */
export interface CommandStatusResponse {
  generated_at: string;
  workspace: CommandWorkspaceStatus;
  entitlement: CommandEntitlementStatus;
  uplink: CommandUplinkStatus;
  budget: CommandBudgetSummary;
  security: CommandSecurityStatus;
  operating: CommandOperatingStatus;
  recent_runs: CommandRecentRun[];
  recent_sessions: CommandRecentSession[];
}

// ---------------------------------------------------------------------------
// Fetcher
// T-2026-06-14-13 (Lane F1): routes through the shared apiFetch client.
// Public signature is UNCHANGED so CommandView compiles without modification.
// ---------------------------------------------------------------------------

import { apiFetch } from './apiClient';

/**
 * Fetch the command status from GET /api/command/status.
 * Throws ScopeApiError on network error or non-2xx response, surfacing any
 * problem+json detail verbatim.
 */
export function fetchCommandStatus(): Promise<CommandStatusResponse> {
  return apiFetch<CommandStatusResponse>('/api/command/status');
}
