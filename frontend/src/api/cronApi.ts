// =============================================================================
// Cron Agents API client (T-2026-06-12-20, CR-M1E).
//
// Mirrors the SERVED JSON shapes from src/Scope.Core/App/ScopeApp.Cron.cs (snake_case
// wire names) exactly. This is the single place the SPA imports cron contract types
// and fetchers from, so a contract change is a one-file swap. View logic lives in the
// Cron view/components, never here.
//
// The server (src/Scope.Server/Program.cs /api/cron/*) is the AUTHORITATIVE validator:
// these fetchers surface the server's 400/404 problem body verbatim (including the
// free-tier 3-enabled-agent limit message) rather than re-deriving it client-side.
//
// T-2026-06-14-13 (Lane F1): All fetchers now route through the shared apiFetch client
// from apiClient.ts. The public exported function signatures are UNCHANGED so CronView
// and all other consumers continue to compile without modification.
// =============================================================================

/** A cron-agent definition as served by GET/POST /api/cron/agents. */
export interface CronAgent {
  id: string;
  label: string;
  schedule: string;
  prompt: string;
  repo_path: string;
  harness: string;
  /**
   * Optional model override. Null/absent (WhenWritingNull omits null) means harness default.
   * Typed `| undefined` so `undefined` from JSON omission is structurally valid.
   */
  model?: string | null;
  /**
   * Optional token budget. Null/absent means unbounded/harness default.
   * Typed `| undefined` so `undefined` from JSON omission is structurally valid.
   */
  token_budget?: number | null;
  enabled: boolean;
  created_at: string;
  updated_at: string;
  /**
   * Whether a second concurrent run-now is blocked (R7 concurrency guard). Defaults to true
   * server-side; surfaced here so the edit form can prefill the toggle. Typed `| undefined` for
   * JSON omission safety, though the server always emits it.
   */
  block_concurrent_runs?: boolean;
  /**
   * Optional per-agent wall-clock cutoff in seconds (T-2026-06-17-06). Null/absent means the
   * global supervisor default applies. WhenWritingNull omits null, so `undefined` is valid.
   */
  max_runtime_seconds?: number | null;
  // T-2026-06-13-44: last-run summary fields (nullable; absent on agents that have never run).
  // WhenWritingNull: null values are OMITTED from the JSON and arrive as `undefined` on the FE.
  // Use `== null` guards (not `=== null`) and `?? null` coalescing throughout.
  last_run_status?: string | null;
  last_run_at?: string | null;
  last_run_cost?: number | null;
  last_run_cost_provenance?: string | null;
}

/** One run-history row as served by GET /api/cron/agents/{label}/runs. */
export interface CronRun {
  id: string;
  cron_agent_id: string;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. Use `?? null` or `== null`. */
  dispatch_id?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  session_key?: string | null;
  started_at: string;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  completed_at?: string | null;
  /**
   * Run status string from the server. Narrow to typed RunStatus via
   * `toRunStatus(run.status)` from `lib/status.ts` before display.
   * Wire type stays string (server open-enum); unknown values -> 'failed'.
   */
  status: string;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. Use `!= null` to guard. */
  exit_code?: number | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. Use `== null` or `?? null`. */
  total_cost_usd?: number | null;
  /**
   * Cost provenance when FE-B lands. Narrow via `toCostProvenance(run.cost_provenance)`
   * from `lib/provenance.ts`. Absent until FE-B emits the field.
   */
  cost_provenance?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  skip_reason?: string | null;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  error_message?: string | null;
}

