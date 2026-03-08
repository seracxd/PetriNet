// ── Global keyboard handler ───────────────────────────────────────────────
window.registerGlobalKeyHandler = (dotnetRef) => {
    const keyHandler = (e) => {
        dotnetRef.invokeMethodAsync('OnGlobalKey', e.key, e.ctrlKey, e.shiftKey, e.altKey);
    };
    document.addEventListener('keydown', keyHandler);
    window._globalKeyHandler = keyHandler;

    // pointermove for selection rectangle — on window so it works even when
    // the pointer leaves the diagram container during a drag
    const moveHandler = (e) => {
        if (!window._selRectActive) return;
        const container = document.getElementById('diagram-container');
        if (!container) return;
        const rect = container.getBoundingClientRect();
        dotnetRef.invokeMethodAsync('OnSelPointerMoveJS',
            e.clientX - rect.left,
            e.clientY - rect.top);
    };
    window.addEventListener('pointermove', moveHandler);
    window._selMoveHandler = moveHandler;

    window._dotNetRef = dotnetRef;
};

window.unregisterGlobalKeyHandler = () => {
    if (window._globalKeyHandler) {
        document.removeEventListener('keydown', window._globalKeyHandler);
        window._globalKeyHandler = null;
    }
    if (window._selMoveHandler) {
        window.removeEventListener('pointermove', window._selMoveHandler);
        window._selMoveHandler = null;
    }
    if (window._panBlockHandler) {
        const container = document.getElementById('diagram-container');
        if (container) container.removeEventListener('pointerdown', window._panBlockHandler, true);
        window._panBlockHandler = null;
    }
};

// ── Returns container-relative offset for a clientX/clientY point ─────────
window.getContainerOffset = (clientX, clientY) => {
    const container = document.getElementById('diagram-container');
    if (!container) return { x: clientX, y: clientY };
    const rect = container.getBoundingClientRect();
    return { x: clientX - rect.left, y: clientY - rect.top };
};

// ── Pan blocker ───────────────────────────────────────────────────────────
// Attaches a capture-phase pointerdown listener. When select tool is active,
// stops propagation on ALL canvas clicks so the diagram pan never starts.
// Node/link elements already call stopPropagation themselves in their own
// handlers, so the diagram library still processes them correctly — the only
// thing we're preventing here is the PAN behavior that runs when no model
// stops the event.
window._selectToolActive = false;
window._selRectActive = false;

window.setSelectToolActive = (active) => {
    window._selectToolActive = active;
};

window.setSelRectActive = (active) => {
    window._selRectActive = active;
};

window.installPanBlocker = () => {
    const container = document.getElementById('diagram-container');
    if (!container || window._panBlockHandler) return;

    const handler = (e) => {
        if (!window._selectToolActive) return;
        if (e.button !== 0) return;

        // Check if click landed on a petri node or link — let those through untouched.
        let el = e.target;
        let onModel = false;
        while (el && el !== container) {
            if (el.classList && (
                el.classList.contains('petri-node') ||
                el.classList.contains('petri-link-group')
            )) { onModel = true; break; }
            el = el.parentElement;
        }
        if (onModel) return;

        // Empty canvas click — let the event propagate normally so Blazor.Diagrams
        // fires PointerDown(model==null), but immediately dispatch a synthetic
        // pointerup to cancel whatever pan state the diagram started.
        // We use setTimeout(0) so the diagram's pointerdown handler runs first.
        setTimeout(() => {
            const synth = new PointerEvent('pointerup', {
                bubbles: true, cancelable: true,
                pointerId: e.pointerId,
                clientX: e.clientX, clientY: e.clientY,
                button: 0, buttons: 0
            });
            container.dispatchEvent(synth);
        }, 0);
    };

    container.addEventListener('pointerdown', handler, { capture: true });
    window._panBlockHandler = handler;
};

window.armPointerUpListener = () => {
    if (window._pointerUpHandler) return;
    const handler = (e) => {
        if (e.button !== 0) return;
        window._pointerUpHandler = null;
        window.removeEventListener('pointerup', handler);
        if (window._dotNetRef) {
            window._dotNetRef.invokeMethodAsync('OnCanvasPointerUp');
        }
    };
    window.addEventListener('pointerup', handler);
    window._pointerUpHandler = handler;
};

window.cancelPointerUpListener = () => {
    if (window._pointerUpHandler) {
        window.removeEventListener('pointerup', window._pointerUpHandler);
        window._pointerUpHandler = null;
    }
};

window.installPanBlockerOnce = () => {
    // Wait for DiagramCanvas to render before installing
    const tryInstall = () => {
        const container = document.getElementById('diagram-container');
        if (container) {
            window.installPanBlocker();
        } else {
            requestAnimationFrame(tryInstall);
        }
    };
    tryInstall();
};