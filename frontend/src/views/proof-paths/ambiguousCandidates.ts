// =============================================================================
// ambiguousCandidates.ts (PP-12c, Proof Paths)
//
// Pure parser for the candidate FQNs carried by a 409 AmbiguousSymbol error.
//
// The candidate list lives in ScopeApiError.details (PP-17), which is typed
// `unknown` because Scope.Server's emission shape is intentionally not assumed:
//
//   1. PIPE-DELIMITED STRING (today's shape):
//        "symbol=Foo.Bar; candidates=Ns.A.Bar|Ns.B.Bar|Ns.C.Bar"
//      The candidate FQNs are the `|`-joined segment after `candidates=`.
//      The leading `symbol=...;` prefix (the ambiguous query) is NOT a candidate.
//
//   2. STRUCTURED ARRAY (the documented future / JSON-stdout shape):
//        AmbiguousSymbolEntry[] = [{ fqn, kind, file? }, ...]
//      OR a bare string[] of FQNs.
//
//   3. STRUCTURED OBJECT with a `candidates` key:
//        { symbol: "Foo.Bar", candidates: ["Ns.A.Bar", ...] }
//        OR { candidates: [{ fqn: "Ns.A.Bar" }, ...] }
//
// This function handles ALL of the above defensively and returns a deduplicated,
// order-preserving array of candidate FQN strings. It never throws and returns
// [] when nothing usable is present (the caller then falls back to the coarse
// 409 copy).
// =============================================================================

/**
 * Extracts the candidate FQN strings from a 409 AmbiguousSymbol error's
 * `details` payload. Pure; never throws; returns [] when nothing is parseable.
 *
 * Handles, defensively, every observed/anticipated shape:
 *   - pipe-delimited string ("symbol=X; candidates=A|B|C")
 *   - bare string[]
 *   - AmbiguousSymbolEntry[] ({ fqn, kind, file? }[])
 *   - object with a `candidates` field (string[] | entry[])
 *
 * @param details - The `ScopeApiError.details` value (typed `unknown`).
 */
export function parseAmbiguousCandidates(details: unknown): string[] {
  const raw = collectRaw(details);
  // Deduplicate while preserving first-seen order.
  const seen = new Set<string>();
  const out: string[] = [];
  for (const c of raw) {
    const trimmed = c.trim();
    if (trimmed === '' || seen.has(trimmed)) continue;
    seen.add(trimmed);
    out.push(trimmed);
  }
  return out;
}

/** Collects candidate strings from any supported shape (pre-dedup). */
function collectRaw(details: unknown): string[] {
  if (details == null) return [];

  // Shape 1: pipe-delimited string.
  if (typeof details === 'string') {
    return parsePipeDelimited(details);
  }

  // Shape 2: bare array (string[] or entry[]).
  if (Array.isArray(details)) {
    return details.flatMap(coerceEntry);
  }

  // Shape 3: object with a `candidates` key (and possibly a `symbol` key we ignore).
  if (typeof details === 'object') {
    const obj = details as Record<string, unknown>;
    const candidates = obj.candidates;
    if (typeof candidates === 'string') {
      // candidates may itself be a pipe-delimited string.
      return splitPipes(candidates);
    }
    if (Array.isArray(candidates)) {
      return candidates.flatMap(coerceEntry);
    }
  }

  return [];
}

/**
 * Parses the pipe-delimited string form.
 *
 * The server emits a semicolon-separated key=value preamble where one key is
 * `candidates=A|B|C`. The `symbol=` value (the ambiguous query itself) is NOT a
 * candidate and is excluded. If no `candidates=` key is present (older/looser
 * formatting), fall back to treating the whole string's pipe segments as
 * candidates so we degrade gracefully rather than dropping everything.
 */
function parsePipeDelimited(s: string): string[] {
  // Find a `candidates=...` segment (split on `;` first, then look for the key).
  const segments = s.split(';');
  for (const seg of segments) {
    const eq = seg.indexOf('=');
    if (eq < 0) continue;
    const key = seg.slice(0, eq).trim().toLowerCase();
    if (key === 'candidates') {
      return splitPipes(seg.slice(eq + 1));
    }
  }
  // No explicit `candidates=` key. If the string contains pipes and no `symbol=`
  // preamble, treat the pipe segments as candidates. Otherwise nothing usable.
  if (s.includes('|') && !s.toLowerCase().includes('symbol=')) {
    return splitPipes(s);
  }
  return [];
}

/** Splits a `|`-joined value into trimmed, non-empty segments. */
function splitPipes(value: string): string[] {
  return value
    .split('|')
    .map((p) => p.trim())
    .filter((p) => p !== '');
}

/**
 * Coerces a single array element into candidate string(s).
 *   - string                  -> [string]
 *   - { fqn: string, ... }     -> [fqn]
 *   - { symbol_id: string }    -> [symbol_id] (defensive alt key)
 *   - anything else           -> []
 */
function coerceEntry(entry: unknown): string[] {
  if (typeof entry === 'string') return [entry];
  if (entry != null && typeof entry === 'object') {
    const obj = entry as Record<string, unknown>;
    const fqn = obj.fqn;
    if (typeof fqn === 'string') return [fqn];
    const symbolId = obj.symbol_id;
    if (typeof symbolId === 'string') return [symbolId];
  }
  return [];
}
