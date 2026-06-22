// =============================================================================
// SymbolSelector.tsx — PP-06: URL-owned combobox for symbol search.
//
// Design contract:
//   - URL is the single source of truth for the selected FQN (PP-01).
//   - Debounced search via useDebouncedCallback(fn, 250) from use-debounce.
//   - useQuery keyed on the debounced query; AbortSignal forwarded per request.
//   - On select: navigate({ name:'proof-paths', symbol: row.fqn }).
//   - ARIA: role=combobox, aria-expanded, aria-activedescendant; role=listbox
//     with role=option children; arrow up/down + Enter select; Escape closes.
//   - No raw hex — only --scope-* tokens.
//   - Sharp geometry: radius 0 (--scope-radius-sm only on the input).
//   - Selected option = INVERSE FILL BLOCK (no left accent stripe).
//   - 44px min option targets.
// =============================================================================

import { useCallback, useId, useMemo, useRef, useState } from 'react';
import { useDebouncedCallback } from 'use-debounce';
import { useQuery } from '@tanstack/react-query';
import { fetchSymbolSearch, type SymbolSearchResult } from '../../api/symbolSearchApi';
import { ScopeApiError } from '../../api/apiClient';
import { navigate } from '../../lib/router';
import styles from './SymbolSelector.module.css';

// ---------------------------------------------------------------------------
// Internal query key factory
// ---------------------------------------------------------------------------

