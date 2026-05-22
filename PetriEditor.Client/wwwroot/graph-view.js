// ── Canvas pan/zoom renderer for the reachability/coverability graph view.
//    Layered layout (M0 at top) provided by the C# side.  Forward edges are
//    drawn as down-going beziers; back-edges curve around to the right.
window.graphView = (() => {
    const _state = {};

    function _nodeColors(n) {
        const isInit  = !!n.isInit;
        const isDead  = !!n.isDead;
        const isOmega = !!n.isOmega;
        const isCut   = !!n.isCutOff;
        const fill   = isInit  ? '#e8faf8' : isDead ? '#ffebee' : isOmega ? '#f0f0fb'
                    : isCut    ? '#f3e5f5' : '#ffffff';
        const stroke = isInit  ? '#00a499' : isDead ? '#e53935' : isOmega ? '#5c6bc0'
                    : isCut    ? '#8e24aa' : '#9dafc0';
        const textC  = isInit  ? '#00695c' : isDead ? '#b71c1c' : isOmega ? '#283593'
                    : isCut    ? '#6a1b9a' : '#111827';
        const sw     = isInit  ? 2.5 : 1.5;
        return { fill, stroke, textC, sw };
    }

    // Back-edge routing.  Two cases:
    //   same layer  — short horizontal arrow between the facing sides of the nodes.
    //   upward     — bow around whichever side of the source has more clearance
    //                 (the side the target is on).
    function _backEdgeControlPoints(e, nodeW, nodeH) {
        // Same-layer: draw a shallow curved arrow between adjacent sides.
        if (e.fromLayer === e.toLayer) {
            const sourceLeft = e.cx1 < e.cx2;
            // Attach points: right side of the left node → left side of the right node.
            const sx1 = sourceLeft ? e.cx1 + nodeW : e.cx1;
            const sx2 = sourceLeft ? e.cx2        : e.cx2 + nodeW;
            const sy1 = e.cy1 + nodeH * 0.5;
            const sy2 = e.cy2 + nodeH * 0.5;
            // Small arc so the edge is visibly curved (helps if there are two parallel edges).
            const dip = Math.min(Math.abs(sx2 - sx1) * 0.25, nodeH * 1.2);
            const c1x = sx1 + (sourceLeft ? dip : -dip);
            const c2x = sx2 + (sourceLeft ? -dip : dip);
            const c1y = sy1 + dip * 0.3;
            const c2y = sy2 + dip * 0.3;
            return { sx1, sy1, sx2: sx2, sy2: sy2, c1x, c1y, c2x, c2y };
        }

        // Upward back-edge: route through the lane between the source's row
        // and the row above it. Source exits its top, dips up into the lane,
        // travels horizontally toward the target column, then enters the
        // target from below. Per-edge `bowOffset` staggers the lane height so
        // many parallel back-edges don't sit on the same line.
        const sx1 = e.cx1 + nodeW * 0.5;
        const sx2 = e.cx2 + nodeW * 0.5;
        const sy1 = e.cy1;                    // source top
        const sy2 = e.cy2 + nodeH;            // target bottom
        const offset = e.bowOffset || 0;
        const laneY = sy1 - nodeH * 0.4 - offset * (nodeH * 0.45);
        const c1x = sx1, c1y = laneY;
        const c2x = sx2, c2y = laneY;
        return { sx1, sy1, sx2, sy2, c1x, c1y, c2x, c2y };
    }

    function _selfLoopPath(e, nodeW, nodeH) {
        // Loop on the right side of the node
        const nx = e.cx1, ny = e.cy1;
        const sx = nx + nodeW, sy = ny + nodeH * 0.35;
        const ex = nx + nodeW, ey = ny + nodeH * 0.65;
        const r  = nodeH * 1.2;
        const c1x = sx + r, c1y = sy - r * 0.4;
        const c2x = ex + r, c2y = ey + r * 0.4;
        return { sx, sy, ex, ey, c1x, c1y, c2x, c2y };
    }

    function _drawArrow(ctx, ax2, ay2, tdx, tdy, scale, color) {
        const len = Math.sqrt(tdx * tdx + tdy * tdy) || 1;
        const ux = tdx / len, uy = tdy / len;
        const nx = -uy, ny = ux;
        const asz = Math.max(5, 7 * scale);
        ctx.beginPath();
        ctx.moveTo(ax2, ay2);
        ctx.lineTo(ax2 - ux * asz + nx * asz * 0.4, ay2 - uy * asz + ny * asz * 0.4);
        ctx.lineTo(ax2 - ux * asz - nx * asz * 0.4, ay2 - uy * asz - ny * asz * 0.4);
        ctx.closePath();
        ctx.fillStyle = color;
        ctx.fill();
    }

    function _draw(s) {
        const canvas = s.canvas;
        const ctx    = s.ctx;
        const dpr    = s.dpr;
        const cssW   = canvas.width  / dpr;
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        const scale = cssW / s.vw;
        const ox    = -s.vx * scale;
        const oy    = -s.vy * scale;
        const nodeW = s.nodeW;
        const nodeH = s.nodeH;

        ctx.save();
        ctx.scale(dpr, dpr);

        const showLabels   = nodeW * scale >= 24;
        const showEdgeLbls = nodeW * scale >= 24;

        // Viewport bounds in world coords — used to cull off-screen edges/nodes.
        const margin = nodeW * 2;
        const wx0 = s.vx - margin, wy0 = s.vy - margin;
        const wx1 = s.vx + s.vw + margin, wy1 = s.vy + s.vh + margin;

        // ── Edges ─────────────────────────────────────────────────────────
        // Two passes:
        //   1. Stroke all edges + arrows (so curves never cover labels).
        //   2. Draw labels sorted so default → neighbour → hovered, with the
        //      hovered label drawn last so it stays on top of any neighbour
        //      label it might overlap.
        const edges = s.edges;
        const hoverIdx = s.hoveredEdgeIdx;
        const parentEdges = s.parentEdges;
        const childEdges  = s.childEdges;
        const anyHover = !!s.hoveredMarking;   // dim non-neighbour edges while hovering
        const pendingLabels = [];   // populated during the stroke pass, drawn after
        for (let i = 0; i < edges.length; i++) {
            const e = edges[i];
            // Cull: skip edges whose bounding box lies fully outside viewport.
            const eMinX = Math.min(e.x1, e.x2) - nodeW;
            const eMaxX = Math.max(e.x1, e.x2) + nodeW * 2; // extra for back-edge bow
            const eMinY = Math.min(e.y1, e.y2);
            const eMaxY = Math.max(e.y1, e.y2);
            if (eMaxX < wx0 || eMinX > wx1 || eMaxY < wy0 || eMinY > wy1) continue;

            const isHovered = i === hoverIdx;
            const isParent  = parentEdges.has(i);  // INCOMING edge — comes from a parent marking
            const isChild   = childEdges.has(i);   // OUTGOING edge — goes to a child marking
            const isNeighbor = isParent || isChild;
            // Five-tier styling:
            //   hovered  → strong teal (focus edge)
            //   parent   → green (incoming, "where flow comes from")
            //   child    → orange (outgoing, "where flow goes")
            //   dim      → faded grey
            //   default  → normal slate
            const accent = isHovered || isNeighbor;
            const dim    = anyHover && !accent;
            const baseColor = isHovered ? '#00897b'
                            : isParent  ? '#059669'   // emerald
                            : isChild   ? '#ea580c'   // burnt orange
                            : dim ? '#dfe5ec' : '#9dafc0';
            const forwardColor = baseColor;
            const backColor    = baseColor;
            const hoverWidth   = isHovered ? Math.max(2.5, 3 * scale) : 0;

            // Tier passed to _drawLabel.
            //   3 = hovered (top z-order)
            //   2 = parent (green)
            //   1 = child  (orange)
            //  -1 = dimmed
            //   0 = default
            const labelTier = isHovered ? 3
                            : isParent  ? 2
                            : isChild   ? 1
                            : dim ? -1 : 0;

            if (e.isSelf) {
                const p = _selfLoopPath(e, nodeW, nodeH);
                const asx = ox + p.sx * scale, asy = oy + p.sy * scale;
                const aex = ox + p.ex * scale, aey = oy + p.ey * scale;
                const ac1x = ox + p.c1x * scale, ac1y = oy + p.c1y * scale;
                const ac2x = ox + p.c2x * scale, ac2y = oy + p.c2y * scale;
                ctx.beginPath();
                ctx.moveTo(asx, asy);
                ctx.bezierCurveTo(ac1x, ac1y, ac2x, ac2y, aex, aey);
                ctx.strokeStyle = backColor;
                ctx.lineWidth   = isHovered ? hoverWidth : isNeighbor ? Math.max(2, 2 * scale) : Math.max(1, 1.5 * scale);
                ctx.stroke();
                // Arrowhead tangent from (c2 → e)
                _drawArrow(ctx, aex, aey, aex - ac2x, aey - ac2y, scale, backColor);
                // Label — at t=0.75 on the actual bezier curve so it always sits on the line.
                if (e.label && showEdgeLbls) {
                    const pt = _bezierAt(0.75, asx, asy, ac1x, ac1y, ac2x, ac2y, aex, aey);
                    pendingLabels.push({ text: e.label, x: pt.x, y: pt.y, tier: labelTier });
                }
                continue;
            }

            if (e.isBack) {
                const p = _backEdgeControlPoints(e, nodeW, nodeH, s.svgW);
                const asx = ox + p.sx1 * scale, asy = oy + p.sy1 * scale;
                const aex = ox + p.sx2 * scale, aey = oy + p.sy2 * scale;
                const ac1x = ox + p.c1x * scale, ac1y = oy + p.c1y * scale;
                const ac2x = ox + p.c2x * scale, ac2y = oy + p.c2y * scale;
                ctx.beginPath();
                ctx.moveTo(asx, asy);
                ctx.bezierCurveTo(ac1x, ac1y, ac2x, ac2y, aex, aey);
                ctx.strokeStyle = backColor;
                ctx.lineWidth   = isHovered ? hoverWidth : isNeighbor ? Math.max(2, 2 * scale) : Math.max(1, 1.5 * scale);
                ctx.stroke();
                _drawArrow(ctx, aex, aey, aex - ac2x, aey - ac2y, scale, backColor);
                if (e.label && showEdgeLbls) {
                    // Label at t=0.75 along the bezier — closer to the target marking
                    // and guaranteed to sit on the line.
                    const pt = _bezierAt(0.75, asx, asy, ac1x, ac1y, ac2x, ac2y, aex, aey);
                    pendingLabels.push({ text: e.label, x: pt.x, y: pt.y, tier: labelTier });
                }
                continue;
            }

            // Forward edge
            const ax1 = ox + e.x1 * scale, ay1 = oy + e.y1 * scale;
            const ax2 = ox + e.x2 * scale, ay2 = oy + e.y2 * scale;
            const levelH = (e.y2 - e.y1);
            const cy1 = oy + (e.y1 + Math.max(levelH * 0.35, nodeH * 0.8)) * scale;
            const cy2 = oy + (e.y2 - Math.max(levelH * 0.35, nodeH * 0.8)) * scale;
            ctx.beginPath();
            ctx.moveTo(ax1, ay1);
            ctx.bezierCurveTo(ax1, cy1, ax2, cy2, ax2, ay2);
            ctx.strokeStyle = forwardColor;
            ctx.lineWidth   = isHovered ? hoverWidth : Math.max(1, 1.5 * scale);
            ctx.stroke();
            // Tangent at endpoint: from (ax2, cy2) → (ax2, ay2)
            _drawArrow(ctx, ax2, ay2, 0, ay2 - cy2, scale, forwardColor);

            if (e.label && showEdgeLbls) {
                // Evaluate the actual bezier at t=0.75 — the prior midpoint was
                // a linear average of the endpoints, which floated off the curve
                // because the bezier dips through cy1/cy2.
                const pt = _bezierAt(0.75, ax1, ay1, ax1, cy1, ax2, cy2, ax2, ay2);
                pendingLabels.push({ text: e.label, x: pt.x, y: pt.y, tier: labelTier });
            }
        }

        // ── Nodes ─────────────────────────────────────────────────────────
        const nodes = s.nodes;
        const parentKeys = s.parentKeys;
        const childKeys  = s.childKeys;
        for (let i = 0; i < nodes.length; i++) {
            const n = nodes[i];
            if (n.x + nodeW < wx0 || n.x > wx1 || n.y + nodeH < wy0 || n.y > wy1) continue;
            const ax = ox + n.x * scale;
            const ay = oy + n.y * scale;
            const aw = nodeW * scale;
            const ah = nodeH * scale;
            const rx = Math.max(2, aw * 0.12);
            const c  = n._colors;
            const hovered = s.hoveredMarking === n.markingKey;
            const isParent = !hovered && parentKeys.has(n.markingKey);
            const isChild  = !hovered && childKeys.has(n.markingKey);
            const neighbor = isParent || isChild;
            const dim     = anyHover && !hovered && !neighbor;

            ctx.beginPath();
            ctx.roundRect(ax, ay, aw, ah, rx);
            // Five-tier fill/stroke:
            //   hovered  → strong teal (clearly the focus)
            //   parent   → green  (incoming flow source)
            //   child    → orange (outgoing flow target)
            //   dim      → fade non-neighbour content while something is hovered
            //   default  → original node color
            ctx.fillStyle = hovered  ? '#c8f0ec'
                          : isParent ? '#d1fae5'
                          : isChild  ? '#ffedd5'
                          : dim ? '#f7f9fc' : c.fill;
            ctx.fill();
            ctx.strokeStyle = hovered  ? '#00796b'
                            : isParent ? '#059669'
                            : isChild  ? '#ea580c'
                            : dim ? '#dfe5ec' : c.stroke;
            ctx.lineWidth = Math.max(1, (neighbor ? c.sw + 0.5 : c.sw) * scale);
            ctx.stroke();

            if (showLabels) {
                const MIN_TEXT_PX = 12;
                const naturalPx = n._labelF * nodeW * scale;
                const drawPx    = Math.max(MIN_TEXT_PX, naturalPx);
                ctx.fillStyle = hovered  ? '#00796b'
                              : isParent ? '#065f46'
                              : isChild  ? '#9a3412'
                              : dim ? '#aab1bb' : c.textC;
                ctx.textAlign = 'center';
                ctx.textBaseline = 'middle';
                // Fast path when natural size is already at least the minimum:
                // no save/clip/scale needed.  This is the case when the user hasn't
                // zoomed out far enough to need text upscaling for legibility.
                if (naturalPx >= MIN_TEXT_PX) {
                    ctx.font = `700 ${drawPx}px Inter,sans-serif`;
                    ctx.fillText(n.label, ax + aw / 2, ay + ah / 2);
                } else {
                    const inv = naturalPx / drawPx;
                    ctx.save();
                    ctx.beginPath();
                    ctx.roundRect(ax, ay, aw, ah, rx);
                    ctx.clip();
                    ctx.translate(ax + aw / 2, ay + ah / 2);
                    ctx.scale(inv, inv);
                    ctx.font = `700 ${drawPx}px Inter,sans-serif`;
                    ctx.fillText(n.label, 0, 0);
                    ctx.restore();
                }
            }
        }

        // ── Edge labels (top of stack) ────────────────────────────────────
        // Drawn after edges AND nodes so they never get covered by either.
        // Sorted by tier ascending so the hovered label is drawn last and
        // sits on top of any neighbour/default label it overlaps.
        pendingLabels.sort((a, b) => a.tier - b.tier);
        for (const L of pendingLabels)
            _drawLabel(ctx, L.text, L.x, L.y, nodeW * scale, L.tier);

        ctx.restore();
    }

    // tier:
    //   0  = default (no hover anywhere)
    //   1  = child  (outgoing edge from hovered marking) — orange
    //   2  = parent (incoming edge to hovered marking)   — green
    //   3  = directly hovered edge — teal
    //   -1 = dimmed (something is hovered but this label belongs to neither it nor a neighbour)
    function _drawLabel(ctx, text, mx, my, screenNodeW, tier) {
        const naturalPx = screenNodeW * 0.14;
        const labelPx   = Math.max(11, naturalPx);
        ctx.font = `600 ${labelPx}px Inter,sans-serif`;
        const tw = ctx.measureText(text).width;
        const pad = labelPx * 0.3;
        const rh = labelPx + pad * 2, rw = tw + pad * 2;

        let fill, stroke, text_color, strokeW;
        switch (tier) {
            case 3:  fill = '#c8f0ec'; stroke = '#00796b'; text_color = '#00474a'; strokeW = 1.5; break;
            case 2:  fill = '#d1fae5'; stroke = '#059669'; text_color = '#065f46'; strokeW = 1.0; break;
            case 1:  fill = '#ffedd5'; stroke = '#ea580c'; text_color = '#9a3412'; strokeW = 1.0; break;
            case -1: fill = '#f7f9fc'; stroke = '#e4e7ec'; text_color = '#aab1bb'; strokeW = 0.5; break;
            default: fill = '#ffffff'; stroke = '#d1d5db'; text_color = '#374151'; strokeW = 0.5; break;
        }

        ctx.fillStyle = fill;
        ctx.beginPath();
        ctx.roundRect(mx - rw / 2, my - rh / 2, rw, rh, Math.min(3, rh / 2));
        ctx.fill();
        ctx.strokeStyle = stroke;
        ctx.lineWidth = strokeW;
        ctx.stroke();
        ctx.fillStyle = text_color;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(text, mx, my);
    }

    // Cubic bezier point at parameter t. Returns {x, y}.
    function _bezierAt(t, x1, y1, c1x, c1y, c2x, c2y, x2, y2) {
        const u = 1 - t;
        const tt = t * t, uu = u * u;
        const x = uu*u*x1 + 3*uu*t*c1x + 3*u*tt*c2x + tt*t*x2;
        const y = uu*u*y1 + 3*uu*t*c1y + 3*u*tt*c2y + tt*t*y2;
        return { x, y };
    }

    function _scheduleRedraw(s) {
        if (s.rafId != null) return;
        s.rafId = requestAnimationFrame(() => { s.rafId = null; _draw(s); });
    }

    function _resizeCanvas(s) {
        const dpr = window.devicePixelRatio || 1;
        s.dpr = dpr;
        const cssW = s.wrap.clientWidth  || 1;
        const cssH = s.wrap.clientHeight || 1;
        s.canvas.width  = Math.round(cssW * dpr);
        s.canvas.height = Math.round(cssH * dpr);
        s._wrapWidth = cssW; s._wrapHeight = cssH;
    }

    function _clampViewport(s) {
        const padX = s.vw * 2, padY = s.vh * 2;
        s.vx = Math.max(-padX, Math.min(s.svgW + padX, s.vx));
        s.vy = Math.max(-padY, Math.min(s.svgH + padY, s.vy));
    }

    function init(containerId, nodes, edges, svgW, svgH, nodeW, nodeH, placeNames, dotNetRef) {
        const wrap = document.getElementById(containerId);
        if (!wrap) return;
        const canvas = wrap.querySelector('canvas');
        if (!canvas) return;

        wrap.style.touchAction = 'none';
        wrap.style.overscrollBehavior = 'none';
        if (_state[containerId]) _destroyState(containerId, wrap);

        // Pre-compute colors + label-fit fractions
        const mc = document.createElement('canvas').getContext('2d');
        const REF  = 100;
        const maxW = REF * 0.72;
        const BASE_L_F = 0.28, MIN_F = 0.08;
        function fitFraction(text, startF) {
            let f = startF;
            mc.font = `700 ${f * REF}px Inter,sans-serif`;
            while (f > MIN_F && mc.measureText(text).width > maxW) {
                f -= 0.005;
                mc.font = `700 ${f * REF}px Inter,sans-serif`;
            }
            return f;
        }
        for (let i = 0; i < nodes.length; i++) {
            nodes[i]._colors = _nodeColors(nodes[i]);
            nodes[i]._labelF = fitFraction(nodes[i].label, BASE_L_F);
        }

        // Stagger back-edges that share the same inter-row lane (the gap above
        // the source row). Sort by horizontal span so longer edges sit higher
        // and shorter ones nest underneath them.
        const backGroups = {};
        for (let i = 0; i < edges.length; i++) {
            const e = edges[i];
            if (!e.isBack || e.isSelf) continue;
            if (e.fromLayer === e.toLayer) continue;
            const key = `${e.fromLayer}`;
            (backGroups[key] = backGroups[key] || []).push(e);
        }
        for (const key in backGroups) {
            const list = backGroups[key];
            list.sort((a, b) => Math.abs(b.cx2 - b.cx1) - Math.abs(a.cx2 - a.cx1));
            for (let i = 0; i < list.length; i++) list[i].bowOffset = i;
        }

        // ── Adjacency map keyed by marking ────────────────────────────────
        // For neighbour-highlight on hover: given a marking key, look up the
        // set of parent + child marking keys and the edge indices connecting
        // them. Keyed by markingKey (not node id) so two visually-merged
        // duplicate nodes resolve the same.
        const adj = {};
        function ensure(key) {
            return (adj[key] = adj[key] || {
                parents:  new Set(),  // keys of markings with an edge INTO this key
                children: new Set(),  // keys of markings reached BY an edge FROM this key
                outEdges: new Set(),  // edge indices where this key is the source
                inEdges:  new Set(),  // edge indices where this key is the target
            });
        }
        // Map node id → markingKey for fast edge resolution.
        const idToKey = {};
        for (const n of nodes) idToKey[n.id] = n.markingKey;
        for (let i = 0; i < edges.length; i++) {
            const e = edges[i];
            const fk = idToKey[e.from], tk = idToKey[e.to];
            if (!fk || !tk) continue;
            ensure(fk).children.add(tk);
            ensure(fk).outEdges.add(i);
            ensure(tk).parents.add(fk);
            ensure(tk).inEdges.add(i);
        }

        const minVW = 100;
        const maxVW = svgW * 1.1;
        const s = {
            wrap, canvas, ctx: canvas.getContext('2d'),
            dpr: window.devicePixelRatio || 1,
            svgW, svgH, nodeW, nodeH,
            vx: 0, vy: 0, vw: svgW, vh: svgH,
            nodes, edges,
            drag: null, rafId: null,
            hoveredMarking: null,
            hoveredEdgeIdx: -1,
            adj,
            // Neighbour-highlight feature. Toggle via setHoverHighlight(...).
            highlightNeighbours: true,
            // Recomputed on every hover change.
            // Split the hovered-marking neighbourhood into parents (incoming) and
            // children (outgoing) so they can be styled with distinct colours.
            parentKeys:  new Set(),
            childKeys:   new Set(),
            parentEdges: new Set(),
            childEdges:  new Set(),
            dotNetRef,
            _wrapWidth: 1, _wrapHeight: 1,
        };

        _resizeCanvas(s);

        // Spatial hash for hit-test
        const _cellSize = Math.max(nodeW, nodeH) * 3;
        const _grid = {};
        const hitNodes = nodes.map(n => ({
            x: n.x, y: n.y, markingKey: n.markingKey, label: n.label,
        }));
        for (const n of hitNodes) {
            const cx = Math.floor(n.x / _cellSize);
            const cy = Math.floor(n.y / _cellSize);
            const k = cx + ',' + cy;
            (_grid[k] = _grid[k] || []).push(n);
        }
        function hitTest(wx, wy) {
            const cx = Math.floor(wx / _cellSize), cy = Math.floor(wy / _cellSize);
            for (let dx = -1; dx <= 1; dx++)
                for (let dy = -1; dy <= 1; dy++) {
                    const bucket = _grid[(cx + dx) + ',' + (cy + dy)];
                    if (!bucket) continue;
                    for (const n of bucket)
                        if (wx >= n.x && wx <= n.x + nodeW && wy >= n.y && wy <= n.y + nodeH) return n;
                }
            return null;
        }

        // Find the index of the edge whose curve passes nearest (wx, wy).
        // Returns -1 if no edge is within the threshold. We sample the cubic
        // bezier at fixed t-values; this is fast enough for thousands of edges.
        function hitTestEdge(wx, wy) {
            const threshold = nodeH * 0.4;
            const t2 = threshold * threshold;
            let bestIdx = -1, bestDist = t2;
            for (let i = 0; i < edges.length; i++) {
                const e = edges[i];
                // Cheap bounding-box reject (back-edges can extend above source).
                const minX = Math.min(e.x1, e.x2) - nodeW;
                const maxX = Math.max(e.x1, e.x2) + nodeW;
                const minY = Math.min(e.y1, e.y2) - nodeH * 2;
                const maxY = Math.max(e.y1, e.y2) + nodeH * 2;
                if (wx < minX || wx > maxX || wy < minY || wy > maxY) continue;

                let p;
                if (e.isSelf)      p = _selfLoopPath(e, nodeW, nodeH);
                else if (e.isBack) p = _backEdgeControlPoints(e, nodeW, nodeH, s.svgW);
                else {
                    const lh = e.y2 - e.y1;
                    const cy1 = e.y1 + Math.max(lh * 0.35, nodeH * 0.8);
                    const cy2 = e.y2 - Math.max(lh * 0.35, nodeH * 0.8);
                    p = { sx1: e.x1, sy1: e.y1, sx2: e.x2, sy2: e.y2,
                          c1x: e.x1, c1y: cy1, c2x: e.x2, c2y: cy2 };
                }
                const sx = e.isSelf ? p.sx : p.sx1;
                const sy = e.isSelf ? p.sy : p.sy1;
                const ex = e.isSelf ? p.ex : p.sx2;
                const ey = e.isSelf ? p.ey : p.sy2;
                for (let k = 1; k < 16; k++) {
                    const t = k / 16;
                    const u = 1 - t;
                    const bx = u*u*u*sx + 3*u*u*t*p.c1x + 3*u*t*t*p.c2x + t*t*t*ex;
                    const by = u*u*u*sy + 3*u*u*t*p.c1y + 3*u*t*t*p.c2y + t*t*t*ey;
                    const ddx = bx - wx, ddy = by - wy;
                    const d2 = ddx*ddx + ddy*ddy;
                    if (d2 < bestDist) { bestDist = d2; bestIdx = i; }
                }
            }
            return bestIdx;
        }

        // Tooltip
        let tooltip = wrap.querySelector('.graph-tooltip');
        if (!tooltip) {
            tooltip = document.createElement('div');
            tooltip.className = 'graph-tooltip';
            tooltip.style.cssText = `position:absolute;pointer-events:none;display:none;
                background:#fff;border:1px solid #e4e6ea;border-radius:8px;
                box-shadow:0 4px 16px rgba(0,0,0,0.12);padding:8px 10px;
                min-width:140px;max-width:280px;font-family:Inter,sans-serif;font-size:11px;z-index:9999;`;
            wrap.appendChild(tooltip);
        }
        const _placeNames = placeNames || [];
        function showTooltip(hit, clientX, clientY) {
            const vals = hit.markingKey.split(',');
            let html = `<div style="font-weight:700;color:#374151;margin-bottom:6px;padding-bottom:5px;border-bottom:1px solid #f0f0f0;">${hit.label}</div>`;
            for (let i = 0; i < vals.length; i++) {
                const name = _placeNames[i] || ('p' + i);
                const raw = vals[i];
                const isOmega = raw === 'w';
                const val = isOmega ? 'ω' : raw;
                const num = isOmega ? -1 : parseInt(raw, 10);
                const hot = num > 0 || isOmega;
                html += `<div style="display:flex;justify-content:space-between;align-items:baseline;gap:8px;padding:1px 0;
                    color:${hot ? '#222' : '#9aa0ad'};font-weight:${hot ? '600' : '400'};">
                    <span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;min-width:0;flex:1;">${name}</span>
                    <span style="color:${isOmega ? '#7c4dff' : hot ? '#00a499' : '#bbb'};flex-shrink:0;">${val}</span></div>`;
            }
            tooltip.innerHTML = html;
            tooltip.style.display = 'block';
            const wr = wrap.getBoundingClientRect();
            const tw = tooltip.offsetWidth || 280;
            const th = tooltip.offsetHeight || 120;
            const cx = clientX - wr.left, cy = clientY - wr.top;

            // Build a set of highlighted-edge screen bboxes so we can pick a
            // tooltip position that doesn't sit on top of one. World→screen:
            //   screenX = (worldX - vx) / vw * wrap.clientWidth
            //   screenY = (worldY - vy) / vh * wrap.clientHeight
            const W = wrap.clientWidth, H = wrap.clientHeight;
            const sxOf = wx => (wx - s.vx) / s.vw * W;
            const syOf = wy => (wy - s.vy) / s.vh * H;
            const highlightRects = [];
            for (let i = 0; i < edges.length; i++) {
                const e = edges[i];
                if (!(i === s.hoveredEdgeIdx || s.parentEdges.has(i) || s.childEdges.has(i))) continue;
                const r = {
                    x: Math.min(sxOf(e.x1), sxOf(e.x2)) - 4,
                    y: Math.min(syOf(e.y1), syOf(e.y2)) - 4,
                    w: Math.abs(sxOf(e.x2) - sxOf(e.x1)) + 8,
                    h: Math.abs(syOf(e.y2) - syOf(e.y1)) + 8,
                };
                highlightRects.push(r);
            }

            // Score a candidate tooltip rect by counting overlapping highlights.
            const overlap = (rx, ry) => {
                let n = 0;
                for (const h of highlightRects) {
                    if (rx < h.x + h.w && rx + tw > h.x &&
                        ry < h.y + h.h && ry + th > h.y) n++;
                }
                return n;
            };

            // Candidate positions in preference order. Right of cursor first
            // (matches existing behaviour); also try left, below, above.
            const candidates = [
                { x: cx + 12,       y: cy - 10 },          // right
                { x: cx - tw - 8,   y: cy - 10 },          // left
                { x: cx + 12,       y: cy + 18 },          // right + below
                { x: cx - tw - 8,   y: cy + 18 },          // left + below
                { x: cx - tw / 2,   y: cy + 24 },          // below cursor
                { x: cx - tw / 2,   y: cy - th - 12 },     // above cursor
            ];

            // Pick the candidate with the fewest overlaps; ties broken by order.
            let best = candidates[0];
            let bestScore = Infinity;
            for (const c of candidates) {
                // Skip candidates entirely outside the wrap.
                if (c.x + tw < 0 || c.x > W || c.y + th < 0 || c.y > H) continue;
                const score = overlap(c.x, c.y);
                if (score < bestScore) { best = c; bestScore = score; if (score === 0) break; }
            }

            let tx = best.x, ty = best.y;
            if (tx + tw > W) tx = W - tw - 4;
            if (tx < 0)      tx = 4;
            const maxTyViewport  = window.innerHeight - wr.top - th - 4;
            const maxTyContainer = H - th - 4;
            if (ty + th > H) ty = Math.min(maxTyViewport, maxTyContainer);
            if (ty < 0)      ty = 4;
            tooltip.style.left = tx + 'px';
            tooltip.style.top  = ty + 'px';
        }

        function clientToWorld(clientX, clientY) {
            const r = wrap.getBoundingClientRect();
            const fx = (clientX - r.left) / r.width;
            const fy = (clientY - r.top)  / r.height;
            return { wx: s.vx + fx * s.vw, wy: s.vy + fy * s.vh };
        }

        s.onWheel = (e) => {
            e.preventDefault(); e.stopPropagation();
            const r = wrap.getBoundingClientRect();
            const fx = (e.clientX - r.left) / r.width;
            const fy = (e.clientY - r.top)  / r.height;
            const mx = s.vx + fx * s.vw, my = s.vy + fy * s.vh;
            const raw = e.deltaMode === 1 ? e.deltaY * 32 : e.deltaMode === 2 ? e.deltaY * 300 : e.deltaY;
            const factor = raw > 0 ? 1.12 : (1 / 1.12);
            // Recompute the zoom-out cap each wheel tick so narrow-and-long graphs
            // can still be zoomed out far enough to see vertically. The cap is the
            // larger of svgW or "viewport width that fits svgH at current aspect".
            const aspect    = r.height / r.width;
            const dynMaxVW  = Math.max(svgW, aspect > 0 ? svgH / aspect : svgW) * 1.1;
            const nw = Math.max(minVW, Math.min(dynMaxVW, s.vw * factor));
            const nh = nw * aspect;
            s.vx = mx - fx * nw; s.vy = my - fy * nh;
            s.vw = nw; s.vh = nh;
            _clampViewport(s); _scheduleRedraw(s);
        };
        s.onPointerDown = (e) => {
            if (e.button !== 0) return;
            e.preventDefault();
            wrap.setPointerCapture(e.pointerId);
            s.drag = { px: e.clientX, py: e.clientY, vx: s.vx, vy: s.vy };
            wrap.style.cursor = 'grabbing';
        };
        s.onPointerMove = (e) => {
            if (!s.drag) return;
            e.preventDefault();
            const r = wrap.getBoundingClientRect();
            const sc = Math.min(s.vw / r.width, s.vh / r.height);
            s.vx = s.drag.vx - (e.clientX - s.drag.px) * sc;
            s.vy = s.drag.vy - (e.clientY - s.drag.py) * sc;
            _clampViewport(s); _scheduleRedraw(s);
        };
        s.onPointerUp = () => { s.drag = null; wrap.style.cursor = 'grab'; };
        function _updateNeighbourSets(key) {
            s.parentKeys  = new Set();
            s.childKeys   = new Set();
            s.parentEdges = new Set();
            s.childEdges  = new Set();
            if (!key || !s.highlightNeighbours) return;
            const entry = s.adj[key];
            if (!entry) return;
            for (const k of entry.parents)  s.parentKeys.add(k);
            for (const k of entry.children) s.childKeys.add(k);
            for (const i of entry.inEdges)  s.parentEdges.add(i);
            for (const i of entry.outEdges) s.childEdges.add(i);
        }

        s.onMouseMove = (e) => {
            if (s.drag) { tooltip.style.display = 'none'; return; }
            const { wx, wy } = clientToWorld(e.clientX, e.clientY);
            const hit = hitTest(wx, wy);
            const key = hit ? hit.markingKey : null;
            const edgeIdx = hit ? -1 : hitTestEdge(wx, wy);
            let needsRedraw = false;
            if (key !== s.hoveredMarking) {
                s.hoveredMarking = key;
                _updateNeighbourSets(key);
                needsRedraw = true;
                if (key) {
                    if (s.dotNetRef) s.dotNetRef.invokeMethodAsync('OnNodeHovered', key, key);
                } else {
                    if (s.dotNetRef) s.dotNetRef.invokeMethodAsync('OnNodeLeft');
                }
            }
            if (edgeIdx !== s.hoveredEdgeIdx) {
                s.hoveredEdgeIdx = edgeIdx;
                needsRedraw = true;
            }
            if (needsRedraw) _scheduleRedraw(s);
            if (hit) showTooltip(hit, e.clientX, e.clientY);
            else     tooltip.style.display = 'none';
        };
        s.onMouseLeave = () => {
            tooltip.style.display = 'none';
            let needsRedraw = false;
            if (s.hoveredMarking) {
                s.hoveredMarking = null;
                _updateNeighbourSets(null);
                needsRedraw = true;
                if (s.dotNetRef) s.dotNetRef.invokeMethodAsync('OnNodeLeft');
            }
            if (s.hoveredEdgeIdx !== -1) { s.hoveredEdgeIdx = -1; needsRedraw = true; }
            if (needsRedraw) _scheduleRedraw(s);
        };

        wrap.addEventListener('wheel',        s.onWheel,       { passive: false, capture: true });
        wrap.addEventListener('pointerdown',  s.onPointerDown, { passive: false });
        wrap.addEventListener('pointermove',  s.onPointerMove, { passive: false });
        wrap.addEventListener('pointerup',    s.onPointerUp);
        wrap.addEventListener('pointercancel',s.onPointerUp);
        wrap.addEventListener('mousemove',    s.onMouseMove);
        wrap.addEventListener('mouseleave',   s.onMouseLeave);

        function _initView() {
            if (s._viewInited) return;
            const cssW = s._wrapWidth, cssH = s._wrapHeight;
            if (cssW < 4 || cssH < 4) return;
            s._viewInited = true;
            const aspect = cssH / cssW;
            s.vw = Math.min(maxVW, Math.max(300, svgW * 0.4));
            s.vh = s.vw * aspect;
            if (nodes.length > 0) {
                s.vx = nodes[0].x + nodeW / 2 - s.vw / 2;
                s.vy = nodes[0].y + nodeH / 2 - s.vh / 2;
            }
            _clampViewport(s); _draw(s);
        }

        // Canvas needs to refit whenever the wrap's css size changes OR the
        // device pixel ratio changes (e.g. dragging the window across monitors).
        const refit = () => {
            _resizeCanvas(s);
            if (!s._viewInited) _initView(); else _scheduleRedraw(s);
        };

        s._ro = new ResizeObserver(refit);
        s._ro.observe(wrap);

        s._onWindowResize = () => requestAnimationFrame(refit);
        window.addEventListener('resize', s._onWindowResize);

        s._dprDispose = null;
        const watchDpr = () => {
            const mql = window.matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`);
            const onChange = () => { refit(); watchDpr(); };
            if (mql.addEventListener) mql.addEventListener('change', onChange);
            else if (mql.addListener) mql.addListener(onChange);
            s._dprDispose = () => {
                if (mql.removeEventListener) mql.removeEventListener('change', onChange);
                else if (mql.removeListener) mql.removeListener(onChange);
            };
        };
        watchDpr();

        _state[containerId] = s;
        wrap.style.cursor = 'grab';
        _resizeCanvas(s);
        _initView();
    }

    function _destroyState(containerId, wrap) {
        const s = _state[containerId];
        if (!s) return;
        if (s.rafId != null) cancelAnimationFrame(s.rafId);
        if (s._ro) s._ro.disconnect();
        if (s._onWindowResize) window.removeEventListener('resize', s._onWindowResize);
        if (s._dprDispose) s._dprDispose();
        const w = wrap || document.getElementById(containerId);
        if (w) {
            w.removeEventListener('wheel',        s.onWheel,       { capture: true });
            w.removeEventListener('pointerdown',  s.onPointerDown);
            w.removeEventListener('pointermove',  s.onPointerMove);
            w.removeEventListener('pointerup',    s.onPointerUp);
            w.removeEventListener('pointercancel',s.onPointerUp);
            w.removeEventListener('mousemove',    s.onMouseMove);
            w.removeEventListener('mouseleave',   s.onMouseLeave);
        }
        delete _state[containerId];
    }
    function destroy(containerId) { _destroyState(containerId, null); }

    function resetView(containerId) {
        const s = _state[containerId];
        if (!s) return;
        s.vx = 0; s.vy = 0; s.vw = s.svgW; s.vh = s.svgH;
        _resizeCanvas(s); _clampViewport(s); _scheduleRedraw(s);
    }

    // Minimal SVG/PNG export: full extent, forward + back edges as beziers.
    function exportSvg(containerId) {
        const s = _state[containerId];
        if (!s || !s.nodes || s.nodes.length === 0) return null;

        const nodeW = s.nodeW, nodeH = s.nodeH;
        const pad = 20;
        const W = s.svgW + pad * 2, H = s.svgH + pad * 2;
        const ox = pad, oy = pad;
        const esc = (x) => String(x ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;');

        const out = [];
        out.push(`<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">`);
        out.push(`<rect width="${W}" height="${H}" fill="#fafbfc"/>`);

        for (const e of s.edges) {
            const color = (e.isBack || e.isSelf) ? '#9dafc0' : '#9dafc0';
            let d;
            if (e.isSelf) {
                const p = _selfLoopPath(e, nodeW, nodeH);
                d = `M${ox+p.sx},${oy+p.sy} C${ox+p.c1x},${oy+p.c1y} ${ox+p.c2x},${oy+p.c2y} ${ox+p.ex},${oy+p.ey}`;
            } else if (e.isBack) {
                const p = _backEdgeControlPoints(e, nodeW, nodeH, s.svgW);
                d = `M${ox+p.sx1},${oy+p.sy1} C${ox+p.c1x},${oy+p.c1y} ${ox+p.c2x},${oy+p.c2y} ${ox+p.sx2},${oy+p.sy2}`;
            } else {
                const levelH = (e.y2 - e.y1);
                const cy1 = e.y1 + Math.max(levelH * 0.35, nodeH * 0.8);
                const cy2 = e.y2 - Math.max(levelH * 0.35, nodeH * 0.8);
                d = `M${ox+e.x1},${oy+e.y1} C${ox+e.x1},${oy+cy1} ${ox+e.x2},${oy+cy2} ${ox+e.x2},${oy+e.y2}`;
            }
            out.push(`<path d="${d}" fill="none" stroke="${color}" stroke-width="1.5"/>`);
            if (e.label && !e.isBack && !e.isSelf) {
                const mx = ox + (e.x1 + e.x2) / 2, my = oy + (e.y1 + e.y2) / 2;
                const fs = 11;
                const tw = e.label.length * fs * 0.52;
                const rw = tw + fs * 0.6, rh = fs + fs * 0.6;
                out.push(`<rect x="${mx-rw/2}" y="${my-rh/2}" width="${rw}" height="${rh}" rx="3" fill="#ffffff" stroke="#d1d5db" stroke-width="0.5"/>`);
                out.push(`<text x="${mx}" y="${my}" text-anchor="middle" dominant-baseline="middle" font-size="${fs}" font-weight="600" fill="#374151" font-family="Inter,sans-serif">${esc(e.label)}</text>`);
            }
        }
        for (const n of s.nodes) {
            const ax = ox + n.x, ay = oy + n.y;
            const c = n._colors;
            const rx = Math.max(2, nodeW * 0.12);
            out.push(`<rect x="${ax}" y="${ay}" width="${nodeW}" height="${nodeH}" rx="${rx}" fill="${c.fill}" stroke="${c.stroke}" stroke-width="${c.sw}"/>`);
            const cx = ax + nodeW/2, cy = ay + nodeH/2;
            const fs = Math.max(8, nodeW * 0.28 * 0.72);
            out.push(`<text x="${cx}" y="${cy}" text-anchor="middle" dominant-baseline="middle" font-size="${fs}" font-weight="700" fill="${c.textC}" font-family="Inter,sans-serif">${esc(n.label)}</text>`);
        }
        out.push('</svg>');
        return out.join('\n');
    }

    function exportPng(containerId, maxPx) {
        const s = _state[containerId];
        if (!s || !s.nodes || s.nodes.length === 0) return null;
        maxPx = maxPx || 4096;
        const pad = 20;
        const fullW = s.svgW + pad * 2, fullH = s.svgH + pad * 2;
        const scale = Math.min(1, maxPx / Math.max(fullW, fullH));
        const pw = Math.ceil(fullW * scale), ph = Math.ceil(fullH * scale);
        const off = document.createElement('canvas');
        off.width = pw; off.height = ph;
        const ctx = off.getContext('2d');
        ctx.fillStyle = '#fafbfc';
        ctx.fillRect(0, 0, pw, ph);

        // Stash state, render into offscreen by reusing _draw with a modified s.
        // Simpler: just draw beziers and nodes directly.
        const nodeW = s.nodeW * scale, nodeH = s.nodeH * scale;
        const ox = pad * scale, oy = pad * scale;
        for (const e of s.edges) {
            const color = (e.isBack || e.isSelf) ? '#9dafc0' : '#9dafc0';
            ctx.strokeStyle = color;
            ctx.lineWidth = Math.max(1, 1.5 * scale);
            ctx.beginPath();
            if (e.isSelf) {
                const p = _selfLoopPath(e, s.nodeW, s.nodeH);
                ctx.moveTo(ox + p.sx * scale, oy + p.sy * scale);
                ctx.bezierCurveTo(ox + p.c1x * scale, oy + p.c1y * scale,
                                  ox + p.c2x * scale, oy + p.c2y * scale,
                                  ox + p.ex * scale, oy + p.ey * scale);
            } else if (e.isBack) {
                const p = _backEdgeControlPoints(e, s.nodeW, s.nodeH, s.svgW);
                ctx.moveTo(ox + p.sx1 * scale, oy + p.sy1 * scale);
                ctx.bezierCurveTo(ox + p.c1x * scale, oy + p.c1y * scale,
                                  ox + p.c2x * scale, oy + p.c2y * scale,
                                  ox + p.sx2 * scale, oy + p.sy2 * scale);
            } else {
                const levelH = (e.y2 - e.y1);
                const cy1 = (e.y1 + Math.max(levelH * 0.35, s.nodeH * 0.8)) * scale;
                const cy2 = (e.y2 - Math.max(levelH * 0.35, s.nodeH * 0.8)) * scale;
                ctx.moveTo(ox + e.x1 * scale, oy + e.y1 * scale);
                ctx.bezierCurveTo(ox + e.x1 * scale, oy + cy1,
                                  ox + e.x2 * scale, oy + cy2,
                                  ox + e.x2 * scale, oy + e.y2 * scale);
            }
            ctx.stroke();
        }
        for (const n of s.nodes) {
            const ax = ox + n.x * scale, ay = oy + n.y * scale;
            const rx = Math.max(2, nodeW * 0.12);
            const c = n._colors;
            ctx.beginPath();
            ctx.roundRect(ax, ay, nodeW, nodeH, rx);
            ctx.fillStyle = c.fill; ctx.fill();
            ctx.strokeStyle = c.stroke;
            ctx.lineWidth = Math.max(1, c.sw * scale);
            ctx.stroke();
            const fs = Math.max(8, s.nodeW * 0.28 * 0.72 * scale);
            ctx.fillStyle = c.textC;
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.font = `700 ${fs}px Inter,sans-serif`;
            ctx.fillText(n.label, ax + nodeW / 2, ay + nodeH / 2);
        }
        return off.toDataURL('image/png').split(',')[1];
    }

    /// Toggle the "highlight parents + children on hover" feature.
    /// When disabled, hovering only highlights the hovered node itself.
    function setHoverHighlight(containerId, enabled) {
        const s = _state[containerId];
        if (!s) return;
        s.highlightNeighbours = !!enabled;
        // Reset transient state so the next hover starts clean.
        s.parentKeys  = new Set();
        s.childKeys   = new Set();
        s.parentEdges = new Set();
        s.childEdges  = new Set();
        _scheduleRedraw(s);
    }

    return { init, destroy, resetView, exportSvg, exportPng, setHoverHighlight };
})();
