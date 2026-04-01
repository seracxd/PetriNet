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
        });
    }

    return { setup, setupTabScroll };
})();

// ── Cytoscape.js interop ───────────────────────────────────────────────────
window.petriEditor = window.petriEditor || {};
window.petriEditor._cy = {};

window.petriEditor.initCytoscape = function (containerId, elements, layoutName) {
    const container = document.getElementById(containerId);
    if (!container) return;

    // Destroy any previous instance in this container
    if (window.petriEditor._cy[containerId]) {
        window.petriEditor._cy[containerId].destroy();
        delete window.petriEditor._cy[containerId];
    }

    window.petriEditor._cy[containerId] = cytoscape({
        container,
        elements,
        style: [
            {
                selector: 'node',
                style: {
                    'label': 'data(label)',
                    'text-wrap': 'wrap',
                    'text-max-width': '140px',
                    'font-size': '10px',
                    'background-color': '#e3f2fd',
                    'border-color': '#1e88e5',
                    'border-width': 2,
                    'width': 'label',
                    'height': 'label',
                    'padding': '8px',
                    'shape': 'roundrectangle',
                    'text-valign': 'center',
                    'text-halign': 'center',
                }
            },
            {
                selector: 'node.initial',
                style: { 'border-width': 4, 'border-color': '#2e7d32', 'background-color': '#e8f5e9' }
            },
            {
                selector: 'node.deadlock',
                style: { 'background-color': '#ffebee', 'border-color': '#c62828' }
            },
            {
                selector: 'node.duplicate',
                style: {
                    'background-color': '#f3e5f5',
                    'border-color': '#7b1fa2',
                    'border-style': 'dashed'
                }
            },
            {
                selector: 'node.omega',
                style: { 'background-color': '#e8eaf6', 'border-color': '#3949ab' }
            },
            {
                selector: 'edge',
                style: {
                    'label': 'data(label)',
                    'font-size': '10px',
                    'curve-style': 'bezier',
                    'target-arrow-shape': 'triangle',
                    'arrow-scale': 1.2,
                    'line-color': '#90a4ae',
                    'target-arrow-color': '#90a4ae',
                    'text-background-color': '#ffffff',
                    'text-background-opacity': 0.8,
                    'text-background-padding': '2px',
                }
            }
        ],
        layout: { name: layoutName || 'breadthfirst', directed: true, spacingFactor: 1.4, padding: 20 }
    });
};

window.petriEditor.destroyCytoscape = function (containerId) {
    if (window.petriEditor._cy[containerId]) {
        window.petriEditor._cy[containerId].destroy();
        delete window.petriEditor._cy[containerId];
    }
};

window.petriEditor.fitCytoscape = function (containerId) {
    window.petriEditor._cy[containerId]?.fit(undefined, 20);
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