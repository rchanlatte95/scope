import { useMemo, useState } from 'react';
import styles from './Snippet.module.css';

// =============================================================================
// Snippet (owner feedback item 5).
//
// Edit/command input/output snippets must be EXPANDABLE, not permanently truncated.
// Three states: collapsed (a few lines), "show more" (a larger bounded window), and
// "show all" (the full text). The old renderer hard-truncated, hiding the rest of a
// command or diff. Here the full text is always present in the DOM payload (it is the
// local-full snippet the server already sent); we only gate how much is rendered.
//
// `text` is the raw snippet (often JSON-ish tool input/output). We do not pretty-print
// it (that would risk misrepresenting content); we render it verbatim in a <pre>.
// =============================================================================

const COLLAPSED_LINES = 4;
const MORE_LINES = 20;

interface SnippetProps {
  label: string;
  text: string;
}

export default function Snippet({ label, text }: SnippetProps) {
  const [level, setLevel] = useState<'collapsed' | 'more' | 'all'>('collapsed');
  const lines = useMemo(() => text.split('\n'), [text]);
  const total = lines.length;

  const shown =
    level === 'all' ? lines : level === 'more' ? lines.slice(0, MORE_LINES) : lines.slice(0, COLLAPSED_LINES);
  const hiddenCount = total - shown.length;

  return (
    <div className={styles.snippet}>
      <div className={styles.head}>
        <span className={styles.label}>{label}</span>
        <span className={styles.meta}>
          {total} line{total === 1 ? '' : 's'}
        </span>
      </div>
      <pre className={styles.body}>{shown.join('\n')}</pre>
      {hiddenCount > 0 && (
        <div className={styles.actions}>
          {level === 'collapsed' && total > COLLAPSED_LINES && (
            <button type="button" onClick={() => { setLevel('more'); }}>
              show more ({Math.min(MORE_LINES, total) - COLLAPSED_LINES} more lines)
            </button>
          )}
          {level !== 'all' && (
            <button type="button" onClick={() => { setLevel('all'); }}>
              show all ({total} lines)
            </button>
          )}
        </div>
      )}
      {level !== 'collapsed' && (
        <div className={styles.actions}>
          <button type="button" onClick={() => { setLevel('collapsed'); }}>
            collapse
          </button>
        </div>
      )}
    </div>
  );
}
