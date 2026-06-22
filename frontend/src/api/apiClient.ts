// =============================================================================
// Shared API client (T-2026-06-14-13, Lane F1 / R0).
//
// All three API modules (cronApi, commandApi, traceApi) route through this ONE
// typed fetch wrapper. Key features:
//   - Typed ScopeApiError carrying HTTP status, verbatim problem+json detail,
//     and an optional retryAfterMs field parsed from the Retry-After header.
//   - AbortSignal pass-through to the underlying fetch call.
//   - Optional Idempotency-Key request header (for run-now mutations).
//   - application/problem+json body surfaced verbatim (never re-invented).
//   - 204 / empty-body safe JSON parse.
//
// This module has NO retry logic — retries are the responsibility of the
// TanStack Query client (queryClient.ts + retryConfig.ts). The error shape is
// defined here so retryConfig can inspect it without a circular import.
// =============================================================================

/**
 * Typed error thrown by apiFetch on transport failures or non-2xx responses.
 *
 * - `status`: HTTP status code. 0 means transport failure (server unreachable,
 *   DNS, or aborted). Use `== 0` to distinguish from a real HTTP error.
 * - `retryAfterMs`: parsed from the `Retry-After` response header when present
 *   (RFC 9110: integer seconds or HTTP-date). Undefined when the header is absent.
 *   `retryDelayWithHeader` in retryConfig.ts reads this field to honour the header.
 * - `title`: the problem+json `title`, which Scope.Server sets to the error `Code`
 *   (e.g. "NoSymbolFound" / "AmbiguousSymbol" / "StaleIndex"). Undefined when absent.
 * - `exitCode`: the Scope exit code, surfaced by Scope.Server as a problem+json
 *   extension. ASP.NET Core flattens `Results.Problem(extensions: ...)` onto the
 *   JSON ROOT of the body, so this is read from `body.exit_code` (NOT nested under
 *   an `extensions` object). Undefined when absent.
 * - `details`: the problem+json `details` extension, also flattened onto the body
 *   ROOT (`body.details`). Its shape is intentionally NOT assumed: Scope.Server
 *   emits it as a pipe-delimited string today (e.g. "symbol=X; candidates=A|B|C"),
 *   but it may carry a structured object/array (e.g. `AmbiguousSymbolEntry[]`).
 *   Typed `unknown`; consumers narrow it themselves.
 *
 * This is the generalised form of the former `CronApiError` from cronApi.ts.
 * CronApiError is kept as a re-export alias so existing catch sites compile unchanged.
 */
export class ScopeApiError extends Error {
  readonly status: number;
  readonly retryAfterMs?: number;
  readonly title?: string;
  readonly exitCode?: number;
  readonly details?: unknown;

  constructor(message: string, status: number, options: ScopeApiErrorOptions = {}) {
    super(message);
    this.name = 'ScopeApiError';
    this.status = status;
    // exactOptionalPropertyTypes: only assign when defined to avoid undefined in optional slot.
    if (options.retryAfterMs !== undefined) this.retryAfterMs = options.retryAfterMs;
    if (options.title !== undefined) this.title = options.title;
    if (options.exitCode !== undefined) this.exitCode = options.exitCode;
    if (options.details !== undefined) this.details = options.details;
  }
}

/**
 * Optional, strictly additive fields for a {@link ScopeApiError}. All callers that
 * read only `error.message` / `error.status` are unaffected; new fields are set
 * only when the server actually supplied them.
 */
export interface ScopeApiErrorOptions {
  retryAfterMs?: number;
  title?: string;
  exitCode?: number;
  details?: unknown;
}

/** Options forwarded to the underlying fetch call. */
export interface ApiFetchInit {
  method?: string;
  headers?: Record<string, string>;
  body?: string;
  signal?: AbortSignal;
}

/**
 * The structured result of reading a non-ok response body. `message` is always
 * present (the most honest human-facing string available). The remaining fields
 * are populated only when the body is application/problem+json AND the server
 * supplied them; they are strictly additive.
 */
export interface ErrorPayload {
  message: string;
  title?: string;
  exitCode?: number;
  details?: unknown;
}

/**
 * Reads a non-ok response into the most honest structured payload available.
 *
 * `message` mirrors the long-standing behaviour: a problem+json `detail`, else
 * `title`, else the raw text, else a status-line fallback. Never invents text.
 *
 * `title` / `exitCode` / `details` come from the problem+json body. CRITICAL:
 * ASP.NET Core's `Results.Problem(extensions: ...)` FLATTENS extension keys onto
 * the JSON ROOT of the problem+json body (siblings of `title`/`detail`/`status`),
 * so the Scope exit code and details are read as `body.exit_code` and
 * `body.details`, NOT from a nested `extensions` object. See Scope.Server
 * `ToHttpResult` (Program.cs ~735-751). `details` shape is not assumed (string or
 * structured), so it is carried through verbatim as `unknown`.
 */
