// =============================================================================
// TraceToolType bucket definitions and 16->9 taxonomy collapse (FE-1).
//
// Decision taxonomy-collapse (owner, 2026-06-13, signed off):
//   shell.exec          -> shell
//   search.content      -> find
//   search.files        -> find
//   file.write          -> edit
//   file.edit           -> edit
//   file.read           -> system
//   fs.list             -> system
//   web.fetch           -> external
//   web.search          -> external
//   mcp.other           -> external
//   agent.message       -> model
//   agent.dispatch      -> model
//   user.question       -> operator   (first-class human-in-the-loop bucket)
//   code.intel          -> find       (Scope retrieval; no pack subdivision from SPA)
//   task.manage         -> system
//   skill.invoke        -> system
//   other (fallback)    -> system
//   <unknown id>        -> system     (mandatory safe fallback, never an error)
//
// The 9 TraceToolType buckets correspond 1:1 to --scope-trace-tool-* CSS tokens.
// Only ToolTypeMark, TraceLegend, TraceGraph, and TraceTimeline may consume those tokens.
//
// RESERVED operator glyph (frozen in FE-0b): person-raise-hand
// =============================================================================

/** Closed union for the 9 trace tool type buckets. */
export type TraceToolType =
  | 'find'
  | 'pack'
  | 'hook'
  | 'edit'
  | 'model'
  | 'shell'
  | 'external'
  | 'operator'
  | 'system';

/** All 9 TraceToolType bucket values in display order. */
export const TRACE_TOOL_TYPES: readonly TraceToolType[] = [
  'find',
  'pack',
  'hook',
  'edit',
  'model',
  'shell',
  'external',
  'operator',
  'system',
] as const;

/** Human-readable label for each TraceToolType bucket. */
export const TOOL_TYPE_LABEL: Readonly<Record<TraceToolType, string>> = {
  find:     'Find',
  pack:     'Pack',
  hook:     'Hook',
  edit:     'Edit',
  model:    'Model',
  shell:    'Shell',
  external: 'External',
  operator: 'Operator',
  system:   'System',
};

/**
 * Glyph/icon name for each TraceToolType bucket.
 * operator uses the RESERVED glyph 'person-raise-hand' (frozen by FE-0b).
 * All other glyphs are semantic action icons.
 */
export const TOOL_TYPE_GLYPH: Readonly<Record<TraceToolType, string>> = {
  find:     'magnifying-glass',
  pack:     'archive-box',
  hook:     'bolt',
  edit:     'pencil-square',
  model:    'cpu-chip',
  shell:    'command-line',
  external: 'arrow-top-right-on-square',
  operator: 'person-raise-hand',  // FROZEN: FE-0b reserved glyph
  system:   'cog-6-tooth',
};

/**
 * The --scope-trace-tool-* CSS custom property name for each bucket.
 * Used in inline styles on card surfaces only (constraint: 3:1 contrast
 * against card surface, not bg/surface-2).
 */
export const TOOL_TYPE_TOKEN: Readonly<Record<TraceToolType, string>> = {
  find:     '--scope-trace-tool-find',
  pack:     '--scope-trace-tool-pack',
  hook:     '--scope-trace-tool-hook',
  edit:     '--scope-trace-tool-edit',
  model:    '--scope-trace-tool-model',
  shell:    '--scope-trace-tool-shell',
  external: '--scope-trace-tool-external',
  operator: '--scope-trace-tool-operator',
  system:   '--scope-trace-tool-system',
};

/**
 * 16->9 taxonomy collapse map.
 * Signed off by owner 2026-06-13. Maps every tool-taxonomy-v1 id to its
 * TraceToolType bucket. Unknown ids always fall back to 'system'.
 */
const TAXONOMY_COLLAPSE: Readonly<Record<string, TraceToolType>> = {
  'shell.exec':       'shell',
  'search.content':   'find',
  'search.files':     'find',
  'file.write':       'edit',
  'file.edit':        'edit',
  'file.read':        'system',
  'fs.list':          'system',
  'web.fetch':        'external',
  'web.search':       'external',
  'mcp.other':        'external',
  'agent.message':    'model',
  'agent.dispatch':   'model',
  'user.question':    'operator',
  'code.intel':       'find',
  'task.manage':      'system',
  'skill.invoke':     'system',
  'other':            'system',
};

/**
 * Collapses a tool-taxonomy-v1 id (from ToolRef.taxonomy_id) to a TraceToolType bucket.
 * Unknown ids (future categories, unknown versions) return 'system' as the safe fallback.
 * Never throws.
 */
export function collapseToToolType(taxonomyId: string | null | undefined): TraceToolType {
  if (taxonomyId == null || taxonomyId === '') return 'system';
  return TAXONOMY_COLLAPSE[taxonomyId] ?? 'system';
}

/** Expose the collapse table for exhaustive test coverage. */
export { TAXONOMY_COLLAPSE };
