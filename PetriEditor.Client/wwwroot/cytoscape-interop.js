
// ── Cytoscape.js interop ───────────────────────────────────────────────────
window.petriEditor = window.petriEditor || {};
window.petriEditor._cy = {};
// Layout position cache: fingerprint → { nodeId: {x, y} }
// Avoids re-running expensive breadthfirst layout when the same graph is shown again.
window.petriEditor._cyLayoutCache = {};

function _cyFingerprint(elements) {
    return elements
        .filter(e => e.group === 'nodes')
        .map(e => e.data.id)
        .sort()
        .join(',');
}


// ── Cytoscape node/edge styles ──────────────────────────────────────────
const CY_STYLES = [
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
];

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
        style: CY_STYLES,
        layout: (function() {
            const fp = _cyFingerprint(elements);
            const cached = window.petriEditor._cyLayoutCache[fp];
            if (cached) {
                // Restore saved positions — no layout computation needed
                return {
                    name: 'preset',
                    positions: node => cached[node.id()],
                    padding: 48,
                };
            }
            return {
                name: 'cose',
                idealEdgeLength: 140,
                nodeRepulsion: 500000,
                nodeOverlap: 20,
                edgeElasticity: 100,
                nestingFactor: 5,
                gravity: 60,
                numIter: 1000,
                initialTemp: 250,
                coolingFactor: 0.95,
                minTemp: 1.0,
                randomize: false,
                fit: true,
                padding: 56,
                componentSpacing: 80,
            };
        })()
    });

    // ── Separate parallel edges so labels don't overlap ──────────────────
    // Group edges by (source, target) pair. Groups with >1 edge get staggered
    // control-point-distances so each arc fans out visibly.
    function separateParallelEdges() {
        const groups = {};
        cy.edges().forEach(e => {
            const key = `${e.data('source')}__${e.data('target')}`;
            if (!groups[key]) groups[key] = [];
            groups[key].push(e);
        });
        cy.batch(function () {
            Object.values(groups).forEach(group => {
                if (group.length < 2) return;
                const step = 80;
                const half = (group.length - 1) / 2;
                group.forEach((e, i) => {
                    const dist = Math.round((i - half) * step);
                    e.style('curve-style', 'unbundled-bezier');
                    e.style('control-point-distances', [dist]);
                    e.style('control-point-weights', [0.5]);
                });
            });
        });
    }

    // ── Route edges around intermediate nodes ────────────────────────────
    // If an edge's straight-line path passes through another node, curve it.
    // Distance scales with the number of blocking nodes; direction alternates
    // across edges so multiple long-range arcs fan on opposite sides.
    function routeEdgesAroundNodes() {
        let bypassIndex = 0;
        cy.batch(() => {
            cy.edges().forEach(edge => {
                // Skip edges already given an explicit curve by separateParallelEdges
                if (edge.style('curve-style') === 'unbundled-bezier') return;

                const src = cy.getElementById(edge.data('source'));
                const tgt = cy.getElementById(edge.data('target'));
                if (src.empty() || tgt.empty()) return;

                const sp = src.position();
                const tp = tgt.position();
                const dx = tp.x - sp.x;
                const dy = tp.y - sp.y;
                const len = Math.sqrt(dx * dx + dy * dy);
                if (len < 1) return;

                let blockCount = 0;
                cy.nodes().forEach(n => {
                    if (n.id() === edge.data('source') || n.id() === edge.data('target')) return;
                    const np = n.position();
                    const t = ((np.x - sp.x) * dx + (np.y - sp.y) * dy) / (len * len);
                    if (t <= 0.05 || t >= 0.95) return;
                    const perpDist = Math.abs((np.y - sp.y) * dx - (np.x - sp.x) * dy) / len;
                    const nodeRadius = (parseFloat(n.style('width')) || 54) / 2 + 8;
                    if (perpDist < nodeRadius) blockCount++;
                });

                if (blockCount > 0) {
                    const sign = (bypassIndex % 2 === 0) ? 1 : -1;
                    const dist = sign * Math.max(70, blockCount * 60);
                    bypassIndex++;
                    edge.style('curve-style', 'unbundled-bezier');
                    edge.style('control-point-distances', [dist]);
                    edge.style('control-point-weights', [0.5]);
                }
            });
        });
    }

    // ── Save layout positions after layout completes ──────────────────────
    cy.one('layoutstop', function() {
        const fp = _cyFingerprint(elements);
        const pos = {};
        cy.nodes().forEach(n => { pos[n.id()] = { x: n.position('x'), y: n.position('y') }; });
        window.petriEditor._cyLayoutCache[fp] = pos;
        // Evict old entries if cache grows large (keep newest 20)
        const keys = Object.keys(window.petriEditor._cyLayoutCache);
        if (keys.length > 20) delete window.petriEditor._cyLayoutCache[keys[0]];
        separateParallelEdges();
        routeEdgesAroundNodes();
    });

    // Also run immediately for preset (cached) layout where layoutstop fires before our listener
    separateParallelEdges();
    routeEdgesAroundNodes();

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
        const maxTyViewport   = window.innerHeight - rect.top - th - 4;
        const maxTyContainer  = container.clientHeight - th - 4;
        if (ty + th > container.clientHeight) ty = Math.min(maxTyViewport, maxTyContainer);
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

window.petriEditor.clearCytoscapeLayoutCache = function () {
    window.petriEditor._cyLayoutCache = {};
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
