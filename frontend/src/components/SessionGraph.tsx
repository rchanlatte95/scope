import { useCallback, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { GraphNode, GraphEdge } from '../api/traceApi';
import styles from './SessionGraph.module.css';

// =============================================================================
// SessionGraph (owner feedback item 1).
//
// Bounded, zoomable, pannable session graph. Horizontal left-to-right tree with
// Bezier edges, mirroring the LOOK of the old HtmlTraceFormatter session graph (none
// of its code reused). The contract guarantees (verified live on session 5b4d9750):
//  - one root session node + span pseudo-nodes (kind="span") at depth 1, child-linked
//    to the root via link_source="span_enrichment". Multi-level nesting appears when
//    parent_span_id propagation lands; the layout already handles arbitrary depth.
//
// Item 1 specifics:
//  - BOUNDED viewport: the <svg> has a fixed CSS max-height/width and clips; the graph
//    NEVER grows the page. Content larger than the frame is reached via pan/zoom.
//  - zoom: wheel (cursor-anchored) + buttons, clamped [MIN_SCALE, MAX_SCALE].
//  - pan: drag anywhere on the canvas.
//  - "fit" resets the view to frame all nodes.
//
// Layout is computed purely from nodes+edges (depth -> column x, sibling order -> y),
// independent of array order, so it is stable across refreshes.
// =============================================================================

const NODE_W = 150;
const NODE_H = 44;
const COL_GAP = 90; // horizontal gap between depth columns
const ROW_GAP = 18; // vertical gap between sibling rows
const PAD = 28; // canvas padding around the laid-out content
const MIN_SCALE = 0.25;
const MAX_SCALE = 2.5;

interface LaidOutNode {
  node: GraphNode;
  x: number;
  y: number;
}

interface Layout {
  nodes: LaidOutNode[];
  edges: { from: LaidOutNode; to: LaidOutNode; linkSource: string }[];
  width: number;
  height: number;
}

/**
 * Pure tree layout: assigns each node an (x,y) from its depth (column) and a stable
 * in-column order. Roots are nodes with no incoming edge. A simple subtree-packing
 * pass stacks rows so siblings do not overlap. Independent of input array order.
 */
function layoutGraph(nodes: GraphNode[], edges: GraphEdge[]): Layout {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const children = new Map<string, string[]>();
  const hasParent = new Set<string>();
  for (const e of edges) {
    if (!byId.has(e.parent) || !byId.has(e.child)) continue;
    let parentChildren = children.get(e.parent);
    if (parentChildren === undefined) {
      parentChildren = [];
      children.set(e.parent, parentChildren);
    }
    parentChildren.push(e.child);
    hasParent.add(e.child);
  }
  // Deterministic child order: by display_id then id.
  for (const list of children.values()) {
    list.sort((a, b) => {
      const na = byId.get(a);
      const nb = byId.get(b);
      if (na === undefined || nb === undefined) return 0;
      return na.display_id.localeCompare(nb.display_id) || a.localeCompare(b);
    });
  }

  const roots = nodes
    .filter((n) => !hasParent.has(n.id))
    .sort((a, b) => a.depth - b.depth || a.display_id.localeCompare(b.display_id));

  const pos = new Map<string, { x: number; y: number }>();
  let cursorY = PAD;
  const visited = new Set<string>();

  // DFS that allocates one row per leaf and centers parents over their children.
  function place(id: string, depth: number): { top: number; bottom: number } {
    if (visited.has(id)) {
      const p = pos.get(id);
      if (p === undefined) return { top: 0, bottom: NODE_H };
      return { top: p.y, bottom: p.y + NODE_H };
    }
    visited.add(id);
    const x = PAD + depth * (NODE_W + COL_GAP);
    const kids = (children.get(id) ?? []).filter((k) => !visited.has(k));
    if (kids.length === 0) {
      const y = cursorY;
      cursorY += NODE_H + ROW_GAP;
      pos.set(id, { x, y });
      return { top: y, bottom: y + NODE_H };
    }
    let top = Infinity;
    let bottom = -Infinity;
    for (const k of kids) {
      const span = place(k, depth + 1);
      top = Math.min(top, span.top);
      bottom = Math.max(bottom, span.bottom);
    }
    const y = (top + bottom) / 2 - NODE_H / 2;
    pos.set(id, { x, y });
    return { top: Math.min(top, y), bottom: Math.max(bottom, y + NODE_H) };
  }

  for (const r of roots) place(r.id, r.depth);
  // Any node not reached (orphan with no edges) gets appended in a trailing column.
  for (const n of nodes) {
    if (!pos.has(n.id)) {
      pos.set(n.id, { x: PAD, y: cursorY });
      cursorY += NODE_H + ROW_GAP;
    }
  }

  const laid: LaidOutNode[] = nodes.map((n) => {
    const p = pos.get(n.id) ?? { x: PAD, y: 0 };
    return { node: n, x: p.x, y: p.y };
  });
  const laidById = new Map(laid.map((l) => [l.node.id, l]));
  const laidEdges = edges
    .filter((e) => laidById.has(e.parent) && laidById.has(e.child))
    .map((e) => {
      const from = laidById.get(e.parent);
      const to = laidById.get(e.child);
      // Guaranteed by the filter above.
      if (from === undefined || to === undefined) return null;
      return { from, to, linkSource: e.link_source };
    })
    .filter((e): e is NonNullable<typeof e> => e !== null);

  let width = 0;
  let height = 0;
  for (const l of laid) {
    width = Math.max(width, l.x + NODE_W + PAD);
    height = Math.max(height, l.y + NODE_H + PAD);
  }
  return { nodes: laid, edges: laidEdges, width: Math.max(width, 200), height: Math.max(height, 120) };
}

/** Horizontal Bezier path from the right edge of `from` to the left edge of `to`. */
function bezierPath(from: LaidOutNode, to: LaidOutNode): string {
  const x1 = from.x + NODE_W;
  const y1 = from.y + NODE_H / 2;
  const x2 = to.x;
  const y2 = to.y + NODE_H / 2;
  const mx = (x1 + x2) / 2;
  return `M ${String(x1)} ${String(y1)} C ${String(mx)} ${String(y1)}, ${String(mx)} ${String(y2)}, ${String(x2)} ${String(y2)}`;
}

interface SessionGraphProps {
  nodes: GraphNode[];
  edges: GraphEdge[];
  /** Highlighted node id (e.g. from the id resolver). */
  highlightId?: string | null;
  /** Click handler so the parent can scroll the trace to the node. */
  onSelect?: (node: GraphNode) => void;
}

export default function SessionGraph({ nodes, edges, highlightId, onSelect }: SessionGraphProps) {
  const layout = useMemo(() => layoutGraph(nodes, edges), [nodes, edges]);
  const [scale, setScale] = useState(1);
  const [tx, setTx] = useState(0);
  const [ty, setTy] = useState(0);
  const drag = useRef<{ x: number; y: number; tx: number; ty: number } | null>(null);
  const frameRef = useRef<HTMLDivElement | null>(null);

  const clampScale = (s: number) => Math.min(MAX_SCALE, Math.max(MIN_SCALE, s));

  const fit = useCallback(() => {
    const frame = frameRef.current;
    if (!frame) return;
    const fw = frame.clientWidth;
    const fh = frame.clientHeight;
    const s = clampScale(Math.min(fw / layout.width, fh / layout.height) * 0.95);
    setScale(s);
    setTx((fw - layout.width * s) / 2);
    setTy((fh - layout.height * s) / 2);
  }, [layout.width, layout.height]);

  // Fit to frame whenever the laid-out graph changes (initial mount + session switch),
  // so a tall hook-session graph (one root + many spans) starts fully framed rather
  // than scrolled to a corner. The user can then zoom/pan freely.
  useLayoutEffect(() => {
    fit();
  }, [fit]);

  const onWheel = useCallback(
    (e: React.WheelEvent) => {
      e.preventDefault();
      const frame = frameRef.current;
      if (!frame) return;
      const rect = frame.getBoundingClientRect();
      const cx = e.clientX - rect.left;
      const cy = e.clientY - rect.top;
      const factor = e.deltaY < 0 ? 1.1 : 1 / 1.1;
      const next = clampScale(scale * factor);
      // Anchor zoom at the cursor.
      setTx(cx - ((cx - tx) * next) / scale);
      setTy(cy - ((cy - ty) * next) / scale);
      setScale(next);
    },
    [scale, tx, ty],
  );

  const onMouseDown = useCallback(
    (e: React.MouseEvent) => {
      drag.current = { x: e.clientX, y: e.clientY, tx, ty };
    },
    [tx, ty],
  );
  const onMouseMove = useCallback((e: React.MouseEvent) => {
    if (!drag.current) return;
    setTx(drag.current.tx + (e.clientX - drag.current.x));
    setTy(drag.current.ty + (e.clientY - drag.current.y));
  }, []);
  const endDrag = useCallback(() => {
    drag.current = null;
  }, []);

  return (
    <div className={styles.container}>
      <div className={styles.controls}>
        <button type="button" onClick={() => { setScale((s) => clampScale(s * 1.2)); }} title="Zoom in">
          +
        </button>
        <button type="button" onClick={() => { setScale((s) => clampScale(s / 1.2)); }} title="Zoom out">
          −
        </button>
        <button type="button" onClick={fit} title="Fit to view">
          fit
        </button>
        <span className={styles.zoomReadout}>{Math.round(scale * 100)}%</span>
      </div>
      <div
        ref={frameRef}
        className={styles.frame}
        onWheel={onWheel}
        onMouseDown={onMouseDown}
        onMouseMove={onMouseMove}
        onMouseUp={endDrag}
        onMouseLeave={endDrag}
      >
        <svg
          className={styles.canvas}
          width={layout.width}
          height={layout.height}
          style={{ transform: `translate(${String(tx)}px, ${String(ty)}px) scale(${String(scale)})`, transformOrigin: '0 0' }}
        >
          <g className={styles.edges}>
            {layout.edges.map((e, i) => (
              <path
                key={i}
                d={bezierPath(e.from, e.to)}
                className={styles.edge}
                data-link-source={e.linkSource}
              />
            ))}
          </g>
          <g>
            {layout.nodes.map((l) => {
              const isSpan = l.node.kind === 'span';
              const selected = highlightId === l.node.id;
              return (
                <g
                  key={l.node.id}
                  className={`${styles.node ?? ''} ${isSpan ? (styles.span ?? '') : (styles.session ?? '')} ${selected ? (styles.selected ?? '') : ''}`}
                  transform={`translate(${String(l.x)}, ${String(l.y)})`}
                  onClick={() => onSelect?.(l.node)}
                >
                  <rect width={NODE_W} height={NODE_H} rx={7} />
                  <text className={styles.nodeAlias} x={9} y={17}>
                    {isSpan ? `#${l.node.alias ?? l.node.display_id}` : l.node.display_id}
                  </text>
                  <text className={styles.nodeLabel} x={9} y={33}>
                    {truncate(l.node.label ?? l.node.agent_type ?? (isSpan ? 'agent span' : 'session'), 22)}
                  </text>
                </g>
              );
            })}
          </g>
        </svg>
      </div>
    </div>
  );
}

function truncate(s: string, max: number): string {
  return s.length <= max ? s : s.slice(0, max - 1) + '…';
}
