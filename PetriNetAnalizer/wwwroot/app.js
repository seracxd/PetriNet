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

// ── Flags set from Blazor to control JS behaviour ─────────────────────────
window._selectToolActive = false;
window.setSelectToolActive = (active) => { window._selectToolActive = active; };

window._panDrag = null;
window._selDrag = null;