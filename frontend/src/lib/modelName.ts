// =============================================================================
// modelName.ts — friendly display names for model IDs (T-2026-06-15-02).
//
// Pure helper: no DOM, no side effects. Maps a raw model id string to a
// short display name suitable for UI labels (e.g. "Sonnet 4.6").
// Falls back to the raw id for any unknown model — never throws, never blank.
//
// Used by CostProvenanceChip to render the estimation basis label:
//   recovered=false -> "Scope estimate - assumed {FriendlyModel} - prices as of {date}"
//   recovered=true  -> "Provider estimate - {FriendlyModel} - prices as of {date}"
// =============================================================================

/** Known model id -> display name mapping. */
const MODEL_DISPLAY_NAMES: Readonly<Record<string, string>> = {
  'claude-sonnet-4-6': 'Sonnet 4.6',
  'claude-opus-4-8':   'Opus 4.8',
  'claude-opus-4-7':   'Opus 4.7',
  'claude-opus-4-6':   'Opus 4.6',
  'claude-haiku-4-5':  'Haiku 4.5',
  'claude-fable-5':    'Fable 5',
};

/**
 * Returns a short display name for a model id.
 *
 * - Known ids map to friendly names (e.g. "claude-sonnet-4-6" -> "Sonnet 4.6").
 * - Unknown ids return the raw id unchanged (never throws, never returns blank).
 * - Null/undefined/empty input returns the empty string (safe sentinel for callers
 *   that guard before calling, but callers should not pass null).
 */
export function friendlyModelName(modelId: string | null | undefined): string {
  if (modelId == null || modelId === '') return '';
  return MODEL_DISPLAY_NAMES[modelId] ?? modelId;
}
