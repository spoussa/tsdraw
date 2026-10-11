// Drives test/harness.html in headless Edge: draws a stroke with real mouse events,
// feeds a remote stroke, checks what the mod sent, and saves screenshots.
// usage: node test/run-harness.mjs <outDir>
import { spawn } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const out = resolve(process.argv[2] || "test-out");
mkdirSync(out, { recursive: true });
const port = 9333;
const edge = "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe";
const page = pathToFileURL(resolve("test/harness.html")).href;
const proc = spawn(edge, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${join(out, "profile")}`,
  "--window-size=1000,740", "--autoplay-policy=no-user-gesture-required", page], { stdio: "ignore" });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let target;
for (let i = 0; i < 50 && !target; i++) {
  await sleep(200);
  try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find((t) => t.type === "page"); } catch {}
}
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0; const pending = new Map();
ws.onmessage = (ev) => { const m = JSON.parse(ev.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
const call = (method, params = {}) => new Promise((r) => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const ev = async (expr) => (await call("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true })).result.result.value;
const mouse = (type, x, y, buttons = 0) => call("Input.dispatchMouseEvent", { type, x, y, button: type === "mouseMoved" && !buttons ? "none" : "left", buttons, clickCount: 1 });
const shot = async (name) => { const r = await call("Page.captureScreenshot", { format: "png" }); writeFileSync(join(out, name), Buffer.from(r.result.data, "base64")); };

await sleep(1500);
const results = {};
results.layer = await ev(`(() => { const l = document.querySelector('.tsdraw-layer'), c = l && l.querySelector('canvas'); const r = c && c.getBoundingClientRect(); return l ? { canvas: [r.x, r.y, r.width, r.height].map(Math.round), cw: c.width, ch: c.height } : null; })()`);
results.sentAtStart = await ev("window.sent.map(m => m.t)");

// Helper says hello; remote viewer "Alice" draws a stroke.
await ev(`__tsdraw.recv({t:'hello', relay:'connected', name:'me', holdMs: 60000})`);
await ev(`__tsdraw.recv({t:'stroke', sid:'11111111-2222-3333-4444-555555555555', from:'alice1', name:'Alice', id:'s1', c:'#34c759', w:0.006, p:[0.6,0.3, 0.65,0.35, 0.7,0.32, 0.75,0.4, 0.8,0.38], end:true})`);

// Hover to reveal the toolbar, click the pen, draw a circle-ish stroke.
const c = results.layer.canvas;
await mouse("mouseMoved", c[0] + 100, c[1] + 100);
await sleep(300);
const pen = await ev(`(() => { const r = document.querySelector('.tsdraw-bar > .tsdraw-btn').getBoundingClientRect(); return [r.x + r.width / 2, r.y + r.height / 2]; })()`);
await mouse("mouseMoved", pen[0], pen[1]);
await mouse("mousePressed", pen[0], pen[1], 1);
await mouse("mouseReleased", pen[0], pen[1], 0);
await sleep(100);
results.drawingOn = await ev("document.querySelector('.tsdraw-layer').classList.contains('tsdraw-on')");
const cx = c[0] + c[2] * 0.3, cy = c[1] + c[3] * 0.5, R = c[3] * 0.18;
await mouse("mouseMoved", cx + R, cy);
await mouse("mousePressed", cx + R, cy, 1);
for (let a = 0; a <= Math.PI * 2; a += Math.PI / 24) { await mouse("mouseMoved", cx + R * Math.cos(a), cy + R * Math.sin(a), 1); await sleep(8); }
await mouse("mouseReleased", cx + R, cy, 0);
await sleep(200);
await shot("1-drawn.png");

const strokes = await ev(`window.sent.filter(m => m.t === 'stroke')`);
results.strokeMsgs = strokes.length;
results.points = strokes.reduce((n, m) => n + m.p.length / 2, 0);
results.lastEnd = strokes.length ? strokes[strokes.length - 1].end : null;
results.firstPoint = strokes.length ? strokes[0].p.slice(0, 2) : null;   // expect ~[0.48, 0.5]
results.ar = strokes.length ? strokes[0].ar : null;
results.sessions = await ev(`window.sent.filter(m => m.t === 'sessions').length`);

// Escape leaves draw mode; clear removes own strokes.
await call("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
await sleep(100);
results.drawingAfterEsc = await ev("document.querySelector('.tsdraw-layer').classList.contains('tsdraw-on')");

// Fade: shrink hold so strokes disappear.
await ev(`__tsdraw.state.holdMs = 200; __tsdraw.state.fadeMs = 200;`);
await sleep(800);
results.strokesLeft = await ev(`[...__tsdraw.state.strokes.values()].reduce((n, m) => n + m.size, 0)`);
await shot("2-faded.png");
results.errors = await ev("window.__errs || []");

writeFileSync(join(out, "results.json"), JSON.stringify(results, null, 1));
console.log(JSON.stringify(results, null, 1));
ws.close();
proc.kill();
