// Streamer reaction rules (policy, cooldown, opacity) over the peer-to-peer simulation (test/p2p.html): viewer 208 sends a 7TV emote
// and one of their own images; the streamer and viewer 209 must show both, and the streamer
// must prepare overlay frames ("asset") for TSDraw.exe. Needs internet (7TV).
// usage: node test/run-reactions.mjs <outDir>
import { spawn } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

const out = resolve(process.argv[2] || "test-out");
mkdirSync(out, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const http = spawn("python", ["-I", "-m", "http.server", "8767", "--bind", "127.0.0.1"], { cwd: resolve("."), stdio: "ignore" });
const edge = spawn("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", ["--headless=new", "--remote-debugging-port=9336",
  `--user-data-dir=${join(out, "profile")}`, "--window-size=1300,560", "--autoplay-policy=no-user-gesture-required",
  "http://127.0.0.1:8767/test/p2p.html"], { stdio: "ignore" });

try {
  let target;
  for (let i = 0; i < 60 && !target; i++) {
    await sleep(250);
    try { target = (await (await fetch("http://127.0.0.1:9336/json/list")).json()).find((t) => t.type === "page" && t.url.includes("p2p.html")); } catch {}
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
  await call("Emulation.setDeviceMetricsOverride", { width: 1300, height: 560, deviceScaleFactor: 1, mobile: false });
  await ev(`document.querySelectorAll('iframe').forEach(f => f.style.height = '420px')`);

  for (let i = 0; i < 40; i++) {   // wait for the peer-to-peer links
    await sleep(500);
    const s = await ev(`${W("streamer")}.sent.filter(m => m.t === 'p2p').pop()`);
    if (s && s.open === 2) break;
  }
  const results = {};
  const SID = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
  const clickTile = (frame, selector, idx = 0) => ev(`(() => {
    const w = ${W(frame)}, t = w.document.querySelectorAll(${JSON.stringify(selector)})[${idx}];
    if (!t) return 'no tile';
    const r = t.getBoundingClientRect(), o = { bubbles: true, cancelable: true, pointerId: 7, button: 0, buttons: 1, clientX: r.x + 5, clientY: r.y + 5 };
    t.dispatchEvent(new w.PointerEvent('pointerdown', o));
    t.dispatchEvent(new w.PointerEvent('pointerup', Object.assign({}, o, { buttons: 0 })));
    return t.title;
  })()`);
  const rxReacts = () => ev(`${W("streamer")}.sent.filter(m => m.t === 'rx' && m.msg.t === 'react').length`);

  // The streamer (via TSDraw.exe's status message) turns custom images off, 3 s cooldown, 60% opacity.
  await ev(`${W("streamer")}.__tsdraw.recv({ t: 'status', reactions: { emotes: true, images: false, opacity: 60, cooldown: 3 } })`);
  await sleep(500);
  results.viewerPolicy = await ev(`(() => { const p = ${W("v208")}.__tsdraw.state.policies.get('${SID}'); return p ? JSON.stringify(p) : null; })()`);
  results.streamerOpacity = await ev(`[...${W("streamer")}.__tsdraw.state.layers][0].reactBox.style.opacity`);

  // Viewer 208's panel reflects it.
  await ev(`[...${W("v208")}.__tsdraw.state.layers][0].togglePanel(true)`);
  for (let i = 0; i < 30; i++) { await sleep(400); if (await ev(`!!${W("v208")}.document.querySelector('.tsdraw-tile')`)) break; }
  results.panelNote = await ev(`${W("v208")}.document.querySelector('.tsdraw-note').textContent`);
  results.addImageDisabled = await ev(`${W("v208")}.document.querySelector('.tsdraw-add').disabled`);
  results.sections = await ev(`[...${W("v208")}.document.querySelectorAll('.tsdraw-sec')].map(s => s.textContent)`);

  // Two quick reactions from 208: the second is held back by the cooldown in 208's own panel.
  const before = await rxReacts();
  await clickTile("v208", ".tsdraw-tile", 0);
  await sleep(200);
  await clickTile("v208", ".tsdraw-tile", 1);
  await sleep(600);
  results.v208CooldownHint = await ev(`${W("v208")}.document.querySelector('.tsdraw-hint').textContent`);
  results.v208Cooling = await ev(`${W("v208")}.document.querySelector('.tsdraw-pbody').classList.contains('tsdraw-cooling')`);
  results.streamerGotFrom208 = (await rxReacts()) - before;

  // Viewer 209 ignores the rules (like an old version): the streamer still drops the extra ones.
  await ev(`${W("v209")}.__tsdraw.state.policies.delete('${SID}')`);
  await ev(`(() => { const t = ${W("v209")}.__tsdraw; const st = t.state; st.policies.set = () => {}; })()`);
  await ev(`[...${W("v209")}.__tsdraw.state.layers][0].togglePanel(true)`);
  for (let i = 0; i < 30; i++) { await sleep(400); if (await ev(`!!${W("v209")}.document.querySelector('.tsdraw-tile')`)) break; }
  const before209 = await rxReacts();
  await clickTile("v209", ".tsdraw-tile", 0);
  await sleep(150);
  await clickTile("v209", ".tsdraw-tile", 1);
  await sleep(150);
  await clickTile("v209", ".tsdraw-tile", 2);
  await sleep(800);
  results.streamerGotFrom209_of3 = (await rxReacts()) - before209;

  // 209 sends one of its own images: images are off on this stream, so nobody else sees it.
  await sleep(3200);
  results.v209AddImage = await ev(`(async () => {
    const w = ${W("v209")}, cv = w.document.createElement('canvas'); cv.width = 64; cv.height = 64;
    cv.getContext('2d').fillStyle = '#34c759'; cv.getContext('2d').fillRect(0, 0, 64, 64);
    const blob = await new Promise(r => cv.toBlob(r, 'image/png'));
    const dt = new w.DataTransfer(); dt.items.add(new w.File([blob], 'green.png', { type: 'image/png' }));
    const input = w.document.querySelector('.tsdraw-panel input[type=file]'); input.files = dt.files; input.dispatchEvent(new w.Event('change'));
    for (let i = 0; i < 40; i++) { await new Promise(r => setTimeout(r, 100)); if (/Added|Couldn/.test(w.document.querySelector('.tsdraw-hint').textContent)) return 'added'; }
    return 'timeout';
  })()`);
  await sleep(300);
  const beforeImg = await rxReacts();
  await clickTile("v209", ".tsdraw-tile[title^='green.png']");
  await sleep(1500);
  results.imageReachedStreamer = (await rxReacts()) - beforeImg;
  results.imageAtV208 = await ev(`[...${W("v208")}.document.querySelectorAll('.tsdraw-react img')].filter(i => i.src.startsWith('blob:')).length`);

  // Settings view in the panel.
  await ev(`(() => { const p = [...${W("v208")}.__tsdraw.state.layers][0].panel; p.settingsOpen = true; p.render(); })()`);
  results.settingsView = await ev(`(() => { const b = ${W("v208")}.document.querySelector('.tsdraw-settings'); return b ? [...b.querySelectorAll('label')].map(l => l.textContent.trim()) : null; })()`);
  results.settingsSent = await ev(`(() => { const w = ${W("v208")}; const cb = w.document.querySelector('[data-k=images]'); cb.checked = false; cb.dispatchEvent(new w.Event('change', { bubbles: true })); return w.sent.filter(m => m.t === 'settings').map(m => JSON.stringify(m)); })()`);
  await call("Page.captureScreenshot", { format: "png" }).then((r) => writeFileSync(join(out, "policy.png"), Buffer.from(r.result.data, "base64")));

  writeFileSync(join(out, "policy-results.json"), JSON.stringify(results, null, 1));
  console.log(JSON.stringify(results, null, 1));
  ws.close();
} finally {
  edge.kill();
  http.kill();
}
