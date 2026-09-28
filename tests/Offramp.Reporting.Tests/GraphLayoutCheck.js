// Runs the graph page's layout script (<script id="offramp-layout">) outside a browser on the page's
// own embedded data, and checks what the layout promises. Prints { problems, selfTestCaught } as JSON.
//   node GraphLayoutCheck.js graph.html
'use strict';
const fs = require('fs');
const vm = require('vm');

const html = fs.readFileSync(process.argv[2], 'utf8');
const script = /<script id="offramp-layout">([\s\S]*?)<\/script>/.exec(html);
const json = /<script type="application\/json" id="offramp-graph">([\s\S]*?)<\/script>/.exec(html);
if (!script || !json) {
  console.log(JSON.stringify({ problems: ['the page has no offramp-layout or offramp-graph script'], selfTestCaught: false }));
  process.exit(0);
}

const context = vm.createContext({});
vm.runInContext(script[1], context);
const layout = context.offrampLayout.layout;
const data = JSON.parse(json[1]);
const EPS = 0.05;

function points(d) {
  const result = [];
  const re = /([MLQ])([^MLQ]*)/g;
  let m;
  while ((m = re.exec(d))) {
    const n = m[2].trim().split(/[\s,]+/).map(Number);
    for (let i = 0; i + 1 < n.length; i += 2) { result.push([n[i], n[i + 1]]); }
  }
  return result;
}

function crossesInterior(a, b, box) {
  const x1 = box.x + 1, x2 = box.x + box.w - 1, y1 = box.y + 1, y2 = box.y + box.h - 1;
  if (Math.abs(a[1] - b[1]) < EPS) {
    const lo = Math.min(a[0], b[0]), hi = Math.max(a[0], b[0]);
    return a[1] > y1 && a[1] < y2 && hi > x1 && lo < x2;
  }
  const lo = Math.min(a[1], b[1]), hi = Math.max(a[1], b[1]);
  return a[0] > x1 && a[0] < x2 && hi > y1 && lo < y2;
}

function check(lay, nodes, edges, label) {
  const problems = [];
  const boxes = nodes.map(n => [n.id, lay.at[n.id]]);
  boxes.forEach(([id, box]) => { if (!box) { problems.push(`${label}: ${id} has no box`); } });
  for (let i = 0; i < boxes.length; i++) {
    for (let j = i + 1; j < boxes.length; j++) {
      const a = boxes[i][1], b = boxes[j][1];
      if (a && b && a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h) {
        problems.push(`${label}: ${boxes[i][0]} overlaps ${boxes[j][0]}`);
      }
    }
  }
  if (lay.routes.length !== edges.length) { problems.push(`${label}: ${lay.routes.length} routes for ${edges.length} edges`); }
  edges.forEach((e, k) => {
    const name = `${label}: ${e.from} -> ${e.to}`;
    const p = points(lay.routes[k] || '');
    const from = lay.at[e.from], to = lay.at[e.to];
    if (p.length < 2 || !from || !to) { problems.push(`${name} has no route`); return; }
    const startX = from.layer === to.layer ? from.x + from.w : from.x;
    const first = p[0], last = p[p.length - 1];
    if (Math.abs(first[0] - startX) > EPS || Math.abs(first[1] - (from.y + from.h / 2)) > EPS) { problems.push(`${name} does not start at its source (${first})`); }
    if (Math.abs(last[0] - (to.x + to.w)) > EPS || Math.abs(last[1] - (to.y + to.h / 2)) > EPS) { problems.push(`${name} does not end at its target (${last})`); }
    for (let i = 0; i + 1 < p.length; i++) {
      const a = p[i], b = p[i + 1];
      if (Math.abs(a[0] - b[0]) > EPS && Math.abs(a[1] - b[1]) > EPS) { problems.push(`${name} has a diagonal segment ${a} ${b}`); break; }
      const hit = boxes.find(([, box]) => box && crossesInterior(a, b, box));
      if (hit) { problems.push(`${name} passes through ${hit[0]}`); break; }
      if (Math.min(a[0], b[0]) < -EPS || Math.max(a[0], b[0]) > lay.width + EPS || Math.min(a[1], b[1]) < -EPS || Math.max(a[1], b[1]) > lay.height + EPS) {
        problems.push(`${name} leaves the layout's bounds`); break;
      }
    }
  });
  return problems;
}

const problems = [];
let selfTestCaught = false;
for (const mode of ['none', 'directory', 'kind']) {
  const lay = layout(data.nodes, data.edges, mode);
  if (JSON.stringify(lay) !== JSON.stringify(layout(data.nodes, data.edges, mode))) { problems.push(`${mode}: two runs differ`); }
  problems.push(...check(lay, data.nodes, data.edges, mode));

  // The check must be able to fail: put a box on a route and expect it to be reported.
  if (mode === 'none' && data.edges.length) {
    const p = points(lay.routes[0]);
    const broken = JSON.parse(JSON.stringify(lay));
    const [a, b] = [p[0], p[1]];
    const other = data.nodes.find(n => n.id !== data.edges[0].from && n.id !== data.edges[0].to) || data.nodes[0];
    broken.at[other.id] = { x: (a[0] + b[0]) / 2 - 20, y: (a[1] + b[1]) / 2 - 15, w: 40, h: 30, layer: -1 };
    selfTestCaught = check(broken, data.nodes, data.edges, 'broken').length > 0;
  }
}

console.log(JSON.stringify({ problems, selfTestCaught }));