function symbolSearchQueryKey(q: string): [string, string, string] {
  return ['proof-paths', 'symbol-search', q];
}

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface SymbolSelectorProps {
  /**
   * Optional initial input display value (does NOT pre-select; the selected FQN
   * is read from the URL by the parent view, not held here).
   */
  initialInputValue?: string;
  /** Optional label for the input. Defaults to "Search symbols". */
  label?: string;
  /** Additional CSS class for the outer wrapper. */
  className?: string;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * URL-owned combobox for selecting a symbol FQN.
 *
 * On select: navigate({ name: 'proof-paths', symbol: row.fqn })
 * The selected FQN is NOT kept in local state (the URL is the source of truth).
 *
 * Keyboard: Arrow-down/up move through options; Enter selects; Escape closes.
 * ARIA: role=combobox on the input, role=listbox on the dropdown, role=option
 * on each result, aria-expanded and aria-activedescendant maintained.
 */
export function SymbolSelector({
  initialInputValue = '',
  label = 'Search symbols',
  className,
}: SymbolSelectorProps) {
  // Local controlled input — user types here; the debounced value drives the query.
  const [inputValue, setInputValue] = useState<string>(initialInputValue);
  // Debounced query string (cleared when blank to suppress fetch).
  const [debouncedQuery, setDebouncedQuery] = useState<string>('');
  // Whether the listbox is open.
  const [isOpen, setIsOpen] = useState<boolean>(false);
  // The 0-based index of the keyboard-focused option (-1 = none).
  const [activeIndex, setActiveIndex] = useState<number>(-1);

  const inputRef = useRef<HTMLInputElement>(null);
  // Stable IDs for ARIA.
  const comboboxId = useId();
  const listboxId = `${comboboxId}-listbox`;
  // Returns the stable option id for index i.
  const optionId = useCallback(
    (i: number) => `${comboboxId}-option-${String(i)}`,
    [comboboxId],
  );

  // ---------------------------------------------------------------------------
  // Debounced commit: updates debouncedQuery 250ms after the user stops typing.
  // useDebouncedCallback from use-debounce — do NOT hand-roll a timer (EDGE-PERF-3).
  // ---------------------------------------------------------------------------
  const commitDebounced = useDebouncedCallback((q: string) => {
    // Blank guard: never fire a search for whitespace (EDGE-API-6).
    const trimmed = q.trim();
    setDebouncedQuery(trimmed);
    if (trimmed !== '') {
      setIsOpen(true);
      setActiveIndex(-1);
    } else {
      setIsOpen(false);
    }
  }, 250);

  // ---------------------------------------------------------------------------
  // Search query (keyed on debouncedQuery; disabled when blank).
  // The AbortSignal forwarded here cancels the in-flight fetch when the query
  // key changes (superseded search is cancelled — EDGE-PERF-3).
  // ---------------------------------------------------------------------------
  const searchQuery = useQuery({
    queryKey: symbolSearchQueryKey(debouncedQuery),
    queryFn: ({ signal }) => fetchSymbolSearch(debouncedQuery, signal),
    enabled: debouncedQuery !== '',
    staleTime: 30_000,
  });

  // Stable reference via useMemo so the array identity does not change every
  // render and invalidate the useCallback that closes over it (react-hooks/exhaustive-deps).
  const results: SymbolSearchResult[] = useMemo(
    () => searchQuery.data ?? [],
    [searchQuery.data],
  );

  // ---------------------------------------------------------------------------
  // Input change handler
  // ---------------------------------------------------------------------------
  const handleInputChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const val = e.target.value;
      setInputValue(val);
      commitDebounced(val);
      if (val.trim() === '') {
        setIsOpen(false);
        setActiveIndex(-1);
        setDebouncedQuery('');
      }
    },
    [commitDebounced],
  );

  // ---------------------------------------------------------------------------
  // Select handler: writes the FQN to the URL via navigate().
  // The selected FQN is NOT held in local state; URL is the source of truth.
  // ---------------------------------------------------------------------------
  const handleSelect = useCallback((row: SymbolSearchResult) => {
    navigate({ name: 'proof-paths', symbol: row.fqn });
    // Show the FQN in the input as a display hint (not the source of truth).
    setInputValue(row.fqn);
    setIsOpen(false);
    setActiveIndex(-1);
    // Move focus back to the input so the user can refine or navigate by keyboard.
    inputRef.current?.focus();
  }, []);

  // ---------------------------------------------------------------------------
  // Keyboard handler for the combobox input (EDGE-A11Y-7).
  // Arrow down/up: move activeIndex; Enter: select; Escape: close.
  // ---------------------------------------------------------------------------
  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (!isOpen) {
        if (e.key === 'ArrowDown' && results.length > 0) {
          e.preventDefault();
          setIsOpen(true);
          setActiveIndex(0);
        }
        return;
      }

      if (e.key === 'ArrowDown') {
        e.preventDefault();
        setActiveIndex((prev) =>
          prev < results.length - 1 ? prev + 1 : prev,
        );
      } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        setActiveIndex((prev) => (prev > 0 ? prev - 1 : 0));
      } else if (e.key === 'Enter') {
        e.preventDefault();
        if (activeIndex >= 0 && activeIndex < results.length) {
          const row = results[activeIndex];
          if (row !== undefined) {
            handleSelect(row);
          }
        }
      } else if (e.key === 'Escape') {
        e.preventDefault();
        setIsOpen(false);
        setActiveIndex(-1);
      }
    },
    [isOpen, results, activeIndex, handleSelect],
  );

  // ---------------------------------------------------------------------------
  // Blur handler: close when focus leaves the widget entirely.
  // Use setTimeout(0) so a click on an option fires before blur hides it.
  // ---------------------------------------------------------------------------
  const handleBlur = useCallback(() => {
    setTimeout(() => {
      // If focus is still on the input, keep open.
      if (document.activeElement !== inputRef.current) {
        setIsOpen(false);
        setActiveIndex(-1);
      }
    }, 0);
  }, []);

  // ---------------------------------------------------------------------------
  // Render state classification
  // ---------------------------------------------------------------------------
  const showListbox = isOpen && debouncedQuery !== '';
  const isLoading = searchQuery.isFetching;
  const isError = searchQuery.isError;
  const isEmpty =
    !isLoading && !isError && searchQuery.isSuccess && results.length === 0;

  const activeDescendant =
    isOpen && activeIndex >= 0 && activeIndex < results.length
      ? optionId(activeIndex)
      : undefined;

  // Error message: surface ScopeApiError detail verbatim (EDGE-API-4).
  // When isError is true, TanStack Query guarantees searchQuery.error is non-null.
  let errorMessage = 'Search failed.';
  if (isError) {
    const err: unknown = searchQuery.error;
    if (err instanceof ScopeApiError) {
      if (err.status === 0) {
        errorMessage = '[!] Cannot reach Scope server at 127.0.0.1:5130.';
      } else {
        errorMessage = `[!] ${err.message}`;
      }
    } else if (err instanceof Error) {
      errorMessage = `[!] ${err.message}`;
    }
  }

  return (
    <div
      className={
        [styles.wrapper, className]
          .filter((c): c is string => c !== undefined)
          .join(' ')
      }
    >
      {/* Visually-hidden label: associated with the input via htmlFor.
          The label is not hidden via aria-hidden so AT reads it when focusing. */}
      <label
        htmlFor={comboboxId}
        style={{
          position: 'absolute',
          width: 1,
          height: 1,
          overflow: 'hidden',
          clip: 'rect(0,0,0,0)',
          whiteSpace: 'nowrap',
        }}
      >
        {label}
      </label>

      {/*
        Combobox input.
        role=combobox + aria-haspopup=listbox + aria-expanded convey to AT that
        this input controls a listbox popup (ARIA 1.2 combobox pattern).
        aria-activedescendant points to the keyboard-focused option ID.
        aria-controls names the listbox element.
        aria-autocomplete=list signals list-based autocompletion.
      */}
      <input
        id={comboboxId}
        ref={inputRef}
        role="combobox"
        type="text"
        className={styles.input}
        value={inputValue}
        placeholder={label}
        aria-haspopup="listbox"
        aria-expanded={showListbox}
        aria-controls={listboxId}
        aria-autocomplete="list"
        aria-activedescendant={activeDescendant}
        autoComplete="off"
        spellCheck={false}
        onChange={handleInputChange}
        onKeyDown={handleKeyDown}
        onBlur={handleBlur}
        onFocus={() => {
          // Re-open the dropdown if there is a non-blank query and results available.
          if (debouncedQuery !== '' && results.length > 0) {
            setIsOpen(true);
          }
        }}
      />

      {/*
        Listbox popup.
        hidden attribute + display:none prevents AT from reading the list when
        it is not open, while keeping the DOM node available for aria-controls.
      */}
      <ul
        id={listboxId}
        role="listbox"
        aria-label={label}
        className={styles.listbox}
        hidden={!showListbox}
        style={!showListbox ? { display: 'none' } : undefined}
      >
        {/* Loading state: AT reads "Searching..." as the sole option */}
        {isLoading && (
          <li
            role="option"
            aria-selected={false}
            aria-disabled="true"
            className={styles.notice}
          >
            Searching...
          </li>
        )}

        {/* Empty state: 0 results for a non-blank query */}
        {isEmpty && (
          <li
            role="option"
            aria-selected={false}
            aria-disabled="true"
            className={styles.notice}
          >
            No matches for &ldquo;{debouncedQuery}&rdquo;
          </li>
        )}

        {/* Error state: text + "[!]" glyph (never color alone — EDGE-A11Y-1) */}
        {isError && !isLoading && (
          <li
            role="option"
            aria-selected={false}
            aria-disabled="true"
            className={styles.errorNotice}
          >
            {errorMessage}
          </li>
        )}

        {/* Results: each option shows fqn + kind + file (OQ-3, EDGE-DATA-8).
            44px min-height via .option in the CSS module (EDGE-A11Y-5). */}
        {!isLoading &&
          !isError &&
          results.map((row, i) => {
            const isActive = i === activeIndex;
            return (
              <li
                key={row.fqn}
                id={optionId(i)}
                role="option"
                aria-selected={isActive}
                className={
                  [styles.option, isActive ? styles.optionActive : undefined]
                    .filter((c): c is string => c !== undefined)
                    .join(' ')
                }
                // onMouseDown prevents the blur from firing before onClick,
                // which would hide the listbox before the click is processed.
                onMouseDown={(e) => { e.preventDefault(); }}
                onClick={() => { handleSelect(row); }}
                onMouseEnter={() => { setActiveIndex(i); }}
              >
                {/*
                  Primary: FQN (monospaced, primary identity).
                  Secondary: kind + file (disambiguation line so the user can
                  distinguish overloads before selecting — OQ-3, EDGE-DATA-8).
                  Kind rendered as a glyph-block (text + square background shape)
                  so the type signal is never color-alone (design contract).
                */}
                <span className={styles.optionFqn}>{row.fqn}</span>
                <span className={styles.optionKindFile}>
                  <span className={styles.optionKind}>{row.kind}</span>
                  <span className={styles.optionDot} aria-hidden="true">·</span>
                  <span className={styles.optionFile}>{row.file}</span>
                </span>
              </li>
            );
          })}
      </ul>
    </div>
  );
}
