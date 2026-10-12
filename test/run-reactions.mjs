// Reactions over the peer-to-peer simulation (test/p2p.html): viewer 208 sends a 7TV emote
// and one of their own images; the streamer and viewer 209 must show both, and the streamer
// must prepare overlay frames ("asset") for TSDraw.exe. Needs internet (7TV).
// usage: node test/run-reactions.mjs <outDir>
import { spawn } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

const out = resolve(process.argv[2] || "test-out");
mkdirSync(out, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const http = spawn("python", ["-I", "-m", "http.server", "8766", "--bind", "127.0.0.1"], { cwd: resolve("."), stdio: "ignore" });
const edge = spawn("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", ["--headless=new", "--remote-debugging-port=9335",
  `--user-data-dir=${join(out, "profile")}`, "--window-size=1300,560", "--autoplay-policy=no-user-gesture-required",
  "http://127.0.0.1:8766/test/p2p.html"], { stdio: "ignore" });

try {
  let target;
  for (let i = 0; i < 60 && !target; i++) {
    await sleep(250);
    try { target = (await (await fetch("http://127.0.0.1:9335/json/list")).json()).find((t) => t.type === "page" && t.url.includes("p2p.html")); } catch {}
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
  // click helper: synthetic pointer events on a panel tile inside an iframe
  const clickTile = (frame, selector) => ev(`(() => {
    const w = ${W(frame)}, t = w.document.querySelector(${JSON.stringify(selector)});
    if (!t) return 'no tile';
    const r = t.getBoundingClientRect(), o = { bubbles: true, cancelable: true, pointerId: 7, button: 0, buttons: 1, clientX: r.x + 5, clientY: r.y + 5 };
    t.dispatchEvent(new w.PointerEvent('pointerdown', o));
    t.dispatchEvent(new w.PointerEvent('pointerup', Object.assign({}, o, { buttons: 0 })));
    return t.title;
  })()`);
  const reacts = (f) => ev(`[...${W(f)}.document.querySelectorAll('.tsdraw-react img')].map(i => i.src.startsWith('blob:') ? 'blob' : i.src.replace(/^.*emote\\//, ''))`);

  // 1. open the panel in viewer 208 and click the first popular 7TV emote
  await ev(`[...${W("v208")}.__tsdraw.state.layers][0].togglePanel(true)`);
  for (let i = 0; i < 30; i++) { await sleep(400); if (await ev(`!!${W("v208")}.document.querySelector('.tsdraw-tile')`)) break; }
  results.popularTiles = await ev(`${W("v208")}.document.querySelectorAll('.tsdraw-tile').length`);
  results.clicked = await clickTile("v208", ".tsdraw-tile");
  await sleep(1500);
  results.after7tv = { v208: await reacts("v208"), streamer: await reacts("streamer"), v209: await reacts("v209"),
    streamerToApp: await ev(`${W("streamer")}.sent.filter(m => m.t === 'rx' && m.msg.t === 'react').map(m => m.msg.k + ':' + m.msg.ref + ' @' + m.msg.x + ',' + m.msg.y)`) };

  // 2. viewer 208 adds their own image (a generated PNG) through the file input, then uses it
  results.addImage = await ev(`(async () => {
    const w = ${W("v208")}, cv = w.document.createElement('canvas'); cv.width = 120; cv.height = 80;
    const g = cv.getContext('2d'); g.fillStyle = '#ff2d92'; g.fillRect(0, 0, 120, 80); g.fillStyle = '#fff'; g.font = 'bold 40px sans-serif'; g.fillText('GG', 22, 55);
    const blob = await new Promise(r => cv.toBlob(r, 'image/png'));
    const dt = new w.DataTransfer(); dt.items.add(new w.File([blob], 'gg.png', { type: 'image/png' }));
    const input = w.document.querySelector('.tsdraw-panel input[type=file]'); input.files = dt.files; input.dispatchEvent(new w.Event('change'));
    for (let i = 0; i < 40; i++) { await new Promise(r => setTimeout(r, 100)); const h = w.document.querySelector('.tsdraw-hint').textContent; if (/Added|Couldn/.test(h)) return h; }
    return 'timeout';
  })()`);
  await sleep(300);
  results.clickedImage = await clickTile("v208", ".tsdraw-tile[title^='gg.png']");
  await sleep(2500);
  results.afterImage = { v208: await reacts("v208"), streamer: await reacts("streamer"), v209: await reacts("v209") };
  results.assetsForApp = await ev(`${W("streamer")}.sent.filter(m => m.t === 'asset').map(m => ({ key: m.key.slice(0, 16), frames: m.n, size: m.w + 'x' + m.h, pngKB: Math.round(m.png.length * 0.75 / 1024) }))`);
  await call("Page.captureScreenshot", { format: "png" }).then((r) => writeFileSync(join(out, "reactions.png"), Buffer.from(r.result.data, "base64")));
  results.errors = await ev(`['streamer','v208','v209'].flatMap(f => document.getElementById(f).contentWindow.sent.filter(m => m.t === 'log' && /fail|couldn|error/i.test(m.msg)).map(m => f + ': ' + m.msg))`);

  writeFileSync(join(out, "reactions-results.json"), JSON.stringify(results, null, 1));
  console.log(JSON.stringify(results, null, 1));
  ws.close();
} finally {
  edge.kill();
  http.kill();
}
