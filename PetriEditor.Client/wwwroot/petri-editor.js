// ── Petri editor browser helpers ──────────────────────────────────────────
// Generic file/export helpers that the Blazor client invokes via JS interop.
window.petriEditor = window.petriEditor || {};

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
