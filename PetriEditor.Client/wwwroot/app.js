// ── Global keyboard handler ───────────────────────────────────────────────
window.registerGlobalKeyHandler = (dotnetRef) => {
    document.addEventListener('keydown', (e) => {
        // Don't steal keys while the user is typing in an input field
        const tag = e.target?.tagName;
        const isEditable = tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT'
            || e.target?.isContentEditable;
        if (isEditable) {
            // Still allow Escape to blur/cancel, but nothing else
            if (e.key !== 'Escape') return;
        }
        dotnetRef.invokeMethodAsync('OnGlobalKey', e.key, e.ctrlKey, e.shiftKey, e.altKey);
    });
    window._dotNetRef = dotnetRef;
};

window.unregisterGlobalKeyHandler = () => {
    // dotNetRef disposed — nothing further needed; the keydown listener
    // will stop invoking because the ref is gone.
    window._dotNetRef = null;
};

// Called by Blazor when the select tool is activated / deactivated
window.setSelectToolActive = (active) => {
    window._selectToolActive = active;
};

// ── Settings persistence ──────────────────────────────────────────────────────
const SETTINGS_KEY = 'petri-diagram-settings';

window.saveSettings = (obj) => {
    try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(obj)); } catch (_) {}
};

window.loadSettings = () => {
    try {
        const raw = localStorage.getItem(SETTINGS_KEY);
        return raw ? JSON.parse(raw) : null;
    } catch (_) { return null; }
};

// ── Placement ghost ───────────────────────────────────────────────────────
// nodeW, nodeH: actual rendered node dimensions in diagram-space pixels
// initClientX/Y: pointer position at mousedown — show ghost immediately
window.ghostStart = (type, gridSize, gridEnabled, nodeW, nodeH, initClientX, initClientY) => {
    window._ghost = { type, gridSize, gridEnabled, nodeW, nodeH };
    window._lastMouseX = initClientX;
    window._lastMouseY = initClientY;
    _updateGhost(initClientX, initClientY);
};
window.ghostStop = () => {
    window._ghost = null;
    const el = document.getElementById('placement-ghost');
    if (el) el.style.display = 'none';
};

function _updateGhost(clientX, clientY) {
    const g = window._ghost;
    if (!g) return;
    const ghostEl = document.getElementById('placement-ghost');
    const cont = document.getElementById('diagram-container');
    if (!ghostEl || !cont) return;

    const b = cont.getBoundingClientRect();
    const rawX = clientX - b.left;
    const rawY = clientY - b.top;
    // Only snap when grid is enabled
    let sx = rawX, sy = rawY;
    if (g.gridEnabled) {
        const gs = Math.max(4, g.gridSize || 20);
        sx = Math.round(rawX / gs) * gs;
        sy = Math.round(rawY / gs) * gs;
    }

    if (g.type === 'place') {
        const w = g.nodeW || 60;
        const h = g.nodeH || 60;
        const hw = w / 2, hh = h / 2;
        const r = hw - 2;
        ghostEl.style.width = w + 'px';
        ghostEl.style.height = h + 'px';
        ghostEl.style.left = (sx - hw) + 'px';
        ghostEl.style.top = (sy - hh) + 'px';
        ghostEl.innerHTML =
            `<svg width="${w}" height="${h}" viewBox="0 0 ${w} ${h}" style="overflow:visible;display:block;">` +
            `<circle cx="${hw}" cy="${hh}" r="${r}" ` +
            `fill="rgba(0,137,123,0.15)" stroke="#00897b" stroke-width="2" stroke-dasharray="6 3"/>` +
            `</svg>`;
    } else {
        const w = g.nodeW || 36;
        const h = g.nodeH || 72;
        const hw = w / 2, hh = h / 2;
        ghostEl.style.width = w + 'px';
        ghostEl.style.height = h + 'px';
        ghostEl.style.left = (sx - hw) + 'px';
        ghostEl.style.top = (sy - hh) + 'px';
        ghostEl.innerHTML =
            `<svg width="${w}" height="${h}" viewBox="0 0 ${w} ${h}" style="overflow:visible;display:block;">` +
            `<rect x="1" y="1" width="${w - 2}" height="${h - 2}" rx="3" ` +
            `fill="rgba(0,137,123,0.15)" stroke="#00897b" stroke-width="2" stroke-dasharray="6 3"/>` +
            `</svg>`;
    }
    ghostEl.style.display = 'block';
}

// ── One-time setup: pan + selection — called after first render ───────────
window.setupPanHandlerOnce = () => {
    const tryInstall = () => {
        const container = document.getElementById('diagram-container');
        if (container) setupOnContainer(container);
        else requestAnimationFrame(tryInstall);
    };
    tryInstall();
};