/** The run-now result as served by POST /api/cron/agents/{label}/run. */
export interface CronRunResult {
  run_id: string;
  cron_agent_id: string;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. Use `?? 'not recorded'`. */
  dispatch_id?: string | null | undefined;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  session_key?: string | null | undefined;
  /**
   * Run status string. Narrow via `toRunStatus(result.status)` from `lib/status.ts`.
   * Wire type stays string; unknown values -> 'failed'.
   */
  status: string;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. Use `!= null` to guard. */
  exit_code?: number | null | undefined;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. Use `== null` or `?? null`. */
  total_cost_usd?: number | null | undefined;
  launched: boolean;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  skip_reason?: string | null | undefined;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  error_message?: string | null | undefined;
  /** WhenWritingNull: omitted when null; arrives as `undefined`. */
  capability_detail?: string | null | undefined;
  /**
   * Cost provenance for the run-now result. Narrow via `toCostProvenance(result.cost_provenance)`
   * from `lib/provenance.ts`. The backend always emits one of the 6 values; absent/unknown
   * narrows to 'unavailable'.
   */
  cost_provenance?: string | null | undefined;
}

/** One harness capability row as served by GET /api/cron/capabilities. */
export interface CronCapability {
  harness: string;
  status: string;
  detail: string;
}

/** The request body for POST /api/cron/agents. Empty optionals are omitted by the form. */
export interface CronCreateRequest {
  label: string;
  schedule: string;
  prompt: string;
  repo_path: string;
  harness: string;
  model?: string | null;
  token_budget?: number | null;
  enabled?: boolean;
  /** R7 concurrency guard. Omitted -> server default (true). */
  block_concurrent_runs?: boolean;
  /** Optional per-agent wall-clock cutoff in seconds. Omitted -> global default. */
  max_runtime_seconds?: number | null;
}

/**
 * The request body for PUT /api/cron/agents/{label} (T-2026-06-17-05, FULL REPLACE).
 * The label is the immutable URL path key and is NOT a body field. `enabled` is NOT here either:
 * it stays on the enable/disable endpoints. Empty optionals are omitted by the form.
 */
export interface CronUpdateRequest {
  schedule: string;
  prompt: string;
  repo_path: string;
  harness: string;
  model?: string | null;
  token_budget?: number | null;
  block_concurrent_runs?: boolean;
  max_runtime_seconds?: number | null;
}

// ---------------------------------------------------------------------------
// Shared client import.
// CronApiError is kept as a type alias for ScopeApiError so existing catch
// sites (views, tests) continue to compile unchanged.
// ---------------------------------------------------------------------------
import { apiFetch, ScopeApiError, readErrorBody } from './apiClient';

export { readErrorBody };

/**
 * An error raised by a cron fetcher carrying the server's HTTP status and the
 * verbatim, human-readable problem detail so the UI can show it honestly (the free
 * tier limit, an invalid schedule, an unknown label). `status` is 0 for a transport
 * failure (server unreachable), which the UI distinguishes from a 4xx.
 *
 * @deprecated Alias for ScopeApiError. New code should import ScopeApiError from
 *   './apiClient' directly. Kept here so existing catch sites compile unchanged.
 */
export { ScopeApiError as CronApiError };

/** Internal helper: thin wrapper that keeps the existing call-sites readable. */
function request<T>(path: string, init?: Parameters<typeof apiFetch>[1]): Promise<T> {
  return apiFetch<T>(path, init);
}

/** Lists all cron-agent definitions. */
export function fetchCronAgents(): Promise<CronAgent[]> {
  return request<CronAgent[]>('/api/cron/agents');
}

/** Gets one cron-agent definition by label (404 -> CronApiError status 404). */
export function fetchCronAgent(label: string): Promise<CronAgent> {
  return request<CronAgent>(`/api/cron/agents/${encodeURIComponent(label)}`);
}

