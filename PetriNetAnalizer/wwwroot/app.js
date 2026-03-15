// ── Global keyboard handler ───────────────────────────────────────────────
window.registerGlobalKeyHandler = (dotnetRef) => {
    document.addEventListener('keydown', (e) => {
        dotnetRef.invokeMethodAsync('OnGlobalKey', e.key, e.ctrlKey, e.shiftKey, e.altKey);
    });
    window._dotNetRef = dotnetRef;
};

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

    // ── Capture-phase pointerdown — decides pan vs sel-rect vs pass-through ──
    container.addEventListener('pointerdown', (e) => {

        // ── Right button → pan ───────────────────────────────────────────────
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

        // ── Left button, select tool, empty canvas → sel-rect ────────────────
        if (e.button === 0 && window._selectToolActive) {
            // Walk up from target — if we hit a petri-node or petri-link-group
            // let the event fall through to the diagram normally.
            let el = e.target;
            let onModel = false;
            while (el && el !== container) {
                if (el.classList &&
                    (el.classList.contains('petri-node') ||
                        el.classList.contains('petri-link-group'))) {
                    onModel = true; break;
                }
                el = el.parentElement;
            }
            if (!onModel) {
                // Empty canvas click — arm the selection rect.
                // Don't stopPropagation; Blazor still sees it (model==null) which
                // is fine since OnPointerDown only primes _pendingAction for
                // place/transition/arc tools, and returns early for "select".
                const bounds = container.getBoundingClientRect();
                window._selDrag = {
                    armed: true,
                    active: false,
                    startX: e.clientX - bounds.left,
                    startY: e.clientY - bounds.top,
                    pointerId: e.pointerId,
                };
                // Notify Blazor so it can initialise _selStart/_selCurrent
                if (window._dotNetRef)
                    window._dotNetRef.invokeMethodAsync(
                        'OnSelRectArmed',
                        window._selDrag.startX,
                        window._selDrag.startY);
            }
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

        // Sel-rect
        const s = window._selDrag;
        if (!s || !s.armed) return;
        const container = document.getElementById('diagram-container');
        if (!container) return;
        const bounds = container.getBoundingClientRect();
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

    // Active pointer state
    let _drag = null; // { type: 'move'|'resize', dir, startX, startY, origX, origY, origW, origH }

    function setup(floating, dockedW, dockedMin, dockedMax,
        fx, fy, fw, fh, fMinW, fMinH, dotnet) {
        _floating = floating;
        _dockedW = dockedW; _dockedMin = dockedMin; _dockedMax = dockedMax;
        _fx = fx; _fy = fy; _fw = fw; _fh = fh; _fMinW = fMinW; _fMinH = fMinH;
        _dotnet = dotnet;

        // Small delay so Blazor has painted the new DOM
        requestAnimationFrame(() => {
            _unbind();
            if (_floating) {
                _bindFloating();
            } else {
                _bindDocked();
            }
        });
    }

    // ── Docked resize (left edge) ─────────────────────────────────────────
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
        // Dragging left handle leftward = wider
        const delta = _drag.startX - e.clientX;
        const newW = Math.max(_dockedMin, Math.min(_dockedMax, _drag.origW + delta));
        _dotnet.invokeMethodAsync('UpdateDockedWidth', newW);
    }

    function _onDockedResizeUp() {
        _drag = null;
        document.removeEventListener('pointermove', _onDockedResizeMove);
        document.removeEventListener('pointerup', _onDockedResizeUp);
    }

    // ── Floating drag + resize ────────────────────────────────────────────
    function _bindFloating() {
        const titlebar = document.getElementById('ap-titlebar');
        if (titlebar) {
            titlebar.addEventListener('pointerdown', _onMoveDown, { passive: false });
        }
        // All resize handles
        const dirs = ['n', 's', 'e', 'w', 'ne', 'nw', 'se', 'sw'];
        for (const dir of dirs) {
            const h = document.getElementById(`ap-resize-${dir}`);
            if (h) h.addEventListener('pointerdown', (e) => _onResizeDown(e, dir), { passive: false });
        }
    }

    function _onMoveDown(e) {
        if (e.button !== 0) return;
        // Don't start drag on buttons inside titlebar
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
        // Clamp so titlebar stays on screen
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

    // ── Cleanup ───────────────────────────────────────────────────────────
    function _unbind() {
        // Remove old listeners by replacing elements isn't practical here;
        // instead we rely on setup() always being called after DOM rebuild,
        // so stale listeners on removed elements are GC'd automatically.
    }

    // ── Tab scroll arrows ─────────────────────────────────────────────────
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