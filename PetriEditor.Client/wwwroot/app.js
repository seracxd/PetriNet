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

    return { setup, setupTabScroll };
})();

// ── SVG pan/zoom for tree views ───────────────────────────────────────────
window.treeView = (() => {
    const _state = {};   // keyed by containerId

    function init(containerId, svgW, svgH) {
        const wrap = document.getElementById(containerId);
        if (!wrap) return;
        const svg = wrap.querySelector('svg');
        if (!svg) return;

        // Prevent browser scroll/pinch from stealing events on this element
        wrap.style.touchAction = 'none';
        wrap.style.overscrollBehavior = 'none';

        // Destroy previous listeners if re-initialising
        if (_state[containerId]) {
            const s = _state[containerId];
            wrap.removeEventListener('wheel',        s.onWheel);
            wrap.removeEventListener('pointerdown',  s.onPointerDown);
            wrap.removeEventListener('pointermove',  s.onPointerMove);
            wrap.removeEventListener('pointerup',    s.onPointerUp);
            wrap.removeEventListener('pointercancel',s.onPointerUp);
        }

        // Zoom limits
        const minVW = svgW * 0.05;   // zoom in up to 20×
        const maxVW = svgW * 3;       // zoom out to 3× overview

        const s = {
            svgW, svgH,
            vx: 0, vy: 0, vw: svgW, vh: svgH,
            drag: null,
        };

        function clampViewBox() {
            s.vx = Math.max(-s.vw * 0.5, Math.min(s.svgW - s.vw * 0.5, s.vx));
            s.vy = Math.max(-s.vh * 0.5, Math.min(s.svgH - s.vh * 0.5, s.vy));
        }

        function applyViewBox() {
            svg.setAttribute('viewBox',
                `${s.vx.toFixed(1)} ${s.vy.toFixed(1)} ${s.vw.toFixed(1)} ${s.vh.toFixed(1)}`);
        }

        // ── Wheel = zoom around cursor ────────────────────────────────────
        s.onWheel = (e) => {
            e.preventDefault();
            e.stopPropagation();
            const rect = wrap.getBoundingClientRect();
            const cx = (e.clientX - rect.left) / rect.width;
            const cy = (e.clientY - rect.top)  / rect.height;
            // Normalise delta — trackpads send small floats, mice send 100/120
            const delta = e.deltaMode === 1 ? e.deltaY * 32 : e.deltaY;
            const factor = delta > 0 ? 1.1 : (1 / 1.1);
            const newW = Math.max(minVW, Math.min(maxVW, s.vw * factor));
            const newH = newW * (s.svgH / s.svgW);
            s.vx += cx * (s.vw - newW);
            s.vy += cy * (s.vh - newH);
            s.vw = newW;
            s.vh = newH;
            clampViewBox();
            applyViewBox();
        };

        // ── Pointer drag = pan ────────────────────────────────────────────
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
            const scaleX = s.vw / rect.width;
            const scaleY = s.vh / rect.height;
            s.vx = s.drag.vx - (e.clientX - s.drag.px) * scaleX;
            s.vy = s.drag.vy - (e.clientY - s.drag.py) * scaleY;
            clampViewBox();
            applyViewBox();
        };

        s.onPointerUp = () => {
            s.drag = null;
            wrap.style.cursor = 'grab';
        };

        wrap.addEventListener('wheel',        s.onWheel,        { passive: false, capture: true });
        wrap.addEventListener('pointerdown',  s.onPointerDown,  { passive: false });
        wrap.addEventListener('pointermove',  s.onPointerMove,  { passive: false });
        wrap.addEventListener('pointerup',    s.onPointerUp);
        wrap.addEventListener('pointercancel',s.onPointerUp);

        _state[containerId] = s;
        wrap.style.cursor = 'grab';

        // Focus on M0 at a comfortable zoom level
        const firstNode = svg.querySelector('.tree-node-g');
        if (firstNode && svgW > 0 && svgH > 0) {
            const wrapRect = wrap.getBoundingClientRect();
            const targetW = Math.min(svgW, Math.max(300, wrapRect.width * 0.75));
            const targetH = targetW * (svgH / svgW);
            const transform = firstNode.getAttribute('transform');
            const match = transform && transform.match(/translate\(\s*([\d.]+)[,\s]+([\d.]+)\s*\)/);
            if (match) {
                const nx = parseFloat(match[1]) + 30;
                const ny = parseFloat(match[2]) + 15;
                s.vw = targetW;
                s.vh = targetH;
                s.vx = nx - targetW / 2;
                s.vy = ny - targetH / 3;
            } else {
                s.vw = targetW;
                s.vh = targetH;
                s.vx = (svgW - targetW) / 2;
                s.vy = 0;
            }
        }
        clampViewBox();
        applyViewBox();
    }

    function resetView(containerId) {
        const s = _state[containerId];
        if (!s) return;
        const wrap = document.getElementById(containerId);
        if (!wrap) return;
        const svg = wrap.querySelector('svg');
        if (!svg) return;
        s.vx = 0; s.vy = 0; s.vw = s.svgW; s.vh = s.svgH;
        svg.setAttribute('viewBox', `0 0 ${s.svgW} ${s.svgH}`);
    }

    return { init, resetView };
})();

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