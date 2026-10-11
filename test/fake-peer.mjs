// A stand-in TSDraw peer for testing relays and overlays.
//   node test/fake-peer.mjs <host:port> <sid> listen              - print decrypted messages for the stream
//   node test/fake-peer.mjs <host:port> <sid> draw [name] [color] - draw a star-ish shape, then listen
//   node test/fake-peer.mjs <host:port> <sid> here [name]         - pretend to be the streamer's TSDraw (presence)
// Speaks the same wire format and crypto as TSDraw.exe (Relay.cs / Util.cs RoomKeys).
import net from "node:net";
import crypto from "node:crypto";

const [hostPort, sid, mode = "listen", name = "Tester", color = "#0a84ff", password = ""] = process.argv.slice(2);
const [host, port] = hostPort.includes(":") ? [hostPort.slice(0, hostPort.lastIndexOf(":")), +hostPort.slice(hostPort.lastIndexOf(":") + 1)] : [hostPort, 9989];
const HELLO = 1, JOIN = 2, PUB = 4, MSG = 5, PING = 6, ERR = 8;
const sha = (s) => crypto.createHash("sha256").update(s, "utf8").digest();
const topic = "v1/" + sha("tsdraw-topic|" + sid).toString("hex").slice(0, 32);
const encKey = sha("tsdraw-enc|" + sid), macKey = sha("tsdraw-mac|" + sid);
const from = crypto.randomBytes(6).toString("hex");

function seal(obj) {
  const iv = crypto.randomBytes(16);
  const c = crypto.createCipheriv("aes-256-cbc", encKey, iv);
  const ct = Buffer.concat([c.update(JSON.stringify(obj), "utf8"), c.final()]);
  const body = Buffer.concat([iv, ct]);
  const tag = crypto.createHmac("sha256", macKey).update(body).digest().subarray(0, 16);
  return Buffer.concat([body, tag]);
}
function open(buf) {
  const ct = buf.subarray(16, buf.length - 16), tag = buf.subarray(buf.length - 16);
  const want = crypto.createHmac("sha256", macKey).update(buf.subarray(0, buf.length - 16)).digest().subarray(0, 16);
  if (!crypto.timingSafeEqual(tag, want)) return null;
  const d = crypto.createDecipheriv("aes-256-cbc", encKey, buf.subarray(0, 16));
  return JSON.parse(Buffer.concat([d.update(ct), d.final()]).toString("utf8"));
}
const frame = (type, payload = Buffer.alloc(0)) => { const h = Buffer.alloc(5); h.writeUInt32BE(payload.length + 1); h[4] = type; return Buffer.concat([h, payload]); };
const topicPayload = (data) => { const t = Buffer.from(topic); return Buffer.concat([Buffer.from([t.length]), t, data]); };

const sock = net.connect(port, host);
let buf = Buffer.alloc(0);
sock.on("data", (d) => {
  buf = Buffer.concat([buf, d]);
  while (buf.length >= 4 && buf.length >= 4 + buf.readUInt32BE(0)) {
    const len = buf.readUInt32BE(0), type = buf[4], p = buf.subarray(5, 4 + len);
    buf = buf.subarray(4 + len);
    if (type === HELLO) {
      console.log("relay: hello ok"); sock.write(frame(JOIN, Buffer.from(topic)));
      if (mode === "draw") setTimeout(draw, 300);
      if (mode === "here") { const hi = () => sock.write(frame(PUB, topicPayload(seal({ t: "here", sid, from, name })))); setTimeout(hi, 300); setInterval(hi, 5000); }
    }
    else if (type === ERR) { console.log("relay error:", p.toString()); process.exit(1); }
    else if (type === MSG) {
      const n = p[0], data = p.subarray(1 + n), m = open(data);
      console.log("recv", m ? JSON.stringify(m).slice(0, 200) : "(undecryptable)");
    }
  }
});
sock.on("connect", () => sock.write(frame(HELLO, Buffer.from("TSDRAW1\n" + password))));
sock.on("error", (e) => { console.log("socket error:", e.message); process.exit(1); });
setInterval(() => sock.write(frame(PING)), 20000);

async function draw() {
  const id = crypto.randomBytes(4).toString("hex");
  const pts = [];
  for (let i = 0; i <= 10; i++) { const a = -Math.PI / 2 + i * Math.PI * 4 / 5; pts.push(0.5 + 0.18 * Math.cos(a) * 9 / 16, 0.5 + 0.18 * Math.sin(a)); }
  for (let i = 0; i < pts.length; i += 4) {
    const end = i + 4 >= pts.length;
    sock.write(frame(PUB, topicPayload(seal({ t: "stroke", sid, from, name, id, c: color, w: 0.006, p: pts.slice(i, i + 4), end, ar: 16 / 9 }))));
    await new Promise((r) => setTimeout(r, 60));
  }
  console.log("drew a star as", name);
}