/** Creates a cron agent. 201 -> the created definition; 400 -> CronApiError verbatim. */
export function createCronAgent(body: CronCreateRequest): Promise<CronAgent> {
  return request<CronAgent>('/api/cron/agents', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/**
 * Updates (FULL REPLACE) an existing cron agent by label (T-2026-06-17-05).
 * 200 -> the updated definition; 404 -> CronApiError status 404 (unknown/soft-deleted label);
 * 400 -> CronApiError verbatim (validation: bad schedule/harness, token_budget<=0, max_runtime<=0).
 * The label is the immutable identity and is taken from the URL, never the body.
 */
export function updateCronAgent(label: string, body: CronUpdateRequest): Promise<CronAgent> {
  return request<CronAgent>(`/api/cron/agents/${encodeURIComponent(label)}`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/** Removes a cron agent by label (destructive; UI confirms before calling). */
export function removeCronAgent(label: string): Promise<{ label: string; removed: boolean }> {
  return request<{ label: string; removed: boolean }>(`/api/cron/agents/${encodeURIComponent(label)}`, {
    method: 'DELETE',
  });
}

/** Lists run history for one agent, newest first as ordered server-side. */
export function fetchCronRuns(label: string, limit = 50): Promise<CronRun[]> {
  return request<CronRun[]>(`/api/cron/agents/${encodeURIComponent(label)}/runs?limit=${String(limit)}`);
}

/**
 * Polls a single run by id until the caller stops polling or the run reaches terminal state.
 * Returns the current CronRun row (may still be 'running' if called immediately after start).
 * 404 -> CronApiError status 404.
 */
export function fetchCronRun(label: string, runId: string): Promise<CronRun> {
  return request<CronRun>(`/api/cron/agents/${encodeURIComponent(label)}/runs/${encodeURIComponent(runId)}`);
}

/**
 * Starts an agent run NOW. Returns 202 immediately with the initial run row (status 'running',
 * 'skipped', or 'failed'). The harness continues server-side; the client polls
 * fetchCronRun until the row reaches a terminal state.
 *
 * @param label - The cron agent label.
 * @param signal - Optional AbortSignal; lets the UI cancel a navigation-orphaned request.
 * @param idempotencyKey - Optional idempotency key (UUID v4 recommended). When provided,
 *   the `Idempotency-Key` header is attached and the backend returns 409 Conflict if a
 *   run for this key is already in-flight. The caller should treat a 409 as a typed
 *   non-error "already running" outcome and not surface it as an error to the user.
 *   Generate per-click via `crypto.randomUUID()`. Wire contract: header name is
 *   `Idempotency-Key` (canonical capitalisation per IETF draft).
 */
export function runCronAgent(
  label: string,
  signal?: AbortSignal,
  idempotencyKey?: string,
): Promise<CronRunResult> {
  const headers: Record<string, string> = {};
  if (idempotencyKey != null && idempotencyKey !== '') {
    headers['Idempotency-Key'] = idempotencyKey;
  }
  // exactOptionalPropertyTypes: only include optional props when defined.
  const init: Parameters<typeof request>[1] = { method: 'POST' };
  if (Object.keys(headers).length > 0) init.headers = headers;
  if (signal !== undefined) init.signal = signal;
  return request<CronRunResult>(`/api/cron/agents/${encodeURIComponent(label)}/run`, init);
}

/** Probes harness capabilities (always 200). Drives the create form's harness options. */
export function fetchCronCapabilities(): Promise<CronCapability[]> {
  return request<CronCapability[]>('/api/cron/capabilities');
}

/**
 * Enables a cron agent by label.
 * 200 -> updated CronAgent with enabled=true.
 * 400 -> CronApiError with the cap message (free tier: max 3 enabled agents).
 * 404 -> CronApiError status 404.
 */
export function enableCronAgent(label: string): Promise<CronAgent> {
  return request<CronAgent>(`/api/cron/agents/${encodeURIComponent(label)}/enable`, {
    method: 'POST',
  });
}

/**
 * Disables a cron agent by label.
 * 200 -> updated CronAgent with enabled=false.
 * 404 -> CronApiError status 404.
 * Always allowed (no cap).
 */
export function disableCronAgent(label: string): Promise<CronAgent> {
  return request<CronAgent>(`/api/cron/agents/${encodeURIComponent(label)}/disable`, {
    method: 'POST',
  });
}