function setupOnContainer(container) {
    if (container._installed) return;
    container._installed = true;

    // Suppress right-click context menu
    container.addEventListener('contextmenu', (e) => e.preventDefault(), true);

    // ── Capture-phase pointerdown ─────────────────────────────────────────
    container.addEventListener('pointerdown', (e) => {

        // ── Right button → pan ───────────────────────────────────────────
        if (e.button === 2) {
            e.preventDefault();
            e.stopPropagation();
            container.setPointerCapture(e.pointerId);
            window._panDrag = {
                startX: e.clientX, startY: e.clientY,
                lastX: e.clientX, lastY: e.clientY,
                prevDx: 0, prevDy: 0,
                pointerId: e.pointerId,
                rafPending: false,
            };
            return;
        }

        // ── Left button, select tool ─────────────────────────────────────
        if (e.button === 0 && window._selectToolActive) {
            // Walk up from target to see if we landed on a diagram model.
            let el = e.target;
            let onModel = false;
            while (el && el !== container) {
                if (el.classList &&
                    (el.classList.contains('petri-node') ||
                        el.classList.contains('petri-link-group'))) {
                    onModel = true;
                    break;
                }
                el = el.parentElement;
            }

            if (onModel) {
                // Clicked a node/link — let Blazor.Diagrams handle it
                // completely (selection + drag). Do NOT stopPropagation,
                // do NOT arm the sel-rect.
                return;
            }

            // Empty canvas — arm the selection rect.
            // We do NOT stopPropagation so Blazor still receives model==null.
            const bounds = container.getBoundingClientRect();
            const sx = e.clientX - bounds.left;
            const sy = e.clientY - bounds.top;
            window._selDrag = {
                armed: true,
                active: false,
                startX: sx,
                startY: sy,
                pointerId: e.pointerId,
                rafPending: false,
            };
            // Create the visual rect overlay in JS — no Blazor roundtrip needed
            let overlay = document.getElementById('sel-rect-overlay-js');
            if (!overlay) {
                overlay = document.createElement('div');
                overlay.id = 'sel-rect-overlay-js';
                overlay.style.cssText = 'position:absolute;pointer-events:none;z-index:50;display:none;' +
                    'border:2px dashed #00a499;background:rgba(0,164,153,0.08);';
                container.appendChild(overlay);
            }
            overlay.style.display = 'none';
            overlay._sx = sx; overlay._sy = sy;
            if (window._dotNetRef)
                window._dotNetRef.invokeMethodAsync(
                    'OnSelRectArmed', sx, sy);
        }

    }, true); // capture phase

    // ── window pointermove — updates sel-rect and pan ─────────────────────
    window.addEventListener('pointermove', (e) => {

        // Pan
        const d = window._panDrag;
        if (d && e.pointerId === d.pointerId) {
            e.preventDefault();
            d.lastX = e.clientX; d.lastY = e.clientY;
            if (!d.rafPending) {
                d.rafPending = true;
                requestAnimationFrame(() => {
                    const drag = window._panDrag;
                    if (!drag) return;
                    drag.rafPending = false;
                    const ddx = (drag.lastX - drag.startX) - drag.prevDx;
                    const ddy = (drag.lastY - drag.startY) - drag.prevDy;
                    drag.prevDx += ddx; drag.prevDy += ddy;
                    if ((ddx || ddy) && window._dotNetRef)
                        window._dotNetRef.invokeMethodAsync('PanByDelta', ddx, ddy);
                });
            }
            return;
        }

        // ── Placement ghost — always track mouse, update ghost if active ───
        window._lastMouseX = e.clientX;
        window._lastMouseY = e.clientY;
        if (window._ghost) _updateGhost(e.clientX, e.clientY);

        // Sel-rect
        const s = window._selDrag;
        if (!s || !s.armed) return;
        const cont = document.getElementById('diagram-container');
        if (!cont) return;
        const bounds = cont.getBoundingClientRect();
        const cx = e.clientX - bounds.left;
        const cy = e.clientY - bounds.top;
        const dx = cx - s.startX, dy = cy - s.startY;
        const minDrag = 4;
        if (!s.active && (Math.abs(dx) > minDrag || Math.abs(dy) > minDrag))
            s.active = true;
        if (s.active) {
            // Update overlay directly in JS — instant, no Blazor roundtrip
            const overlay = document.getElementById('sel-rect-overlay-js');
            if (overlay) {
                const rx = Math.min(s.startX, cx);
                const ry = Math.min(s.startY, cy);
                const rw = Math.abs(dx);
                const rh = Math.abs(dy);
                overlay.style.left   = rx + 'px';
                overlay.style.top    = ry + 'px';
                overlay.style.width  = rw + 'px';
                overlay.style.height = rh + 'px';
                overlay.style.display = 'block';
            }
        }
        if (!s.rafPending) {
            s.rafPending = true;
            requestAnimationFrame(() => {
                if (!window._selDrag) return;
                window._selDrag.rafPending = false;
                if (window._dotNetRef)
                    window._dotNetRef.invokeMethodAsync('OnSelPointerMoveJS', cx, cy);
            });
        }
    });

    // ── window pointerup — commit sel-rect, end pan ───────────────────────
    window.addEventListener('pointerup', (e) => {
        if (window._panDrag && e.pointerId === window._panDrag.pointerId) {
            window._panDrag = null;
            return;
        }
        if (window._selDrag && window._selDrag.armed && e.button === 0) {
            const overlay = document.getElementById('sel-rect-overlay-js');
            if (overlay) overlay.style.display = 'none';
            window._selDrag = null;
            if (window._dotNetRef)
                window._dotNetRef.invokeMethodAsync('OnCanvasPointerUp');
        }
    });
}

