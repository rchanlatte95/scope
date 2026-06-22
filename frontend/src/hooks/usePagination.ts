/**
 * usePagination.ts — Reusable pagination state hook (T-2026-06-17-03).
 *
 * Manages page/pageSize state for both server-side and client-side pagination surfaces.
 * Derives the page count analytically from totalCount / pageSize (proof-correct).
 * On page-size change, clamps the current page so it stays within the new page count.
 *
 * Page is 1-based (1 = first page). pageSize must be one of [10, 25, 50, 100].
 * totalCount is the analytically correct total records from the server (or list.length
 * for client-side mode). If totalCount is unknown/loading, pass 0 — page count = 0.
 */

import { useCallback, useState } from 'react';
import { DEFAULT_PAGE_SIZE, VALID_PAGE_SIZES } from '../lib/viewState';

export interface PaginationState {
  /** Current 1-based page number. Always >= 1. */
  page: number;
  /** Records per page. Always a valid page size (10|25|50|100). */
  pageSize: number;
  /** Total pages derived from totalCount / pageSize. 0 when totalCount is unknown. */
  pageCount: number;
  /** 0-based row offset for server-side queries: (page - 1) * pageSize. */
  offset: number;
  /** Whether there is a previous page (page > 1). */
  hasPrev: boolean;
  /** Whether there is a next page (page < pageCount). */
  hasNext: boolean;
}

export interface PaginationActions {
  /** Go to a specific page (clamped to [1, pageCount]). */
  goToPage: (p: number) => void;
  /** Go to the previous page. No-op when already on page 1. */
  prevPage: () => void;
  /** Go to the next page. No-op when already on the last page. */
  nextPage: () => void;
  /**
   * Change the page size. Clamps the current page so it remains within the
   * new page count (e.g. was on page 5 of 5, pageSize doubles -> now page 3 of 3).
   * If pageSize is not in VALID_PAGE_SIZES it is silently ignored.
   */
  setPageSize: (ps: number) => void;
  /** Reset to page 1 (called on filter change, e.g. date-range or show-deleted toggle). */
  resetToFirstPage: () => void;
}

/**
 * Compute the total page count from a total record count and page size.
 * Returns 0 when totalCount is 0 (no data or not yet loaded).
 */
export function computePageCount(totalCount: number, pageSize: number): number {
  if (totalCount <= 0 || pageSize <= 0) return 0;
  return Math.ceil(totalCount / pageSize);
}

/**
 * Clamp a page to [1, pageCount]. When pageCount is 0 (unknown total), returns 1.
 */
function clampPage(p: number, pageCount: number): number {
  if (pageCount <= 0) return 1;
  return Math.max(1, Math.min(p, pageCount));
}

/**
 * usePagination: manages page/pageSize state for a paginated surface.
 *
 * @param totalCount - Total record count from the server (or 0 when unknown/loading).
 * @param initialPage - Starting page (default 1). Used on mount; ignored on re-renders.
 * @param initialPageSize - Starting page size (default DEFAULT_PAGE_SIZE = 10).
 *
 * Returns [state, actions]. The caller uses state.offset / state.pageSize to build
 * server queries and state.pageCount / state.page to render the Pagination component.
 */
export function usePagination(
  totalCount: number,
  initialPage = 1,
  initialPageSize: number = DEFAULT_PAGE_SIZE,
): [PaginationState, PaginationActions] {
  const safeInitPage = Math.max(1, initialPage);
  const safeInitSize = VALID_PAGE_SIZES.has(initialPageSize) ? initialPageSize : DEFAULT_PAGE_SIZE;

  const [page, setPage] = useState<number>(safeInitPage);
  const [pageSize, setPageSizeState] = useState<number>(safeInitSize);

  const pageCount = computePageCount(totalCount, pageSize);
  const clampedPage = clampPage(page, pageCount);
  // Sync page state when it exceeds the new page count (lazy clamp — avoids extra render).
  // Calling setState during render is allowed in React when it is a correction (not a new mutation).
  // See: https://react.dev/reference/react/useState#storing-information-from-previous-renders
  if (clampedPage !== page && pageCount > 0) {
    setPage(clampedPage);
  }

  const offset = (clampedPage - 1) * pageSize;

  const goToPage = useCallback((p: number) => {
    setPage(() => {
      const pc = computePageCount(totalCount, pageSize);
      return clampPage(p, pc);
    });
  }, [totalCount, pageSize]);

  const prevPage = useCallback(() => {
    setPage((p) => Math.max(1, p - 1));
  }, []);

  const nextPage = useCallback(() => {
    setPage((p) => {
      const pc = computePageCount(totalCount, pageSize);
      return pc > 0 ? Math.min(p + 1, pc) : p;
    });
  }, [totalCount, pageSize]);

  const setPageSize = useCallback((ps: number) => {
    if (!VALID_PAGE_SIZES.has(ps)) return;
    setPageSizeState(ps);
    // Clamp current page to the new page count after resize.
    setPage((current) => {
      const newPageCount = computePageCount(totalCount, ps);
      return clampPage(current, newPageCount);
    });
  }, [totalCount]);

  const resetToFirstPage = useCallback(() => {
    setPage(1);
  }, []);

  const state: PaginationState = {
    page: clampedPage,
    pageSize,
    pageCount,
    offset,
    hasPrev: clampedPage > 1,
    hasNext: pageCount > 0 && clampedPage < pageCount,
  };

  const actions: PaginationActions = {
    goToPage,
    prevPage,
    nextPage,
    setPageSize,
    resetToFirstPage,
  };

  return [state, actions];
}
