// =============================================================================
// ProofPathsView (PP-12a + PP-12b + PP-12c + PP-12d, Proof Paths Wave 3)
//
// The Proof Paths view: given an anchor symbol FQN (read from the URL), it shows
// the compiler-derived impact report — affected tests (strongest evidence,
// LEADS), affected dependents, and heuristic risk flags. Read-only: clipboard
// writes only; no POST/PUT/DELETE.
//
// URL is the single source of truth for the selected symbol (PP-01). The view
// holds NO authoritative copy of it — it reads `currentRoute().symbol` on every
// render and reacts to hashchange via the App-level router re-render.
//
// State machine (order matters — empty-leaf is checked BEFORE any error branch):
//   1. no symbol           -> landing EmptyState (no fetch)
//   2. fetching, no data   -> loading
//   3. success, both empty -> empty-leaf (honest, NOT the error component)
//   4. success, any data   -> results (tests + dependents + risks)
//   5. error               -> 404 / 409-ambiguous / 409-stale / transport / 5xx / 4xx
//
// TWO-TABLE SORT DECISION (documented per the task brief):
//   The router exposes a single `sort`/`order` slot owned by the cron table.
//   Two fully-controlled proof-paths tables would collide if they shared it.
//   Decision: the PRIMARY affected-tests table is URL-owned under a dedicated
//   `tsort`/`torder` namespace (added to the proof-paths route branch in
//   router.ts, mirroring PP-01's encode/omit pattern). The secondary dependents
//   table owns its sort as LOCAL component state. Rationale: (a) the lead table's
//   sort is the one worth deep-linking/sharing; (b) two distinct state owners can
//   never silently reorder each other; (c) it adds the minimum router surface
//   (one namespaced pair) rather than two more URL pairs for a secondary table.
//
// Canonical pattern: TraceView.tsx (useQuery + ScopeApiError handling).
// =============================================================================