// ── Analysis Panel — drag & resize ───────────────────────────────────────
window.analysisPanel = (() => {
    let _dotnet = null;
    let _floating = false;
    let _dockedW = 300, _dockedMin = 200, _dockedMax = 600;
    let _fx = 80, _fy = 80, _fw = 360, _fh = 480, _fMinW = 220, _fMinH = 200;

    let _drag = null;

    function setup(floating, dockedW, dockedMin, dockedMax,
        fx, fy, fw, fh, fMinW, fMinH, dotnet) {
        _floating = floating;
        _dockedW = dockedW; _dockedMin = dockedMin; _dockedMax = dockedMax;
        _fx = fx; _fy = fy; _fw = fw; _fh = fh; _fMinW = fMinW; _fMinH = fMinH;
        _dotnet = dotnet;

        requestAnimationFrame(() => {
            _unbind();
            if (_floating) {
                _bindFloating();
            } else {
                _bindDocked();
            }
        });
    }

    function _bindDocked() {
        const handle = document.getElementById('ap-resize-left');
        if (!handle) return;
        handle.addEventListener('pointerdown', _onDockedResizeDown, { passive: false });
    }

    function _onDockedResizeDown(e) {
        if (e.button !== 0) return;
        e.preventDefault();
        e.stopPropagation();
        const panel = document.getElementById('ap-panel');
        if (!panel) return;
        _drag = {
            type: 'docked-resize',
            startX: e.clientX,
            origW: panel.getBoundingClientRect().width,
        };
        document.addEventListener('pointermove', _onDockedResizeMove, { passive: false });
        document.addEventListener('pointerup', _onDockedResizeUp);
    }

    function _onDockedResizeMove(e) {
        if (!_drag) return;
        const delta = _drag.startX - e.clientX;
        const newW = Math.max(_dockedMin, Math.min(_dockedMax, _drag.origW + delta));
        _dotnet.invokeMethodAsync('UpdateDockedWidth', newW);
    }

    function _onDockedResizeUp() {
        _drag = null;
        document.removeEventListener('pointermove', _onDockedResizeMove);
        document.removeEventListener('pointerup', _onDockedResizeUp);
    }

    function _bindFloating() {
        const titlebar = document.getElementById('ap-titlebar');
        if (titlebar) {
            titlebar.addEventListener('pointerdown', _onMoveDown, { passive: false });
        }
        const dirs = ['n', 's', 'e', 'w', 'ne', 'nw', 'se', 'sw'];
        for (const dir of dirs) {
            const h = document.getElementById(`ap-resize-${dir}`);
            if (h) h.addEventListener('pointerdown', (e) => _onResizeDown(e, dir), { passive: false });
        }
    }

    function _onMoveDown(e) {
        if (e.button !== 0) return;
        if (e.target.closest('.ap-tb-btn')) return;
        e.preventDefault();
        e.stopPropagation();
        _drag = { type: 'move', startX: e.clientX, startY: e.clientY, origX: _fx, origY: _fy };
        document.addEventListener('pointermove', _onMoveMove, { passive: false });
        document.addEventListener('pointerup', _onMoveUp);
    }

    function _onMoveMove(e) {
        if (!_drag || _drag.type !== 'move') return;
        _fx = _drag.origX + (e.clientX - _drag.startX);
        _fy = _drag.origY + (e.clientY - _drag.startY);
        _fx = Math.max(-_fw + 60, _fx);
        _fy = Math.max(0, _fy);
        _dotnet.invokeMethodAsync('UpdateFloating', _fx, _fy, _fw, _fh);
    }

    function _onMoveUp() {
        _drag = null;
        document.removeEventListener('pointermove', _onMoveMove);
        document.removeEventListener('pointerup', _onMoveUp);
    }

    function _onResizeDown(e, dir) {
        if (e.button !== 0) return;
        e.preventDefault();
        e.stopPropagation();
        _drag = {
            type: 'resize', dir, startX: e.clientX, startY: e.clientY,
            origX: _fx, origY: _fy, origW: _fw, origH: _fh
        };
        document.addEventListener('pointermove', _onResizeMove, { passive: false });
        document.addEventListener('pointerup', _onResizeUp);
    }

    function _onResizeMove(e) {
        if (!_drag || _drag.type !== 'resize') return;
        const dx = e.clientX - _drag.startX;
        const dy = e.clientY - _drag.startY;
        const dir = _drag.dir;
        let { origX: x, origY: y, origW: w, origH: h } = _drag;

        if (dir.includes('e')) w = Math.max(_fMinW, w + dx);
        if (dir.includes('s')) h = Math.max(_fMinH, h + dy);
        if (dir.includes('w')) { const nw = Math.max(_fMinW, w - dx); x += w - nw; w = nw; }
        if (dir.includes('n')) { const nh = Math.max(_fMinH, h - dy); y += h - nh; h = nh; }

        _fx = x; _fy = y; _fw = w; _fh = h;
        _dotnet.invokeMethodAsync('UpdateFloating', x, y, w, h);
    }

    function _onResizeUp() {
        _drag = null;
        document.removeEventListener('pointermove', _onResizeMove);
        document.removeEventListener('pointerup', _onResizeUp);
    }

    function _unbind() {
        // Stale listeners on removed elements are GC'd automatically
    }

    function setupTabScroll() {
        requestAnimationFrame(() => {
            const scroll = document.getElementById('ap-tabs-scroll');
            const left = document.getElementById('ap-tabs-arrow-left');
            const right = document.getElementById('ap-tabs-arrow-right');
            if (!scroll || !left || !right) return;

            function update() {
                const atStart = scroll.scrollLeft <= 2;
                const atEnd = scroll.scrollLeft + scroll.clientWidth >= scroll.scrollWidth - 2;
                const hasOverflow = scroll.scrollWidth > scroll.clientWidth + 2;
                left.style.display = (hasOverflow && !atStart) ? 'flex' : 'none';
                right.style.display = (hasOverflow && !atEnd) ? 'flex' : 'none';
            }

            scroll.addEventListener('scroll', update, { passive: true });
            new ResizeObserver(update).observe(scroll);
            update();

            // Scroll wheel → horizontal scroll
            scroll.addEventListener('wheel', (e) => {
                e.preventDefault();
                scroll.scrollBy({ left: e.deltaY !== 0 ? e.deltaY : e.deltaX, behavior: 'smooth' });
            }, { passive: false });

            // Drag to scroll (skip if clicking directly on a button)
            let _tabDrag = null;
            scroll.addEventListener('pointerdown', (e) => {
                if (e.button !== 0) return;
                if (e.target.closest('button')) return;
                _tabDrag = { startX: e.clientX, startScroll: scroll.scrollLeft, pointerId: e.pointerId };
                scroll.setPointerCapture(e.pointerId);
                scroll.style.cursor = 'grabbing';
            });
            scroll.addEventListener('pointermove', (e) => {
                if (!_tabDrag || e.pointerId !== _tabDrag.pointerId) return;
                const dx = e.clientX - _tabDrag.startX;
                // Only activate drag if moved more than 4px (so clicks still work)
                if (Math.abs(dx) > 4) {
                    scroll.scrollLeft = _tabDrag.startScroll - dx;
                }
            });
            scroll.addEventListener('pointerup', (e) => {
                if (!_tabDrag || e.pointerId !== _tabDrag.pointerId) return;
                scroll.style.cursor = '';
                _tabDrag = null;
            });
            scroll.addEventListener('pointercancel', () => {
                scroll.style.cursor = '';
                _tabDrag = null;
            });
        });
    }

    function maximise() {
        const panel = document.getElementById('ap-panel');
        if (!panel) return;
        const margin = 10;
        const w = window.innerWidth  - margin * 2;
        const h = window.innerHeight - margin * 2;
        panel.style.left   = margin + 'px';
        panel.style.top    = margin + 'px';
        panel.style.width  = w + 'px';
        panel.style.height = h + 'px';
    }

    return { setup, setupTabScroll, maximise };
})();

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

            // Edge label — scales with zoom, hidden when zoomed out
            if (e.label && showEdgeLbls) {
                const mx = ox + ((e.x1 + e.x2) / 2) * scale;
                const my = oy + ((e.y1 + e.y2) / 2) * scale;
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
                const naturalPx = n._labelF * nodeW * scale;  // what size we want on screen
                const drawPx    = Math.max(MIN_TEXT_PX, naturalPx); // rasterize at least this big
                const inv       = naturalPx / drawPx;           // scale-down factor to compensate

                ctx.save();
                // Clip to node box (in current scale space)
                ctx.beginPath();
                ctx.roundRect(ax, ay, aw, ah, rx);
                ctx.clip();

                // Translate to node center, apply inverse scale so text renders at drawPx
                // but appears at naturalPx on screen
                ctx.translate(ax + aw / 2, ay + ah / 2);
                ctx.scale(inv, inv);

                ctx.fillStyle = hovered ? '#00796b' : c.textC;
                ctx.textAlign = 'center';
                ctx.textBaseline = 'middle';

                if (n._subText) {
                    const sNatural = n._subF * nodeW * scale;
                    const sDrawPx  = Math.max(MIN_TEXT_PX - 1, sNatural);
                    const sInv     = sNatural / sDrawPx;
                    // Label at 35% height relative to center = -ah*0.15 / inv
                    ctx.font = `700 ${drawPx}px Inter,sans-serif`;
                    ctx.fillText(n.label, 0, -ah * 0.15 / inv);
                    ctx.scale(sInv / inv, sInv / inv);  // adjust for sub size
                    ctx.font = `600 ${sDrawPx}px Inter,sans-serif`;
                    ctx.fillText(n._subText, 0, ah * 0.22 / sInv);
                } else {
                    ctx.font = `700 ${drawPx}px Inter,sans-serif`;
                    ctx.fillText(n.label, 0, 0);
                }

                ctx.restore();
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
            // Clamp to container
            if (tx + tw > wrap.clientWidth)  tx = cx - tw - 8;
            if (tx < 0)                       tx = 0;
            if (ty + th > wrap.clientHeight)  ty = wrap.clientHeight - th - 4;
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

        // ResizeObserver: refit canvas when panel resizes, and trigger initial view
        s._ro = new ResizeObserver(() => {
            _resizeCanvas(s);
            if (!s._viewInited) {
                _initView();
            } else {
                _scheduleRedraw(s);
            }
        });
        s._ro.observe(wrap);

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

            // Edge label
            if (e.label) {
                const mx = ox + (e.x1 + e.x2) / 2;
                const my = oy + (e.y1 + e.y2) / 2;
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
window.bringElementToFront = (el) => {
    if (el && el.parentNode) el.parentNode.appendChild(el);
};

// ── Cytoscape.js interop ───────────────────────────────────────────────────
window.petriEditor = window.petriEditor || {};
window.petriEditor._cy = {};

window.petriEditor.initCytoscape = function (containerId, elements, layoutName, dotnetCallback) {
    const container = document.getElementById(containerId);
    if (!container) return;

    if (window.petriEditor._cy[containerId]) {
        window.petriEditor._cy[containerId].destroy();
        delete window.petriEditor._cy[containerId];
    }

    // If container is hidden (display:none), defer init until it becomes visible
    if (container.offsetWidth === 0 && container.offsetHeight === 0) {
        const ro = new ResizeObserver((entries, obs) => {
            const el = entries[0].target;
            if (el.offsetWidth > 0 || el.offsetHeight > 0) {
                obs.disconnect();
                window.petriEditor.initCytoscape(containerId, elements, layoutName, dotnetCallback);
            }
        });
        ro.observe(container);
        return;
    }

    const cy = cytoscape({
        container,
        elements,
        userPanningEnabled: true,
        userZoomingEnabled: true,
        boxSelectionEnabled: false,
        autoungrabify: true,
        minZoom: 0.05,
        maxZoom: 4,
        wheelSensitivity: 0.15,
        textureOnViewport: false,
        hideEdgesOnViewport: false,
        hideLabelsOnViewport: false,
        style: [
            {
                selector: 'node',
                style: {
                    'label': 'data(label)',
                    'font-size': '14px',
                    'font-weight': '800',
                    'font-family': 'Inter, Segoe UI, sans-serif',
                    'color': '#111827',
                    'text-outline-color': '#ffffff',
                    'text-outline-width': 4,
                    'background-color': '#ffffff',
                    'border-color': '#9dafc0',
                    'border-width': 2,
                    'width': 62,
                    'height': 62,
                    'shape': 'ellipse',
                    'text-valign': 'center',
                    'text-halign': 'center',
                    'text-max-width': '54px',
                    'text-wrap': 'ellipsis',
                    'min-zoomed-font-size': 0,     // let LOD handle hiding, not Cytoscape's default
                    'transition-property': 'background-color, border-color, border-width',
                    'transition-duration': '0.12s',
                }
            },
            // LOD: hide labels when zoomed out far
            {
                selector: 'node.lod-hide-label',
                style: {
                    'label': '',
                    'text-outline-width': 0,
                }
            },
            {
                selector: 'node.initial',
                style: {
                    'border-width': 3,
                    'border-color': '#00a499',
                    'background-color': '#e8faf8',
                    'color': '#00796b',
                    'text-outline-color': '#e8faf8',
                }
            },
            {
                selector: 'node.deadlock',
                style: {
                    'background-color': '#fff0f0',
                    'border-color': '#e53935',
                    'color': '#b71c1c',
                    'text-outline-color': '#fff0f0',
                }
            },
            {
                selector: 'node.duplicate',
                style: {
                    'background-color': '#faf0ff',
                    'border-color': '#9c27b0',
                    'border-style': 'dashed',
                    'color': '#6a1b9a',
                    'text-outline-color': '#faf0ff',
                }
            },
            {
                selector: 'node.omega',
                style: {
                    'background-color': '#f0f0fb',
                    'border-color': '#5c6bc0',
                    'color': '#3949ab',
                    'text-outline-color': '#f0f0fb',
                }
            },
            {
                selector: 'node.cutoff',
                style: {
                    'background-color': '#f3e5f5',
                    'border-color': '#8e24aa',
                    'color': '#6a1b9a',
                    'text-outline-color': '#f3e5f5',
                }
            },
            {
                selector: 'node:selected',
                style: {
                    'border-color': '#00a499',
                    'border-width': 3,
                    'background-color': '#e0f5f3',
                }
            },
            {
                selector: 'edge',
                style: {
                    'label': 'data(label)',
                    'font-size': '11px',
                    'font-weight': '600',
                    'font-family': 'Inter, Segoe UI, sans-serif',
                    'color': '#374151',
                    'curve-style': 'bezier',
                    'control-point-step-size': 40,
                    'target-arrow-shape': 'triangle',
                    'arrow-scale': 1,
                    'line-color': '#9dafc0',
                    'target-arrow-color': '#9dafc0',
                    'text-background-color': '#ffffff',
                    'text-background-opacity': 1,
                    'text-background-padding': '3px',
                    'text-background-shape': 'roundrectangle',
                }
            },
            // LOD: hide edge labels when zoomed out
            {
                selector: 'edge.lod-hide-label',
                style: {
                    'label': '',
                    'text-background-opacity': 0,
                }
            }
        ],
        layout: {
            name: layoutName || 'breadthfirst',
            directed: true,
            spacingFactor: 2.4,
            nodeDimensionsIncludeLabels: true,
            padding: 48,
            avoidOverlap: true,
            grid: false,
        }
    });

    // ── LOD: toggle labels based on zoom level ───────────────────────────
    let _lodState = 'full'; // 'full' | 'noEdgeLabels' | 'noLabels'
    function updateLod() {
        const zoom = cy.zoom();
        let newState;
        // Match tree LOD: hide all labels when node screen size < 20px (zoom < 20/54 ≈ 0.37)
        if (zoom < 0.25) newState = 'noLabels';
        else newState = 'full';

        if (newState === _lodState) return;
        _lodState = newState;

        cy.batch(function() {
            if (newState === 'noLabels') {
                cy.nodes().addClass('lod-hide-label');
                cy.edges().addClass('lod-hide-label');
            } else {
                cy.nodes().removeClass('lod-hide-label');
                cy.edges().removeClass('lod-hide-label');
            }
        });
    }

    cy.on('zoom', updateLod);

    // After layout finishes positioning nodes, zoom to M0 at a comfortable level
    cy.one('layoutstop', function() {
        const initNode = cy.nodes('.initial');
        if (initNode.length > 0) {
            cy.fit(initNode, 80);
            cy.zoom(Math.min(cy.zoom() * 1.5, 1.4));
            cy.center(initNode);
        } else {
            cy.fit(undefined, 48);
        }
        updateLod();
    });

    // Tooltip element
    let tooltip = container.querySelector('.cy-tooltip');
    if (!tooltip) {
        tooltip = document.createElement('div');
        tooltip.className = 'cy-tooltip';
        tooltip.style.cssText = `
            position:absolute;pointer-events:none;display:none;
            background:#fff;border:1px solid #e4e6ea;border-radius:8px;
            box-shadow:0 4px 16px rgba(0,0,0,0.12);padding:8px 10px;
            min-width:140px;max-width:280px;font-family:Inter,sans-serif;
            font-size:11px;z-index:9999;
        `;
        container.style.position = 'relative';
        container.appendChild(tooltip);
    }

    cy.on('mouseover', 'node', function(e) {
        const node = e.target;
        const data = node.data();
        if (!data.marking || data.marking.length === 0) return;

        const names = data.placeNames || [];
        let html = `<div style="font-weight:700;color:#374151;margin-bottom:6px;padding-bottom:5px;border-bottom:1px solid #f0f0f0;">${data.label}</div>`;
        for (let i = 0; i < data.marking.length; i++) {
            const name = names[i] || `p${i}`;
            const val  = data.marking[i] === -1 ? 'ω' : data.marking[i];
            const hot  = data.marking[i] > 0 || data.marking[i] === -1;
            const omega = data.marking[i] === -1;
            html += `<div style="display:flex;justify-content:space-between;align-items:baseline;gap:8px;padding:1px 0;
                     color:${hot ? '#222' : '#9aa0ad'};font-weight:${hot ? '600' : '400'};">
                       <span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;min-width:0;flex:1;">${name}</span>
                       <span style="color:${omega ? '#7c4dff' : hot ? '#00a499' : '#bbb'};flex-shrink:0;">${val}</span>
                     </div>`;
        }
        tooltip.innerHTML = html;
        tooltip.style.display = 'block';

        if (dotnetCallback) {
            dotnetCallback.invokeMethodAsync('OnNodeHover', node.id());
        }
    });

    cy.on('mouseout', 'node', function() {
        tooltip.style.display = 'none';
        if (dotnetCallback) {
            dotnetCallback.invokeMethodAsync('OnNodeOut');
        }
    });

    cy.on('mousemove', function(e) {
        if (tooltip.style.display === 'none') return;
        const rect = container.getBoundingClientRect();
        const tw = tooltip.offsetWidth || 230;
        const th = tooltip.offsetHeight || 120;
        const cx = e.originalEvent.clientX - rect.left;
        const cy2 = e.originalEvent.clientY - rect.top;
        let tx = cx + 12, ty = cy2 - 10;
        if (tx + tw > container.clientWidth)  tx = cx - tw - 8;
        if (tx < 0)                            tx = 0;
        if (ty + th > container.clientHeight) ty = container.clientHeight - th - 4;
        if (ty < 0)                            ty = 0;
        tooltip.style.left = tx + 'px';
        tooltip.style.top  = ty + 'px';
    });

    if (dotnetCallback) {
        cy.on('tap', 'node', function(e) {
            const nodeId = e.target.id();
            dotnetCallback.invokeMethodAsync('OnNodeClick', nodeId);
        });
    }

    window.petriEditor._cy[containerId] = cy;

    // Resize cy canvas when panel is resized
    const resizeObs = new ResizeObserver(() => cy.resize());
    resizeObs.observe(container);
    cy.on('destroy', () => resizeObs.disconnect());
};

// ── Diagram PNG export ────────────────────────────────────────────────────
// Draws the Petri net onto an offscreen canvas by reading node positions from
// the DOM and arc paths from the SVG layer. Crops tightly to node bounds.
// Returns a Promise<string|null> (base64 PNG, no data: prefix).
window.petriEditor.exportDiagramPng = function (maxPx, nodes) {
    // nodes: array of { id, type:'place'|'transition', x, y, w, h, label, tokens, arcColor }
    // passed from Blazor since JS can't read Blazor component state directly
    maxPx = maxPx || 3072;
    const pad = 32;

    const container = document.getElementById('diagram-container');
    if (!container) return Promise.resolve(null);

    // ── 1. Compute tight bounding box from node list ──────────────────────
    if (!nodes || nodes.length === 0) return Promise.resolve(null);
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (const n of nodes) {
        minX = Math.min(minX, n.x);
        minY = Math.min(minY, n.y);
        maxX = Math.max(maxX, n.x + n.w);
        maxY = Math.max(maxY, n.y + n.h);
    }
    const contentW = maxX - minX;
    const contentH = maxY - minY;
    const fullW = contentW + pad * 2;
    const fullH = contentH + pad * 2;

    // ── 2. Scale to maxPx ─────────────────────────────────────────────────
    const scale = Math.min(2, maxPx / Math.max(fullW, fullH));
    const pw = Math.ceil(fullW * scale);
    const ph = Math.ceil(fullH * scale);

    // Offset: world coords → canvas coords
    const ox = (pad - minX) * scale;
    const oy = (pad - minY) * scale;

    const off = document.createElement('canvas');
    off.width  = pw;
    off.height = ph;
    const ctx = off.getContext('2d');
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, pw, ph);

    // ── 3. Draw arcs from the live SVG ────────────────────────────────────
    // Blazor.Diagrams renders arcs as <path> elements in the top-level SVG.
    // Their coordinates are in diagram world space (same coordinate system as node x/y).
    // The SVG has a <g> with a transform="translate(tx,ty) scale(s)" — extract it.
    const liveSvg = container.querySelector('svg');
    if (liveSvg) {
        // Find the pan/zoom group — Blazor.Diagrams wraps everything in a <g class="diagram-canvas">
        const panGroup = liveSvg.querySelector('g[class*="diagram"]') || liveSvg.querySelector('g');
        let tx = 0, ty = 0, sc = 1;
        if (panGroup) {
            const tf = panGroup.getAttribute('transform') || '';
            const tMatch = tf.match(/translate\(\s*([-\d.]+)[,\s]+([-\d.]+)\s*\)/);
            const sMatch = tf.match(/scale\(\s*([-\d.]+)\s*\)/);
            if (tMatch) { tx = parseFloat(tMatch[1]); ty = parseFloat(tMatch[2]); }
            if (sMatch) { sc = parseFloat(sMatch[1]); }
        }

        // Collect all arc paths (skip UI chrome)
        const paths = liveSvg.querySelectorAll('path[d]');
        ctx.save();
        for (const path of paths) {
            // Skip if it's inside a foreignObject or is a UI element
            if (path.closest('foreignObject')) continue;
            if (path.closest('.endpoint-handle, .vertex-handle, [data-id="sel-rect"]')) continue;

            const d = path.getAttribute('d');
            if (!d) continue;
            const stroke = path.getAttribute('stroke') || path.style.stroke || '#555';
            const sw = parseFloat(path.getAttribute('stroke-width') || '2');
            const fill = path.getAttribute('fill') || 'none';
            const dashArr = path.getAttribute('stroke-dasharray') || '';

            // Re-parse the path data, applying the pan/zoom transform then our offset
            // We need to transform from SVG coords → world coords → canvas coords
            // SVG coords = world*sc + [tx,ty], so world = (SVG - [tx,ty]) / sc
            // canvas = world * scale + [ox, oy]
            // Combined: canvas = (SVG - [tx,ty]) / sc * scale + [ox, oy]
            const combinedScale = scale / sc;
            const ctx2d = _transformPath(ctx, d, tx, ty, combinedScale, ox, oy);

            ctx.strokeStyle = stroke;
            ctx.lineWidth = Math.max(1, sw * combinedScale);
            ctx.fillStyle = fill === 'none' ? 'transparent' : fill;
            if (dashArr) ctx.setLineDash(dashArr.split(/[\s,]+/).map(Number).map(v => v * combinedScale));
            else ctx.setLineDash([]);
            if (fill !== 'none') ctx.fill();
            ctx.stroke();
        }

        // Draw circles (inhibitor arc endpoints)
        const circles = liveSvg.querySelectorAll('circle');
        for (const el of circles) {
            if (el.closest('foreignObject')) continue;
            if (el.closest('marker')) continue;
            const r = el.getAttribute('r');
            if (!r || parseFloat(r) < 3) continue; // skip tiny port dots
            const cxAttr = parseFloat(el.getAttribute('cx') || '0');
            const cyAttr = parseFloat(el.getAttribute('cy') || '0');
            const [pcx, pcy] = [(cxAttr - tx) * combinedScale + ox, (cyAttr - ty) * combinedScale + oy];
            const pr = parseFloat(r) * combinedScale;
            const fill = el.getAttribute('fill') || 'white';
            const stroke = el.getAttribute('stroke') || '#555';
            const sw = parseFloat(el.getAttribute('stroke-width') || '2');
            ctx.beginPath();
            ctx.arc(pcx, pcy, pr, 0, Math.PI * 2);
            ctx.fillStyle = fill;
            ctx.fill();
            ctx.strokeStyle = stroke;
            ctx.lineWidth = Math.max(1, sw * combinedScale);
            ctx.stroke();
        }

        // Draw arc weight labels (text elements outside foreignObject)
        const texts = liveSvg.querySelectorAll('text');
        for (const el of texts) {
            if (el.closest('foreignObject')) continue;
            const xAttr = parseFloat(el.getAttribute('x') || '0');
            const yAttr = parseFloat(el.getAttribute('y') || '0');
            const [px, py] = [(xAttr - tx) * combinedScale + ox, (yAttr - ty) * combinedScale + oy];
            const content = el.textContent?.trim();
            if (!content) continue;
            const fill = el.getAttribute('fill') || '#333';
            const fontSize = parseFloat(el.getAttribute('font-size') || '12') * combinedScale;
            const anchor = el.getAttribute('text-anchor') || 'start';
            ctx.save();
            ctx.font = `bold ${Math.max(8, fontSize)}px Inter,sans-serif`;
            ctx.fillStyle = fill;
            ctx.textAlign = anchor === 'middle' ? 'center' : anchor === 'end' ? 'right' : 'left';
            ctx.textBaseline = el.getAttribute('dominant-baseline') === 'central' ? 'middle' : 'alphabetic';
            ctx.fillText(content, px, py);
            ctx.restore();
        }

        ctx.restore();
    }

    // ── 4. Draw nodes ─────────────────────────────────────────────────────
    for (const n of nodes) {
        const cx = ox + (n.x + n.w / 2) * scale;
        const cy = oy + (n.y + n.h / 2) * scale;
        const nw = n.w * scale;
        const nh = n.h * scale;
        const color = n.arcColor || '#555555';

        ctx.save();
        if (n.type === 'place') {
            const r = nw / 2;
            ctx.beginPath();
            ctx.arc(cx, cy, r - scale, 0, Math.PI * 2);
            ctx.fillStyle = '#ffffff';
            ctx.fill();
            ctx.strokeStyle = color;
            ctx.lineWidth = 2 * scale;
            ctx.stroke();

            // Tokens
            const tokens = n.tokens || 0;
            if (tokens > 0) {
                ctx.fillStyle = '#000000';
                if (tokens <= 5) {
                    const positions = _tokenPositions(tokens, 0, 0, r * 0.5);
                    for (const [tx2, ty2] of positions) {
                        ctx.beginPath();
                        ctx.arc(cx + tx2 * scale, cy + ty2 * scale, 5 * scale, 0, Math.PI * 2);
                        ctx.fill();
                    }
                } else {
                    ctx.font = `bold ${Math.max(11, r * 0.6)}px Inter,sans-serif`;
                    ctx.textAlign = 'center';
                    ctx.textBaseline = 'middle';
                    ctx.fillText(String(tokens), cx, cy);
                }
            }
        } else {
            // Transition — rectangle
            ctx.beginPath();
            ctx.rect(ox + n.x * scale, oy + n.y * scale, nw, nh);
            ctx.fillStyle = color;
            ctx.fill();
            ctx.strokeStyle = color;
            ctx.lineWidth = 2 * scale;
            ctx.stroke();
        }

        // Label (node title)
        if (n.label) {
            const fs = Math.max(10, Math.min(14, nw * 0.28)) * scale;
            ctx.font = `600 ${fs}px Inter,sans-serif`;
            ctx.textAlign = 'center';
            ctx.textBaseline = 'top';
            ctx.fillStyle = '#333333';
            ctx.fillText(n.label, cx, oy + (n.y + n.h + 4) * scale);
        }
        ctx.restore();
    }

    return Promise.resolve(off.toDataURL('image/png').split(',')[1]);
};

// Parse SVG path "d" attribute and stroke it on a canvas context,
// transforming from SVG space (with pan tx/ty and zoom sc) to canvas space.
function _transformPath(ctx, d, tx, ty, combinedScale, ox, oy) {
    // Transform a single coordinate pair
    function T(x, y) {
        return [(x - tx) * combinedScale + ox, (y - ty) * combinedScale + oy];
    }
    // Very small subset parser for M, L, C, Q, Z (covers Blazor.Diagrams arc paths)
    const tokens = d.trim().match(/[MLCQZmlcqz]|[-+]?[0-9]*\.?[0-9]+(?:e[-+]?[0-9]+)?/gi) || [];
    ctx.beginPath();
    let i = 0, cx = 0, cy = 0;
    while (i < tokens.length) {
        const cmd = tokens[i];
        if (!/[a-z]/i.test(cmd)) { i++; continue; }
        i++;
        if (cmd === 'M' || cmd === 'm') {
            const x = parseFloat(tokens[i++]), y = parseFloat(tokens[i++]);
            const [px, py] = T(x, y);
            ctx.moveTo(px, py); cx = x; cy = y;
        } else if (cmd === 'L' || cmd === 'l') {
            const x = parseFloat(tokens[i++]), y = parseFloat(tokens[i++]);
            const [px, py] = T(x, y);
            ctx.lineTo(px, py); cx = x; cy = y;
        } else if (cmd === 'C' || cmd === 'c') {
            const x1 = parseFloat(tokens[i++]), y1 = parseFloat(tokens[i++]);
            const x2 = parseFloat(tokens[i++]), y2 = parseFloat(tokens[i++]);
            const x  = parseFloat(tokens[i++]), y  = parseFloat(tokens[i++]);
            const [p1x,p1y] = T(x1,y1), [p2x,p2y] = T(x2,y2), [px,py] = T(x,y);
            ctx.bezierCurveTo(p1x,p1y,p2x,p2y,px,py); cx=x; cy=y;
        } else if (cmd === 'Q' || cmd === 'q') {
            const x1 = parseFloat(tokens[i++]), y1 = parseFloat(tokens[i++]);
            const x  = parseFloat(tokens[i++]), y  = parseFloat(tokens[i++]);
            const [p1x,p1y] = T(x1,y1), [px,py] = T(x,y);
            ctx.quadraticCurveTo(p1x,p1y,px,py); cx=x; cy=y;
        } else if (cmd === 'Z' || cmd === 'z') {
            ctx.closePath();
        } else { i++; }
    }
}

// Token dot positions for ≤5 tokens (relative offsets from center, in world units)
function _tokenPositions(count, cx, cy, r) {
    const positions = {
        1: [[0,0]],
        2: [[-r*0.4,0],[r*0.4,0]],
        3: [[0,-r*0.4],[-r*0.4,r*0.3],[r*0.4,r*0.3]],
        4: [[-r*0.35,-r*0.35],[r*0.35,-r*0.35],[-r*0.35,r*0.35],[r*0.35,r*0.35]],
        5: [[0,0],[-r*0.4,-r*0.4],[r*0.4,-r*0.4],[-r*0.4,r*0.4],[r*0.4,r*0.4]],
    };
    return (positions[count] || positions[1]).map(([dx,dy]) => [cx+dx, cy+dy]);
}

// Keep old SVG export for standalone .svg file downloads
window.petriEditor.exportDiagramSvg = function () {
    const container = document.getElementById('diagram-container');
    if (!container) return null;
    const liveSvg = container.querySelector('svg');
    if (!liveSvg) return null;
    const clone = liveSvg.cloneNode(true);
    clone.querySelectorAll(
        '.endpoint-handle, .vertex-handle, [data-id="sel-rect"], ' +
        '.link--pending, .link--dragging-endpoint'
    ).forEach(el => el.remove());
    clone.querySelectorAll('foreignObject').forEach(el => el.remove());
    let styleText = '';
    try {
        for (const sheet of document.styleSheets) {
            try { for (const rule of sheet.cssRules) styleText += rule.cssText + '\n'; } catch { }
        }
    } catch { }
    const styleEl = document.createElementNS('http://www.w3.org/2000/svg', 'style');
    styleEl.textContent = styleText;
    clone.insertBefore(styleEl, clone.firstChild);
    clone.setAttribute('style', 'background:#ffffff;');
    return new XMLSerializer().serializeToString(clone);
};

window.petriEditor.exportCytoscapeSvg = function (containerId) {
    const cy = window.petriEditor._cy[containerId];
    if (!cy) return null;

    function esc(s) {
        return String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
    }

    const ext   = cy.extent();
    const pad   = 60;
    const ox    = -ext.x1 + pad;
    const oy    = -ext.y1 + pad;
    const W     = ext.x2 - ext.x1 + pad * 2;
    const H     = ext.y2 - ext.y1 + pad * 2;
    const nodeR = 31; // radius (cy node width = 62)

    // Node colour map matching the cytoscape styles
    function nodeColors(node) {
        const cls = node.classes();
        if (cls.has('initial'))   return { fill: '#e8faf8', stroke: '#00a499', text: '#00695c', sw: 3 };
        if (cls.has('deadlock'))  return { fill: '#ffebee', stroke: '#e53935', text: '#b71c1c', sw: 2 };
        if (cls.has('omega'))     return { fill: '#f0f0fb', stroke: '#5c6bc0', text: '#283593', sw: 2 };
        if (cls.has('duplicate')) return { fill: '#fffde7', stroke: '#f9a825', text: '#e65100', sw: 2 };
        if (cls.has('cutoff'))    return { fill: '#f3e5f5', stroke: '#8e24aa', text: '#6a1b9a', sw: 2 };
        return { fill: '#ffffff', stroke: '#9dafc0', text: '#111827', sw: 2 };
    }

    let out = [];
    out.push(`<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">`);
    out.push(`<rect width="${W}" height="${H}" fill="#fafbfc"/>`);
    out.push(`<defs><marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0,0 L0,6 L8,3 Z" fill="#9dafc0"/></marker></defs>`);
    out.push(`<style>text{font-family:Inter,Segoe UI,sans-serif;}</style>`);

    // Edges first (under nodes)
    cy.edges().forEach(edge => {
        const src = edge.source().position();
        const tgt = edge.target().position();
        const sx = src.x + ox, sy = src.y + oy;
        const tx = tgt.x + ox, ty = tgt.y + oy;

        // Shorten line to circle boundary
        const dx = tx - sx, dy = ty - sy;
        const dist = Math.sqrt(dx*dx + dy*dy) || 1;
        const ux = dx/dist, uy = dy/dist;
        const x1 = sx + ux * nodeR, y1 = sy + uy * nodeR;
        const x2 = tx - ux * (nodeR + 8), y2 = ty - uy * (nodeR + 8);

        const label = edge.data('label') ?? '';
        // Slight curve for parallel edges
        const mx = (x1+x2)/2, my = (y1+y2)/2;
        const cx1 = mx - uy*20, cy1 = my + ux*20;
        out.push(`<path d="M${x1},${y1} Q${cx1},${cy1} ${x2},${y2}" fill="none" stroke="#9dafc0" stroke-width="1.5" marker-end="url(#arr)"/>`);
        if (label) {
            const lx = (x1 + cx1 + x2) / 3, ly = (y1 + cy1 + y2) / 3;
            const fs = 11;
            const tw = label.length * fs * 0.52 + 8;
            out.push(`<rect x="${lx - tw/2}" y="${ly - 9}" width="${tw}" height="16" rx="3" fill="#fff" stroke="#d1d5db" stroke-width="0.5"/>`);
            out.push(`<text x="${lx}" y="${ly}" text-anchor="middle" dominant-baseline="middle" font-size="${fs}" font-weight="600" fill="#374151">${esc(label)}</text>`);
        }
    });

    // Nodes
    cy.nodes().forEach(node => {
        const p = node.position();
        const x = p.x + ox, y = p.y + oy;
        const c = nodeColors(node);
        const label = node.data('label') ?? '';
        out.push(`<circle cx="${x}" cy="${y}" r="${nodeR}" fill="${c.fill}" stroke="${c.stroke}" stroke-width="${c.sw}"/>`);
        if (label) {
            const fs = Math.min(14, Math.max(8, nodeR * 0.42));
            out.push(`<text x="${x}" y="${y}" text-anchor="middle" dominant-baseline="middle" font-size="${fs}" font-weight="800" fill="${c.text}">${esc(label)}</text>`);
        }
    });

    out.push('</svg>');
    return out.join('\n');
};

// Returns base64 PNG (no data: prefix) of the full Cytoscape graph, capped at maxPx on the longer side.
window.petriEditor.exportCytoscapePng = function (containerId, maxPx) {
    const cy = window.petriEditor._cy[containerId];
    if (!cy) return null;
    maxPx = maxPx || 4096;
    const bb = cy.elements().boundingBox();
    const w = bb.w || 1, h = bb.h || 1;
    const scale = Math.min(2, maxPx / Math.max(w, h));   // up to 2× for sharpness
    const dataUrl = cy.png({ scale, full: true, bg: '#fafbfc' });
    return dataUrl.split(',')[1];   // strip "data:image/png;base64,"
};

window.petriEditor.destroyCytoscape = function (containerId) {
    if (window.petriEditor._cy[containerId]) {
        window.petriEditor._cy[containerId].destroy();
        delete window.petriEditor._cy[containerId];
    }
};

window.petriEditor.fitCytoscape = function (containerId) {
    window.petriEditor._cy[containerId]?.fit(undefined, 32);
};

// ── File download ─────────────────────────────────────────────────────────
// Triggers a browser download of arbitrary bytes.
// contentBase64: the file bytes encoded as a base64 string (use Convert.ToBase64String in C#)
window.petriEditor.downloadFile = function (filename, contentBase64, mimeType) {
    const bytes = Uint8Array.from(atob(contentBase64), c => c.charCodeAt(0));
    const blob = new Blob([bytes], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};

// ── File upload (open file dialog) ───────────────────────────────────────
// Opens a file-picker limited to the given accept pattern (e.g. ".pnml,.xml").
// Returns the file contents as a UTF-8 string via a Promise.
window.petriEditor.openFileText = function (accept) {
    return new Promise((resolve, reject) => {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = accept || '*';
        input.onchange = () => {
            const file = input.files[0];
            if (!file) { reject(new Error('No file selected.')); return; }
            const reader = new FileReader();
            reader.onload = e => resolve(e.target.result);
            reader.onerror = () => reject(new Error('Failed to read file.'));
            reader.readAsText(file, 'UTF-8');
        };
        input.click();
    });
};