// Drives test/autosrc.html in headless Edge: the "follow the game" switches TSDraw.exe asks
// for, the stream staying alive when a guarded game window closes, and the fallbacks.
// usage: node test/run-autosrc.mjs <outDir>
import { spawn } from "node:child_process";
import { mkdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const out = resolve(process.argv[2] || "test-out");
mkdirSync(out, { recursive: true });
const port = 9335;
const edge = "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe";
const page = pathToFileURL(resolve("test/autosrc.html")).href;
const proc = spawn(edge, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${join(out, "profile-autosrc")}`, page], { stdio: "ignore" });

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
const ev = async (expr) => {
  const r = (await call("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true })).result;
  if (r.exceptionDetails) throw new Error(expr + ": " + JSON.stringify(r.exceptionDetails).slice(0, 300));
  return r.result.value;
};
const SID = "99999999-8888-7777-6666-555555555555";
const recv = (m) => ev(`__tsdraw.recv(${JSON.stringify(Object.assign({ t: "autosrc", sid: SID }, m))})`);
const replies = () => ev(`window.sent.filter(m => m.t === 'autosrc')`);
const S = (expr) => ev(`(() => { const s = window.stream; return ${expr}; })()`);

const checks = [];
const check = (name, ok, detail) => { checks.push({ name, ok: !!ok, detail }); };

await sleep(1500);
await ev(`__tsdraw.recv({t:'hello', relay:'p2p', transport:'p2p', master:true, name:'me'})`);
await sleep(1200);
const sessions = await ev(`window.sent.filter(m => m.t === 'sessions').pop()`);
check("sessions report the capture status", sessions && sessions.outbound[0] && sessions.outbound[0].status === 4, sessions && sessions.outbound[0]);

// 1. Game started: switch to it and guard it.
await recv({ to: "200", guard: "200" });
await sleep(300);
let r = await replies();
check("switch to game: TeamSpeak's updateSession used", (await S("s.updates.length")) === 1 && (await S("s.updates[0].fromInfo.id")) === "200", await S("s.updates"));
check("switch to game: config name and quality passed", (await S("s.updates[0].fromConfig.audio")) === true && (await S("s.updates[0].fromQuality.width")) === 1280);
check("switch to game: answered ok with the title", r.length === 1 && r[0].ok && r[0].to === "200" && r[0].title === "League of Legends (TM) Client", r);

// 2. The game window closes: TeamSpeak would leave; we don't.
await S("(s.captureStatus = -1, 0)");
await sleep(100);
check("guarded failure: stream not left", (await S("s.left")) === 0);
check("guarded failure: TeamSpeak's own handler still ran", (await S("s.statusCalls")) === 1);
check("guarded failure: TSDraw.exe told", (await ev(`window.sent.filter(m => m.t === 'srclost').length`)) === 1);

// 3. Back to the launcher (old game still guarded during the switch), capture recovers.
await recv({ to: "100", guard: "200" });
await sleep(300);
await S("(s.captureStatus = 4, 0)");
check("switch back: source is the launcher", (await S("s.sourceInfo.id")) === "100");
await sleep(700);   // past the (shortened) 60 s wait
check("switch back: the wait was cancelled, stream kept", (await S("s.left")) === 0);
await recv({ guard: "" });

// 4. Unguarded window closing: TeamSpeak's normal behaviour (stream ends).
await S("(s.captureStatus = -1, 0)");
await sleep(100);
check("unguarded failure: stream left as TeamSpeak does", (await S("s.left")) === 1);

// 5. Game closes and no launcher window shows up: the stream ends after the wait.
await S("(s.left = 0, s.captureStatus = 4, 0)");
await recv({ to: "200", guard: "200" });
await sleep(300);
await S("(s.captureStatus = -1, 0)");
await sleep(100);
const leftBefore = await S("s.left");
await sleep(700);
check("no launcher: kept during the wait, ended after it", leftBefore === 0 && (await S("s.left")) === 1, { leftBefore, after: await S("s.left") });

// 6. TeamSpeak's object refuses: the direct capture switch is used instead.
await S("(s.left = 0, s.rejectUpdates = true, s._captureStatus = 4, 0)");
await recv({ to: "300", guard: "300" });
await sleep(300);
const ns = await ev("window.nativeSwitches");
r = await replies();
check("fallback: native switchSource with the window", ns.length === 1 && ns[0].source_id === "300" && ns[0].id === 7 && ns[0].type === 3, ns);
check("fallback: object's source updated and answered ok", (await S("s.sourceInfo.id")) === "300" && r[r.length - 1].ok, r[r.length - 1]);

// 7. A window TeamSpeak can't share: refused with a reason, nothing changed.
await recv({ to: "999", guard: "" });
await sleep(300);
r = await replies();
check("unknown window: answered not ok", !r[r.length - 1].ok && /can't share/.test(r[r.length - 1].err), r[r.length - 1]);
check("unknown window: source unchanged", (await S("s.sourceInfo.id")) === "300");

const logs = await ev(`window.sent.filter(m => m.t === 'log').map(m => m.msg)`);
for (const c of checks) console.log((c.ok ? "PASS " : "FAIL ") + c.name + (c.ok ? "" : "  " + JSON.stringify(c.detail)));
console.log("page log:\n  " + logs.join("\n  "));
ws.close(); proc.kill();
process.exit(checks.every((c) => c.ok) ? 0 : 1);
