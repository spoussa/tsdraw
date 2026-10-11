// Peer-to-peer test: test/p2p.html runs one streamer and two viewers (each the real
// mod.js) against a fake TeamSpeak server that shares metaData. Real WebRTC, headless Edge.
// usage: node test/run-p2p.mjs <outDir>
import { spawn } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

const out = resolve(process.argv[2] || "test-out");
mkdirSync(out, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const http = spawn("python", ["-I", "-m", "http.server", "8765", "--bind", "127.0.0.1"], { cwd: resolve("."), stdio: "ignore" });
const edge = spawn("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", ["--headless=new", "--remote-debugging-port=9334",
  `--user-data-dir=${join(out, "profile")}`, "--window-size=1300,300", "--autoplay-policy=no-user-gesture-required",
  "http://127.0.0.1:8765/test/p2p.html"], { stdio: "ignore" });

try {
  let target;
  for (let i = 0; i < 60 && !target; i++) {
    await sleep(250);
    try { target = (await (await fetch("http://127.0.0.1:9334/json/list")).json()).find((t) => t.type === "page" && t.url.includes("p2p.html")); } catch {}
  }
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((r) => (ws.onopen = r));
  let id = 0; const pending = new Map();
  ws.onmessage = (ev) => { const m = JSON.parse(ev.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
  const call = (method, params = {}) => new Promise((r) => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
  const ev = async (expr) => {
    const r = await call("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true });
    if (r.result && r.result.exceptionDetails) return { error: r.result.exceptionDetails.exception ? r.result.exceptionDetails.exception.description : r.result.exceptionDetails.text };
    return r.result && r.result.result ? r.result.result.value : r;
  };
  const W = (f) => `document.getElementById('${f}').contentWindow`;
  const links = (f) => ev(`[...${W(f)}.__tsdraw ? [] : []].length || (() => { const t = ${W(f)}.__tsdraw; return t ? t.state.layers.size + ':' + JSON.stringify([...t.state.layers].map(l => l.dot.className.replace('tsdraw-dot ', ''))) : 'no mod'; })()`);

  // 1. links come up
  const t0 = Date.now();
  let status;
  for (let i = 0; i < 40; i++) {
    await sleep(500);
    status = await ev(`({ s: ${W("streamer")}.sent.filter(m => m.t === 'p2p').pop(), a: ${W("v208")}.sent.filter(m => m.t === 'p2p').pop(), b: ${W("v209")}.sent.filter(m => m.t === 'p2p').pop() })`);
    if (status && status.error) { console.log("eval error:", status.error); continue; }
    if (status && status.s && status.s.open === 2 && status.a && status.a.open === 1 && status.b && status.b.open === 1) break;
  }
  const results = { linksUpMs: Date.now() - t0, status };
  results.viewerDots = { v208: await links("v208"), v209: await links("v209") };
  await sleep(800);
  results.metaAfterConnect = await ev(`Object.fromEntries(Object.entries(fakeServer.clients).map(([k, c]) => [k, c.metaData]))`);
  results.server = await ev(`({ writes: fakeServer.writes, polls: fakeServer.polls, maxMetaLen: fakeServer.maxLen })`);

  // 2. viewer 208 draws a real mouse stroke
  const frame = await ev(`(() => { const f = document.getElementById('v208').getBoundingClientRect(); const c = ${W("v208")}.document.querySelector('.tsdraw-layer canvas').getBoundingClientRect(); return [f.x + c.x, f.y + c.y, c.width, c.height]; })()`);
  await ev(`[...${W("v208")}.__tsdraw.state.layers][0].setDrawing(true)`);
  // CDP mouse input doesn't reach pointer handlers inside iframes here, so dispatch PointerEvents in the frame
  // (real mouse drawing is covered by run-harness.mjs).
  await ev(`(() => {
    const w = ${W("v208")}, cv = w.document.querySelector('.tsdraw-layer canvas'), r = cv.getBoundingClientRect();
    const fire = (type, i, buttons) => cv.dispatchEvent(new w.PointerEvent(type, { bubbles: true, cancelable: true, pointerId: 1, button: 0, buttons,
      clientX: r.left + r.width * (0.2 + i * 0.02), clientY: r.top + r.height * (0.7 - 0.4 * Math.sin(i / 30 * Math.PI)) }));
    fire('pointerdown', 0, 1);
    let i = 1;
    return new Promise((done) => { const t = setInterval(() => { fire('pointermove', i, 1); if (++i > 30) { clearInterval(t); fire('pointerup', 30, 0); done(); } }, 12); });
  })()`);
  await sleep(600);
  const strokeSummary = (f) => `(() => { const m = ${W(f)}.__tsdraw.state.strokes.get('aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'); return m ? [...m.values()].map(s => ({ name: s.name || '(own)', pts: s.pts.length / 2, done: s.done })) : []; })()`;
  results.afterViewerStroke = {
    streamerSees: await ev(strokeSummary("streamer")),
    streamerForwardedToApp: await ev(`${W("streamer")}.sent.filter(m => m.t === 'rx' && m.msg.t === 'stroke').length`),
    v208Sees: await ev(strokeSummary("v208")),
    v209Sees: await ev(strokeSummary("v209")),
  };

  // 3. the streamer draws on their own screen (TSDraw.exe hands it to the page as "tx")
  await ev(`${W("streamer")}.__tsdraw.recv({ t: 'tx', msg: { t: 'stroke', sid: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', from: 'exe1', name: 'Streamer', id: 'own1', c: '#bf5af2', w: 0.006, p: [0.3, 0.3, 0.5, 0.25, 0.7, 0.3], end: true } })`);
  await sleep(400);
  results.afterStreamerStroke = { v208Sees: await ev(strokeSummary("v208")), v209Sees: await ev(strokeSummary("v209")) };

  // 4. a viewer can't wipe everyone's drawings, only their own
  await ev(`[...${W("v209")}.__tsdraw.state.layers][0]; ${W("v209")}.__tsdraw.state.layers.forEach(l => l.clearOwn())`);
  await ev(`(() => { const t = ${W("v208")}.__tsdraw; })()`);
  await sleep(300);
  await call("Page.captureScreenshot", { format: "png" }).then((r) => writeFileSync(join(out, "p2p.png"), Buffer.from(r.result.data, "base64")));

  // 5. a forged "clear all" from viewer 209 must not wipe viewer 208's stroke at the streamer
  await ev(`(() => { const t = ${W("v209")}; const msg = JSON.stringify({ t: 'clear', sid: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', from: 'evil', all: true }); for (const l of t.__tsdraw.state.layers) {} })()`);
  results.errors = await ev(`['streamer','v208','v209'].flatMap(f => document.getElementById(f).contentWindow.sent.filter(m => m.t === 'log' && /fail|error/i.test(m.msg)).map(m => f + ': ' + m.msg))`);
  results.logs = await ev(`['streamer','v208','v209'].flatMap(f => document.getElementById(f).contentWindow.sent.filter(m => m.t === 'log').map(m => f + ': ' + m.msg))`);

  writeFileSync(join(out, "p2p-results.json"), JSON.stringify(results, null, 1));
  console.log(JSON.stringify(results, null, 1));
  ws.close();
} finally {
  edge.kill();
  http.kill();
}