export async function readErrorPayload(response: Response): Promise<ErrorPayload> {
  const text = await response.text().catch(() => '');
  if (text) {
    try {
      const parsed = JSON.parse(text) as {
        detail?: unknown;
        title?: unknown;
        // Flattened problem+json extensions live on the body ROOT, not under `extensions`.
        exit_code?: unknown;
        details?: unknown;
      };
      const detail = typeof parsed.detail === 'string' && parsed.detail.trim() ? parsed.detail : undefined;
      const title = typeof parsed.title === 'string' && parsed.title.trim() ? parsed.title : undefined;
      const message = detail ?? title ?? text;

      const payload: ErrorPayload = { message };
      if (title !== undefined) payload.title = title;
      if (typeof parsed.exit_code === 'number') payload.exitCode = parsed.exit_code;
      if (parsed.details !== undefined) payload.details = parsed.details;
      return payload;
    } catch {
      // Body was not JSON; fall through to the raw text.
    }
    return { message: text };
  }
  return { message: `${String(response.status)} ${response.statusText}`.trim() };
}

/**
 * Reads a non-ok response into the most honest message available:
 * a problem+json `detail`/`title`, a plain-text body, or a status-line fallback.
 * Never invents text.
 *
 * Kept as a thin wrapper over {@link readErrorPayload} for the many existing
 * call sites (and the cronApi re-export) that only need the message string.
 */
export async function readErrorBody(response: Response): Promise<string> {
  return (await readErrorPayload(response)).message;
}

/**
 * Parses a `Retry-After` header value into milliseconds.
 *
 * RFC 9110 allows either:
 *   - An integer number of seconds: "30"
 *   - An HTTP-date string: "Wed, 21 Oct 2026 07:28:00 GMT"
 *
 * Returns undefined when the value is absent or unparseable.
 */
function parseRetryAfterMs(retryAfter: string | null): number | undefined {
  if (retryAfter == null || retryAfter === '') return undefined;
  // Try integer seconds first.
  const seconds = Number(retryAfter);
  if (!Number.isNaN(seconds) && seconds >= 0) return seconds * 1000;
  // Try HTTP-date.
  const date = new Date(retryAfter);
  const delta = date.getTime() - Date.now();
  if (!Number.isNaN(delta) && delta > 0) return delta;
  return undefined;
}

/**
 * Unified fetch wrapper for all Scope API calls.
 *
 * Throws `ScopeApiError` on:
 *   - Transport failures (network, DNS, abort) — status 0.
 *   - Non-2xx HTTP responses — status = HTTP status code.
 *
 * On 429, the `retryAfterMs` field is set when the `Retry-After` header is present.
 */
export async function apiFetch<T>(path: string, init: ApiFetchInit = {}): Promise<T> {
  let response: Response;
  try {
    // Build RequestInit without undefined-valued optional properties so that
    // exactOptionalPropertyTypes is satisfied (undefined !== absent).
    const fetchInit: RequestInit = {};
    if (init.method !== undefined) fetchInit.method = init.method;
    if (init.headers !== undefined) fetchInit.headers = init.headers;
    if (init.body !== undefined) fetchInit.body = init.body;
    if (init.signal !== undefined) fetchInit.signal = init.signal;
    response = await fetch(path, fetchInit);
  } catch (err) {
    // Transport failure: server unreachable, DNS, aborted. status 0 => UI shows a
    // "could not reach Scope.Server" message rather than a fake validation error.
    throw new ScopeApiError(
      err instanceof Error ? err.message : String(err),
      0,
    );
  }

  if (!response.ok) {
    const retryAfterMs = response.status === 429
      ? parseRetryAfterMs(response.headers.get('Retry-After'))
      : undefined;
    const payload = await readErrorPayload(response);
    // message stays EXACTLY as before (detail || title || raw text); title/exitCode/details
    // are strictly additive and set only when the problem+json body supplied them.
    const options: ScopeApiErrorOptions = {};
    if (retryAfterMs !== undefined) options.retryAfterMs = retryAfterMs;
    if (payload.title !== undefined) options.title = payload.title;
    if (payload.exitCode !== undefined) options.exitCode = payload.exitCode;
    if (payload.details !== undefined) options.details = payload.details;
    throw new ScopeApiError(payload.message, response.status, options);
  }

  // 204 / empty bodies are valid for some calls; guard the JSON parse.
  const text = await response.text();
  return (text ? JSON.parse(text) : null) as T;
}
