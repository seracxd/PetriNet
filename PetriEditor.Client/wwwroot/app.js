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
            window._selDrag = {
                armed: true,
                active: false,
                startX: e.clientX - bounds.left,
                startY: e.clientY - bounds.top,
                pointerId: e.pointerId,
                rafPending: false,
            };
            if (window._dotNetRef)
                window._dotNetRef.invokeMethodAsync(
                    'OnSelRectArmed',
                    window._selDrag.startX,
                    window._selDrag.startY);
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
    const _state = {};   // keyed by containerId

    // Margin (in SVG units) outside viewBox where elements are still kept visible.
    // Elements beyond this margin get display:none.
    const CULL_MARGIN = 40;

    function _cullNodes(s) {
        const x0 = s.vx - CULL_MARGIN;
        const y0 = s.vy - CULL_MARGIN;
        const x1 = s.vx + s.vw + CULL_MARGIN;
        const y1 = s.vy + s.vh + CULL_MARGIN;

        const nodeW = s.nodeW, nodeH = s.nodeH;

        // LOD: pixel size of a node on screen.
        // When nodes are tiny, hide text elements for massive perf gain.
        const wrapW = s._wrapWidth || 800;
        const nodeScreenPx = (nodeW / s.vw) * wrapW;
        const showText = nodeScreenPx > 10;   // hide text when nodes are tiny on screen
        const showEdgeLabels = nodeScreenPx > 8;

        // Cached node groups (built during init)
        const nodeGs = s._nodeGs;
        for (let i = 0; i < nodeGs.length; i++) {
            const g = nodeGs[i];
            const t = g._treePos;
            const visible = t.x + nodeW >= x0 && t.x <= x1 && t.y + nodeH >= y0 && t.y <= y1;
            g.style.display = visible ? '' : 'none';
            if (visible && g._textEls) {
                const d = showText ? '' : 'none';
                const els = g._textEls;
                for (let j = 0; j < els.length; j++) els[j].style.display = d;
            }
        }

        // Cached edge groups
        const edgeGs = s._edgeGs;
        for (let i = 0; i < edgeGs.length; i++) {
            const g = edgeGs[i];
            const t = g._treeEdge;
            const ex0 = Math.min(t.x1, t.x2) - CULL_MARGIN;
            const ey0 = Math.min(t.y1, t.y2) - CULL_MARGIN;
            const ex1 = Math.max(t.x1, t.x2) + CULL_MARGIN;
            const ey1 = Math.max(t.y1, t.y2) + CULL_MARGIN;
            const visible = ex1 >= x0 && ex0 <= x1 && ey1 >= y0 && ey0 <= y1;
            g.style.display = visible ? '' : 'none';
            if (visible && g._labelEls) {
                const d = showEdgeLabels ? '' : 'none';
                const els = g._labelEls;
                for (let j = 0; j < els.length; j++) els[j].style.display = d;
            }
        }
    }

    const SVG_NS = 'http://www.w3.org/2000/svg';

    function _buildSvgContent(svg, nodes, edges, markerId, nodeW, nodeH) {
        // Clear any previous content
        while (svg.firstChild) svg.removeChild(svg.firstChild);

        const levelH = nodeH * 2;

        // defs + arrowhead marker
        const defs = document.createElementNS(SVG_NS, 'defs');
        const marker = document.createElementNS(SVG_NS, 'marker');
        marker.setAttribute('id', markerId);
        marker.setAttribute('markerWidth', '8');
        marker.setAttribute('markerHeight', '8');
        marker.setAttribute('refX', '4');
        marker.setAttribute('refY', '4');
        marker.setAttribute('orient', 'auto');
        const arrowPath = document.createElementNS(SVG_NS, 'path');
        arrowPath.setAttribute('d', 'M 0 1 L 8 4 L 0 7 Z');
        arrowPath.setAttribute('fill', '#c8d0dc');
        marker.appendChild(arrowPath);
        defs.appendChild(marker);
        svg.appendChild(defs);

        // edges (rendered first, under nodes)
        const edgeGs = [];
        for (let i = 0; i < edges.length; i++) {
            const e = edges[i];
            const x1 = e.x1, y1 = e.y1, x2 = e.x2, y2 = e.y2;
            const mx = (x1 + x2) / 2, my = (y1 + y2) / 2;

            const g = document.createElementNS(SVG_NS, 'g');
            g.setAttribute('class', 'tree-edge-g');
            g._treeEdge = { x1, y1, x2, y2 };

            const path = document.createElementNS(SVG_NS, 'path');
            const cy1 = y1 + levelH * 0.4;
            const cy2 = y2 - levelH * 0.4;
            path.setAttribute('d', `M ${x1} ${y1} C ${x1} ${cy1} ${x2} ${cy2} ${x2} ${y2}`);
            path.setAttribute('stroke', e.isDashed ? '#f9a825' : '#c8d0dc');
            path.setAttribute('stroke-width', '1.5');
            path.setAttribute('stroke-dasharray', e.isDashed ? '5 3' : 'none');
            path.setAttribute('fill', 'none');
            path.setAttribute('marker-end', `url(#${markerId})`);
            g.appendChild(path);

            if (e.label) {
                const lw = e.label.length * 5.5 + 6;
                const bg = document.createElementNS(SVG_NS, 'rect');
                bg.setAttribute('x', mx - lw / 2);
                bg.setAttribute('y', my - 7);
                bg.setAttribute('width', lw);
                bg.setAttribute('height', '13');
                bg.setAttribute('rx', '3');
                bg.setAttribute('fill', '#f7f8fa');
                g.appendChild(bg);

                const txt = document.createElementNS(SVG_NS, 'text');
                txt.setAttribute('x', mx);
                txt.setAttribute('y', my);
                txt.setAttribute('text-anchor', 'middle');
                txt.setAttribute('dominant-baseline', 'middle');
                txt.setAttribute('font-size', '9');
                txt.setAttribute('font-family', 'Inter,sans-serif');
                txt.setAttribute('fill', '#8a94a6');
                txt.style.pointerEvents = 'none';
                txt.textContent = e.label;
                g.appendChild(txt);

                g._labelEls = [bg, txt]; // cached for LOD toggling
            }

            svg.appendChild(g);
            edgeGs.push(g);
        }

        // nodes
        const nodeArr = [];
        const nodeGs = [];
        for (let i = 0; i < nodes.length; i++) {
            const n = nodes[i];
            const isRef    = n.isRef;
            const isCutOff = !!n.isCutOff;
            const isInit   = n.isInit;
            const isDead   = n.isDead;
            const isOmega  = !!n.isOmega;

            const fill   = isRef    ? '#fffde7'
                         : isCutOff ? '#f3e5f5'
                         : isOmega  ? '#f0f0fb'
                         : isInit   ? '#e8faf8'
                         : isDead   ? '#ffebee'
                         :            '#ffffff';
            const stroke = isRef    ? '#f9a825'
                         : isCutOff ? '#8e24aa'
                         : isOmega  ? '#5c6bc0'
                         : isInit   ? '#00a499'
                         : isDead   ? '#e53935'
                         :            '#c8d0dc';
            const textC  = isRef    ? '#f57f17'
                         : isCutOff ? '#6a1b9a'
                         : isOmega  ? '#3949ab'
                         : isInit   ? '#00796b'
                         : isDead   ? '#b71c1c'
                         :            '#374151';

            const g = document.createElementNS(SVG_NS, 'g');
            g.setAttribute('class', 'tree-node-g');
            g.setAttribute('transform', `translate(${n.x} ${n.y})`);
            g.style.cursor = isRef ? 'pointer' : 'default';
            g._treePos = { x: n.x, y: n.y };
            g._marking = n.markingKey;

            const rect = document.createElementNS(SVG_NS, 'rect');
            rect.setAttribute('x', '0');
            rect.setAttribute('y', '0');
            rect.setAttribute('width', nodeW);
            rect.setAttribute('height', nodeH);
            rect.setAttribute('rx', Math.max(2, nodeW * 0.12));
            rect.setAttribute('fill', fill);
            rect.setAttribute('stroke', stroke);
            rect.setAttribute('stroke-width', isInit ? '2.5' : '1.5');
            rect.setAttribute('stroke-dasharray', isRef ? '4 2' : 'none');
            g.appendChild(rect);

            const textEls = [];  // cache all text elements for LOD toggling

            const fontSize    = Math.max(6, Math.round(nodeW * 0.18));
            const refFontSize = Math.max(5, Math.round(nodeW * 0.13));
            const labelY = isRef ? nodeH / 2 - nodeH * 0.13 : nodeH / 2;
            const labelTxt = document.createElementNS(SVG_NS, 'text');
            labelTxt.setAttribute('x', nodeW / 2);
            labelTxt.setAttribute('y', labelY);
            labelTxt.setAttribute('text-anchor', 'middle');
            labelTxt.setAttribute('dominant-baseline', 'middle');
            labelTxt.setAttribute('font-size', fontSize);
            labelTxt.setAttribute('font-weight', '700');
            labelTxt.setAttribute('font-family', 'Inter,sans-serif');
            labelTxt.setAttribute('fill', textC);
            labelTxt.style.pointerEvents = 'none';
            labelTxt.textContent = n.label;
            g.appendChild(labelTxt);
            textEls.push(labelTxt);

            if (isCutOff) {
                const ellipsis = document.createElementNS(SVG_NS, 'text');
                ellipsis.setAttribute('x', nodeW / 2);
                ellipsis.setAttribute('y', nodeH - nodeH * 0.08);
                ellipsis.setAttribute('text-anchor', 'middle');
                ellipsis.setAttribute('dominant-baseline', 'auto');
                ellipsis.setAttribute('font-size', Math.max(5, Math.round(nodeW * 0.13)));
                ellipsis.setAttribute('font-family', 'Inter,sans-serif');
                ellipsis.setAttribute('fill', '#8e24aa');
                ellipsis.style.pointerEvents = 'none';
                ellipsis.textContent = '…';
                g.appendChild(ellipsis);
                textEls.push(ellipsis);
            }

            if (isRef && n.refLabel) {
                const refTxt = document.createElementNS(SVG_NS, 'text');
                refTxt.setAttribute('x', nodeW / 2);
                refTxt.setAttribute('y', nodeH - nodeH * 0.1);
                refTxt.setAttribute('text-anchor', 'middle');
                refTxt.setAttribute('dominant-baseline', 'auto');
                refTxt.setAttribute('font-size', refFontSize);
                refTxt.setAttribute('font-family', 'Inter,sans-serif');
                refTxt.setAttribute('fill', '#f57f17');
                refTxt.style.pointerEvents = 'none';
                refTxt.textContent = '\u2192 ' + n.refLabel;
                g.appendChild(refTxt);
                textEls.push(refTxt);
            }

            if (n.markingTip) {
                const title = document.createElementNS(SVG_NS, 'title');
                title.textContent = n.markingTip;
                g.appendChild(title);
            }

            g._textEls = textEls;  // cache for LOD

            svg.appendChild(g);
            nodeGs.push(g);

            nodeArr.push({
                el: g,
                x: n.x,
                y: n.y,
                marking: n.markingKey,
                canonicalId: n.canonicalId != null ? n.canonicalId : -1,
            });
        }

        return { nodeArr, nodeGs, edgeGs };
    }

    function init(containerId, nodes, edges, svgW, svgH, nodeW, nodeH, placeNames, dotNetRef) {
        const wrap = document.getElementById(containerId);
        if (!wrap) return;
        const svg = wrap.querySelector('svg');
        if (!svg) return;

        wrap.style.touchAction = 'none';
        wrap.style.overscrollBehavior = 'none';

        if (_state[containerId]) {
            _destroyState(containerId, wrap);
        }

        // Determine marker id from container id prefix
        const markerId = containerId.startsWith('tree-cov-') ? 'arr-cov' : 'arr';

        const built = _buildSvgContent(svg, nodes, edges, markerId, nodeW, nodeH);
        const nodeArr = built.nodeArr;

        // Zoom limits: max zoom-out shows the full tree (svgW) with a small margin
        const minVW = 100;
        const maxVW = svgW * 1.1;

        const wrapRect0 = wrap.getBoundingClientRect();
        const s = {
            svgW, svgH, nodeW, nodeH,
            vx: 0, vy: 0, vw: svgW, vh: svgH,
            drag: null,
            rafId: null,
            dirty: false,
            hoveredEl: null,
            hoveredMarking: null,
            dotNetRef,
            _nodeGs: built.nodeGs,
            _edgeGs: built.edgeGs,
            _wrapWidth: Math.max(wrapRect0.width, 1),
        };

        function clampViewBox() {
            const padX = s.vw * 2;
            const padY = s.vh * 2;
            s.vx = Math.max(-padX, Math.min(s.svgW + padX, s.vx));
            s.vy = Math.max(-padY, Math.min(s.svgH + padY, s.vy));
        }

        function flush() {
            s.rafId = null;
            s.dirty = false;
            svg.setAttribute('viewBox',
                `${s.vx.toFixed(1)} ${s.vy.toFixed(1)} ${s.vw.toFixed(1)} ${s.vh.toFixed(1)}`);
            _cullNodes(s);
        }

        function scheduleFlush() {
            if (s.dirty) return;
            s.dirty = true;
            s.rafId = requestAnimationFrame(flush);
        }

        s.onWheel = (e) => {
            e.preventDefault();
            e.stopPropagation();
            const rect = wrap.getBoundingClientRect();
            s._wrapWidth = Math.max(rect.width, 1);
            const fx = (e.clientX - rect.left) / rect.width;
            const fy = (e.clientY - rect.top)  / rect.height;
            const mouseX = s.vx + fx * s.vw;
            const mouseY = s.vy + fy * s.vh;
            const raw = e.deltaMode === 1 ? e.deltaY * 32 : e.deltaMode === 2 ? e.deltaY * 300 : e.deltaY;
            const factor = raw > 0 ? 1.12 : (1 / 1.12);
            const newW = Math.max(minVW, Math.min(maxVW, s.vw * factor));
            const newH = newW * (rect.height / rect.width);
            s.vx = mouseX - fx * newW;
            s.vy = mouseY - fy * newH;
            s.vw = newW;
            s.vh = newH;
            clampViewBox();
            scheduleFlush();
        };

        s.onPointerDown = (e) => {
            if (e.button !== 0) return;
            if (e.target.closest('.tree-node-g')) return;
            e.preventDefault();
            wrap.setPointerCapture(e.pointerId);
            s.drag = { px: e.clientX, py: e.clientY, vx: s.vx, vy: s.vy };
            wrap.style.cursor = 'grabbing';
        };

        s.onPointerMove = (e) => {
            if (!s.drag) return;
            e.preventDefault();
            const rect = wrap.getBoundingClientRect();
            const scale = Math.min(s.vw / rect.width, s.vh / rect.height);
            s.vx = s.drag.vx - (e.clientX - s.drag.px) * scale;
            s.vy = s.drag.vy - (e.clientY - s.drag.py) * scale;
            clampViewBox();
            scheduleFlush();
        };

        s.onPointerUp = () => {
            s.drag = null;
            wrap.style.cursor = 'grab';
        };

        // ── Spatial hash for fast hit-testing ────────────────────────────
        // Cell size chosen so most cells have 1-3 nodes
        const _cellSize = Math.max(nodeW, nodeH) * 3;
        const _grid = {};
        for (let i = 0; i < nodeArr.length; i++) {
            const n = nodeArr[i];
            n._rect = n.el.querySelector('rect'); // cache rect element for hover styling
            const cx = Math.floor(n.x / _cellSize);
            const cy = Math.floor(n.y / _cellSize);
            const key = cx + ',' + cy;
            if (!_grid[key]) _grid[key] = [];
            _grid[key].push(n);
        }

        function hitTestNode(svgX, svgY) {
            const cx = Math.floor(svgX / _cellSize);
            const cy = Math.floor(svgY / _cellSize);
            // Check the cell and its 8 neighbours (node may straddle boundary)
            for (let dx = -1; dx <= 1; dx++) {
                for (let dy = -1; dy <= 1; dy++) {
                    const bucket = _grid[(cx + dx) + ',' + (cy + dy)];
                    if (!bucket) continue;
                    for (let i = 0; i < bucket.length; i++) {
                        const n = bucket[i];
                        if (n.el.style.display === 'none') continue;
                        if (svgX >= n.x && svgX <= n.x + s.nodeW && svgY >= n.y && svgY <= n.y + s.nodeH)
                            return n;
                    }
                }
            }
            return null;
        }

        function setHoverStyle(el, on) {
            if (!el) return;
            const rect = el._rect || el.querySelector('rect');
            if (!rect) return;
            if (on) {
                rect.dataset.origFill   = rect.getAttribute('fill');
                rect.dataset.origStroke = rect.getAttribute('stroke');
                rect.dataset.origSw     = rect.getAttribute('stroke-width');
                rect.setAttribute('fill',         '#c8f0ec');
                rect.setAttribute('stroke',       '#00796b');
                rect.setAttribute('stroke-width', '2.5');
            } else {
                if (rect.dataset.origFill)   rect.setAttribute('fill',         rect.dataset.origFill);
                if (rect.dataset.origStroke) rect.setAttribute('stroke',       rect.dataset.origStroke);
                if (rect.dataset.origSw)     rect.setAttribute('stroke-width', rect.dataset.origSw);
            }
        }

        // Index: marking key → list of nodeArr entries (for fast highlight)
        const _markingIndex = {};
        for (let i = 0; i < nodeArr.length; i++) {
            const k = nodeArr[i].marking;
            if (!_markingIndex[k]) _markingIndex[k] = [];
            _markingIndex[k].push(nodeArr[i]);
        }

        // Tooltip element (same style as Cytoscape graph tooltip)
        let tooltip = wrap.querySelector('.tree-tooltip');
        if (!tooltip) {
            tooltip = document.createElement('div');
            tooltip.className = 'tree-tooltip';
            tooltip.style.cssText = `
                position:absolute;pointer-events:none;display:none;
                background:#fff;border:1px solid #e4e6ea;border-radius:8px;
                box-shadow:0 4px 16px rgba(0,0,0,0.12);padding:8px 10px;
                min-width:120px;max-width:220px;font-family:Inter,sans-serif;
                font-size:11px;z-index:9999;
            `;
            wrap.appendChild(tooltip);
        }
        const _placeNames = placeNames || [];
        const _isCov = containerId.startsWith('tree-cov-');

        function showTooltip(hit, clientX, clientY) {
            const vals = hit.marking.split(',');
            const label = hit.el.querySelector('text')?.textContent || '';
            let html = `<div style="font-weight:700;color:#374151;margin-bottom:6px;padding-bottom:5px;border-bottom:1px solid #f0f0f0;">${label}</div>`;
            for (let i = 0; i < vals.length; i++) {
                const name = _placeNames[i] || ('p' + i);
                const raw = vals[i];
                const isOmega = raw === 'w' || raw === '-1';
                const val = isOmega ? 'ω' : raw;
                const num = isOmega ? -1 : parseInt(raw, 10);
                const hot = num > 0 || isOmega;
                html += `<div style="display:flex;justify-content:space-between;gap:10px;padding:1px 0;
                         color:${hot ? '#222' : '#9aa0ad'};font-weight:${hot ? '600' : '400'};">
                           <span>${name}</span>
                           <span style="color:${isOmega ? '#7c4dff' : hot ? '#00a499' : '#bbb'}">${val}</span>
                         </div>`;
            }
            tooltip.innerHTML = html;
            tooltip.style.display = 'block';
            const wr = wrap.getBoundingClientRect();
            let tx = clientX - wr.left + 12;
            let ty = clientY - wr.top - 10;
            if (tx + 230 > wrap.clientWidth) tx = clientX - wr.left - 240;
            tooltip.style.left = tx + 'px';
            tooltip.style.top = ty + 'px';
        }

        s.onMouseMove = (e) => {
            if (s.drag) { tooltip.style.display = 'none'; return; }
            const rect = wrap.getBoundingClientRect();
            const fx = (e.clientX - rect.left) / rect.width;
            const fy = (e.clientY - rect.top)  / rect.height;
            const svgX = s.vx + fx * s.vw;
            const svgY = s.vy + fy * s.vh;
            const hit = hitTestNode(svgX, svgY);
            const key = hit ? hit.marking : null;
            if (key !== s.hoveredMarking) {
                // Un-highlight nodes with old key
                if (s.hoveredMarking) {
                    const old = _markingIndex[s.hoveredMarking];
                    if (old) for (let i = 0; i < old.length; i++) setHoverStyle(old[i].el, false);
                }
                s.hoveredMarking = key;
                if (key) {
                    const cur = _markingIndex[key];
                    if (cur) for (let i = 0; i < cur.length; i++) setHoverStyle(cur[i].el, true);
                    if (s.dotNetRef) s.dotNetRef.invokeMethodAsync('OnNodeHovered', key, key);
                } else {
                    if (s.dotNetRef) s.dotNetRef.invokeMethodAsync('OnNodeLeft');
                }
            }
            // Update tooltip position / content
            if (hit) {
                showTooltip(hit, e.clientX, e.clientY);
            } else {
                tooltip.style.display = 'none';
            }
        };

        s.onMouseLeave = () => {
            tooltip.style.display = 'none';
            if (!s.hoveredMarking) return;
            const old = _markingIndex[s.hoveredMarking];
            if (old) for (let i = 0; i < old.length; i++) setHoverStyle(old[i].el, false);
            s.hoveredMarking = null;
            if (s.dotNetRef) s.dotNetRef.invokeMethodAsync('OnNodeLeft');
        };

        // ── Click on ref node: pan to canonical ──────────────────────────
        s.onClick = (e) => {
            if (s.drag) return;
            const rect = wrap.getBoundingClientRect();
            const fx = (e.clientX - rect.left) / rect.width;
            const fy = (e.clientY - rect.top)  / rect.height;
            const svgX = s.vx + fx * s.vw;
            const svgY = s.vy + fy * s.vh;
            const hit = hitTestNode(svgX, svgY);
            if (!hit || hit.canonicalId < 0) return;
            // Find the canonical (non-ref) node with the same marking key
            const target = nodeArr.find(n => n.canonicalId < 0 && n.marking === hit.marking);
            if (!target) return;
            // Pan viewport so target node is centred
            s.vx = target.x + s.nodeW / 2 - s.vw / 2;
            s.vy = target.y + s.nodeH / 2 - s.vh / 2;
            clampViewBox();
            scheduleFlush();
        };

        wrap.addEventListener('wheel',        s.onWheel,        { passive: false, capture: true });
        wrap.addEventListener('pointerdown',  s.onPointerDown,  { passive: false });
        wrap.addEventListener('pointermove',  s.onPointerMove,  { passive: false });
        wrap.addEventListener('pointerup',    s.onPointerUp);
        wrap.addEventListener('pointercancel',s.onPointerUp);
        wrap.addEventListener('mousemove',    s.onMouseMove);
        wrap.addEventListener('mouseleave',   s.onMouseLeave);
        wrap.addEventListener('click',        s.onClick);

        _state[containerId] = s;
        wrap.style.cursor = 'grab';

        // Initial view: show ~40% of tree width centred on M0 (reasonable starting zoom)
        {
            const wrapRect = wrap.getBoundingClientRect();
            const containerAspect = wrapRect.height / Math.max(wrapRect.width, 1);
            s.vw = Math.min(maxVW, Math.max(300, svgW * 0.4));
            s.vh = s.vw * containerAspect;

            if (nodeArr.length > 0) {
                const nodeX = nodeArr[0].x + s.nodeW / 2;
                const nodeY = nodeArr[0].y + s.nodeH / 2;
                s.vx = nodeX - s.vw / 2;
                s.vy = nodeY - s.vh / 2;
            } else {
                s.vx = 0;
                s.vy = 0;
            }
        }
        clampViewBox();
        flush();
    }

    function _destroyState(containerId, wrap) {
        const s = _state[containerId];
        if (!s) return;
        if (s.rafId != null) cancelAnimationFrame(s.rafId);
        wrap = wrap || document.getElementById(containerId);
        if (wrap) {
            wrap.removeEventListener('wheel',        s.onWheel,       { capture: true });
            wrap.removeEventListener('pointerdown',  s.onPointerDown);
            wrap.removeEventListener('pointermove',  s.onPointerMove);
            wrap.removeEventListener('pointerup',    s.onPointerUp);
            wrap.removeEventListener('pointercancel',s.onPointerUp);
            wrap.removeEventListener('mousemove',    s.onMouseMove);
            wrap.removeEventListener('mouseleave',   s.onMouseLeave);
            wrap.removeEventListener('click',        s.onClick);
        }
        delete _state[containerId];
    }

    function destroy(containerId) {
        _destroyState(containerId, null);
    }

    function resetView(containerId) {
        const s = _state[containerId];
        if (!s) return;
        const wrap = document.getElementById(containerId);
        if (!wrap) return;
        const svg = wrap.querySelector('svg');
        if (!svg) return;
        s.vx = 0; s.vy = 0; s.vw = s.svgW; s.vh = s.svgH;
        const rect = wrap.getBoundingClientRect();
        s._wrapWidth = Math.max(rect.width, 1);
        svg.setAttribute('viewBox', `0 0 ${s.svgW} ${s.svgH}`);
        _cullNodes(s);
    }

    return { init, resetView, destroy };
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
                    'font-size': '11px',
                    'font-weight': '700',
                    'font-family': 'Inter, Segoe UI, sans-serif',
                    'color': '#374151',
                    'text-outline-color': '#ffffff',
                    'text-outline-width': 2,
                    'background-color': '#ffffff',
                    'border-color': '#c8d0dc',
                    'border-width': 2,
                    'width': 44,
                    'height': 44,
                    'shape': 'ellipse',
                    'text-valign': 'center',
                    'text-halign': 'center',
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
                    'font-size': '9px',
                    'font-family': 'Inter, Segoe UI, sans-serif',
                    'color': '#8a94a6',
                    'curve-style': 'bezier',
                    'target-arrow-shape': 'triangle',
                    'arrow-scale': 1,
                    'line-color': '#c8d0dc',
                    'target-arrow-color': '#c8d0dc',
                    'text-background-color': '#fafbfc',
                    'text-background-opacity': 1,
                    'text-background-padding': '2px',
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
        if (zoom < 0.07) newState = 'noLabels';
        else if (zoom < 0.15) newState = 'noEdgeLabels';
        else newState = 'full';

        if (newState === _lodState) return;
        _lodState = newState;

        cy.batch(function() {
            if (newState === 'noLabels') {
                cy.nodes().addClass('lod-hide-label');
                cy.edges().addClass('lod-hide-label');
            } else if (newState === 'noEdgeLabels') {
                cy.nodes().removeClass('lod-hide-label');
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
            min-width:120px;max-width:220px;font-family:Inter,sans-serif;
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
            html += `<div style="display:flex;justify-content:space-between;gap:10px;padding:1px 0;
                     color:${hot ? '#222' : '#9aa0ad'};font-weight:${hot ? '600' : '400'};">
                       <span>${name}</span>
                       <span style="color:${omega ? '#7c4dff' : hot ? '#00a499' : '#bbb'}">${val}</span>
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
        let tx = e.originalEvent.clientX - rect.left + 12;
        let ty = e.originalEvent.clientY - rect.top - 10;
        // keep tooltip inside container
        if (tx + 230 > container.clientWidth) tx = e.originalEvent.clientX - rect.left - 240;
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