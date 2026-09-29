#!/usr/bin/env node
/**
 * Converts an indexed color (or non-antialiased RGB) raster map into GeoJSON regions.
 *
 * Pure-JS port of regions.py. Requires:
 *   npm install pngjs
 *   npm install mapshaper
 *
 * Example usage: node regions.js regions.png -o regions.json
 *
 * See https://github.com/joric/maps/wiki for details.
 *
 * Notes on parity with the Python/rasterio original:
 *  - Labels are always derived as (r<<16)|(g<<8)|b from the decoded RGBA pixels.
 *    This covers both indexed-palette and true-RGB PNGs without needing to special
 *    case them, since pngjs always expands palette images to RGBA on read.
 *  - The sieve step re-implements GDAL's "merge into the neighboring polygon with
 *    the most shared border pixels" rule via connected-component labeling + an
 *    iterative merge pass.
 *  - The boundary tracer walks pixel-grid edges with a fixed "hardest right turn
 *    first" rule at junctions. This always yields valid, non-self-intersecting
 *    simple rings. For ordinary region maps (flat blobs several pixels wide) this
 *    matches rasterio.features.shapes() exactly. The one place it can differ is
 *    the degenerate case of two same-color pixels touching only at a corner
 *    (checkerboard-style) with connectivity=8: GDAL sometimes emits a single
 *    self-touching ring there, whereas this port emits two separate simple
 *    polygons for the same color. Real-world "non-antialiased" region maps don't
 *    produce that pattern, so this is noted rather than bit-replicated.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { PNG } = require('pngjs');
const mapshaper = require('mapshaper');

// ---------------------------------------------------------------------------
// CLI argument parsing (mirrors argparse options in the Python original)
// ---------------------------------------------------------------------------

function parseArgs(argv) {
    const args = {
        input: null,
        output: null,
        destRes: 812900,
        flipY: false,
        dx: 0,
        dy: 0,
        sieveSize: 2,
        connectivity: 8,
        simplify: '5%',
        keepIntermediate: false,
        skipMapshaper: false,
        skipColor: [],
    };

    const rest = argv.slice(2);
    for (let i = 0; i < rest.length; i++) {
        const a = rest[i];
        switch (a) {
            case '-o':
            case '--output':
                args.output = rest[++i];
                break;
            case '--dest-res':
                args.destRes = parseFloat(rest[++i]);
                break;
            case '--flip-y':
                args.flipY = true;
                break;
            case '--dx':
                args.dx = parseFloat(rest[++i]);
                break;
            case '--dy':
                args.dy = parseFloat(rest[++i]);
                break;
            case '--sieve-size':
                args.sieveSize = parseInt(rest[++i], 10);
                break;
            case '--connectivity': {
                const c = parseInt(rest[++i], 10);
                if (c !== 4 && c !== 8) {
                    console.error('Error: --connectivity must be 4 or 8');
                    process.exit(2);
                }
                args.connectivity = c;
                break;
            }
            case '--simplify':
                args.simplify = rest[++i];
                break;
            case '--keep-intermediate':
                args.keepIntermediate = true;
                break;
            case '--skip-mapshaper':
                args.skipMapshaper = true;
                break;
            case '--skip-color':
                args.skipColor.push(rest[++i]);
                break;
            case '-h':
            case '--help':
                printHelp();
                process.exit(0);
                break;
            default:
                if (a.startsWith('-')) {
                    console.error(`Error: unrecognized argument: ${a}`);
                    process.exit(2);
                }
                if (args.input === null) {
                    args.input = a;
                } else {
                    console.error(`Error: unexpected positional argument: ${a}`);
                    process.exit(2);
                }
        }
    }

    if (!args.input) {
        console.error('Error: the following argument is required: input');
        printHelp();
        process.exit(2);
    }

    return args;
}

function printHelp() {
    console.log(`usage: regions.js input [-o OUTPUT] [--dest-res DEST_RES] [--flip-y] [--dx DX] [--dy DY]
                   [--sieve-size SIEVE_SIZE] [--connectivity {4,8}]
                   [--simplify SIMPLIFY] [--keep-intermediate]
                   [--skip-mapshaper] [--skip-color SKIP_COLOR]

Convert a non-antialiased raster polygon map (PNG) into a simplified GeoJSON.

positional arguments:
  input                 Path to the input raster image (e.g. T_Regions_Map.png)

options:
  -o, --output          Path to the final output GeoJSON (default: regions.json next to input)
  --dest-res            Destination resolution used to compute the scale factor (default: 812900)
  --flip-y              Flip the Y axis before applying dx/dy (useful when the image origin is top-left)
  --dx                  X offset applied after scaling (default: 0)
  --dy                  Y offset applied after scaling (default: 0)
  --sieve-size          Remove isolated regions smaller than this many pixels (default: 2)
  --connectivity        Pixel connectivity used for sieving/polygon extraction (default: 8)
  --simplify            Mapshaper -simplify percentage, e.g. '5%' (default: 5%)
  --keep-intermediate   Keep the raw (unsimplified) GeoJSON instead of deleting it
  --skip-mapshaper      Skip the mapshaper simplify/clean/explode step entirely
  --skip-color          Skip color in hex, e.g. '#0000ff' (can be used multiple times)`);
}

// ---------------------------------------------------------------------------
// Raster IO
// ---------------------------------------------------------------------------

function readLabels(inputPath) {
    const buf = fs.readFileSync(inputPath);
    const png = PNG.sync.read(buf);
    const { width, height, data } = png; // data: RGBA, 4 bytes/pixel

    const labels = new Int32Array(width * height);
    for (let i = 0, p = 0; i < labels.length; i++, p += 4) {
        const r = data[p];
        const g = data[p + 1];
        const b = data[p + 2];
        labels[i] = (r << 16) | (g << 8) | b;
    }
    return { width, height, labels };
}

// ---------------------------------------------------------------------------
// Connected-component labeling (4- or 8-connectivity) over an arbitrary
// per-pixel value array, grouping pixels with equal value.
// Returns { compId: Int32Array, compValue: Int32Array, compSize: Int32Array, numComps }
// ---------------------------------------------------------------------------

function labelComponents(values, width, height, connectivity) {
    const n = width * height;
    const compId = new Int32Array(n).fill(-1);
    const compValue = [];
    const compSize = [];

    const neighborOffsets = connectivity === 8
        ? [[-1, 0], [1, 0], [0, -1], [0, 1], [-1, -1], [-1, 1], [1, -1], [1, 1]]
        : [[-1, 0], [1, 0], [0, -1], [0, 1]];

    const stack = new Int32Array(n);

    let nextId = 0;
    for (let start = 0; start < n; start++) {
        if (compId[start] !== -1) continue;

        const value = values[start];
        let sp = 0;
        stack[sp++] = start;
        compId[start] = nextId;
        let size = 0;

        while (sp > 0) {
            const idx = stack[--sp];
            size++;
            const r = (idx / width) | 0;
            const c = idx % width;

            for (const [dr, dc] of neighborOffsets) {
                const nr = r + dr;
                const nc = c + dc;
                if (nr < 0 || nr >= height || nc < 0 || nc >= width) continue;
                const nidx = nr * width + nc;
                if (compId[nidx] !== -1) continue;
                if (values[nidx] !== value) continue;
                compId[nidx] = nextId;
                stack[sp++] = nidx;
            }
        }

        compValue.push(value);
        compSize.push(size);
        nextId++;
    }

    return { compId, compValue: Int32Array.from(compValue), compSize: Int32Array.from(compSize), numComps: nextId };
}

// ---------------------------------------------------------------------------
// Sieve: merge components smaller than `sieveSize` pixels into whichever
// neighboring component shares the most border pixels (GDAL sieve semantics).
// Operates on, and returns, a new label array (same shape as input).
// ---------------------------------------------------------------------------

function sieve(labels, width, height, sieveSize, connectivity) {
    if (sieveSize <= 1) return labels.slice();

    let { compId, compValue, compSize, numComps } = labelComponents(labels, width, height, connectivity);

    // union-find so a component can be repeatedly redirected as merges cascade
    const parent = new Int32Array(numComps);
    for (let i = 0; i < numComps; i++) parent[i] = i;
    function find(x) {
        while (parent[x] !== x) {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }
        return x;
    }

    const n = width * height;
    const orthOffsets = [[-1, 0], [1, 0], [0, -1], [0, 1]];

    // process components smallest-first, as GDAL's sieve does
    const order = Array.from({ length: numComps }, (_, i) => i)
        .sort((a, b) => compSize[a] - compSize[b]);

    for (const comp of order) {
        const root = find(comp);
        if (compSize[root] >= sieveSize) continue;

        // tally shared-border pixel counts against neighboring (already-resolved) components
        const contact = new Map(); // neighborRoot -> shared border pixel count
        for (let idx = 0; idx < n; idx++) {
            if (find(compId[idx]) !== root) continue;
            const r = (idx / width) | 0;
            const c = idx % width;
            for (const [dr, dc] of orthOffsets) {
                const nr = r + dr;
                const nc = c + dc;
                if (nr < 0 || nr >= height || nc < 0 || nc >= width) continue;
                const nroot = find(compId[nr * width + nc]);
                if (nroot === root) continue;
                contact.set(nroot, (contact.get(nroot) || 0) + 1);
            }
        }

        if (contact.size === 0) continue; // isolated whole image edge case; nothing to merge into

        let best = -1;
        let bestCount = -1;
        for (const [nroot, count] of contact) {
            if (count > bestCount) {
                bestCount = count;
                best = nroot;
            }
        }

        parent[root] = best;
        compSize[best] += compSize[root];
    }

    const out = new Int32Array(n);
    for (let idx = 0; idx < n; idx++) {
        out[idx] = compValue[find(compId[idx])];
    }
    return out;
}

// ---------------------------------------------------------------------------
// Vectorize: connected-component label the (sieved) raster, then trace each
// component's pixel-grid boundary into shell/hole rings.
// ---------------------------------------------------------------------------

function rotateRight(dx, dy) { return [dy, -dx]; }   // clockwise 90
function rotateLeft(dx, dy) { return [-dy, dx]; }    // counter-clockwise 90

function ringArea2(ring) {
    // 2x signed area (shoelace); negative => "CW" in (x,y) with our convention => shell
    let s = 0;
    for (let i = 0; i < ring.length - 1; i++) {
        const [x0, y0] = ring[i];
        const [x1, y1] = ring[i + 1];
        s += x0 * y1 - x1 * y0;
    }
    return s;
}

function pointInRing(px, py, ring) {
    // standard ray-casting point-in-polygon test
    let inside = false;
    for (let i = 0, j = ring.length - 2; i < ring.length - 1; j = i++) {
        const [xi, yi] = ring[i];
        const [xj, yj] = ring[j];
        const intersects = ((yi > py) !== (yj > py)) &&
            (px < (xj - xi) * (py - yi) / (yj - yi) + xi);
        if (intersects) inside = !inside;
    }
    return inside;
}

function traceComponentRings(compId, root, find, width, height) {
    // collect directed boundary edges for this component, keyed by start point
    const edgesFrom = new Map(); // "x,y" -> [[dx,dy,tx,ty], ...]

    function addEdge(x0, y0, x1, y1) {
        const key = `${x0},${y0}`;
        const list = edgesFrom.get(key) || [];
        list.push([x1 - x0, y1 - y0, x1, y1]);
        edgesFrom.set(key, list);
    }

    const inComp = (r, c) => {
        if (r < 0 || r >= height || c < 0 || c >= width) return false;
        return find(compId[r * width + c]) === root;
    };

    for (let r = 0; r < height; r++) {
        for (let c = 0; c < width; c++) {
            if (!inComp(r, c)) continue;
            if (!inComp(r - 1, c)) addEdge(c + 1, r, c, r);         // top
            if (!inComp(r + 1, c)) addEdge(c, r + 1, c + 1, r + 1); // bottom
            if (!inComp(r, c - 1)) addEdge(c, r, c, r + 1);         // left
            if (!inComp(r, c + 1)) addEdge(c + 1, r + 1, c + 1, r); // right
        }
    }

    const visited = new Set(); // "x0,y0->x1,y1"
    const rings = [];

    for (const startKey of Array.from(edgesFrom.keys())) {
        for (const startEdge of edgesFrom.get(startKey)) {
            const edgeKey = `${startKey}->${startEdge[2]},${startEdge[3]}`;
            if (visited.has(edgeKey)) continue;

            const [sx, sy] = startKey.split(',').map(Number);
            const ring = [[sx, sy]];
            let cx = sx, cy = sy;
            let [dx, dy, tx, ty] = startEdge;
            visited.add(`${cx},${cy}->${tx},${ty}`);

            while (true) {
                ring.push([tx, ty]);
                cx = tx; cy = ty;
                if (cx === sx && cy === sy) break;

                const candidates = edgesFrom.get(`${cx},${cy}`) || [];
                // priority: hardest right turn first, then straight, then left, then U-turn
                const [rdx, rdy] = rotateRight(dx, dy);
                const [ldx, ldy] = rotateLeft(dx, dy);
                const priority = [[rdx, rdy], [dx, dy], [ldx, ldy], [-dx, -dy]];

                let chosen = null;
                for (const [pdx, pdy] of priority) {
                    const cand = candidates.find(([edx, edy, ex, ey]) => {
                        const k = `${cx},${cy}->${ex},${ey}`;
                        return edx === pdx && edy === pdy && !visited.has(k);
                    });
                    if (cand) { chosen = cand; break; }
                }
                if (!chosen) {
                    // fall back to any unvisited outgoing edge (shouldn't normally trigger)
                    chosen = candidates.find(([edx, edy, ex, ey]) => !visited.has(`${cx},${cy}->${ex},${ey}`));
                }
                if (!chosen) break; // dead end (malformed input); stop this ring

                [dx, dy, tx, ty] = chosen;
                visited.add(`${cx},${cy}->${tx},${ty}`);
            }

            rings.push(ring);
        }
    }

    return rings;
}

function shapes(labels, width, height, connectivity) {
    const { compId, compSize, compValue, numComps } = labelComponents(labels, width, height, connectivity);
    const parent = new Int32Array(numComps);
    for (let i = 0; i < numComps; i++) parent[i] = i;
    const find = (x) => x; // components are already final here (no merging at this stage)

    const features = [];
    const processedRoots = new Set();

    for (let comp = 0; comp < numComps; comp++) {
        if (processedRoots.has(comp)) continue;
        processedRoots.add(comp);

        const rings = traceComponentRings(compId, comp, find, width, height);
        if (rings.length === 0) continue;

        const shells = [];
        const holes = [];
        for (const ring of rings) {
            if (ringArea2(ring) < 0) shells.push(ring);
            else holes.push(ring);
        }

        for (const shell of shells) {
            const myHoles = holes.filter((h) => pointInRing(h[0][0], h[0][1], shell));
            features.push({
                type: 'Feature',
                properties: { color: `#${(compValue[comp] >>> 0).toString(16).padStart(6, '0')}` },
                geometry: { type: 'Polygon', coordinates: [shell, ...myHoles] },
            });
        }
    }

    return features;
}

// ---------------------------------------------------------------------------
// Mapshaper JS API wrapper
//
// Runs the mapshaper command chain in-process (no subprocess, no global
// binary needed) using applyCommands(), which takes an in-memory input map
// and returns an in-memory output map. This avoids writing the raw GeoJSON
// to disk and re-reading it.
// ---------------------------------------------------------------------------

async function runMapshaperInMemory(rawGeoJSON, simplify) {
    const commands = [
        '-i input.geojson',
        '-clean',
        '-explode',
        `-simplify ${simplify}`,
        '-o output.geojson',
    ].join(' ');

    const input = { 'input.geojson': JSON.stringify(rawGeoJSON) };
    const output = await mapshaper.applyCommands(commands, input);

    if (!output || !output['output.geojson']) {
        throw new Error('mapshaper produced no output.geojson');
    }
    return output['output.geojson'].toString('utf8');
}

// ---------------------------------------------------------------------------
// Main vectorizer pipeline (mirrors vectorizer() in the Python original)
// ---------------------------------------------------------------------------

function vectorizer(inputPath, destRes, dx, dy, sieveSize, connectivity, skipColor, flipY) {
    const { width, height, labels } = readLabels(inputPath);
    console.log(`Input: ${width} x ${height}`);

    const srcRes = width;
    const scaleFactor = destRes / srcRes;

    const sieved = sieve(labels, width, height, sieveSize, connectivity);
    const rawFeatures = shapes(sieved, width, height, connectivity);

    const skip = new Set(skipColor || []);
    const features = [];
    for (const f of rawFeatures) {
        if (skip.has(f.properties.color)) continue;
        const scaledCoords = f.geometry.coordinates.map((ring) =>
            ring.map(([x, y]) => {
                const yy = flipY ? (height - y) : y;
                return [(x * scaleFactor) + dx, (yy * scaleFactor) + dy];
            }));
        features.push({
            type: 'Feature',
            properties: f.properties,
            geometry: { type: 'Polygon', coordinates: scaledCoords },
        });
    }

    console.log(`Done: ${features.length} polygons`);
    return features;
}

// ---------------------------------------------------------------------------
// CLI entry point (mirrors main() in the Python original)
// ---------------------------------------------------------------------------

async function main() {
    const args = parseArgs(process.argv);

    if (!fs.existsSync(args.input) || !fs.statSync(args.input).isFile()) {
        console.error(`Error: input file not found: ${args.input}`);
        process.exit(1);
    }

    const parsed = path.parse(args.input);
    const rawGeojsonPath = path.join(parsed.dir, `${parsed.name}-raw.json`);
    const finalOutputPath = path.normalize(
        args.output || path.join(parsed.dir || '.', 'regions.json')
    );

    const features = vectorizer(
        args.input,
        args.destRes,
        args.dx,
        args.dy,
        args.sieveSize,
        args.connectivity,
        args.skipColor,
        args.flipY
    );

    const rawGeoJSON = { type: 'FeatureCollection', features };

    // --keep-intermediate (or --skip-mapshaper) writes the raw GeoJSON to disk
    if (args.keepIntermediate || args.skipMapshaper) {
        fs.writeFileSync(rawGeojsonPath, JSON.stringify(rawGeoJSON, null, 2));
        console.log(`Wrote raw GeoJSON: ${rawGeojsonPath}`);
    }

    if (args.skipMapshaper) {
        if (path.resolve(finalOutputPath) !== path.resolve(rawGeojsonPath)) {
            fs.copyFileSync(rawGeojsonPath, finalOutputPath);
        }
        console.log(`Skipped mapshaper step. Output: ${finalOutputPath}`);
        return;
    }

    let finalGeoJSON;
    try {
        finalGeoJSON = await runMapshaperInMemory(rawGeoJSON, args.simplify);
    } catch (err) {
        console.error('Error: mapshaper failed. Is it installed? (npm install mapshaper)');
        console.error(err && err.message ? err.message : err);
        process.exit(1);
    }

    fs.writeFileSync(finalOutputPath, finalGeoJSON);

    // Clean up the intermediate raw file if we wrote one but the user didn't
    // ask to keep it.
    if (fs.existsSync(rawGeojsonPath) && !args.keepIntermediate) {
        fs.unlinkSync(rawGeojsonPath);
    }

    console.log(`Final output: ${finalOutputPath}`);
}

if (require.main === module) {
    main().catch((err) => {
        console.error(err && err.stack ? err.stack : err);
        process.exit(1);
    });
}

module.exports = { readLabels, labelComponents, sieve, shapes, vectorizer, runMapshaperInMemory };
