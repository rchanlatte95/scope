// =============================================================================
// tool-taxonomy-v1 registry, consumer side (owner feedback item 4/4a/4b).
//
// SINGLE SOURCE of category -> {label, cssVarRef} for the SPA. The SERVER owns
// the raw tool-name -> taxonomy_id resolution (Scope.Core.Telemetry.Trace.ToolTaxonomy)
// and ships the resolved taxonomy_id + taxonomy_version on every event's `tool` block.
// This file is the rendering half the contract assigns to ST-14: the human label and
// the --scope-trace-tool-* CSS variable reference for each id.
//
// MIGRATION (FE-4a): Colors are now returned as CSS var() references pointing to
// --scope-trace-tool-* tokens, NOT raw hex. The 16->9 bucket collapse (decision
// taxonomy-collapse, owner 2026-06-13) is applied here so each of the 16 source
// taxonomy ids maps to one of the 9 canonical TraceToolType var refs.
//
// SANCTIONED consumers of --scope-trace-tool-* tokens:
//   SessionGraph (TraceTimeline role), ToolTypeMark, TraceLegend. taxonomy.ts
//   returns var refs that those components apply. No raw hex here.
//
// 16->9 collapse mapping (signed off):
//   shell.exec                -> shell     var(--scope-trace-tool-shell)
//   search.content            -> find      var(--scope-trace-tool-find)
//   search.files              -> find      var(--scope-trace-tool-find)
//   file.write + file.edit    -> edit      var(--scope-trace-tool-edit)
//   file.read + fs.list       -> system    var(--scope-trace-tool-system)
//   web.fetch + web.search +
//   mcp.other                 -> external  var(--scope-trace-tool-external)
//   agent.message +
//   agent.dispatch            -> model     var(--scope-trace-tool-model)
//   user.question             -> operator  var(--scope-trace-tool-operator)
//   code.intel                -> find      var(--scope-trace-tool-find)
//   task.manage + skill.invoke
//   + other                   -> system    var(--scope-trace-tool-system)
//
// hook and pack have no SPA taxonomy id (emitted by server-side HtmlTraceFormatter
// which knows specific ops). Unknown id -> system (mandatory safe fallback).
// =============================================================================

/** The registry version this table renders. Must match the server's taxonomy_version. */
export const TAXONOMY_VERSION = 'tool-taxonomy-v1';

/** A rendered taxonomy category: stable id, human label, CSS var ref for the accent color. */
export interface TaxonomyCategory {
  id: string;
  label: string;
  /**
   * CSS custom property reference: `var(--scope-trace-tool-X)`.
   * Use this in inline styles on card-surface elements only (FE-0b constraint: 3:1
   * contrast against the card surface). Do NOT use in CSS modules.
   */
  color: string;
}

/** Mandatory fallback id for any unmapped/unknown category (contract: never an error). */
export const FALLBACK_TAXONOMY_ID = 'other';

// CSS var references for the 9 TraceToolType buckets (--scope-trace-tool-*).
// These are passed through to consumers as taxonomy category colors.
// Only ToolTypeMark, TraceLegend, SessionGraph (TraceTimeline), and taxonomy.ts
// should ever reference these var names.
const VAR_FIND     = 'var(--scope-trace-tool-find)';
const VAR_EDIT     = 'var(--scope-trace-tool-edit)';
const VAR_SHELL    = 'var(--scope-trace-tool-shell)';
const VAR_EXTERNAL = 'var(--scope-trace-tool-external)';
const VAR_MODEL    = 'var(--scope-trace-tool-model)';
const VAR_OPERATOR = 'var(--scope-trace-tool-operator)';
const VAR_SYSTEM   = 'var(--scope-trace-tool-system)';

/**
 * The registry, keyed by taxonomy id. Order mirrors the contract appendix.
 *
 * Color values are CSS var() references, NOT raw hex. They resolve to
 * --scope-trace-tool-* tokens at paint time, honoring both light and dark themes.
 */
export const TAXONOMY_REGISTRY: Readonly<Record<string, TaxonomyCategory>> = {
  // 16 canonical source ids + 'other' fallback, collapsed onto 9 buckets:
  'shell.exec':    { id: 'shell.exec',    label: 'Shell',             color: VAR_SHELL    },
  'search.content':{ id: 'search.content',label: 'Content search',    color: VAR_FIND     },
  'search.files':  { id: 'search.files',  label: 'File search',       color: VAR_FIND     },
  'fs.list':       { id: 'fs.list',       label: 'Directory listing',  color: VAR_SYSTEM   },
  'file.read':     { id: 'file.read',     label: 'File read',          color: VAR_SYSTEM   },
  'file.write':    { id: 'file.write',    label: 'File write',         color: VAR_EDIT     },
  'file.edit':     { id: 'file.edit',     label: 'File edit',          color: VAR_EDIT     },
  'web.fetch':     { id: 'web.fetch',     label: 'Web fetch',          color: VAR_EXTERNAL },
  'web.search':    { id: 'web.search',    label: 'Web search',         color: VAR_EXTERNAL },
  'agent.dispatch':{ id: 'agent.dispatch',label: 'Agent dispatch',     color: VAR_MODEL    },
  'agent.message': { id: 'agent.message', label: 'Agent message',      color: VAR_MODEL    },
  'user.question': { id: 'user.question', label: 'Operator',           color: VAR_OPERATOR },
  'code.intel':    { id: 'code.intel',    label: 'Code intelligence',  color: VAR_FIND     },
  'task.manage':   { id: 'task.manage',   label: 'Task management',    color: VAR_SYSTEM   },
  'skill.invoke':  { id: 'skill.invoke',  label: 'Skill',              color: VAR_SYSTEM   },
  'mcp.other':     { id: 'mcp.other',     label: 'Other MCP',          color: VAR_EXTERNAL },
  other:           { id: FALLBACK_TAXONOMY_ID, label: 'Other',         color: VAR_SYSTEM   },
};

/**
 * Resolves a taxonomy id to its render category. An unknown id (unmapped, or from a
 * newer registry version) falls back to "other" rather than erroring (contract:
 * "unknown open-enum value is treated as the documented fallback"). A null/empty id
 * also resolves to the fallback.
 */
// FALLBACK is always present in TAXONOMY_REGISTRY (defined in the literal above).
// eslint-disable-next-line @typescript-eslint/no-non-null-assertion -- FALLBACK_TAXONOMY_ID is always in TAXONOMY_REGISTRY literal above
const FALLBACK_CATEGORY: TaxonomyCategory = TAXONOMY_REGISTRY[FALLBACK_TAXONOMY_ID]!;

export function taxonomyFor(taxonomyId: string | null | undefined): TaxonomyCategory {
  if (taxonomyId == null || taxonomyId === '') return FALLBACK_CATEGORY;
  return TAXONOMY_REGISTRY[taxonomyId] ?? FALLBACK_CATEGORY;
}
