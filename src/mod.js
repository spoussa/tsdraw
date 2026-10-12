// TSDraw - drawing layer injected into the TeamSpeak 6 UI by TSDraw.exe (via the
// Chromium DevTools protocol). Adds a pen toolbar to every stream you watch. Strokes go
// peer-to-peer to the streamer over a WebRTC data channel (or through TSDraw.exe and a
// relay server, if one is configured); the streamer's TSDraw.exe draws them on screen.
(() => {
  "use strict";
  const VERSION = "__TSDRAW_VERSION__";   // filled in by TSDraw.exe when it injects this file
  if (window.__tsdraw) {
    if (window.__tsdraw.version === VERSION) { window.__tsdraw.hello(); return; }
    try { window.__tsdraw.remove(); } catch (e) { delete window.__tsdraw; }
  }

  const COLORS = ["#ff3b30", "#ff9500", "#ffcc00", "#34c759", "#32d4ff", "#0a84ff", "#bf5af2", "#ff2d92", "#ffffff", "#1c1c1e"];
  const COLOR_NAMES = ["red", "orange", "yellow", "green", "cyan", "blue", "purple", "pink", "white", "black"];
  const STROKE_WIDTH = 0.006;      // fraction of the frame height
  const FLUSH_MS = 40;             // how often in-progress strokes are sent
  const SCAN_MS = 700;             // how often we look for stream <video> elements
  const SESSION_POLL_MS = 1000;
  const MIN_VIDEO_PX = 160;        // ignore tiny previews (stream manager menu etc.)
  const PRESENCE_MS = 12000;       // the streamer's TSDraw announces itself every 5 s

  const state = {
    holdMs: 3000, fadeMs: 700,
    present: new Map(),            // sid -> last time the streamer's TSDraw said it is listening
    relay: "unknown", name: "", helper: false, paused: false,
    transport: "", master: false,  // set by TSDraw.exe: "p2p" or "relay"; master = the page that does networking
    myId: Math.random().toString(36).slice(2, 12),
    hotkey: "",                    // TSDraw.exe's draw toggle, for tooltips
    lastLayersJson: "",
    color: loadColor(),
    sessions: { outbound: [], inbound: [] }, lastSessionsJson: "",
    strokes: new Map(),            // sid -> Map(key -> stroke)
    layers: new Set(),
  };

  function loadColor() {
    try { const c = localStorage.getItem("tsdraw.color"); if (COLORS.includes(c)) return c; } catch (e) {}
    return COLORS[Math.floor(Math.random() * 8)];   // any of the bright ones
  }
  function saveColor(c) { try { localStorage.setItem("tsdraw.color", c); } catch (e) {} }

  function send(obj) {
    try { if (typeof window.__tsdrawSend === "function") { window.__tsdrawSend(JSON.stringify(obj)); return true; } } catch (e) {}
    return false;
  }
  function log(msg) { send({ t: "log", msg: String(msg) }); }

  // ---------------------------------------------------------------- sessions

  function sidOf(idData) {
    if (!idData) return null;
    if (idData.serverSessionId) return String(idData.serverSessionId);
    if (idData.roomId && idData.userId) return "m:" + idData.roomId + "|" + idData.userId;
    return null;
  }

  function pollSessions() {
    if (state.paused) return;
    let api;
    try { api = window.tsclient_internal; } catch (e) { return; }
    if (!api || typeof api.postMessage !== "function") return;
    try {
      api.postMessage({ cmd: "requestAllSessions", payload: {} }, (res) => {
        if (!res || res.err !== 0 || !res.payload) return;
        const outbound = [], inbound = [];
        for (const s of res.payload.outbound || []) {
          const sid = sidOf(s.idData);
          if (!sid) continue;
          const src = (s.sourceInfo && s.sourceInfo.source) || {};
          outbound.push({
            sid, sc: s.idData.scHandlerId, viewers: (s.viewers || []).map((v) => v && v.clientId).filter((c) => c != null),
            type: s.sourceInfo ? s.sourceInfo.type : 0,
            srcId: src.sourceId != null ? String(src.sourceId) : "",
            title: src.title || "", displayId: src.displayId != null ? src.displayId : -1,
            minimized: !!src.minimized, name: (s.config && s.config.name) || "",
          });
        }
        for (const s of res.payload.inbound || []) {
          const sid = sidOf(s.idData);
          if (sid) inbound.push({ sid, sc: s.idData.scHandlerId, clientId: s.idData.clientId });
        }
        const json = JSON.stringify([outbound, inbound]);
        state.sessions = { outbound, inbound };
        if (json !== state.lastSessionsJson) {
          state.lastSessionsJson = json;
          send({ t: "sessions", outbound, inbound });
        }
      });
    } catch (e) { /* client not ready yet */ }
  }

  // ---------------------------------------------------------------- strokes

  function strokesFor(sid) {
    let m = state.strokes.get(sid);
    if (!m) { m = new Map(); state.strokes.set(sid, m); }
    return m;
  }

  function applyRemote(msg) {
    if (!msg.sid) return;
    if (msg.t === "react") { spawnReact(msg); return; }
    if (msg.t === "img") { onImgChunk(msg); return; }
    if (msg.t === "clear") {
      const m = state.strokes.get(msg.sid);
      if (m) for (const [k, s] of m) if (msg.all || s.from === msg.from) m.delete(k);
      kick();
      return;
    }
    const key = msg.from + ":" + msg.id;
    const m = strokesFor(msg.sid);
    let s = m.get(key);
    if (!s) {
      s = { key, from: msg.from, name: msg.name || "", c: msg.c || "#ff3b30", w: +msg.w || STROKE_WIDTH, pts: [], last: 0, done: false, own: false };
      m.set(key, s);
    }
    const p = msg.p || [];
    for (let i = 0; i + 1 < p.length; i += 2) s.pts.push(+p[i], +p[i + 1]);
    s.last = performance.now();
    if (msg.end) s.done = true;
    kick();
  }

  function alphaOf(s, now) {
    if (!s.done) return 1;
    const age = now - s.last;
    if (age <= state.holdMs) return 1;
    return Math.max(0, 1 - (age - state.holdMs) / state.fadeMs);
  }

  function pruneStrokes(now) {
    let any = false;
    for (const [sid, m] of state.strokes) {
      for (const [k, s] of m) {
        if (alphaOf(s, now) <= 0) m.delete(k); else any = true;
      }
      if (!m.size) state.strokes.delete(sid);
    }
    return any;
  }

  // ---------------------------------------------------------------- peer-to-peer
  // Each viewer opens a WebRTC data channel straight to the streamer, using TeamSpeak's
  // own STUN servers; the streamer forwards strokes to its other viewers. The one-time
  // offer/answer exchange rides on the clients' hidden TeamSpeak "metaData" field (meant
  // for plugin data), encrypted with a key derived from the stream id, and is removed
  // again once the channel is open.

  const ICE = { iceServers: [{ urls: ["stun:turn.teamspeak.com:3478", "stun:turn2.teamspeak.com:3478"] }] };
  const OFFER_TIMEOUT_MS = 25000;   // no answer by then: the streamer isn't running TSDraw
  const CONNECT_TIMEOUT_MS = 40000;
  const P2P_TICK_MS = 1000;
  const p2p = {
    handlers: new Map(),            // scHandlerId -> TeamSpeak server connection object
    lastHandlerScan: -1e9,
    meta: new Map(),                // scHandlerId -> { o: {room: offer}, a: {clientId: answer}, timer }
    peers: new Map(),               // `${sid}|${clientId}` -> peer
    keys: new Map(),                // sid -> Promise<{ key, room }>
    polls: new Map(),               // `${sc}|${clientId}` -> earliest next requestClientProperties
    busy: false, lastReport: "",
  };
  const enc = new TextEncoder(), dec = new TextDecoder();
  const hex = (buf) => Array.from(new Uint8Array(buf), (b) => b.toString(16).padStart(2, "0")).join("");
  const b64 = (u8) => { let x = ""; for (const b of u8) x += String.fromCharCode(b); return btoa(x); };
  const unb64 = (x) => Uint8Array.from(atob(x), (c) => c.charCodeAt(0));

  function roomKeys(sid) {
    let k = p2p.keys.get(sid);
    if (!k) {
      k = (async () => {
        const raw = await crypto.subtle.digest("SHA-256", enc.encode("tsdraw-enc|" + sid));
        const room = hex(await crypto.subtle.digest("SHA-256", enc.encode("tsdraw-topic|" + sid))).slice(0, 12);
        return { key: await crypto.subtle.importKey("raw", raw, "AES-GCM", false, ["encrypt", "decrypt"]), room };
      })();
      p2p.keys.set(sid, k);
    }
    return k;
  }

  async function seal(sid, obj) {
    const { key } = await roomKeys(sid);
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const ct = new Uint8Array(await crypto.subtle.encrypt({ name: "AES-GCM", iv }, key, enc.encode(JSON.stringify(obj))));
    const out = new Uint8Array(12 + ct.length);
    out.set(iv); out.set(ct, 12);
    return b64(out);
  }

  async function unseal(sid, text) {
    try {
      const { key } = await roomKeys(sid);
      const u = unb64(text);
      return JSON.parse(dec.decode(await crypto.subtle.decrypt({ name: "AES-GCM", iv: u.slice(0, 12) }, key, u.slice(12))));
    } catch (e) { return null; }
  }

  // TeamSpeak's per-server connection objects aren't global; components reach them through a `connection` prop.
  function handler(sc) {
    let h = p2p.handlers.get(sc);
    if (h && h.ownClient) return h;
    const now = performance.now();
    if (now - p2p.lastHandlerScan < 3000) return null;
    p2p.lastHandlerScan = now;
    let root = null;
    for (const el of document.querySelectorAll("body *")) if (el.__vue__) { root = el.__vue__.$root; break; }
    const visit = (vm, d) => {
      if (!vm || d > 60) return;
      try {
        const c = vm.connection;
        if (c && c.ownClient && typeof c.FindClient === "function" && c.id != null) p2p.handlers.set(c.id, c);
      } catch (e) {}
      for (const ch of vm.$children || []) visit(ch, d + 1);
    };
    visit(root, 0);
    h = p2p.handlers.get(sc);
    return h && h.ownClient ? h : null;
  }

  function metaState(sc) {
    let m = p2p.meta.get(sc);
    if (!m) { m = { o: {}, a: {}, timer: 0, warned: false }; p2p.meta.set(sc, m); }
    return m;
  }

  // Publish our pending offers/answers in our own metaData, keeping anything else stored there.
  function writeMeta(sc, now) {
    const m = metaState(sc);
    if (m.timer && !now) return;
    clearTimeout(m.timer);
    m.timer = setTimeout(() => {
      m.timer = 0;
      const h = handler(sc);
      if (!h) return;
      const cur = h.ownClient.properties.metaData || "";
      let base = {};
      if (cur) {
        try { base = JSON.parse(cur); } catch (e) { base = null; }
        if (!base || typeof base !== "object" || Array.isArray(base)) {
          if (!m.warned) { m.warned = true; log("metaData holds someone else's data; peer-to-peer handshake disabled on this server"); }
          return;
        }
      }
      delete base.tsdraw;
      if (Object.keys(m.o).length || Object.keys(m.a).length) base.tsdraw = { v: 1, o: m.o, a: m.a };
      const val = Object.keys(base).length ? JSON.stringify(base) : "";
      if (val === cur) return;
      try { tsclient_internal.setClientSelfVariables(sc, { metaData: val }, true, () => {}); } catch (e) { log("metaData write failed: " + e); }
    }, now ? 0 : 300);
  }

  function peerMeta(sc, clientId) {
    const h = handler(sc);
    if (!h) return null;
    let c = null;
    try { c = h.FindClient(clientId); } catch (e) {}
    const md = c && c.properties && c.properties.metaData;
    if (!md || md[0] !== "{") return null;
    try { const j = JSON.parse(md); return j && j.tsdraw && j.tsdraw.v === 1 ? j.tsdraw : null; } catch (e) { return null; }
  }

  // metaData changes normally arrive on their own; this asks the server for a fresh copy as a fallback.
  function refreshClient(sc, clientId, everyMs) {
    const k = sc + "|" + clientId, now = performance.now();
    if ((p2p.polls.get(k) || 0) > now) return;
    p2p.polls.set(k, now + everyMs);
    try { tsclient_internal.requestClientProperties(sc, clientId, () => {}); } catch (e) {}
  }

  function newPeer(sid, sc, remote, role, prev) {
    const pc = new RTCPeerConnection(ICE);
    const p = {
      sid, sc, remote, role, pc, ch: null, state: "starting", created: performance.now(), offeredAt: 0,
      nonce: Math.random().toString(36).slice(2, 10), retryAt: 0, fails: prev ? prev.fails : 0,
      why: prev ? prev.why : "", offerSeen: null, lastAns: null,
    };
    pc.onconnectionstatechange = () => {
      if (pc.connectionState === "failed") peerDown(p, "couldn't connect (network)");
    };
    return p;
  }

  function wireChannel(p, ch) {
    p.ch = ch;
    ch.onopen = () => {
      if (p.state === "open" || p.state === "dead") return;
      p.state = "open"; p.fails = 0; p.why = "";
      const m = metaState(p.sc);
      if (p.role === "viewer") roomKeys(p.sid).then(({ room }) => { delete m.o[room]; writeMeta(p.sc); });
      else { delete m.a[p.remote]; writeMeta(p.sc); }
      log("peer-to-peer link to client " + p.remote + " open (" + p.role + ")");
      refreshUis(p.sid);
    };
    ch.onclose = () => peerDown(p, "link closed");
    ch.onmessage = (e) => onPeerMessage(p, e.data);
    if (ch.readyState === "open") ch.onopen();   // the answering side can receive an already-open channel
  }

  function gathered(pc) {
    return new Promise((resolve) => {
      if (pc.iceGatheringState === "complete") return resolve();
      const t = setTimeout(resolve, 3000);
      pc.addEventListener("icegatheringstatechange", () => {
        if (pc.iceGatheringState === "complete") { clearTimeout(t); resolve(); }
      });
    });
  }

  function peerDown(p, why) {
    if (p.state === "dead") return;
    const wasOpen = p.state === "open";
    p.state = "dead";
    if (why !== "stopped") p.why = why;
    try { p.pc.close(); } catch (e) {}
    p.fails++;
    p.retryAt = performance.now() + (wasOpen ? 2000 : Math.min(60000, 15000 * Math.pow(2, Math.min(p.fails - 1, 2))));
    const m = metaState(p.sc);
    if (p.role === "viewer") roomKeys(p.sid).then(({ room }) => { delete m.o[room]; writeMeta(p.sc); });
    else { delete m.a[p.remote]; writeMeta(p.sc); }
    if (why !== "stopped") log("peer-to-peer link to client " + p.remote + " down: " + why);
    refreshUis(p.sid);
  }

  async function startViewer(sid, sc, streamer, prev) {
    const h = handler(sc);
    if (!h) return;
    const p = newPeer(sid, sc, streamer, "viewer", prev);
    p2p.peers.set(sid + "|" + streamer, p);
    wireChannel(p, p.pc.createDataChannel("tsdraw", { ordered: true }));
    await p.pc.setLocalDescription(await p.pc.createOffer());
    await gathered(p.pc);
    if (p.state !== "starting") return;
    const { room } = await roomKeys(sid);
    metaState(sc).o[room] = await seal(sid, { sdp: p.pc.localDescription.sdp, n: p.nonce, c: h.ownClient.id });
    writeMeta(sc);
    p.state = "offered"; p.offeredAt = performance.now();
    refreshUis(sid);
  }

  async function viewerTick(p) {
    const age = performance.now() - p.offeredAt;
    if (p.state === "answered") { if (age > CONNECT_TIMEOUT_MS) peerDown(p, "couldn't connect (network)"); return; }
    refreshClient(p.sc, p.remote, 1500);
    const h = handler(p.sc), m = peerMeta(p.sc, p.remote);
    const ans = h && m && m.a && m.a[h.ownClient.id];
    if (ans && ans !== p.lastAns) {
      p.lastAns = ans;
      const d = await unseal(p.sid, ans);
      if (d && d.n === p.nonce && p.state === "offered") {
        p.state = "answered";
        try { await p.pc.setRemoteDescription({ type: "answer", sdp: d.sdp }); } catch (e) { peerDown(p, "bad answer"); }
      }
    } else if (age > OFFER_TIMEOUT_MS) {
      peerDown(p, "no answer");
    }
  }

  async function streamerTick(sid, sc, viewer) {
    const key = sid + "|" + viewer;
    let p = p2p.peers.get(key);
    if (p && (p.state === "open" || p.state === "starting")) return;
    if (p && p.state === "answered" && performance.now() - p.created > CONNECT_TIMEOUT_MS) peerDown(p, "couldn't connect (network)");
    const { room } = await roomKeys(sid);
    const m = peerMeta(sc, viewer);
    const offer = m && m.o && m.o[room];
    if (!offer) { refreshClient(sc, viewer, p ? 10000 : 3000); return; }
    if (p && p.offerSeen === offer) return;
    const d = await unseal(sid, offer);
    if (!d || d.c !== viewer || typeof d.sdp !== "string") return;
    if (p) peerDown(p, "replaced");
    p = newPeer(sid, sc, viewer, "streamer", null);
    p.offerSeen = offer;
    p2p.peers.set(key, p);
    p.pc.ondatachannel = (e) => wireChannel(p, e.channel);
    try {
      await p.pc.setRemoteDescription({ type: "offer", sdp: d.sdp });
      await p.pc.setLocalDescription(await p.pc.createAnswer());
      await gathered(p.pc);
      if (p.state !== "starting") return;
      metaState(sc).a[viewer] = await seal(sid, { sdp: p.pc.localDescription.sdp, n: d.n });
      writeMeta(sc);
      p.state = "answered";
    } catch (e) { peerDown(p, "answer failed: " + e); }
  }

  async function p2pTick() {
    if (state.transport !== "p2p" || !state.master || p2p.busy || typeof RTCPeerConnection !== "function") return;
    p2p.busy = true;
    try {
      const now = performance.now(), want = new Set();
      for (const s of state.sessions.inbound) {
        if (s.sc == null || s.clientId == null) continue;
        const key = s.sid + "|" + s.clientId;
        want.add(key);
        const p = p2p.peers.get(key);
        if (!p || (p.state === "dead" && now >= p.retryAt)) startViewer(s.sid, s.sc, s.clientId, p).catch((e) => log("offer failed: " + e));
        else if (p.state === "offered" || p.state === "answered") await viewerTick(p);
      }
      for (const s of state.sessions.outbound) {
        if (s.sc == null) continue;
        for (const v of s.viewers || []) {
          want.add(s.sid + "|" + v);
          await streamerTick(s.sid, s.sc, v);
        }
      }
      for (const [key, p] of p2p.peers) {
        if (want.has(key)) continue;
        peerDown(p, "stopped");
        p2p.peers.delete(key);
      }
      const open = [...p2p.peers.values()].filter((p) => p.state === "open").length;
      const report = open + "/" + p2p.peers.size;
      if (report !== p2p.lastReport) { p2p.lastReport = report; send({ t: "p2p", open, total: p2p.peers.size }); }
    } catch (e) { log("p2p tick failed: " + e); }
    finally { p2p.busy = false; }
  }

  function p2pSend(msg) {
    const text = JSON.stringify(msg);
    for (const p of p2p.peers.values()) {
      if (p.sid === msg.sid && p.state === "open") { try { p.ch.send(text); } catch (e) {} }
    }
  }

  const HEX_COLOR = /^#[0-9a-fA-F]{6}$/, STROKE_ID = /^[A-Za-z0-9_-]{1,32}$/;

  // Everything from a peer is untrusted: rebuild it from validated fields only.
  function clean(m, sid, allowAll) {
    if (!m || !["stroke", "clear", "react", "img", "imgreq"].includes(m.t)) return null;
    const out = {
      t: m.t, sid, from: String(m.from || "").slice(0, 32),
      name: String(m.name || "").replace(/[\u0000-\u001f\u007f]/g, "").slice(0, 32),
    };
    if (!out.from) return null;
    if (m.t === "clear") { out.all = allowAll && !!m.all; return out; }
    const num = (v, lo, hi) => Math.min(hi, Math.max(lo, isFinite(+v) ? +v : lo));
    if (m.t === "react") {
      if (!STROKE_ID.test(m.id) || (m.k !== "7tv" && m.k !== "img") || !(m.k === "7tv" ? SEVENTV_ID : IMG_HASH).test(m.ref)) return null;
      return Object.assign(out, { id: m.id, k: m.k, ref: m.ref, x: num(m.x, 0, 1), y: num(m.y, 0, 1), s: num(m.s, 0.03, 0.4), ar: num(m.ar, 0, 10) });
    }
    if (m.t === "img") {
      const n = m.n | 0, i = m.i | 0;
      if (!IMG_HASH.test(m.h) || !IMG_TYPES.includes(m.ty) || typeof m.d !== "string" || m.d.length > 16400 || n < 1 || n > 230 || i < 0 || i >= n) return null;
      return Object.assign(out, { h: m.h, ty: m.ty, n, i, d: m.d });
    }
    if (m.t === "imgreq") return IMG_HASH.test(m.h) ? Object.assign(out, { h: m.h }) : null;
    if (!STROKE_ID.test(m.id)) return null;
    out.id = m.id;
    out.c = HEX_COLOR.test(m.c) ? m.c : "#ff3b30";
    out.w = Math.min(0.05, Math.max(0.001, +m.w || STROKE_WIDTH));
    out.ar = Math.min(10, Math.max(0, +m.ar || 0));
    out.end = !!m.end;
    const pts = Array.isArray(m.p) ? m.p.slice(0, 4000) : [];
    out.p = [];
    for (let i = 0; i + 1 < pts.length; i += 2) {
      const x = +pts[i], y = +pts[i + 1];
      if (!isFinite(x) || !isFinite(y)) return null;
      out.p.push(Math.min(1, Math.max(0, x)), Math.min(1, Math.max(0, y)));
    }
    return out;
  }

  function onPeerMessage(p, data) {
    if (typeof data !== "string" || data.length > 65536) return;
    let msg;
    try { msg = JSON.parse(data); } catch (e) { return; }
    msg = clean(msg, p.sid, p.role === "viewer");   // only the streamer may clear everyone's drawings
    if (!msg) return;
    if (msg.t === "imgreq") { if (images.has(msg.h)) shareImage(p.sid, msg.h, p); return; }
    applyRemote(msg);
    if (msg.t !== "img") send({ t: "rx", msg });     // streamer: TSDraw.exe draws it on screen; also feeds pop-out windows
    if (p.role === "streamer") {                     // pass it on to our other viewers
      const text = JSON.stringify(msg);
      for (const q of p2p.peers.values()) {
        if (q === p || q.sid !== p.sid || q.role !== "streamer" || q.state !== "open") continue;
        if (msg.t === "img") (q.sentImgs = q.sentImgs || new Set()).add(msg.h);
        try { q.ch.send(text); } catch (e) {}
      }
    }
  }

  function p2pShutdown() {
    for (const p of p2p.peers.values()) peerDown(p, "stopped");
    p2p.peers.clear();
    for (const [sc, m] of p2p.meta) { m.o = {}; m.a = {}; writeMeta(sc, true); }
  }

  function refreshUis(sid) {
    for (const l of state.layers) if (l.sid === sid) l.refreshUi();
  }

  // [dot colour, problem text] for a stream layer's pen button.
  function linkStatus(layer) {
    const who = layer.streamer || "The streamer";
    if (!state.helper) return ["bad", "TSDraw.exe is not running, so nobody else will see your drawing."];
    if (state.transport === "p2p") {
      if (!layer.inbound) return ["ok", ""];
      const p = [...p2p.peers.values()].find((q) => q.sid === layer.sid && q.role === "viewer");
      if (p && p.state === "open") return ["ok", ""];
      if (p && p.why === "no answer") return ["wait", who + " isn't running TSDraw, so they won't see your drawing."];
      if (p && p.why) return ["bad", "Can't reach " + who + ": " + p.why + ". Retrying..."];
      return ["wait", "Connecting to " + who + "..."];
    }
    if (state.relay !== "connected") {
      return [state.relay === "connecting" ? "wait" : "bad", "Relay " + state.relay + " - the streamer won't see your drawing until it connects."];
    }
    const seen = state.present.get(layer.sid);
    if (layer.inbound && !(seen !== undefined && performance.now() - seen < PRESENCE_MS)) {
      return ["wait", who + " isn't running TSDraw, so they won't see your drawing."];
    }
    return ["ok", ""];
  }

  // Our own drawing: to TSDraw.exe (overlay for our own stream, relay mode, pop-outs) and to peers.
  function emitLocal(msg) {
    send(msg);
    if (state.transport === "p2p" && state.master) p2pSend(Object.assign({}, msg, { from: state.myId, name: state.name }));
  }

  // ---------------------------------------------------------------- reactions
  // Twitch-style reaction images dropped on a stream: 7TV emotes (sent as their id) or
  // your own images (sent once per link in chunks, then referenced by SHA-256 hash).

  const REACT_MS = 4000;
  const REACT_EMOTE_SIZE = 0.12;            // height, as a fraction of the stream frame
  const REACT_IMAGE_SIZE = 0.22;
  const MAX_IMG_BYTES = 2.5 * 1024 * 1024;
  const IMG_CHUNK = 12000;
  const SEVENTV_ID = /^[0-9A-Za-z]{20,32}$/, IMG_HASH = /^[0-9a-f]{64}$/;
  const IMG_TYPES = ["image/gif", "image/png", "image/jpeg", "image/webp"];
  const DEFAULT_HINT = "Click: random spot · Drag: place it · Right-click: favourite";
  const images = new Map();                 // hash -> { type, bytes, url, w, h }
  const incoming = new Map();               // hash -> image still arriving in chunks
  const waitingForImage = new Map();        // hash -> reactions shown once their image arrives
  const helperAssets = new Map();           // "7tv:<id>" / "img:<hash>" -> decode job for TSDraw.exe's overlay
  const imgRequested = new Map(), relayShared = new Map();
  const myReacts = [];                      // timestamps of our own reactions, for the rate limit
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const helperCalls = new Map();
  let helperCallId = 0;

  // A request to TSDraw.exe, answered with {t: "reply", req, ...}. TeamSpeak's UI may only fetch()
  // from TeamSpeak's own servers, so TSDraw.exe talks to 7TV for us.
  function helperCall(t, args, timeoutMs) {
    return new Promise((resolve, reject) => {
      if (!state.helper) { reject(new Error("TSDraw.exe isn't running")); return; }
      const req = ++helperCallId;
      const timer = setTimeout(() => { helperCalls.delete(req); reject(new Error("no answer from TSDraw.exe")); }, timeoutMs || 15000);
      helperCalls.set(req, { resolve, reject, timer });
      send(Object.assign({ t, req }, args));
    });
  }

  function onHelperReply(msg) {
    const c = helperCalls.get(msg.req);
    if (!c) return;
    helperCalls.delete(msg.req);
    clearTimeout(c.timer);
    if (msg.error) c.reject(new Error(msg.error)); else c.resolve(msg);
  }
  const round4 = (n) => Math.round(n * 10000) / 10000;
  const emoteUrl = (id, size) => "https://cdn.7tv.app/emote/" + id + "/" + size + ".webp";
  const assetKey = (m) => (m.k === "7tv" ? "7tv:" : "img:") + m.ref;

  function sniffType(u8) {
    if (!u8 || u8.length < 12) return null;
    if (u8[0] === 0x47 && u8[1] === 0x49 && u8[2] === 0x46) return "image/gif";
    if (u8[0] === 0x89 && u8[1] === 0x50 && u8[2] === 0x4e && u8[3] === 0x47) return "image/png";
    if (u8[0] === 0xff && u8[1] === 0xd8 && u8[2] === 0xff) return "image/jpeg";
    if (u8[0] === 0x52 && u8[1] === 0x49 && u8[2] === 0x46 && u8[3] === 0x46 &&
        u8[8] === 0x57 && u8[9] === 0x45 && u8[10] === 0x42 && u8[11] === 0x50) return "image/webp";
    return null;
  }

  function imageUrl(hash) {
    const e = images.get(hash);
    if (!e) return null;
    if (!e.url) e.url = URL.createObjectURL(new Blob([e.bytes], { type: e.type }));
    return e.url;
  }

  const isStreaming = (sid) => state.sessions.outbound.some((o) => o.sid === sid);

  // Show a reaction on every layer of its stream; as the streamer, also prepare it for TSDraw.exe's overlay.
  function spawnReact(m) {
    if (isStreaming(m.sid)) ensureHelperAsset(m);
    const src = m.k === "7tv" ? emoteUrl(m.ref, "3x") : imageUrl(m.ref);
    if (!src) {   // their image is still on its way
      let list = waitingForImage.get(m.ref);
      if (!list) waitingForImage.set(m.ref, (list = []));
      if (list.length < 20) list.push(m);
      requestImage(m.sid, m.ref);
      setTimeout(() => { const l = waitingForImage.get(m.ref); if (l && l.includes(m)) l.splice(l.indexOf(m), 1); }, 15000);
      return;
    }
    for (const l of state.layers) if (l.sid === m.sid) l.addReact(m, src);
  }

  // Our own reaction: x/y in 0..1 of the stream frame, or null for a random spot.
  function placeReact(layer, item, x, y) {
    const now = performance.now();
    while (myReacts.length && now - myReacts[0] > 3000) myReacts.shift();
    if (myReacts.length >= 6) { if (layer.panel) layer.panel.setHint("Easy - that's a lot of reactions at once.", true); return; }
    myReacts.push(now);
    if (x == null) { x = 0.12 + Math.random() * 0.76; y = 0.18 + Math.random() * 0.64; }
    const v = layer.video, ar = v.videoWidth && v.videoHeight ? round4(v.videoWidth / v.videoHeight) : 0;
    const msg = { t: "react", sid: layer.sid, id: Math.random().toString(36).slice(2, 10), k: item.k, ref: item.ref,
      x: round4(x), y: round4(y), s: item.k === "7tv" ? REACT_EMOTE_SIZE : imageSize(item.ref), ar };
    if (item.k === "img") shareImage(layer.sid, item.ref);
    spawnReact(Object.assign({ from: "self", name: "" }, msg));
    emitLocal(msg);
  }

  function imageSize(hash) {
    const e = images.get(hash), ar = e && e.w && e.h ? e.w / e.h : 1;
    return round4(ar > 1.4 ? REACT_IMAGE_SIZE * 1.4 / ar : REACT_IMAGE_SIZE);
  }

  function imageChunks(sid, hash) {
    const e = images.get(hash), n = Math.ceil(e.bytes.length / IMG_CHUNK), out = [];
    for (let i = 0; i < n; i++) {
      out.push({ t: "img", sid, from: state.myId, h: hash, ty: e.type, n, i, d: b64(e.bytes.subarray(i * IMG_CHUNK, (i + 1) * IMG_CHUNK)) });
    }
    return out;
  }

  // Make sure everyone on the stream has an image before (or soon after) its reaction arrives.
  function shareImage(sid, hash, onlyPeer) {
    if (!images.has(hash)) return;
    if (state.transport === "p2p" && state.master) {
      let chunks = null;
      for (const p of p2p.peers.values()) {
        if (p.sid !== sid || p.state !== "open" || (onlyPeer && p !== onlyPeer)) continue;
        p.sentImgs = p.sentImgs || new Set();
        if (p.sentImgs.has(hash) && !onlyPeer) continue;
        p.sentImgs.add(hash);
        chunks = chunks || imageChunks(sid, hash).map((c) => JSON.stringify(c));
        for (const c of chunks) { try { p.ch.send(c); } catch (e) {} }
      }
      return;
    }
    // Relay mode or a pop-out window: through TSDraw.exe, at most once a minute per image and stream.
    const k = sid + "|" + hash, now = performance.now();
    if ((relayShared.get(k) || 0) > now) return;
    relayShared.set(k, now + 60000);
    for (const c of imageChunks(sid, hash)) send(c);
  }

  function requestImage(sid, hash) {
    const now = performance.now();
    if ((imgRequested.get(hash) || 0) > now) return;
    imgRequested.set(hash, now + 10000);
    if (state.transport === "p2p" && state.master) p2pSend({ t: "imgreq", sid, from: state.myId, h: hash });
  }

  async function onImgChunk(m) {
    if (images.has(m.h)) return;
    let a = incoming.get(m.h);
    if (!a) {
      if (incoming.size >= 6) incoming.delete(incoming.keys().next().value);
      a = { ty: m.ty, n: m.n, parts: new Array(m.n), got: 0, size: 0 };
      incoming.set(m.h, a);
    }
    if (a.n !== m.n || a.ty !== m.ty || a.parts[m.i]) return;
    let bytes;
    try { bytes = unb64(m.d); } catch (e) { incoming.delete(m.h); return; }
    a.size += bytes.length;
    if (a.size > MAX_IMG_BYTES) { incoming.delete(m.h); return; }
    a.parts[m.i] = bytes;
    if (++a.got < a.n) return;
    incoming.delete(m.h);
    const all = new Uint8Array(a.size);
    let off = 0;
    for (const p of a.parts) { all.set(p, off); off += p.length; }
    if (hex(await crypto.subtle.digest("SHA-256", all)) !== m.h || sniffType(all) !== a.ty) return;
    let dims = { w: 0, h: 0 };
    try { dims = await probeImage(all, a.ty); } catch (e) {}
    images.set(m.h, { type: a.ty, bytes: all, w: dims.w, h: dims.h });
    const waiting = waitingForImage.get(m.h);
    waitingForImage.delete(m.h);
    for (const r of waiting || []) spawnReact(r);
  }

  // ---- decoding (Chromium handles GIF, animated WebP, AVIF; TSDraw.exe gets ready-made frames)

  async function probeImage(bytes, type) {
    if (typeof ImageDecoder === "function") {
      const dec = new ImageDecoder({ data: bytes, type });
      try {
        await dec.tracks.ready;
        const frames = dec.tracks.selectedTrack ? dec.tracks.selectedTrack.frameCount : 1;
        const { image } = await dec.decode({ frameIndex: 0 });
        const r = { w: image.displayWidth, h: image.displayHeight, frames };
        image.close();
        return r;
      } finally { dec.close(); }
    }
    const b = await createImageBitmap(new Blob([bytes], { type }));
    const r = { w: b.width, h: b.height, frames: 1 };
    b.close();
    return r;
  }

  async function decodeFrames(bytes, type, maxFrames) {
    if (typeof ImageDecoder !== "function") return [{ img: await createImageBitmap(new Blob([bytes], { type })), ms: 0 }];
    const dec = new ImageDecoder({ data: bytes, type });
    const frames = [];
    try {
      await dec.tracks.ready;
      const total = dec.tracks.selectedTrack ? dec.tracks.selectedTrack.frameCount : 1;
      const step = Math.max(1, Math.ceil(total / maxFrames));
      for (let i = 0; i < total; i += step) {
        let ms = 0, keep = null;
        for (let j = i; j < Math.min(total, i + step); j++) {
          const { image } = await dec.decode({ frameIndex: j });
          ms += image.duration ? image.duration / 1000 : 100;
          if (j === i) keep = image; else image.close();
        }
        frames.push({ img: keep, ms: total > 1 ? Math.max(20, Math.round(ms)) : 0 });
      }
    } finally { dec.close(); }
    return frames;
  }

  // All frames side by side in one PNG, at most 192 px tall.
  async function buildSheet(bytes, type) {
    const frames = await decodeFrames(bytes, type, 48);
    const f0 = frames[0].img;
    const sw = f0.displayWidth || f0.width, sh = f0.displayHeight || f0.height;
    const h = Math.min(192, sh), w = Math.max(1, Math.min(384, Math.round(sw * h / sh)));
    const cols = Math.max(1, Math.min(frames.length, Math.floor(4096 / w)));
    const cv = document.createElement("canvas");
    cv.width = cols * w; cv.height = Math.ceil(frames.length / cols) * h;
    const ctx = cv.getContext("2d");
    frames.forEach((f, i) => { ctx.drawImage(f.img, (i % cols) * w, Math.floor(i / cols) * h, w, h); f.img.close(); });
    const blob = await new Promise((r) => cv.toBlob(r, "image/png"));
    const png = await new Promise((r) => { const fr = new FileReader(); fr.onload = () => r(String(fr.result).split(",")[1]); fr.readAsDataURL(blob); });
    return { w, h, n: frames.length, cols, delays: frames.map((f) => f.ms), png };
  }

  // As the streamer: TSDraw.exe can't decode WebP/AVIF/animations itself, so decode here, once per emote/image.
  function ensureHelperAsset(m) {
    const key = assetKey(m);
    if (helperAssets.has(key)) return;
    const job = (async () => {
      let bytes, type;
      if (m.k === "7tv") {
        bytes = unb64((await helperCall("emotebytes", { id: m.ref }, 30000)).b64);
        type = sniffType(bytes) || "image/webp";
      } else {
        for (let i = 0; i < 30 && !images.has(m.ref); i++) await sleep(500);
        const e = images.get(m.ref);
        if (!e) throw new Error("the image never arrived");
        bytes = e.bytes; type = e.type;
      }
      send(Object.assign({ t: "asset", key }, await buildSheet(bytes, type)));
    })();
    helperAssets.set(key, job);
    job.catch((e) => { helperAssets.delete(key); log("couldn't prepare reaction " + key + " for the overlay: " + e); });
  }

  // ---- your own images (kept in IndexedDB) and favourites (localStorage)

  let favs = loadFavs(), myImages = null, popular = null;

  function validItem(it) { return !!it && ((it.k === "7tv" && SEVENTV_ID.test(it.ref)) || (it.k === "img" && IMG_HASH.test(it.ref))); }
  function loadFavs() {
    try { const v = JSON.parse(localStorage.getItem("tsdraw.favs") || "[]"); return Array.isArray(v) ? v.filter(validItem).slice(0, 60) : []; }
    catch (e) { return []; }
  }
  const isFav = (it) => favs.some((f) => f.k === it.k && f.ref === it.ref);
  function toggleFav(it) {
    favs = isFav(it) ? favs.filter((f) => !(f.k === it.k && f.ref === it.ref)) : [{ k: it.k, ref: it.ref, name: it.name }].concat(favs).slice(0, 60);
    try { localStorage.setItem("tsdraw.favs", JSON.stringify(favs)); } catch (e) {}
  }

  function idb(mode, fn) {
    return new Promise((resolve, reject) => {
      const open = indexedDB.open("tsdraw", 1);
      open.onupgradeneeded = () => open.result.createObjectStore("images", { keyPath: "hash" });
      open.onerror = () => reject(open.error);
      open.onsuccess = () => {
        const tx = open.result.transaction("images", mode), req = fn(tx.objectStore("images"));
        tx.oncomplete = () => { open.result.close(); resolve(req && req.result); };
        tx.onerror = () => { open.result.close(); reject(tx.error); };
      };
    });
  }

  async function loadMyImages() {
    if (myImages) return myImages;
    try {
      const all = (await idb("readonly", (s) => s.getAll())) || [];
      all.sort((a, b) => b.added - a.added);
      for (const e of all) if (!images.has(e.hash)) images.set(e.hash, { type: e.type, bytes: new Uint8Array(e.bytes), w: e.w, h: e.h });
      myImages = all.map((e) => ({ k: "img", ref: e.hash, name: e.name, w: e.w, h: e.h }));
    } catch (e) { myImages = []; log("couldn't load saved images: " + e); }
    return myImages;
  }

  async function importImage(file) {
    if (file.size > 16 * 1024 * 1024) throw new Error("that file is too big");
    let bytes = new Uint8Array(await file.arrayBuffer()), type = sniffType(bytes);
    if (!type) throw new Error("it isn't a GIF, PNG, JPEG or WebP image");
    let info = await probeImage(bytes, type);
    if (info.frames <= 1 && (bytes.length > 300 * 1024 || Math.max(info.w, info.h) > 512)) {   // shrink big stills
      const b = await createImageBitmap(new Blob([bytes], { type }));
      const k = Math.min(1, 512 / Math.max(b.width, b.height));
      const cv = document.createElement("canvas");
      cv.width = Math.max(1, Math.round(b.width * k)); cv.height = Math.max(1, Math.round(b.height * k));
      cv.getContext("2d").drawImage(b, 0, 0, cv.width, cv.height);
      b.close();
      bytes = new Uint8Array(await (await new Promise((r) => cv.toBlob(r, "image/webp", 0.9))).arrayBuffer());
      type = "image/webp";
      info = { w: cv.width, h: cv.height, frames: 1 };
    }
    if (bytes.length > MAX_IMG_BYTES) throw new Error("it's " + (bytes.length / 1048576).toFixed(1) + " MB; the limit is 2.5 MB");
    const hash = hex(await crypto.subtle.digest("SHA-256", bytes));
    images.set(hash, { type, bytes, w: info.w, h: info.h });
    const item = { k: "img", ref: hash, name: file.name.slice(0, 40), w: info.w, h: info.h };
    await loadMyImages();
    const dropped = myImages.filter((i) => i.ref !== hash).slice(39);
    myImages = [item].concat(myImages.filter((i) => i.ref !== hash)).slice(0, 40);
    try {
      await idb("readwrite", (s) => {
        s.put({ hash, type, bytes: bytes.slice().buffer, name: item.name, w: info.w, h: info.h, added: Date.now() });
        for (const d of dropped) s.delete(d.ref);
      });
    } catch (e) { log("couldn't save the image: " + e); }
    return item;
  }

  async function forgetImage(hash) {
    myImages = (myImages || []).filter((i) => i.ref !== hash);
    favs = favs.filter((f) => f.ref !== hash);
    try { localStorage.setItem("tsdraw.favs", JSON.stringify(favs)); } catch (e) {}
    try { await idb("readwrite", (s) => s.delete(hash)); } catch (e) {}
  }

  // ---- 7TV

  async function sevenTv(query) {
    const r = await helperCall("7tv", { q: query });
    return (r.items || []).filter(validItem).map((e) => ({ k: "7tv", ref: e.ref, name: String(e.name || "").slice(0, 40) }));
  }

  // ---- the panel

  class EmotePanel {
    constructor(layer) {
      this.layer = layer;
      const el = this.el = document.createElement("div");
      el.className = "tsdraw-panel";
      el.innerHTML = '<div class="tsdraw-ph"><input class="tsdraw-search" type="search" placeholder="Search 7TV emotes" spellcheck="false">' +
        '<button class="tsdraw-add" title="Add your own image or GIF (max 2.5 MB)">+ Image</button>' +
        '<input type="file" accept="image/gif,image/png,image/jpeg,image/webp" hidden></div>' +
        '<div class="tsdraw-pbody"></div><div class="tsdraw-hint"></div>';
      this.search = el.querySelector(".tsdraw-search");
      this.body = el.querySelector(".tsdraw-pbody");
      this.hint = el.querySelector(".tsdraw-hint");
      this.hint.textContent = DEFAULT_HINT;
      const file = this.file = el.querySelector("input[type=file]");
      el.querySelector(".tsdraw-add").addEventListener("click", () => file.click());
      file.addEventListener("change", () => this.onFile());
      let timer = 0;
      this.search.addEventListener("input", () => { clearTimeout(timer); timer = setTimeout(() => this.render(), 350); });
      el.addEventListener("keydown", (e) => { if (e.key === "Escape") layer.togglePanel(false); });
      for (const ev of ["click", "dblclick", "mousedown", "mouseup", "pointerdown", "contextmenu", "wheel", "keydown", "keyup", "keypress"]) {
        el.addEventListener(ev, (e) => e.stopPropagation());
      }
      layer.el.appendChild(el);
      this.render();
      setTimeout(() => this.search.focus(), 0);
    }

    setHint(text, bad) {
      this.hint.textContent = text;
      this.hint.classList.toggle("tsdraw-bad", !!bad);
      clearTimeout(this.hintTimer);
      this.hintTimer = setTimeout(() => { this.hint.textContent = DEFAULT_HINT; this.hint.classList.remove("tsdraw-bad"); }, 5000);
    }

    async onFile() {
      const f = this.file.files && this.file.files[0];
      this.file.value = "";
      if (!f) return;
      this.setHint("Adding " + f.name + "...");
      try {
        await importImage(f);
        this.search.value = "";
        await this.render();
        this.setHint("Added under My images. Click it to react, or drag it onto the stream.");
      } catch (e) { this.setHint("Couldn't add it: " + (e.message || e), true); }
    }

    async render() {
      const q = this.search.value.trim(), token = (this.token = (this.token || 0) + 1);
      const sections = [];
      try {
        const mine = await loadMyImages();
        if (q) {
          this.body.innerHTML = '<div class="tsdraw-empty">Searching 7TV...</div>';
          const found = await sevenTv(q);
          sections.push(["My images", mine.filter((i) => i.name.toLowerCase().includes(q.toLowerCase()))], ["7TV", found]);
        } else {
          sections.push(["Favourites", favs], ["My images", mine]);
          if (!popular) {
            this.body.innerHTML = '<div class="tsdraw-empty">Loading 7TV emotes...</div>';
            popular = await sevenTv("");
          }
          sections.push(["Popular on 7TV", popular]);
        }
      } catch (e) {
        if (token === this.token) this.body.innerHTML = '<div class="tsdraw-empty">Couldn\'t reach 7TV (' + String(e.message || e).replace(/[<>&]/g, "") + ')</div>';
        return;
      }
      if (token !== this.token) return;
      this.body.textContent = "";
      for (const [title, items] of sections) {
        if (!items || !items.length) continue;
        const head = document.createElement("div");
        head.className = "tsdraw-sec";
        head.textContent = title;
        const grid = document.createElement("div");
        grid.className = "tsdraw-grid";
        for (const it of items) grid.appendChild(this.tile(it));
        this.body.appendChild(head);
        this.body.appendChild(grid);
      }
      if (!this.body.childElementCount) this.body.innerHTML = '<div class="tsdraw-empty">Nothing found</div>';
    }

    tile(it) {
      const b = document.createElement("button");
      b.className = "tsdraw-tile" + (isFav(it) ? " tsdraw-fav" : "");
      b.title = (it.name || "") + (it.k === "img" ? " (Shift+right-click to remove)" : "");
      const im = document.createElement("img");
      im.draggable = false; im.alt = it.name || ""; im.loading = "lazy";
      im.src = it.k === "7tv" ? emoteUrl(it.ref, "2x") : (imageUrl(it.ref) || "");
      b.appendChild(im);
      b.addEventListener("contextmenu", async (e) => {
        e.preventDefault();
        if (e.shiftKey && it.k === "img") await forgetImage(it.ref); else toggleFav(it);
        this.render();
      });
      b.addEventListener("pointerdown", (e) => this.drag(e, b, it, im.src));
      return b;
    }

    // A click drops the reaction somewhere random; dragging it onto the stream drops it there.
    drag(e, tile, it, src) {
      if (e.button !== 0) return;
      e.preventDefault();
      try { tile.setPointerCapture(e.pointerId); } catch (x) {}
      const x0 = e.clientX, y0 = e.clientY;
      let ghost = null;
      const move = (ev) => {
        if (!ghost && Math.hypot(ev.clientX - x0, ev.clientY - y0) < 6) return;
        if (!ghost) {
          ghost = document.createElement("img");
          ghost.className = "tsdraw-ghost"; ghost.src = src;
          document.body.appendChild(ghost);
        }
        ghost.style.left = ev.clientX + "px";
        ghost.style.top = ev.clientY + "px";
      };
      const up = (ev) => {
        tile.removeEventListener("pointermove", move);
        tile.removeEventListener("pointerup", up);
        tile.removeEventListener("pointercancel", up);
        if (!ghost) { if (ev.type === "pointerup") placeReact(this.layer, it); return; }
        ghost.remove();
        const r = this.layer.reactBox.getBoundingClientRect();
        const x = (ev.clientX - r.left) / r.width, y = (ev.clientY - r.top) / r.height;
        if (ev.type === "pointerup" && x >= 0 && x <= 1 && y >= 0 && y <= 1) placeReact(this.layer, it, x, y);
      };
      tile.addEventListener("pointermove", move);
      tile.addEventListener("pointerup", up);
      tile.addEventListener("pointercancel", up);
    }

    destroy() { this.el.remove(); }
  }

  // ---------------------------------------------------------------- styles

  const CSS = `
.tsdraw-layer{position:absolute;overflow:hidden;pointer-events:none;z-index:5}
.tsdraw-layer canvas{position:absolute;left:0;top:0;pointer-events:none;touch-action:none}
.tsdraw-layer.tsdraw-on canvas{pointer-events:auto;cursor:crosshair}
.tsdraw-layer.tsdraw-on{box-shadow:inset 0 0 0 2px var(--tsdraw-color,#0a84ff)}
.tsdraw-bar{position:absolute;top:8px;right:8px;display:flex;align-items:center;gap:4px;padding:4px;border-radius:16px;max-width:calc(100% - 16px);box-sizing:border-box;
  background:rgba(20,24,33,.82);box-shadow:0 2px 8px rgba(0,0,0,.35);pointer-events:auto;opacity:0;transition:opacity .15s;
  font:12px/1 -apple-system,'Segoe UI',sans-serif;color:#fff;user-select:none;z-index:6}
.tsdraw-host-hover .tsdraw-bar,.tsdraw-layer.tsdraw-on .tsdraw-bar{opacity:1}
.tsdraw-btn{width:28px;height:28px;border-radius:999px;border:0;background:transparent;color:#fff;display:flex;align-items:center;
  justify-content:center;cursor:pointer;padding:0;position:relative}
.tsdraw-btn:hover{background:rgba(255,255,255,.14)}
.tsdraw-btn.tsdraw-active{background:var(--tsdraw-color,#0a84ff);color:#111}
.tsdraw-btn svg{width:16px;height:16px;fill:none;stroke:currentColor;stroke-width:2;stroke-linecap:round;stroke-linejoin:round}
.tsdraw-swatch{width:16px;height:16px;border-radius:999px;border:2px solid rgba(255,255,255,.25);cursor:pointer;padding:0;flex:none}
.tsdraw-swatch.tsdraw-sel{border-color:#fff;transform:scale(1.2)}
.tsdraw-tools{display:none;flex-wrap:wrap;justify-content:flex-end;align-items:center;gap:4px;padding:0 2px 0 4px;min-width:0}
.tsdraw-on .tsdraw-tools{display:flex}
.tsdraw-dot{position:absolute;right:3px;bottom:3px;width:7px;height:7px;border-radius:999px;background:#888;box-shadow:0 0 0 1.5px rgba(20,24,33,.9)}
.tsdraw-dot.ok{background:#34c759}.tsdraw-dot.wait{background:#ffcc00}.tsdraw-dot.bad{background:#ff3b30}
.tsdraw-tip{position:absolute;top:44px;right:8px;max-width:260px;padding:6px 8px;border-radius:6px;background:rgba(20,24,33,.92);
  color:#fff;font:12px/1.35 -apple-system,'Segoe UI',sans-serif;pointer-events:none;display:none;z-index:6}
.tsdraw-on .tsdraw-tip.tsdraw-show{display:block}
.tsdraw-layer.tsdraw-panel-open .tsdraw-bar{opacity:1}
.tsdraw-btn.tsdraw-open{background:rgba(255,255,255,.2)}
.tsdraw-reacts{position:absolute;left:0;top:0;pointer-events:none;overflow:hidden}
.tsdraw-react{position:absolute;transform:translate(-50%,-50%);animation:tsdraw-react 4s ease-out forwards}
.tsdraw-react img{height:100%;width:auto;display:block;filter:drop-shadow(0 2px 4px rgba(0,0,0,.45))}
.tsdraw-react span{position:absolute;top:100%;left:50%;transform:translateX(-50%);margin-top:2px;padding:1px 6px;border-radius:6px;
  background:rgba(20,24,33,.8);color:#fff;font:600 11px/1.4 -apple-system,'Segoe UI',sans-serif;white-space:nowrap}
@keyframes tsdraw-react{0%{opacity:0;transform:translate(-50%,-50%) scale(.3)}7%{opacity:1;transform:translate(-50%,-50%) scale(1.15)}
  13%{transform:translate(-50%,-50%) scale(1)}82%{opacity:1;transform:translate(-50%,-62%) scale(1)}100%{opacity:0;transform:translate(-50%,-80%) scale(.8)}}
.tsdraw-panel{position:absolute;top:48px;right:8px;width:296px;max-width:calc(100% - 16px);max-height:calc(100% - 64px);display:flex;flex-direction:column;
  background:rgba(20,24,33,.95);border-radius:12px;box-shadow:0 6px 24px rgba(0,0,0,.45);pointer-events:auto;z-index:7;overflow:hidden;
  font:12px/1.3 -apple-system,'Segoe UI',sans-serif;color:#e8ecf3}
.tsdraw-ph{display:flex;gap:6px;padding:8px}
.tsdraw-search{flex:1;min-width:0;background:rgba(255,255,255,.08);border:1px solid rgba(255,255,255,.14);border-radius:8px;color:#fff;padding:6px 8px;font:inherit;outline:none}
.tsdraw-search:focus{border-color:var(--tsdraw-color,#0a84ff)}
.tsdraw-add{background:rgba(255,255,255,.1);border:0;border-radius:8px;color:#fff;padding:0 10px;font:inherit;cursor:pointer;white-space:nowrap}
.tsdraw-add:hover{background:rgba(255,255,255,.18)}
.tsdraw-pbody{overflow-y:auto;padding:0 8px 4px;min-height:60px;scrollbar-width:thin;scrollbar-color:rgba(255,255,255,.25) transparent}
.tsdraw-pbody::-webkit-scrollbar{width:8px}.tsdraw-pbody::-webkit-scrollbar-thumb{background:rgba(255,255,255,.22);border-radius:4px}
.tsdraw-pbody::-webkit-scrollbar-track{background:transparent}
.tsdraw-sec{margin:6px 2px 4px;font-weight:600;font-size:11px;color:#9aa4b5;text-transform:uppercase;letter-spacing:.04em}
.tsdraw-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(40px,1fr));gap:4px}
.tsdraw-tile{height:40px;border:0;border-radius:6px;background:transparent;cursor:grab;padding:3px;display:flex;align-items:center;justify-content:center;position:relative;touch-action:none}
.tsdraw-tile:hover{background:rgba(255,255,255,.1)}
.tsdraw-tile img{max-width:100%;max-height:100%;pointer-events:none}
.tsdraw-tile.tsdraw-fav::after{content:"\u2605";position:absolute;right:2px;top:0;font-size:10px;color:#ffcc00}
.tsdraw-empty{padding:16px 4px;color:#9aa4b5;text-align:center}
.tsdraw-hint{padding:6px 10px 8px;color:#9aa4b5;font-size:11px;border-top:1px solid rgba(255,255,255,.06)}
.tsdraw-hint.tsdraw-bad{color:#ff8a80}
.tsdraw-ghost{position:fixed;height:56px;transform:translate(-50%,-50%);pointer-events:none;z-index:2147483647;opacity:.9}`;

  const ICON_PEN = '<svg viewBox="0 0 24 24"><path d="M4 20l4.5-1 10-10a2.1 2.1 0 0 0-3-3l-10 10L4 20z"/><path d="M14 7l3 3"/></svg>';
  const ICON_SMILE = '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M8.5 14.5a4.5 4.5 0 0 0 7 0M9 9.5h.01M15 9.5h.01"/></svg>';
  const ICON_TRASH = '<svg viewBox="0 0 24 24"><path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/></svg>';

  function ensureStyle() {
    if (document.getElementById("tsdraw-style")) return;
    const st = document.createElement("style");
    st.id = "tsdraw-style";
    st.textContent = CSS;
    (document.head || document.documentElement).appendChild(st);
  }

  // ---------------------------------------------------------------- layers

  function sessionOfVideo(video) {
    let el = video;
    while (el && !el.__vue__) el = el.parentElement;
    let vm = el && el.__vue__;
    for (let i = 0; vm && i < 6; i++, vm = vm.$parent) {
      const s = vm.session;
      if (s && s.idData) return s;
    }
    return null;
  }

  class Layer {
    constructor(video, sid, inbound) {
      this.video = video; this.sid = sid; this.inbound = inbound;
      this.host = video.parentElement;
      this.drawing = false; this.cur = null; this.pending = []; this.flushTimer = 0;
      this.frame = { x: 0, y: 0, w: 1, h: 1 };

      const el = this.el = document.createElement("div");
      el.className = "tsdraw-layer";
      el.style.setProperty("--tsdraw-color", state.color);
      const cv = this.canvas = document.createElement("canvas");
      el.appendChild(cv);
      const rb = this.reactBox = document.createElement("div");
      rb.className = "tsdraw-reacts";
      el.appendChild(rb);
      this.panel = null;

      const bar = this.bar = document.createElement("div");
      bar.className = "tsdraw-bar";
      const tools = document.createElement("div");
      tools.className = "tsdraw-tools";
      this.swatches = COLORS.map((c) => {
        const b = document.createElement("button");
        const i = COLORS.indexOf(c);
        b.className = "tsdraw-swatch"; b.style.background = c; b.title = COLOR_NAMES[i] + " (" + ((i + 1) % 10) + ")";
        b.addEventListener("click", (e) => { e.stopPropagation(); setColor(c); });
        tools.appendChild(b);
        return b;
      });
      const clear = document.createElement("button");
      clear.className = "tsdraw-btn"; clear.innerHTML = ICON_TRASH; clear.title = "Clear my drawings";
      clear.addEventListener("click", (e) => { e.stopPropagation(); this.clearOwn(); });
      tools.appendChild(clear);
      const pen = this.penBtn = document.createElement("button");
      pen.className = "tsdraw-btn"; pen.innerHTML = ICON_PEN;
      const dot = this.dot = document.createElement("span");
      dot.className = "tsdraw-dot";
      pen.appendChild(dot);
      pen.addEventListener("click", (e) => { e.stopPropagation(); this.setDrawing(!this.drawing); });
      const emo = this.emoBtn = document.createElement("button");
      emo.className = "tsdraw-btn"; emo.innerHTML = ICON_SMILE; emo.title = "Reactions: 7TV emotes and your own images";
      emo.addEventListener("click", (e) => { e.stopPropagation(); this.togglePanel(); });
      bar.appendChild(tools); bar.appendChild(emo); bar.appendChild(pen);
      el.appendChild(bar);

      const tip = this.tip = document.createElement("div");
      tip.className = "tsdraw-tip";
      el.appendChild(tip);

      for (const ev of ["click", "dblclick", "mousedown", "mouseup", "contextmenu", "wheel"]) {
        bar.addEventListener(ev, (e) => e.stopPropagation());
      }
      cv.addEventListener("pointerdown", this.onDown.bind(this));
      cv.addEventListener("pointermove", this.onMove.bind(this));
      cv.addEventListener("pointerup", this.onUp.bind(this));
      cv.addEventListener("pointercancel", this.onUp.bind(this));
      for (const ev of ["click", "dblclick", "mousedown", "mouseup", "contextmenu", "wheel"]) {
        cv.addEventListener(ev, (e) => { if (this.drawing) { e.stopPropagation(); if (ev !== "wheel") e.preventDefault(); } });
      }
      this.onEnter = () => this.host.classList.add("tsdraw-host-hover");
      this.onLeave = () => this.host.classList.remove("tsdraw-host-hover");
      this.host.addEventListener("mouseenter", this.onEnter);
      this.host.addEventListener("mouseleave", this.onLeave);
      if (this.host.matches(":hover")) this.onEnter();

      if (getComputedStyle(this.host).position === "static") this.host.style.position = "relative";
      this.host.appendChild(el);
      this.refreshUi();
      this.layout();
    }

    alive() {
      return this.video.isConnected && this.el.isConnected && this.video.parentElement === this.host;
    }

    togglePanel(on) {
      if (on === undefined) on = !this.panel;
      if (on && !this.panel) {
        for (const l of state.layers) if (l !== this) l.togglePanel(false);
        this.panel = new EmotePanel(this);
      } else if (!on && this.panel) { this.panel.destroy(); this.panel = null; }
      this.el.classList.toggle("tsdraw-panel-open", !!this.panel);
      this.emoBtn.classList.toggle("tsdraw-open", !!this.panel);
    }

    addReact(m, src) {
      const box = this.reactBox;
      while (box.childElementCount >= 40) box.firstElementChild.remove();
      const el = document.createElement("div");
      el.className = "tsdraw-react";
      el.style.left = m.x * 100 + "%"; el.style.top = m.y * 100 + "%"; el.style.height = m.s * 100 + "%";
      el.style.animationDuration = REACT_MS + "ms";
      const im = document.createElement("img");
      im.src = src; im.alt = ""; im.draggable = false;
      el.appendChild(im);
      if (m.from !== "self" && m.name) { const n = document.createElement("span"); n.textContent = m.name; el.appendChild(n); }
      box.appendChild(el);
      setTimeout(() => el.remove(), REACT_MS + 50);
    }

    destroy() {
      this.togglePanel(false);
      this.setDrawing(false);
      this.host.removeEventListener("mouseenter", this.onEnter);
      this.host.removeEventListener("mouseleave", this.onLeave);
      this.host.classList.remove("tsdraw-host-hover");
      this.el.remove();
    }

    refreshUi() {
      this.el.style.setProperty("--tsdraw-color", state.color);
      this.swatches.forEach((b, i) => b.classList.toggle("tsdraw-sel", COLORS[i] === state.color));
      this.penBtn.classList.toggle("tsdraw-active", this.drawing);
      const [level, problem] = linkStatus(this);
      this.dot.className = "tsdraw-dot " + level;
      const key = state.hotkey ? " (" + state.hotkey + ")" : "";
      this.penBtn.title = (this.drawing ? "Stop drawing" + (key || " (Esc)") : "Draw on this stream" + key) + (problem ? " - " + problem : "");
      if (this.drawing && problem) {
        this.tip.textContent = problem;
        this.tip.classList.add("tsdraw-show");
      } else this.tip.classList.remove("tsdraw-show");
    }

    setDrawing(on) {
      if (on === this.drawing) return;
      if (on) for (const l of state.layers) if (l !== this) l.setDrawing(false);
      this.drawing = on;
      this.el.classList.toggle("tsdraw-on", on);
      if (!on && this.cur) this.finishStroke();
      if (on) { this.keyHandler = (e) => this.onKey(e); document.addEventListener("keydown", this.keyHandler, true); }
      else if (this.keyHandler) { document.removeEventListener("keydown", this.keyHandler, true); this.keyHandler = null; }
      this.refreshUi();
      reportLayers();
    }

    onKey(e) {
      if (e.key === "Escape") { this.setDrawing(false); return; }
      const t = e.target;
      const typing = t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName));
      if (typing || e.ctrlKey || e.altKey || e.metaKey || !/^[0-9]$/.test(e.key)) return;
      setColor(COLORS[(+e.key + 9) % 10]);
      e.preventDefault(); e.stopPropagation();
    }

    // Size the layer to the <video> box and the canvas to the video frame inside it (object-fit aware).
    layout() {
      const v = this.video, host = this.host;
      const vr = v.getBoundingClientRect(), hr = host.getBoundingClientRect();
      const left = vr.left - hr.left - host.clientLeft + host.scrollLeft;
      const top = vr.top - hr.top - host.clientTop + host.scrollTop;
      const st = this.el.style;
      const px = (n) => Math.round(n) + "px";
      if (st.left !== px(left)) st.left = px(left);
      if (st.top !== px(top)) st.top = px(top);
      if (st.width !== px(vr.width)) st.width = px(vr.width);
      if (st.height !== px(vr.height)) st.height = px(vr.height);

      const vw = v.videoWidth || 16, vh = v.videoHeight || 9;
      const fit = getComputedStyle(v).objectFit;
      let fw = vr.width, fh = vr.height;
      if (fit === "contain" || fit === "scale-down" || fit === "cover") {
        const k = fit === "cover" ? Math.max(vr.width / vw, vr.height / vh) : Math.min(vr.width / vw, vr.height / vh);
        fw = vw * k; fh = vh * k;
      }
      const fx = (vr.width - fw) / 2, fy = (vr.height - fh) / 2;
      this.frame = { x: fx, y: fy, w: fw, h: fh };
      const dpr = window.devicePixelRatio || 1;
      const cw = Math.max(1, Math.round(fw * dpr)), ch = Math.max(1, Math.round(fh * dpr));
      const cs = this.canvas.style;
      if (this.canvas.width !== cw) this.canvas.width = cw;
      if (this.canvas.height !== ch) this.canvas.height = ch;
      if (cs.left !== px(fx)) cs.left = px(fx);
      if (cs.top !== px(fy)) cs.top = px(fy);
      if (cs.width !== px(fw)) cs.width = px(fw);
      if (cs.height !== px(fh)) cs.height = px(fh);
      const rs = this.reactBox.style;
      if (rs.left !== px(fx)) rs.left = px(fx);
      if (rs.top !== px(fy)) rs.top = px(fy);
      if (rs.width !== px(fw)) rs.width = px(fw);
      if (rs.height !== px(fh)) rs.height = px(fh);
    }

    norm(e) {
      const r = this.canvas.getBoundingClientRect();
      const x = Math.min(1, Math.max(0, (e.clientX - r.left) / r.width));
      const y = Math.min(1, Math.max(0, (e.clientY - r.top) / r.height));
      return [Math.round(x * 10000) / 10000, Math.round(y * 10000) / 10000];
    }

    onDown(e) {
      if (!this.drawing || e.button !== 0) return;
      e.preventDefault(); e.stopPropagation();
      try { this.canvas.setPointerCapture(e.pointerId); } catch (x) {}
      const id = Math.random().toString(36).slice(2, 10);
      const s = { key: "self:" + id, id, from: "self", name: "", c: state.color, w: STROKE_WIDTH, pts: [], last: performance.now(), done: false, own: true };
      strokesFor(this.sid).set(s.key, s);
      this.cur = s; this.pending = [];
      this.addPoint(this.norm(e));
      this.flushTimer = setInterval(() => this.flush(false), FLUSH_MS);
      kick();
    }

    onMove(e) {
      if (!this.cur) return;
      e.preventDefault(); e.stopPropagation();
      const evs = typeof e.getCoalescedEvents === "function" ? e.getCoalescedEvents() : [e];
      for (const ce of (evs.length ? evs : [e])) this.addPoint(this.norm(ce));
      kick();
    }

    onUp(e) {
      if (!this.cur) return;
      e.preventDefault(); e.stopPropagation();
      this.finishStroke();
    }

    addPoint(p) {
      const s = this.cur, n = s.pts.length;
      if (n >= 2 && s.pts[n - 2] === p[0] && s.pts[n - 1] === p[1]) return;
      s.pts.push(p[0], p[1]); this.pending.push(p[0], p[1]);
      s.last = performance.now();
    }

    finishStroke() {
      clearInterval(this.flushTimer); this.flushTimer = 0;
      this.cur.done = true; this.cur.last = performance.now();
      this.flush(true);
      this.cur = null;
      kick();
    }

    flush(end) {
      const s = this.cur;
      if (!s || (!this.pending.length && !end)) return;
      const ar = this.video.videoWidth && this.video.videoHeight ? +(this.video.videoWidth / this.video.videoHeight).toFixed(4) : 0;
      emitLocal({ t: "stroke", sid: this.sid, id: s.id, c: s.c, w: s.w, p: this.pending, end: !!end, ar });
      this.pending = [];
    }

    clearOwn() {
      const m = state.strokes.get(this.sid);
      if (m) for (const [k, s] of m) if (s.own) m.delete(k);
      emitLocal({ t: "clear", sid: this.sid });
      kick();
    }

    render(now) {
      const ctx = this.canvas.getContext("2d");
      const W = this.canvas.width, H = this.canvas.height;
      ctx.clearRect(0, 0, W, H);
      const m = state.strokes.get(this.sid);
      if (!m || !m.size) return;
      const labels = new Map();
      for (const s of m.values()) {
        const a = alphaOf(s, now);
        if (a <= 0 || !s.pts.length) continue;
        drawStroke(ctx, s, W, H, a);
        if (!s.own && s.name) labels.set(s.from, s);
      }
      for (const s of labels.values()) drawLabel(ctx, s, W, H, alphaOf(s, now));
    }
  }

  function drawStroke(ctx, s, W, H, a) {
    const lw = Math.max(2 * (window.devicePixelRatio || 1), s.w * H);
    const p = s.pts;
    ctx.save();
    ctx.globalAlpha = a;
    ctx.lineCap = "round"; ctx.lineJoin = "round";
    ctx.strokeStyle = s.c; ctx.fillStyle = s.c; ctx.lineWidth = lw;
    ctx.shadowColor = "rgba(0,0,0,0.45)"; ctx.shadowBlur = Math.max(2, lw * 0.6);
    if (p.length === 2) {
      ctx.beginPath(); ctx.arc(p[0] * W, p[1] * H, lw / 2, 0, Math.PI * 2); ctx.fill();
    } else {
      ctx.beginPath();
      ctx.moveTo(p[0] * W, p[1] * H);
      for (let i = 2; i + 3 < p.length; i += 2) {
        const mx = (p[i] + p[i + 2]) / 2 * W, my = (p[i + 1] + p[i + 3]) / 2 * H;
        ctx.quadraticCurveTo(p[i] * W, p[i + 1] * H, mx, my);
      }
      ctx.lineTo(p[p.length - 2] * W, p[p.length - 1] * H);
      ctx.stroke();
    }
    ctx.restore();
  }

  function drawLabel(ctx, s, W, H, a) {
    const dpr = window.devicePixelRatio || 1;
    const x = s.pts[s.pts.length - 2] * W, y = s.pts[s.pts.length - 1] * H;
    ctx.save();
    ctx.globalAlpha = a;
    const fs = Math.max(11 * dpr, H / 85);
    ctx.font = `600 ${fs}px -apple-system, 'Segoe UI', sans-serif`;
    const tw = ctx.measureText(s.name).width, pad = fs * 0.45, h = fs * 1.6;
    let lx = x + fs * 0.8, ly = y + fs * 0.6;
    if (lx + tw + pad * 2 > W) lx = x - tw - pad * 2 - fs * 0.8;
    if (ly + h > H) ly = y - h - fs * 0.6;
    ctx.fillStyle = s.c;
    roundRect(ctx, lx, ly, tw + pad * 2, h, fs * 0.35); ctx.fill();
    ctx.fillStyle = isLight(s.c) ? "#111" : "#fff";
    ctx.textBaseline = "middle";
    ctx.fillText(s.name, lx + pad, ly + h / 2);
    ctx.restore();
  }

  function roundRect(ctx, x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y); ctx.arcTo(x + w, y, x + w, y + h, r); ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r); ctx.arcTo(x, y, x + w, y, r); ctx.closePath();
  }

  function isLight(hex) {
    const n = parseInt(hex.slice(1), 16);
    return ((n >> 16) * 299 + ((n >> 8) & 255) * 587 + (n & 255) * 114) / 1000 > 150;
  }

  function setColor(c) {
    state.color = c; saveColor(c);
    for (const l of state.layers) l.refreshUi();
    send({ t: "color", c });   // TSDraw.exe uses it when you draw on your own screen
  }

  // Tell TSDraw.exe whether there's a stream here to draw on, so its hotkey knows what to toggle.
  function reportLayers() {
    const inbound = [...state.layers].filter((l) => l.inbound);
    const json = JSON.stringify([inbound.length, inbound.some((l) => l.drawing)]);
    if (json === state.lastLayersJson) return;
    state.lastLayersJson = json;
    send({ t: "layers", inbound: inbound.length, drawing: inbound.some((l) => l.drawing) });
  }

  // The hotkey: stop drawing if we are, else start on the stream under the mouse, else the biggest one.
  function toggleDrawing() {
    const inbound = [...state.layers].filter((l) => l.inbound);
    const active = inbound.find((l) => l.drawing);
    if (active) { active.setDrawing(false); return; }
    const hovered = inbound.find((l) => l.host.matches(":hover"));
    const target = hovered || inbound.sort((a, b) => b.frame.w * b.frame.h - a.frame.w * a.frame.h)[0];
    if (target) target.setDrawing(true);
  }

  function scan() {
    if (!document.body) return;
    ensureStyle();
    const seen = new Set();
    for (const v of document.querySelectorAll("video")) {
      const r = v.getBoundingClientRect();
      if (r.width < MIN_VIDEO_PX || r.height < MIN_VIDEO_PX * 0.5) continue;
      const s = sessionOfVideo(v);
      const sid = s && sidOf(s.idData);
      if (!sid) continue;
      let layer = null;
      for (const l of state.layers) if (l.video === v) layer = l;
      if (layer && layer.sid !== sid) { layer.destroy(); state.layers.delete(layer); layer = null; }
      if (!layer) { layer = new Layer(v, sid, !!s.isInbound); state.layers.add(layer); }
      try { layer.streamer = s.displayName || ""; } catch (e) {}
      seen.add(layer);
    }
    for (const l of [...state.layers]) {
      if (!seen.has(l) || !l.alive()) { l.destroy(); state.layers.delete(l); }
      else { l.layout(); l.refreshUi(); }
    }
    reportLayers();
  }

  // ---------------------------------------------------------------- render loop

  let raf = 0;
  function kick() { if (!raf) raf = requestAnimationFrame(frame); }
  function frame() {
    raf = 0;
    const now = performance.now();
    const any = pruneStrokes(now);
    for (const l of state.layers) { l.layout(); l.render(now); }
    if (any) kick();
  }

  // ---------------------------------------------------------------- helper link

  const api = {
    version: VERSION,
    state,
    hello() {
      send({ t: "hello", ver: VERSION, url: location.href });
      send({ t: "color", c: state.color });
      state.lastSessionsJson = ""; state.lastLayersJson = "";
    },
    // For troubleshooting from DevTools: the peer-to-peer links and their states.
    links() {
      const now = performance.now();
      return [...p2p.peers.values()].map((p) => ({ sid: p.sid.slice(0, 8), client: p.remote, role: p.role, state: p.state,
        why: p.why, fails: p.fails, retryInS: p.state === "dead" ? Math.round((p.retryAt - now) / 1000) : null }));
    },
    // Called by TSDraw.exe through Runtime.evaluate.
    recv(msg) {
      try {
        if (!msg || !msg.t) return;
        if (msg.t === "hello" || msg.t === "status") {
          state.helper = true;
          if (msg.relay) state.relay = msg.relay;
          if (msg.transport) state.transport = msg.transport;
          if (msg.master != null) state.master = !!msg.master;
          if (msg.name != null) state.name = msg.name;
          if (msg.hotkey != null) state.hotkey = msg.hotkey;
          if (msg.holdMs) state.holdMs = msg.holdMs;
          for (const l of state.layers) l.refreshUi();
        } else if (msg.t === "reply") {
          onHelperReply(msg);
        } else if (msg.t === "toggle") {
          toggleDrawing();
        } else if (["stroke", "clear", "react", "img"].includes(msg.t)) {
          const m = clean(msg, msg.sid, true);
          if (m) applyRemote(m);
        } else if (msg.t === "tx") {
          // Drawn elsewhere on this PC (the streamer's own screen, or a pop-out window): show it and send it on.
          const m = clean(msg.msg, msg.msg && msg.msg.sid, true);
          if (m) { applyRemote(m); if (state.transport === "p2p" && state.master) p2pSend(m); }
        } else if (msg.t === "here") {
          if (msg.sid) state.present.set(msg.sid, performance.now());
          for (const l of state.layers) if (l.sid === msg.sid) l.refreshUi();
        } else if (msg.t === "bye") {
          state.helper = false; state.relay = "TSDraw.exe exited";
          for (const l of state.layers) l.refreshUi();
        }
      } catch (e) { log("recv failed: " + e); }
    },
    remove() {
      clearInterval(scanTimer); clearInterval(pollTimer); clearInterval(p2pTimer);
      p2pShutdown();
      for (const l of state.layers) l.destroy();
      state.layers.clear();
      const st = document.getElementById("tsdraw-style"); if (st) st.remove();
      delete window.__tsdraw;
    },
  };
  window.__tsdraw = api;

  let scanTimer = 0, pollTimer = 0, p2pTimer = 0;
  function start() {
    scanTimer = setInterval(() => { try { scan(); } catch (e) { log("scan failed: " + e); } }, SCAN_MS);
    pollTimer = setInterval(pollSessions, SESSION_POLL_MS);
    p2pTimer = setInterval(p2pTick, P2P_TICK_MS);
    api.hello();
    pollSessions();
    scan();
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start, { once: true });
  else start();
})();
