// ── SVG pan/zoom for tree views ───────────────────────────────────────────
window.treeView = (() => {
    const _state = {};

    // ── Colours ────────────────────────────────────────────────────────────
    function _nodeColors(n) {
        const isRef    = !!n.isRef;
        const isCutOff = !!n.isCutOff;
        const isInit   = !!n.isInit;
        const isDead   = !!n.isDead;
        const isOmega  = !!n.isOmega;
        const fill   = isRef    ? '#fffde7' : isCutOff ? '#f3e5f5' : isOmega ? '#f0f0fb'
                     : isInit   ? '#e8faf8' : isDead   ? '#ffebee' : '#ffffff';
        const stroke = isRef    ? '#f9a825' : isCutOff ? '#8e24aa' : isOmega ? '#5c6bc0'
                     : isInit   ? '#00a499' : isDead   ? '#e53935' : '#9dafc0';
        const textC  = isRef    ? '#e65100' : isCutOff ? '#6a1b9a' : isOmega ? '#283593'
                     : isInit   ? '#00695c' : isDead   ? '#b71c1c' : '#111827';
        const sw     = isInit ? 2.5 : 1.5;
        return { fill, stroke, textC, sw, isRef, isCutOff, isDead, isInit, isOmega };
    }

    // ── Canvas draw ─────────────────────────────────────────────────────────
    function _draw(s) {
        const canvas = s.canvas;
        const ctx    = s.ctx;
        const dpr    = s.dpr;
        const cw     = canvas.width;   // physical pixels
        const ch     = canvas.height;
        const cssW   = cw / dpr;
        const cssH   = ch / dpr;

        ctx.clearRect(0, 0, cw, ch);

        // Transform: world → canvas pixels
        // world unit → css pixel: scale = cssW / s.vw
        const scale = cssW / s.vw;
        const ox    = -s.vx * scale;   // world origin offset in css px
        const oy    = -s.vy * scale;

        // World unit size of 1 css pixel (for minimum line widths)
        const nodeW = s.nodeW;
        const nodeH = s.nodeH;
        const levelH = nodeH * 2;

        // Viewport bounds in world coords (with a small margin so clipping isn't harsh)
        const margin = 4 / scale;
        const wx0 = s.vx - margin, wy0 = s.vy - margin;
        const wx1 = s.vx + s.vw + margin, wy1 = s.vy + s.vh + margin;

        // LOD thresholds — hide labels only when nodes are truly tiny on screen
        const screenW      = nodeW * scale;
        const showLabels   = screenW >= 24;
        const showEdgeLbls = screenW >= 24;

        // ── Edges ─────────────────────────────────────────────────────────
        ctx.save();
        ctx.scale(dpr, dpr);   // work in css pixels

        const edges = s.edges;
        for (let i = 0; i < edges.length; i++) {
            const e = edges[i];
            // Cull: skip if both endpoints far outside
            const eMinX = Math.min(e.x1, e.x2), eMaxX = Math.max(e.x1, e.x2);
            const eMinY = Math.min(e.y1, e.y2), eMaxY = Math.max(e.y1, e.y2);
            if (eMaxX < wx0 || eMinX > wx1 || eMaxY < wy0 || eMinY > wy1) continue;

            const ax1 = ox + e.x1 * scale, ay1 = oy + e.y1 * scale;
            const ax2 = ox + e.x2 * scale, ay2 = oy + e.y2 * scale;
            const cy1 = oy + (e.y1 + levelH * 0.4) * scale;
            const cy2 = oy + (e.y2 - levelH * 0.4) * scale;

            ctx.beginPath();
            ctx.moveTo(ax1, ay1);
            ctx.bezierCurveTo(ax1, cy1, ax2, cy2, ax2, ay2);
            ctx.strokeStyle = e.isDashed ? '#f9a825' : '#9dafc0';
            ctx.lineWidth   = Math.max(1, 1.5 * scale);
            ctx.setLineDash(e.isDashed ? [5 * scale, 3 * scale] : []);
            ctx.stroke();
            ctx.setLineDash([]);

            // Arrowhead at target
            {
                const dx = ax2 - (ox + e.x2 * scale);   // tangent approx: from cy2 to ax2
                const tdx = ax2 - (ox + e.x2 * scale);
                const tdy = ay2 - cy2;
                const len = Math.sqrt(tdx * tdx + tdy * tdy) || 1;
                const ux = tdx / len, uy = tdy / len;
                const nx = -uy, ny = ux;
                const asz = Math.max(5, 7 * scale);
                ctx.beginPath();
                ctx.moveTo(ax2, ay2);
                ctx.lineTo(ax2 - ux * asz + nx * asz * 0.4, ay2 - uy * asz + ny * asz * 0.4);
                ctx.lineTo(ax2 - ux * asz - nx * asz * 0.4, ay2 - uy * asz - ny * asz * 0.4);
                ctx.closePath();
                ctx.fillStyle = e.isDashed ? '#f9a825' : '#9dafc0';
                ctx.fill();
            }

            // Edge label — scales with zoom, hidden when zoomed out.
            // Positioned at 70% along the segment toward the target so it's
            // obvious which marking the transition leads into.
            if (e.label && showEdgeLbls) {
                const t = 0.7;
                const mx = ox + (e.x1 + (e.x2 - e.x1) * t) * scale;
                const my = oy + (e.y1 + (e.y2 - e.y1) * t) * scale;
                // Font scales with node: ~14% of nodeW, minimum 8px on screen
                const naturalPx = nodeW * 0.14 * scale;
                const labelPx   = Math.max(11, naturalPx);
                ctx.font = `600 ${labelPx}px Inter,sans-serif`;
                const tw = e.label.length * labelPx * 0.52;
                const pad = labelPx * 0.3;
                const rh = labelPx + pad * 2, rw = tw + pad * 2;
                ctx.fillStyle = '#ffffff';
                ctx.beginPath();
                ctx.roundRect(mx - rw / 2, my - rh / 2, rw, rh, Math.min(3, rh / 2));
                ctx.fill();
                ctx.strokeStyle = '#d1d5db';
                ctx.lineWidth = 0.5;
                ctx.stroke();
                ctx.fillStyle = '#374151';
                ctx.textAlign = 'center';
                ctx.textBaseline = 'middle';
                ctx.fillText(e.label, mx, my);
            }
        }

        // ── Nodes ─────────────────────────────────────────────────────────
        const nodes = s.nodes;

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

            // Box
            ctx.beginPath();
            ctx.roundRect(ax, ay, aw, ah, rx);
            ctx.fillStyle = hovered ? '#c8f0ec' : c.fill;
            ctx.fill();
            ctx.strokeStyle = hovered ? '#00796b' : c.stroke;
            ctx.lineWidth = Math.max(1, c.sw * scale);
            if (n.isRef) ctx.setLineDash([Math.max(2, 4 * scale), Math.max(1, 2 * scale)]);
            ctx.stroke();
            ctx.setLineDash([]);

            // Text — always rasterized at MIN_TEXT_PX for crispness, then inverse-scaled back
            // so it appears at the correct world size without pixelation from upscaling.
            if (showLabels) {
                const MIN_TEXT_PX = 12;
                const naturalPx = n._labelF * nodeW * scale;
                const drawPx    = Math.max(MIN_TEXT_PX, naturalPx);

                ctx.fillStyle = hovered ? '#00796b' : c.textC;
                ctx.textAlign = 'center';
                ctx.textBaseline = 'middle';

                // Fast path: natural label is already large enough, skip save/clip/scale.
                if (naturalPx >= MIN_TEXT_PX && !n._subText) {
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
                    if (n._subText) {
                        const sNatural = n._subF * nodeW * scale;
                        const sDrawPx  = Math.max(MIN_TEXT_PX - 1, sNatural);
                        const sInv     = sNatural / sDrawPx;
                        ctx.font = `700 ${drawPx}px Inter,sans-serif`;
                        ctx.fillText(n.label, 0, -ah * 0.15 / inv);
                        ctx.scale(sInv / inv, sInv / inv);
                        ctx.font = `600 ${sDrawPx}px Inter,sans-serif`;
                        ctx.fillText(n._subText, 0, ah * 0.22 / sInv);
                    } else {
                        ctx.font = `700 ${drawPx}px Inter,sans-serif`;
                        ctx.fillText(n.label, 0, 0);
                    }
                    ctx.restore();
                }
            }
        }

        ctx.restore();
    }

    function _scheduleRedraw(s) {
        if (s.rafId != null) return;
        s.rafId = requestAnimationFrame(() => {
            s.rafId = null;
            _draw(s);
        });
    }

    function _resizeCanvas(s) {
        const wrap = s.wrap;
        const canvas = s.canvas;
        const dpr = window.devicePixelRatio || 1;
        s.dpr = dpr;
        const cssW = wrap.clientWidth  || 1;
        const cssH = wrap.clientHeight || 1;
        canvas.width  = Math.round(cssW * dpr);
        canvas.height = Math.round(cssH * dpr);
        s._wrapWidth = cssW;
        s._wrapHeight = cssH;
    }

    function _clampViewport(s) {
        const padX = s.vw * 2, padY = s.vh * 2;
        s.vx = Math.max(-padX, Math.min(s.svgW + padX, s.vx));
        s.vy = Math.max(-padY, Math.min(s.svgH + padY, s.vy));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BELOW this line: old SVG-based helpers replaced — new canvas draw above
    // ─────────────────────────────────────────────────────────────────────────

    function init(containerId, nodes, edges, svgW, svgH, nodeW, nodeH, placeNames, dotNetRef) {
        const wrap = document.getElementById(containerId);
        if (!wrap) return;
        const canvas = wrap.querySelector('canvas');
        if (!canvas) return;

        wrap.style.touchAction = 'none';
        wrap.style.overscrollBehavior = 'none';

        if (_state[containerId]) _destroyState(containerId, wrap);

        // Pre-compute colors and label scale-fractions for each node.
        // We measure text at nodeW=100 reference size so the fraction is independent of scale.
        // At draw time: fontPx = fraction * nodeW * scale  →  scales correctly with zoom.
        {
            const mc   = document.createElement('canvas').getContext('2d');
            const REF  = 100;        // reference nodeW for measurement
            const maxW = REF * 0.72; // 72% of node width — long labels shrink more aggressively
            const BASE_L_F = 0.28, BASE_S_F = 0.20, MIN_F = 0.08;

            function fitFraction(text, bold, startF) {
                let f = startF;
                mc.font = `${bold ? '700 ' : ''}${f * REF}px Inter,sans-serif`;
                while (f > MIN_F && mc.measureText(text).width > maxW) {
                    f -= 0.005;
                    mc.font = `${bold ? '700 ' : ''}${f * REF}px Inter,sans-serif`;
                }
                return f;
            }

            for (let i = 0; i < nodes.length; i++) {
                const n = nodes[i];
                n._colors = _nodeColors(n);
                n._labelF = fitFraction(n.label, true, BASE_L_F);
                // Cut-off nodes get an ellipsis sub-label; ref nodes just show their label centered
                if (n.isCutOff) {
                    n._subF    = BASE_S_F;
                    n._subText = '\u2026';
                }
            }

            // Also pre-compute edge label fractions.
            // Edge labels use LevelH as reference for size (they sit between rows).
            // Store the raw label strings — fonts built at draw time using scale.
            // (Edge labels don't need per-label fitting since they're short transition names.)
        }

        const minVW = 100;
        const maxVW = svgW * 1.1;

        const s = {
            wrap, canvas, ctx: canvas.getContext('2d'),
            dpr: window.devicePixelRatio || 1,
            svgW, svgH, nodeW, nodeH,
            vx: 0, vy: 0, vw: svgW, vh: svgH,
            nodes, edges,
            drag: null,
            rafId: null,
            hoveredMarking: null,
            dotNetRef,
            _wrapWidth: 1, _wrapHeight: 1,
        };

        _resizeCanvas(s);

        // ── Spatial hash ──────────────────────────────────────────────────
        const _cellSize = Math.max(nodeW, nodeH) * 3;
        const _grid = {};
        // Build hit-test grid — plain objects, no DOM refs
        const hitNodes = nodes.map(n => ({
            x: n.x, y: n.y, markingKey: n.markingKey, label: n.label,
            isRef: !!n.isRef, canonicalId: n.canonicalId != null ? n.canonicalId : -1,
        }));
        for (let i = 0; i < hitNodes.length; i++) {
            const n = hitNodes[i];
            const cx = Math.floor(n.x / _cellSize);
            const cy = Math.floor(n.y / _cellSize);
            const key = cx + ',' + cy;
            if (!_grid[key]) _grid[key] = [];
            _grid[key].push(n);
        }

        function hitTest(wx, wy) {
            const cx = Math.floor(wx / _cellSize), cy = Math.floor(wy / _cellSize);
            for (let dx = -1; dx <= 1; dx++) {
                for (let dy = -1; dy <= 1; dy++) {
                    const bucket = _grid[(cx + dx) + ',' + (cy + dy)];
                    if (!bucket) continue;
                    for (let i = 0; i < bucket.length; i++) {
                        const n = bucket[i];
                        if (wx >= n.x && wx <= n.x + nodeW && wy >= n.y && wy <= n.y + nodeH) return n;
                    }
                }
            }
            return null;
        }

        // ── Marking index ─────────────────────────────────────────────────
        const _markingIndex = {};
        for (let i = 0; i < hitNodes.length; i++) {
            const k = hitNodes[i].markingKey;
            if (!_markingIndex[k]) _markingIndex[k] = [];
            _markingIndex[k].push(hitNodes[i]);
        }

        // ── Tooltip ───────────────────────────────────────────────────────
        let tooltip = wrap.querySelector('.tree-tooltip');
        if (!tooltip) {
            tooltip = document.createElement('div');
            tooltip.className = 'tree-tooltip';
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
                const isOmega = raw === 'w' || raw === '-1';
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
            const cx = clientX - wr.left;
            const cy = clientY - wr.top;
            // Default: right of and slightly above cursor
            let tx = cx + 12, ty = cy - 10;
            // Clamp horizontally within the container
            if (tx + tw > wrap.clientWidth)  tx = cx - tw - 8;
            if (tx < 0)                       tx = 0;
            // Clamp vertically against the viewport (not just the container) so the
            // tooltip doesn't disappear off the bottom of the screen when the tree
            // panel is near the bottom of the window.
            const maxTyViewport = window.innerHeight - wr.top - th - 4;
            const maxTyContainer = wrap.clientHeight - th - 4;
            if (ty + th > wrap.clientHeight)  ty = Math.min(maxTyViewport, maxTyContainer);
            if (ty < 0)                       ty = 0;
            tooltip.style.left = tx + 'px';
            tooltip.style.top  = ty + 'px';
        }

        // ── Input helpers ─────────────────────────────────────────────────
        function clientToWorld(clientX, clientY) {
            const r = wrap.getBoundingClientRect();
            const fx = (clientX - r.left) / r.width;
            const fy = (clientY - r.top)  / r.height;
            return { wx: s.vx + fx * s.vw, wy: s.vy + fy * s.vh };
        }

        // ── Events ────────────────────────────────────────────────────────
        s.onWheel = (e) => {
            e.preventDefault(); e.stopPropagation();
            const r = wrap.getBoundingClientRect();
            s._wrapWidth = Math.max(r.width, 1);
            const fx = (e.clientX - r.left) / r.width;
            const fy = (e.clientY - r.top)  / r.height;
            const mx = s.vx + fx * s.vw, my = s.vy + fy * s.vh;
            const raw = e.deltaMode === 1 ? e.deltaY * 32 : e.deltaMode === 2 ? e.deltaY * 300 : e.deltaY;
            const factor = raw > 0 ? 1.12 : (1 / 1.12);
            const nw = Math.max(minVW, Math.min(maxVW, s.vw * factor));
            const nh = nw * (r.height / r.width);
            s.vx = mx - fx * nw; s.vy = my - fy * nh;
            s.vw = nw; s.vh = nh;
            _clampViewport(s);
            _scheduleRedraw(s);
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
            _clampViewport(s);
            _scheduleRedraw(s);
        };

        s.onPointerUp = () => { s.drag = null; wrap.style.cursor = 'grab'; };

        s.onMouseMove = (e) => {
            if (s.drag) { tooltip.style.display = 'none'; return; }
            const { wx, wy } = clientToWorld(e.clientX, e.clientY);
            const hit = hitTest(wx, wy);
            const key = hit ? hit.markingKey : null;
            if (key !== s.hoveredMarking) {
                s.hoveredMarking = key;
                _scheduleRedraw(s);
                if (key) {
                    if (s.dotNetRef) s.dotNetRef.invokeMethodAsync('OnNodeHovered', key, key);
                } else {
                    if (s.dotNetRef) s.dotNetRef.invokeMethodAsync('OnNodeLeft');
                }
            }
            if (hit) showTooltip(hit, e.clientX, e.clientY);
            else     tooltip.style.display = 'none';
        };

        s.onMouseLeave = () => {
            tooltip.style.display = 'none';
            if (!s.hoveredMarking) return;
            s.hoveredMarking = null;
            _scheduleRedraw(s);
            if (s.dotNetRef) s.dotNetRef.invokeMethodAsync('OnNodeLeft');
        };

        s.onClick = (e) => {
            if (s.drag) return;
            const { wx, wy } = clientToWorld(e.clientX, e.clientY);
            const hit = hitTest(wx, wy);
            if (!hit || hit.canonicalId < 0) return;
            const target = hitNodes.find(n => n.canonicalId < 0 && n.markingKey === hit.markingKey);
            if (!target) return;
            s.vx = target.x + nodeW / 2 - s.vw / 2;
            s.vy = target.y + nodeH / 2 - s.vh / 2;
            _clampViewport(s);
            _scheduleRedraw(s);
        };

        wrap.addEventListener('wheel',        s.onWheel,       { passive: false, capture: true });
        wrap.addEventListener('pointerdown',  s.onPointerDown, { passive: false });
        wrap.addEventListener('pointermove',  s.onPointerMove, { passive: false });
        wrap.addEventListener('pointerup',    s.onPointerUp);
        wrap.addEventListener('pointercancel',s.onPointerUp);
        wrap.addEventListener('mousemove',    s.onMouseMove);
        wrap.addEventListener('mouseleave',   s.onMouseLeave);
        wrap.addEventListener('click',        s.onClick);

        // Initial view setup — called once we know the real canvas size
        function _initView() {
            if (s._viewInited) return;
            const cssW = s._wrapWidth, cssH = s._wrapHeight;
            if (cssW < 4 || cssH < 4) return;  // not visible yet
            s._viewInited = true;
            const aspect = cssH / cssW;
            s.vw = Math.min(maxVW, Math.max(300, svgW * 0.4));
            s.vh = s.vw * aspect;
            if (nodes.length > 0) {
                s.vx = nodes[0].x + nodeW / 2 - s.vw / 2;
                s.vy = nodes[0].y + nodeH / 2 - s.vh / 2;
            }
            _clampViewport(s);
            _draw(s);
        }

        // Canvas needs to refit whenever the wrap's css size changes OR the device
        // pixel ratio changes (e.g. user drags the window across monitors).
        // - ResizeObserver picks up element-level size changes.
        // - 'resize' on window handles cases where layout was already invalidated
        //   but no element observation fired (DevTools, panel toggles, etc.).
        // - matchMedia('(resolution: ...)') fires when DPR changes mid-session.
        const refit = () => {
            _resizeCanvas(s);
            if (!s._viewInited) _initView(); else _scheduleRedraw(s);
        };

        s._ro = new ResizeObserver(refit);
        s._ro.observe(wrap);

        s._onWindowResize = () => {
            // rAF gives layout one tick to settle so clientWidth/Height are current.
            requestAnimationFrame(refit);
        };
        window.addEventListener('resize', s._onWindowResize);

        // DPR change tracker: re-bound on every change because the previous
        // media-query becomes stale once DPR moves.
        s._dprDispose = null;
        const watchDpr = () => {
            const mql = window.matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`);
            const onChange = () => { refit(); watchDpr(); };
            // addEventListener is the modern path; Safari < 14 needs addListener.
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

        // Try initial view now — works if already visible, no-ops if hidden
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
            w.removeEventListener('click',        s.onClick);
        }
        delete _state[containerId];
    }

    function destroy(containerId) { _destroyState(containerId, null); }

    function resetView(containerId) {
        const s = _state[containerId];
        if (!s) return;
        s.vx = 0; s.vy = 0; s.vw = s.svgW; s.vh = s.svgH;
        _resizeCanvas(s);
        _clampViewport(s);
        _scheduleRedraw(s);
    }

    // ── SVG export ─────────────────────────────────────────────────────────
    // Generates a self-contained SVG string for the full tree (all nodes/edges,
    // independent of current viewport). Returns null if no data is loaded.
    function exportSvg(containerId) {
        const s = _state[containerId];
        if (!s || !s.nodes || s.nodes.length === 0) return null;

        const nodeW  = s.nodeW;
        const nodeH  = s.nodeH;
        const levelH = nodeH * 2;
        const pad    = 20;
        const W      = s.svgW + pad * 2;
        const H      = s.svgH + pad * 2;
        const ox     = pad;
        const oy     = pad;

        function esc(str) {
            return String(str ?? '')
                .replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')
                .replace(/"/g,'&quot;').replace(/'/g,'&#39;');
        }

        function roundRect(x, y, w, h, r, fill, stroke, sw, dashArray) {
            const d = `M${x+r},${y} H${x+w-r} Q${x+w},${y} ${x+w},${y+r} V${y+h-r} Q${x+w},${y+h} ${x+w-r},${y+h} H${x+r} Q${x},${y+h} ${x},${y+h-r} V${y+r} Q${x},${y} ${x+r},${y} Z`;
            const dash = dashArray ? ` stroke-dasharray="${esc(dashArray)}"` : '';
            return `<path d="${esc(d)}" fill="${esc(fill)}" stroke="${esc(stroke)}" stroke-width="${sw}"${dash}/>`;
        }

        let out = [];
        out.push(`<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">`);
        out.push(`<rect width="${W}" height="${H}" fill="#fafbfc"/>`);
        out.push(`<style>text{font-family:Inter,Segoe UI,sans-serif;}</style>`);

        // Edges
        for (const e of s.edges) {
            const ax1 = ox + e.x1, ay1 = oy + e.y1;
            const ax2 = ox + e.x2, ay2 = oy + e.y2;
            const cy1 = oy + e.y1 + levelH * 0.4;
            const cy2 = oy + e.y2 - levelH * 0.4;
            const color = e.isDashed ? '#f9a825' : '#9dafc0';
            const dash  = e.isDashed ? ' stroke-dasharray="5 3"' : '';

            out.push(`<path d="M${ax1},${ay1} C${ax1},${cy1} ${ax2},${cy2} ${ax2},${ay2}" fill="none" stroke="${color}" stroke-width="1.5"${dash}/>`);

            // Arrowhead
            const tdx = ax2 - ax2, tdy = ay2 - cy2;
            const len = Math.sqrt(tdy * tdy) || 1;
            const ux = 0, uy = 1;
            const nx = -1, ny = 0;
            const asz = 7;
            const p1x = ax2, p1y = ay2;
            const p2x = ax2 - ux*asz + nx*asz*0.4, p2y = ay2 - uy*asz + ny*asz*0.4;
            const p3x = ax2 - ux*asz - nx*asz*0.4, p3y = ay2 - uy*asz - ny*asz*0.4;
            out.push(`<polygon points="${p1x},${p1y} ${p2x},${p2y} ${p3x},${p3y}" fill="${color}"/>`);

            // Edge label — biased toward the target marking, matching the canvas view.
            if (e.label) {
                const t = 0.7;
                const mx = ox + (e.x1 + (e.x2 - e.x1) * t);
                const my = oy + (e.y1 + (e.y2 - e.y1) * t);
                const fs = 11;
                const tw = e.label.length * fs * 0.52;
                const pad2 = fs * 0.3;
                const rw = tw + pad2 * 2, rh = fs + pad2 * 2;
                out.push(roundRect(mx - rw/2, my - rh/2, rw, rh, 3, '#ffffff', '#d1d5db', 0.5, null));
                out.push(`<text x="${mx}" y="${my}" text-anchor="middle" dominant-baseline="middle" font-size="${fs}" font-weight="600" fill="#374151">${esc(e.label)}</text>`);
            }
        }

        // Nodes
        for (const n of s.nodes) {
            const ax = ox + n.x, ay = oy + n.y;
            const c  = n._colors;
            const rx = Math.max(2, nodeW * 0.12);
            const dash = n.isRef ? '4 2' : null;
            out.push(roundRect(ax, ay, nodeW, nodeH, rx, c.fill, c.stroke, c.sw, dash));

            // Label
            const cx = ax + nodeW / 2, cy2 = ay + nodeH / 2;
            if (n._subText) {
                const fs1 = Math.max(8, nodeW * 0.28 * 0.72);
                const fs2 = Math.max(7, nodeW * 0.20 * 0.72);
                out.push(`<text x="${cx}" y="${cy2 - nodeH*0.15}" text-anchor="middle" dominant-baseline="middle" font-size="${fs1}" font-weight="700" fill="${c.textC}">${esc(n.label)}</text>`);
                out.push(`<text x="${cx}" y="${cy2 + nodeH*0.22}" text-anchor="middle" dominant-baseline="middle" font-size="${fs2}" font-weight="600" fill="${c.textC}">${esc(n._subText)}</text>`);
            } else {
                const fs1 = Math.max(8, nodeW * 0.28 * 0.72);
                out.push(`<text x="${cx}" y="${cy2}" text-anchor="middle" dominant-baseline="middle" font-size="${fs1}" font-weight="700" fill="${c.textC}">${esc(n.label)}</text>`);
            }
        }

        out.push('</svg>');
        return out.join('\n');
    }

    // ── PNG export ────────────────────────────────────────────────────────
    // Renders the full tree to an offscreen canvas and returns a base64 PNG.
    // maxPx caps the longer dimension so huge trees don't produce enormous images.
    function exportPng(containerId, maxPx) {
        const s = _state[containerId];
        if (!s || !s.nodes || s.nodes.length === 0) return null;

        maxPx = maxPx || 4096;
        const pad = 20;
        const fullW = s.svgW + pad * 2;
        const fullH = s.svgH + pad * 2;

        // Scale to fit within maxPx on the longer side
        const scale = Math.min(1, maxPx / Math.max(fullW, fullH));
        const pw = Math.ceil(fullW * scale);
        const ph = Math.ceil(fullH * scale);

        const off = document.createElement('canvas');
        off.width  = pw;
        off.height = ph;
        const ctx = off.getContext('2d');

        ctx.fillStyle = '#fafbfc';
        ctx.fillRect(0, 0, pw, ph);

        const nodeW  = s.nodeW * scale;
        const nodeH  = s.nodeH * scale;
        const levelH = nodeH * 2;
        const ox = pad * scale, oy = pad * scale;

        // Edges
        for (const e of s.edges) {
            const ax1 = ox + e.x1 * scale, ay1 = oy + e.y1 * scale;
            const ax2 = ox + e.x2 * scale, ay2 = oy + e.y2 * scale;
            const cy1 = oy + (e.y1 + s.nodeH * 2 * 0.4) * scale;
            const cy2 = oy + (e.y2 - s.nodeH * 2 * 0.4) * scale;
            const color = e.isDashed ? '#f9a825' : '#9dafc0';

            ctx.beginPath();
            ctx.moveTo(ax1, ay1);
            ctx.bezierCurveTo(ax1, cy1, ax2, cy2, ax2, ay2);
            ctx.strokeStyle = color;
            ctx.lineWidth   = Math.max(1, 1.5 * scale);
            ctx.setLineDash(e.isDashed ? [5 * scale, 3 * scale] : []);
            ctx.stroke();
            ctx.setLineDash([]);

            // Arrowhead
            const tdy = ay2 - cy2;
            const len = Math.sqrt(tdy * tdy) || 1;
            const ux = 0, uy = 1;
            const nx = -1, ny = 0;
            const asz = Math.max(4, 7 * scale);
            ctx.beginPath();
            ctx.moveTo(ax2, ay2);
            ctx.lineTo(ax2 - ux * asz + nx * asz * 0.4, ay2 - uy * asz + ny * asz * 0.4);
            ctx.lineTo(ax2 - ux * asz - nx * asz * 0.4, ay2 - uy * asz - ny * asz * 0.4);
            ctx.closePath();
            ctx.fillStyle = color;
            ctx.fill();

            // Edge label
            if (e.label) {
                const mx = ox + (e.x1 + e.x2) / 2 * scale;
                const my = oy + (e.y1 + e.y2) / 2 * scale;
                const fs = Math.max(8, 11 * scale);
                ctx.font = `600 ${fs}px Inter,sans-serif`;
                const tw = ctx.measureText(e.label).width;
                const pd = fs * 0.3;
                const rw = tw + pd * 2, rh = fs + pd * 2;
                ctx.fillStyle = '#ffffff';
                ctx.strokeStyle = '#d1d5db';
                ctx.lineWidth = 0.5;
                ctx.beginPath();
                ctx.roundRect(mx - rw / 2, my - rh / 2, rw, rh, 3);
                ctx.fill(); ctx.stroke();
                ctx.fillStyle = '#374151';
                ctx.textAlign = 'center';
                ctx.textBaseline = 'middle';
                ctx.fillText(e.label, mx, my);
            }
        }

        // Nodes
        for (const n of s.nodes) {
            const ax = ox + n.x * scale;
            const ay = oy + n.y * scale;
            const rx = Math.max(2, nodeW * 0.12);
            const c  = n._colors;

            ctx.beginPath();
            ctx.roundRect(ax, ay, nodeW, nodeH, rx);
            ctx.fillStyle = c.fill;
            ctx.fill();
            ctx.strokeStyle = c.stroke;
            ctx.lineWidth = Math.max(1, c.sw * scale);
            if (n.isRef) ctx.setLineDash([Math.max(2, 4 * scale), Math.max(1, 2 * scale)]);
            ctx.stroke();
            ctx.setLineDash([]);

            // Text
            const fs = Math.max(8, s.nodeW * 0.28 * 0.72 * scale);
            ctx.fillStyle = c.textC;
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.font = `700 ${fs}px Inter,sans-serif`;
            const cx = ax + nodeW / 2, cy = ay + nodeH / 2;
            if (n._subText) {
                const fs2 = Math.max(7, s.nodeW * 0.20 * 0.72 * scale);
                ctx.fillText(n.label,    cx, cy - nodeH * 0.15);
                ctx.font = `600 ${fs2}px Inter,sans-serif`;
                ctx.fillText(n._subText, cx, cy + nodeH * 0.22);
            } else {
                ctx.fillText(n.label, cx, cy);
            }
        }

        // Strip the "data:image/png;base64," prefix — C# side uses Convert.FromBase64String
        return off.toDataURL('image/png').split(',')[1];
    }

    return { init, resetView, destroy, exportSvg, exportPng };
})();

// ── SVG element z-order helper ────────────────────────────────────────────
