// =============================================================================
// proofPathChainHelpers.ts (PP-07)
//
// Pure helper functions for ProofPathChain.
//
// Separated from the TSX file so that:
//   1. react-refresh/only-export-components passes (no mixed exports).
//   2. Vitest can test the pure logic without importing React.
//
// No React, no DOM, no I/O. All exports are pure functions.
// =============================================================================

/**
 * Returns the row's fully-qualified symbol name (symbol_id) as the primary
 * copyable ID for the proof-path chain.
 *
 * The displayed chain uses short names (server .Name output), but the copied
 * ID is the full FQN so the artifact is unambiguous (OQ-4, EDGE-HON-1).
 *
 * Pure function: same input always returns the same output. Never generates
 * a random ID. No crypto.randomUUID, no Math.random.
 */
export function proofPathId(row: { symbol_id: string }): string {
  return row.symbol_id;
}

/**
 * Deterministic hash of a proof-path chain, derived from
 * proofPath.join('->').
 *
 * Implementation: djb2 variant (32-bit unsigned, wrapped via unsigned right-
 * shift). Pure function: same input always produces the same output.
 * NO crypto.randomUUID, NO Math.random. (EDGE-HON-1.)
 *
 * Returns an 8-character hex string (32-bit unsigned integer padded to 8 hex
 * digits) for readability.
 *
 * Usage: secondary display identifier alongside the primary proofPathId. The
 * hash encodes the shape of the proof path, not the anchor symbol itself.
 */
export function chainHash(proofPath: string[]): string {
  const str = proofPath.join('->');
  // djb2 hash: seed 5381, multiply-add per char code.
  // Math.imul provides fast 32-bit multiplication without overflow.
  let h = 5381;
  for (let i = 0; i < str.length; i++) {
    h = (Math.imul(h, 33) + (str.charCodeAt(i) | 0)) | 0;
  }
  // Convert to unsigned 32-bit integer, format as 8-char hex.
  return (h >>> 0).toString(16).padStart(8, '0');
}
