import { useCallback, useRef, useState } from 'react';
import styles from './CopyChip.module.css';

// =============================================================================
// CopyChip (owner feedback items 2 + 3).
//
// A monospace, click-to-copy chip used for the BASE36 display_id (item 2) and the
// {agentOrdinal}/{agentOrdinal}.{seq} alias (item 3). Both are surfaced on every node
// so a user can read one out ("look at node 3.7", "node 1F9XCBI") and the resolver
// (lib/displayId.ts) finds it. Clicking copies the raw value to the clipboard.
//
// `kind` only changes the chip's visual accent (alias vs id). `value` is the literal
// copied/displayed string; `label` is an optional aria/title prefix.
// =============================================================================

interface CopyChipProps {
  value: string;
  kind?: 'id' | 'alias';
  label?: string;
}

export default function CopyChip({ value, kind = 'id', label }: CopyChipProps) {
  const [copied, setCopied] = useState(false);
  const timer = useRef<number | null>(null);

  const onClick = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      if (timer.current !== null) window.clearTimeout(timer.current);
      timer.current = window.setTimeout(() => { setCopied(false); }, 1200);
    } catch {
      // Clipboard may be unavailable (insecure context); fail quietly, the value is
      // still selectable/readable on screen.
    }
  }, [value]);

  return (
    <button
      type="button"
      className={`${styles.chip ?? ''} ${kind === 'alias' ? (styles.alias ?? '') : (styles.id ?? '')}`}
      onClick={() => { void onClick(); }}
      title={`${label != null && label !== '' ? label + ' ' : ''}${value} (click to copy)`}
      aria-label={`Copy ${label ?? kind} ${value}`}
    >
      <span className={styles.value}>{value}</span>
      <span className={styles.hint}>{copied ? 'copied' : 'copy'}</span>
    </button>
  );
}
