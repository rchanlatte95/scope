# External Interface Boundaries

This catalogs every place Scope parses a payload whose shape is owned by an EXTERNAL
interface that can change SILENTLY (an upstream rename/move of a field), and the contract
test that now guards each one. A silent shape change is the dangerous class because these
parsers are deliberately tolerant: they never throw on a missing field, they just return
null/zero. The result is data that looks fine but is quietly empty.

## Motivating incident: the ClaudeCodeHookAdapter token regression

Claude Code changed its sub-agent completion payload so that token usage moved out of the
field the adapter read. `ClaudeCodeHookAdapter` did not crash, did not log, did not fail any
test. It simply extracted `null` for sub-agent token usage for roughly a full session before
anyone noticed the cost numbers were zero. Nothing in CI went red because every test asserted
the happy path against the OLD shape, and the parser is (correctly) tolerant of absent fields.

The lesson: a tolerant parser plus a happy-path-only test = a silent-zero failure mode at every
external boundary. The fix class is a CONTRACT test per boundary that (a) pins the exact field
names against a REAL captured payload, and (b) where feasible, includes a rename-regression
guard proving that a renamed field surfaces as null (so the positive pin goes RED on real drift,
instead of the data quietly going to zero in production).

## Boundary catalog

| # | External interface | Scope parsing entry point | Fields Scope depends on | Contract test (file) | Refresh procedure |
|---|---|---|---|---|---|
| 1 | Claude Code PostToolUse hook payload (stdin to `scope log-tool-call`) | `ClaudeCodeHookAdapter.Parse` (`src/Scope.Core/Telemetry/Ingestion/ClaudeCodeHookAdapter.cs`) | `session_id`, `agent_id`, `agent_type`, `tool_use_id`, `tool_name`, `tool_input.model`, `tool_response`, Agent result `agentId` / `isAsync` / `status` / `totalTokens` / `usage.{input,output}_tokens` | `ClaudeCodeHookContractTests` (`test/Scope.Tests/Contracts/`) | Recapture a real payload into `test/Scope.Tests/Fixtures/HookPayloads/raw-posttooluse-samples.jsonl` (see that dir's `PROVENANCE.md`), update the adapter, update expected literals. Never fabricate a shape. |
| 2 | Claude Code UserPromptSubmit hook payload (the privileged human-intervention signal) | `ClaudeCodeHookAdapter.ParseUserSubmit` (same file) | `session_id`, `prompt` | `ClaudeCodeHookContractTests.ParseUserSubmit_*` | Same fixture dir + PROVENANCE; UserPromptSubmit shape is `{session_id, transcript_path, cwd, hook_event_name, prompt}`. |
| 3 | Claude Code Notification / Stop hooks | `ClaudeCodeHookAdapter.ParseNotification` / `ParseStop` (same file) | (intentional STUBs — no machine-decidable human-decision field exists yet) | none (documented gap) | When the host exposes a structured decision discriminator, capture a real payload, then add the parse + a contract test. Do NOT infer the decision from English prose. |
| 4 | Codex `--json` JSONL event stream (cron harness) | `CodexEventStreamParser.Parse` (`src/Scope.Core/Cron/Launch/CodexEventStreamParser.cs`) | `thread_id`/`threadId`/`thread.id`; `usage.{input_tokens,cached_input_tokens,output_tokens,total_tokens}` (+ camelCase / `prompt_tokens` aliases); `error`/`error.message` | `HarnessOutputContractTests.Codex*` (`test/Scope.Tests/Contracts/`) | Codex shapes are Scope-owned contract samples (no captured Codex stdin exists yet, see `Fixtures/HookPayloads/PROVENANCE.md`). Update the sample JSON + parser aliases together. |
| 5 | Claude Code `-p --output-format json` result object (cron harness) | `ClaudeCodeResultParser.Parse` (`src/Scope.Core/Cron/Launch/ClaudeCodeResultParser.cs`) | `session_id`, `usage.{input_tokens,cache_read_input_tokens,cache_creation_input_tokens,output_tokens}`, `total_cost_usd`, `is_error`, `subtype`, `error.message` | `HarnessOutputContractTests.ClaudeResult_*` | Update the representative JSON here to the new captured shape and the parser field names together. |
| 6 | Codex lifecycle hook payload (stdin) | `CodexHookAdapter.ParsePostToolUse` / `ParseUserPromptSubmit` (`src/Scope.Core/Telemetry/Ingestion/CodexHookAdapter.cs`) | `thread_id`/`session_id`/`conversation_id`, `tool_name`/`tool`/`name`, `tool_input`/`input`/`arguments`, `tool_output`/`output`/`result` | `CodexHookAdapterTests` (`test/Scope.Tests/Telemetry/`) + `Fixtures/HookPayloads/codex-hook-contract-samples.jsonl` | Pre-existing contract-sample coverage. When real Codex stdin is captured, replace the sample with the verbatim capture and tighten the asserts. |
| 7 | Anthropic `/v1/messages` STREAMING SSE (metering proxy) | `AnthropicSseScanner` (`Scope.Proxy/AnthropicSseScanner.cs`) | event `message_start`/`content_block_start`/`message_delta`; `message.{id,model,usage}`; `usage.{input_tokens,output_tokens,cache_creation_input_tokens,cache_read_input_tokens}`; `delta.stop_reason`; `content_block.{type,id}` | `AnthropicWireContractTests.Sse*` (`test/Scope.Proxy.Tests/`) | Recapture a real SSE response into `test/Scope.Proxy.Tests/Fixtures/` per `PROVENANCE.md` (the documented `AutomaticDecompression=None` capture), update the scanner field names + expected literals. |
| 8 | Anthropic `/v1/messages` NON-streaming JSON (metering proxy) | `AnthropicMeter.ParseMessageResponse` (`Scope.Proxy/AnthropicMeter.cs`) | top-level `model`, `id`, `stop_reason`; `usage.{input_tokens,output_tokens,cache_creation_input_tokens,cache_read_input_tokens}`; `content[].{type,id}` | `AnthropicWireContractTests.Meter_*` (`test/Scope.Proxy.Tests/`) | Same fixture dir + PROVENANCE; recapture `nonstream_basic.json`. |
| 9 | LiteLLM public pricing catalog (`model_prices_and_context_window.json`, fetched hourly, cached at `.scope/pricing.cache.json`) | `LiteLlmPricingSource.Parse` (`src/Scope.Core/CostEstimation/LiteLlmPricingSource.cs`) | per-model `input_cost_per_token`, `output_cost_per_token`, `cache_read_input_token_cost`, `cache_creation_input_token_cost`, `litellm_provider` | `PricingSourceContractTests` (`test/Scope.Tests/Contracts/`) | Recapture the fixture subset at `test/Scope.Tests/Fixtures/Pricing/litellm_model_prices.sample.json` (see PROVENANCE), update the `[JsonPropertyName]` bindings on `LiteLlmModelEntry`, update expected per-million values. |
| 10 | Entitlement token signed claim payload (issued by the hosted control plane) | `EntitlementTokenVerifier.Verify` -> `EntitlementToken` deserialization (`src/Scope.Core/Entitlement/`) | `token_version`, `tier`, `org_id`, `seat_id`, `limits.{max_enabled_crons,observability_retention_days,max_dashboard_sessions,audit_export_allowed}`, `issued_at_utc`, `expires_at_utc`, `trial_ends_at_utc` | `EntitlementTokenContractTests` (`test/Scope.Tests/Contracts/`) + the production-signed vector in `EntitlementTokenVerifierTests` | A claim rename is a `token_version` bump: update `[JsonPropertyName]` on `EntitlementToken`/`EntitlementLimits` AND the expected claim names here, then regenerate the production-signed test vector per `docs/entitlement-key-rotation.md`. |

## Why some boundaries get a rename-regression guard and some do not

A rename-regression guard mutates a real payload's critical key to a plausible new name and
asserts the extraction goes null/empty. It is the strongest form: it proves the positive pin
would go RED on real drift, not just that the parser tolerates the change. It is feasible where
a single key carries a single value (tokens, session id, prompt, cost fields). It is added for
boundaries 1, 2, 4, 5, 7, 8, 9, 10.

It is NOT applicable to boundary 3 (Notification/Stop are intentional STUBs — there is no field
to guard yet). It is partial for boundary 6 (Codex hook) because that adapter accepts a wide
alias set by design (multiple acceptable names per field), so the contract there is the alias
SET, covered by the existing `CodexHookAdapterTests`.

## Findings surfaced while building these guards

- The entitlement token `limits` claim, if renamed/absent upstream, deserializes to `null`
  (NOT to the record's `FreeTierDefaults` initializer — System.Text.Json source-gen does not
  apply the init default for an absent property). The downstream gate
  (`EntitlementStateResolver`) is what must treat a null/absent `Limits` as free-tier; callers
  must null-guard `token.Limits`. This is pinned by
  `EntitlementTokenContractTests.Verify_RenamedLimitsClaim_DropsLimits_DetectsSilentDrift`.
- The proxy SSE scanner only flips `Failed` on UNPARSEABLE JSON, never on a MISSING usage field.
  So a usage-field rename is a silent-zero, not a parse failure. That is exactly why the
  rename-regression guard (boundaries 7 and 8) matters: it makes the silent-zero observable.

## How to add a new boundary

1. Find the parse entry point (grep `JsonDocument.Parse`, `JsonSerializer.Deserialize`,
   `TryGetProperty` over an externally-owned payload).
2. Capture a REAL payload into the appropriate `Fixtures/` directory and document it in that
   directory's `PROVENANCE.md`. Never fabricate a shape (project rule).
3. Add a contract test under `test/Scope.Tests/Contracts/` (or `test/Scope.Proxy.Tests/` for the
   proxy): pin the critical field names against the real payload, then add a rename-regression
   guard if the boundary has single-value-per-key fields.
4. Add a row to the table above.