import { useEffect, useRef, useCallback, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { fetchImpact } from '../api/impactApi';
import { fetchCommandStatus } from '../api/commandApi';
import { ScopeApiError } from '../api/apiClient';
import {
  queryKeys,
  STALE_COMMANDS_TRACES_MS,
} from '../api/queryClient';
import { currentRoute, navigate } from '../lib/router';
import type { SortOrder } from '../components/EvidenceTable';
import { formatAbsolute, relativeAge, resolveZone, STALE_INDEX_WARN_HOURS } from '../lib/time';
import { readPrefs } from '../lib/prefs';
import { PageHeader } from '../components/PageHeader';
import { EmptyState } from '../components/EmptyState';
import { SymbolSelector } from './proof-paths/SymbolSelector';
import { AffectedTestsTable } from './proof-paths/AffectedTestsTable';
import { AffectedSymbolsTable } from './proof-paths/AffectedSymbolsTable';
import { RisksPanel } from './proof-paths/RisksPanel';
import { parseAmbiguousCandidates } from './proof-paths/ambiguousCandidates';
import styles from './ProofPathsView.module.css';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** problem+json `title` values that map to recoverable / distinct error states. */
const TITLE_NO_SYMBOL = 'NoSymbolFound';
const TITLE_AMBIGUOUS = 'AmbiguousSymbol';
const TITLE_STALE_INDEX = 'StaleIndex';

// ---------------------------------------------------------------------------
// View
// ---------------------------------------------------------------------------

export default function ProofPathsView() {
  // URL is the single source of truth for the selected symbol (PP-01).
  // We do NOT mirror it into local state.
  const route = currentRoute();
  const symbol = route.symbol ?? null;

  // ------------------------------------------------------------------
  // Impact query (PP-12a)
  // ------------------------------------------------------------------
  const query = useQuery({
    queryKey: queryKeys.proofPaths(symbol),
    queryFn: ({ signal }) => fetchImpact(symbol ?? '', signal),
    enabled: symbol != null && symbol !== '',
    staleTime: STALE_COMMANDS_TRACES_MS,
  });

  // ------------------------------------------------------------------
  // Index freshness (PP-11): reuse the SHARED command-status client.
  // indexed_at is optional/nullable — render "unknown" when absent.
  // ------------------------------------------------------------------
  const statusQuery = useQuery({
    queryKey: queryKeys.commandStatus(),
    queryFn: () => fetchCommandStatus(),
    staleTime: STALE_COMMANDS_TRACES_MS,
  });

  const zone = resolveZone(readPrefs().timezone);
  const indexedAt = statusQuery.data?.workspace.indexed_at ?? null;
  // Only build a freshness label once the status query has resolved (success or
  // error), so the header does not flash "unknown" before the first response.
  const statusSettled = statusQuery.isSuccess || statusQuery.isError;
  const freshness = useMemo(
    () => (statusSettled ? buildFreshness(indexedAt, zone) : null),
    [statusSettled, indexedAt, zone],
  );
  const staleNote = useMemo(() => isStaleByAge(indexedAt), [indexedAt]);

  // ------------------------------------------------------------------
  // PP-12c: tests-table sort is URL-owned under tsort/torder (see header).
  // ------------------------------------------------------------------
  const testsSortKey = route.tsort ?? null;
  const testsSortOrder: SortOrder | undefined =
    route.torder === 'asc' || route.torder === 'desc' ? route.torder : undefined;

  const onTestsSortChange = useCallback((key: string, order: SortOrder) => {
    const r = currentRoute();
    navigate({ ...r, name: 'proof-paths', tsort: key, torder: order });
  }, []);

  // ------------------------------------------------------------------
  // PP-12d: focus management. After a successful load (and after a symbol swap
  // via back/forward), move focus to the stable results heading so focus never
  // falls to <body>. The landing action focuses the selector instead.
  // ------------------------------------------------------------------
  const resultsHeadingRef = useRef<HTMLHeadingElement>(null);
  const selectorRegionRef = useRef<HTMLDivElement>(null);

  /** Focus the combobox input inside the always-visible selector. */
  const focusSelector = useCallback(() => {
    const input = selectorRegionRef.current?.querySelector<HTMLElement>('[role="combobox"]');
    input?.focus();
  }, []);

  // Track the symbol whose load we have already focused, so a successful load
  // (or a swap to a new symbol) moves focus exactly once per symbol.
  const focusedForSymbol = useRef<string | null>(null);
  useEffect(() => {
    if (query.isSuccess && symbol != null && symbol !== '') {
      if (focusedForSymbol.current !== symbol) {
        focusedForSymbol.current = symbol;
        // Focus the results heading (tabIndex=-1) after paint so it exists.
        requestAnimationFrame(() => {
          if (resultsHeadingRef.current != null) {
            resultsHeadingRef.current.focus();
          } else {
            // No results region rendered (e.g. empty-leaf path): keep focus on
            // the selector rather than letting it drop to <body>. (PP-12d.)
            focusSelector();
          }
        });
      }
    } else if (symbol == null || symbol === '') {
      // Reset the focus latch when we return to the landing state so a later
      // selection re-focuses.
      focusedForSymbol.current = null;
    }
  }, [query.isSuccess, symbol, focusSelector]);

  // ------------------------------------------------------------------
  // Render: header + always-visible selector + state body
  // ------------------------------------------------------------------
  const data = query.data;
  const counts = data != null
    ? { tests: data.affected_tests.length, symbols: data.affected_symbols.length }
    : null;

  const context: string[] = [];
  if (symbol != null && symbol !== '') {
    // Anchor FULL FQN (not display_name) — EDGE-DATA-10.
    context.push(symbol);
  }
  if (counts != null) {
    context.push(`${String(counts.symbols)} affected ${counts.symbols === 1 ? 'symbol' : 'symbols'}`);
    context.push(`${String(counts.tests)} affected ${counts.tests === 1 ? 'test' : 'tests'}`);
  }

  return (
    <div className={styles.view}>
      <PageHeader
        title="Proof Paths"
        {...(context.length > 0 ? { context } : {})}
        {...(freshness !== null ? { freshness } : {})}
      />

      {/* Staleness advisory (cosmetic; server enforces the hard 409 separately). */}
      {staleNote && (
        <p className={styles.staleNote} role="note">
          <span className={styles.staleGlyph} aria-hidden="true">[stale]</span>
          The index was last refreshed over {STALE_INDEX_WARN_HOURS}h ago. Evidence is only as
          fresh as the last <code>scope index</code> run.
        </p>
      )}

      {/* Selector is ALWAYS visible (PP-12a step 4). */}
      <div ref={selectorRegionRef} className={styles.selectorRegion}>
        <SymbolSelector />
      </div>

      {renderBody()}
    </div>
  );

  // ------------------------------------------------------------------
  // Body state router
  // ------------------------------------------------------------------
  function renderBody() {
    // 1. No symbol -> landing (no API call). (EDGE-UX-2.)
    if (symbol == null || symbol === '') {
      return (
        <EmptyState
          variant="governance"
          glyph="magnifying-glass"
          heading="Pick a symbol to trace its impact"
          description="Proof Paths shows the compiler-derived dependency chains for a symbol: which tests cover it, which symbols depend on it, and the heuristic risk flags for a change. Search for a symbol above to begin."
          action={
            <button
              type="button"
              className={styles.landingAction}
              onClick={focusSelector}
            >
              Search symbols
            </button>
          }
        />
      );
    }

    // 2. Loading first page. (EDGE-UX-1.)
    if (query.isFetching && query.data === undefined) {
      return (
        <p className={styles.loading} role="status" aria-live="polite">
          Resolving impact for <code className={styles.fqn}>{symbol}</code>...
        </p>
      );
    }

    // 3. Empty-leaf — checked BEFORE any error branch. A 200 with both arrays
    //    empty is honest "no evidence", NEVER an error. (EDGE-DATA-1, EDGE-UX-3.)
    if (
      query.isSuccess &&
      data?.affected_symbols.length === 0 &&
      data.affected_tests.length === 0
    ) {
      return (
        <EmptyState
          variant="neutral"
          glyph="inbox"
          heading="No affected symbols or tests"
          description={`No affected symbols or tests found for ${symbol}. This symbol has no recorded dependents or tests in the current index.`}
        />
      );
    }

    // 4. Success with data -> results.
    if (query.isSuccess && data !== undefined) {
      return renderResults();
    }

    // 5. Error branches.
    if (query.isError) {
      return renderError(query.error);
    }

    // Disabled / idle (should be unreachable once symbol is present).
    return null;
  }

  // ------------------------------------------------------------------
  // Results body
  // ------------------------------------------------------------------
  function renderResults() {
    if (data === undefined || symbol == null) return null;
    const hasTests = data.affected_tests.length > 0;
    const hasSymbols = data.affected_symbols.length > 0;

    return (
      <section className={styles.results} aria-labelledby="proof-paths-results-heading">
        {/* Stable results-region heading: focus target (tabIndex=-1) for PP-12d. */}
        <h2
          id="proof-paths-results-heading"
          ref={resultsHeadingRef}
          tabIndex={-1}
          className={styles.resultsHeading}
        >
          Impact for <code className={styles.fqn}>{symbol}</code>
        </h2>

        <p className={styles.countsSummary}>
          {data.affected_tests.length} affected {data.affected_tests.length === 1 ? 'test' : 'tests'}
          {' · '}
          {data.affected_symbols.length} affected {data.affected_symbols.length === 1 ? 'symbol' : 'symbols'}
          {data.risks.length > 0 && (
            <>{' · '}{data.risks.length} risk {data.risks.length === 1 ? 'flag' : 'flags'}</>
          )}
        </p>

        {/* Affected tests LEAD (strongest evidence). URL-owned sort. */}
        <AffectedTestsTable
          affectedTests={data.affected_tests}
          hasOtherContent={hasSymbols}
          sortKey={testsSortKey}
          {...(testsSortOrder !== undefined ? { sortOrder: testsSortOrder } : {})}
          onSortChange={onTestsSortChange}
        />

        {/* Affected dependents. Local-state sort (does not collide with tests). */}
        <AffectedSymbolsTable
          symbols={data.affected_symbols}
          anchorFqn={symbol}
          hasTests={hasTests}
        />

        {/* Heuristic risk flags (hidden when empty). */}
        <RisksPanel risks={data.risks} />
      </section>
    );
  }

  // ------------------------------------------------------------------
  // Error body (PP-12b + PP-12c)
  // ------------------------------------------------------------------
  function renderError(error: unknown) {
    // Non-ScopeApiError throws (e.g. JSON.parse SyntaxError, or the local
    // "symbol is required" guard). (EDGE-API-8.)
    if (!(error instanceof ScopeApiError)) {
      const msg = error instanceof Error ? error.message : String(error);
      return (
        <EmptyState
          variant="governance"
          glyph="x-circle"
          heading="Could not load impact"
          description={msg}
        />
      );
    }

    const title = error.title;
    const status = error.status;

    // --- PP-12c: recoverable 409s (depend on PP-17 title/details) ---
    if (status === 409) {
      // 409 AmbiguousSymbol -> recoverable candidate list.
      if (title === TITLE_AMBIGUOUS) {
        const candidates = parseAmbiguousCandidates(error.details);
        if (candidates.length > 0) {
          return (
            <div className={styles.ambiguous} role="region" aria-label="Ambiguous symbol">
              <p className={styles.ambiguousHeading}>
                <span aria-hidden="true">[?] </span>Ambiguous symbol, pick one:
              </p>
              <ul className={styles.candidateList}>
                {candidates.map((candidate) => (
                  <li key={candidate}>
                    <button
                      type="button"
                      className={styles.candidateButton}
                      onClick={() => { navigate({ name: 'proof-paths', symbol: candidate }); }}
                    >
                      {candidate}
                    </button>
                  </li>
                ))}
              </ul>
            </div>
          );
        }
        // Title says ambiguous but no parseable candidates: fall through to the
        // coarse 409 copy below rather than showing an empty list.
      }

      // 409 StaleIndex -> distinct actionable re-index copy. (EDGE-API-3.)
      if (title === TITLE_STALE_INDEX) {
        return (
          <EmptyState
            variant="governance"
            glyph="clock"
            heading="The index is stale"
            description="The index is stale; re-run `scope index` to refresh evidence, then retry this symbol."
          />
        );
      }

      // Fallback coarse 409 (PP-17-deferred / title absent / unparsed ambiguous).
      // Documented limitation: without a precise title we cannot split stale vs
      // ambiguous, so we surface one actionable, honest message. (EDGE-API-7.)
      return (
        <EmptyState
          variant="governance"
          glyph="no-symbol"
          heading="Cannot resolve precisely"
          description="Cannot resolve precisely (the index may be stale or the symbol ambiguous). Re-run `scope index` or pick a more specific symbol."
        />
      );
    }

    // --- PP-12b: 404 NoSymbolFound (title or bare status). (EDGE-API-1.) ---
    if (title === TITLE_NO_SYMBOL || status === 404) {
      return (
        <EmptyState
          variant="governance"
          glyph="magnifying-glass"
          heading="No symbol found"
          description={`No symbol found matching ${symbol ?? ''}. Check the spelling or pick a symbol from search.`}
        />
      );
    }

    // --- PP-12b: generic transport / 5xx / 4xx. (EDGE-API-4.) ---
    if (status === 0) {
      return (
        <EmptyState
          variant="governance"
          glyph="x-circle"
          heading="Cannot reach Scope server"
          description="Cannot reach Scope server at 127.0.0.1:5130. Is Scope.Server running?"
        />
      );
    }
    if (status >= 500) {
      return (
        <EmptyState
          variant="governance"
          glyph="x-circle"
          heading="Scope server error"
          description={error.message}
        />
      );
    }
    // Other 4xx: surface the problem+json detail (error.message) VERBATIM.
    return (
      <EmptyState
        variant="governance"
        glyph="x-circle"
        heading="Could not load impact"
        description={error.message}
      />
    );
  }
}

// ---------------------------------------------------------------------------
// Freshness helpers (PP-11)
// ---------------------------------------------------------------------------

/**
 * Builds the "Last index update: <abs> (<age>)" freshness label. Always renders
 * the field — "unknown" when indexed_at is null/empty (absence is information,
 * EDGE-UX-6, gt#5).
 */
function buildFreshness(indexedAt: string | null, zone: string): string {
  if (indexedAt == null || indexedAt === '') {
    return 'Last index update: unknown';
  }
  return `Last index update: ${formatAbsolute(indexedAt, zone)} (${relativeAge(indexedAt)})`;
}

/** True when indexed_at is present and older than the staleness warn threshold. */
function isStaleByAge(indexedAt: string | null): boolean {
  if (indexedAt == null || indexedAt === '') return false;
  const ts = Date.parse(indexedAt);
  if (Number.isNaN(ts)) return false;
  const ageHours = (Date.now() - ts) / (1000 * 60 * 60);
  return ageHours > STALE_INDEX_WARN_HOURS;
}
