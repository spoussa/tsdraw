using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TSDraw
{
    // Relay wire format (TCP): [u32 big-endian length L][u8 type][L-1 bytes payload].
    // Topic-carrying payloads (PUB/MSG) are [u8 topic length][topic][data].
    static class Wire
    {
        public const byte HELLO = 1, JOIN = 2, LEAVE = 3, PUB = 4, MSG = 5, PING = 6, PONG = 7, ERR = 8;
        public const int MaxFrame = 64 * 1024;
        public const string Magic = "TSDRAW1\n";

        public static byte[] Frame(byte type, byte[] payload)
        {
            int len = payload.Length + 1;
            var f = new byte[4 + len];
            f[0] = (byte)(len >> 24); f[1] = (byte)(len >> 16); f[2] = (byte)(len >> 8); f[3] = (byte)len;
            f[4] = type;
            Buffer.BlockCopy(payload, 0, f, 5, payload.Length);
            return f;
        }

        public static byte[] TopicPayload(string topic, byte[] data)
        {
            byte[] t = Encoding.UTF8.GetBytes(topic);
            var p = new byte[1 + t.Length + data.Length];
            p[0] = (byte)t.Length;
            Buffer.BlockCopy(t, 0, p, 1, t.Length);
            Buffer.BlockCopy(data, 0, p, 1 + t.Length, data.Length);
            return p;
        }

        public static bool SplitTopic(byte[] p, out string topic, out byte[] data)
        {
            topic = null; data = null;
            if (p.Length < 1 || p.Length < 1 + p[0]) return false;
            topic = Encoding.UTF8.GetString(p, 1, p[0]);
            data = new byte[p.Length - 1 - p[0]];
            Buffer.BlockCopy(p, 1 + p[0], data, 0, data.Length);
            return true;
        }

        public static void ReadFrame(Stream s, out byte type, out byte[] payload)
        {
            var hdr = new byte[4];
            ReadExactly(s, hdr, 4);
            int len = (hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
            if (len < 1 || len > MaxFrame) throw new IOException("bad frame length " + len);
            var body = new byte[len];
            ReadExactly(s, body, len);
            type = body[0];
            payload = new byte[len - 1];
            Buffer.BlockCopy(body, 1, payload, 0, len - 1);
        }

        static void ReadExactly(Stream s, byte[] buf, int n)
        {
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) throw new EndOfStreamException("connection closed");
                off += r;
            }
        }
    }

    class RelayClient
    {
        public event Action<string, byte[]> Message;   // topic, data - raised on the relay thread
        public event Action<string> StatusChanged;

        readonly Func<string> endpoint;
        readonly string password;
        readonly object lk = new object();
        readonly HashSet<string> topics = new HashSet<string>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread thread;
        TcpClient tcp;
        Stream stream;
        volatile bool stopping;
        volatile string status = "idle";

        public RelayClient(Func<string> endpoint, string password)
        {
            this.endpoint = endpoint;
            this.password = password ?? "";
        }

        public string Status { get { return status; } }
        public string Endpoint { get; private set; }

        public void Start()
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "relay" };
            thread.Start();
        }

        public void Stop()
        {
            stopping = true;
            lock (lk) CloseLocked();
            wake.Set();
        }

        public void SetTopics(ICollection<string> want)
        {
            lock (lk)
            {
                foreach (var t in want.Where(t => !topics.Contains(t)).ToList())
                {
                    topics.Add(t);
                    SendLocked(Wire.JOIN, Encoding.UTF8.GetBytes(t));
                }
                foreach (var t in topics.Where(t => !want.Contains(t)).ToList())
                {
                    topics.Remove(t);
                    SendLocked(Wire.LEAVE, Encoding.UTF8.GetBytes(t));
                }
            }
            wake.Set();
        }

        public void Publish(string topic, byte[] data)
        {
            lock (lk) SendLocked(Wire.PUB, Wire.TopicPayload(topic, data));
        }

        void SendLocked(byte type, byte[] payload)
        {
            if (stream == null) return;
            try
            {
                var f = Wire.Frame(type, payload);
                stream.Write(f, 0, f.Length);
            }
            catch (Exception e)
            {
                Log.W("relay send failed: {0}", e.Message);
                CloseLocked();
            }
        }

        void CloseLocked()
        {
            if (tcp != null) { try { tcp.Close(); } catch { } }
            tcp = null; stream = null;
        }

        void SetStatus(string s)
        {
            if (s == status) return;
            status = s;
            Log.W("relay: {0}", s);
            var h = StatusChanged;
            if (h != null) h(s);
        }

        void Loop()
        {
            int backoff = 1000;
            while (!stopping)
            {
                bool want;
                lock (lk) want = topics.Count > 0;
                if (!want) { SetStatus("idle"); wake.WaitOne(5000); continue; }

                string ep = null;
                try { ep = endpoint(); } catch (Exception e) { Log.W("relay endpoint lookup failed: {0}", e.Message); }
                if (string.IsNullOrEmpty(ep))
                {
                    SetStatus("no relay address - set \"relay\" in config.json");
                    wake.WaitOne(10000);
                    continue;
                }
                string host; int port;
                SplitHostPort(ep, out host, out port);
                Endpoint = host + ":" + port;
                SetStatus("connecting");
                try
                {
                    RunConnection(host, port, ref backoff);
                }
                catch (Exception e)
                {
                    if (!stopping) SetStatus("error: " + e.Message + " (" + Endpoint + ")");
                }
                lock (lk) CloseLocked();
                if (!stopping)
                {
                    wake.WaitOne(backoff);
                    backoff = Math.Min(backoff * 2, 15000);
                }
            }
        }

        void RunConnection(string host, int port, ref int backoff)
        {
            var c = new TcpClient(AddressFamily.InterNetworkV6);
            c.Client.DualMode = true;
            c.NoDelay = true;
            var ar = c.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(6000)) { c.Close(); throw new TimeoutException("connect timed out"); }
            c.EndConnect(ar);
            var s = c.GetStream();
            s.ReadTimeout = 60000;
            s.WriteTimeout = 5000;

            var hello = Wire.Frame(Wire.HELLO, Encoding.UTF8.GetBytes(Wire.Magic + password));
            s.Write(hello, 0, hello.Length);
            byte type; byte[] p;
            Wire.ReadFrame(s, out type, out p);
            if (type == Wire.ERR) throw new IOException("relay refused: " + Encoding.UTF8.GetString(p));
            if (type != Wire.HELLO) throw new IOException("not a TSDraw relay");

            lock (lk)
            {
                tcp = c; stream = s;
                foreach (var t in topics) SendLocked(Wire.JOIN, Encoding.UTF8.GetBytes(t));
            }
            SetStatus("connected");
            backoff = 1000;

            DateTime emptySince = DateTime.MinValue;
            using (new Timer(_ => { lock (lk) SendLocked(Wire.PING, new byte[0]); }, null, 20000, 20000))
            {
                while (!stopping)
                {
                    Wire.ReadFrame(s, out type, out p);
                    if (type == Wire.MSG)
                    {
                        string topic; byte[] data;
                        if (Wire.SplitTopic(p, out topic, out data))
                        {
                            var h = Message;
                            if (h != null) { try { h(topic, data); } catch (Exception e) { Log.W("relay handler: {0}", e); } }
                        }
                    }
                    else if (type == Wire.ERR) throw new IOException("relay: " + Encoding.UTF8.GetString(p));

                    // Drop the connection after 30 s without anything to listen to.
                    bool empty;
                    lock (lk) empty = topics.Count == 0;
                    if (!empty) emptySince = DateTime.MinValue;
                    else if (emptySince == DateTime.MinValue) emptySince = DateTime.UtcNow;
                    else if ((DateTime.UtcNow - emptySince).TotalSeconds > 30) return;
                }
            }
        }

        public static void SplitHostPort(string ep, out string host, out int port)
        {
            ep = ep.Trim();
            port = 9989;
            host = ep;
            if (ep.StartsWith("["))
            {
                int close = ep.IndexOf(']');
                host = ep.Substring(1, close - 1);
                if (close + 2 < ep.Length && ep[close + 1] == ':') int.TryParse(ep.Substring(close + 2), out port);
                return;
            }
            int colon = ep.LastIndexOf(':');
            if (colon > 0 && ep.IndexOf(':') == colon)
            {
                host = ep.Substring(0, colon);
                int.TryParse(ep.Substring(colon + 1), out port);
            }
        }
    }

    // `TSDraw.exe --relay [port] [--password secret]`: the same relay as tsdraw-relay.py,
    // for hosting it on a Windows machine.
    static class RelayServer
    {
        class Conn
        {
            public TcpClient Tcp;
            public Stream S;
            public string Addr;
            public readonly object WLock = new object();
            public readonly HashSet<string> Topics = new HashSet<string>();
            public double Tokens = 400;
            public DateTime Refill = DateTime.UtcNow;
        }

        static readonly Dictionary<string, HashSet<Conn>> Rooms = new Dictionary<string, HashSet<Conn>>();
        static readonly object RoomsLock = new object();
        static int connections;

        public static int Run(string[] args)
        {
            Native.EnsureConsole();
            int port = 9989;
            string pw = "";
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--password" && i + 1 < args.Length) pw = args[++i];
                else int.TryParse(args[i], out port);
            }
            var l = new TcpListener(IPAddress.IPv6Any, port);
            l.Server.DualMode = true;
            l.Start();
            Console.WriteLine("TSDraw relay listening on TCP port {0}{1}. Press Ctrl+C to stop.", port, pw.Length > 0 ? " (password required)" : "");
            while (true)
            {
                var c = l.AcceptTcpClient();
                var t = new Thread(() => Serve(c, pw)) { IsBackground = true };
                t.Start();
            }
        }

        static void Serve(TcpClient c, string pw)
        {
            var conn = new Conn { Tcp = c, Addr = c.Client.RemoteEndPoint.ToString() };
            bool joined = false;
            try
            {
                c.NoDelay = true;
                conn.S = c.GetStream();
                conn.S.ReadTimeout = 90000;
                conn.S.WriteTimeout = 3000;
                byte type; byte[] p;
                Wire.ReadFrame(conn.S, out type, out p);
                string hello = Encoding.UTF8.GetString(p);
                if (type != Wire.HELLO || !hello.StartsWith(Wire.Magic)) return;
                if (hello.Substring(Wire.Magic.Length) != pw)
                {
                    Send(conn, Wire.Frame(Wire.ERR, Encoding.UTF8.GetBytes("wrong relay password")));
                    return;
                }
                Send(conn, Wire.Frame(Wire.HELLO, Encoding.UTF8.GetBytes("OK")));
                joined = true;
                Console.WriteLine("{0} + {1} ({2} connected)", DateTime.Now.ToString("HH:mm:ss"), conn.Addr, Interlocked.Increment(ref connections));

                while (true)
                {
                    Wire.ReadFrame(conn.S, out type, out p);
                    if (type == Wire.PING) Send(conn, Wire.Frame(Wire.PONG, new byte[0]));
                    else if (type == Wire.JOIN || type == Wire.LEAVE)
                    {
                        string topic = Encoding.UTF8.GetString(p);
                        if (topic.Length == 0 || topic.Length > 64) continue;
                        lock (RoomsLock)
                        {
                            HashSet<Conn> room;
                            if (type == Wire.JOIN && conn.Topics.Count < 32 && conn.Topics.Add(topic))
                            {
                                if (!Rooms.TryGetValue(topic, out room)) Rooms[topic] = room = new HashSet<Conn>();
                                room.Add(conn);
                            }
                            else if (type == Wire.LEAVE && conn.Topics.Remove(topic) && Rooms.TryGetValue(topic, out room))
                            {
                                room.Remove(conn);
                                if (room.Count == 0) Rooms.Remove(topic);
                            }
                        }
                    }
                    else if (type == Wire.PUB)
                    {
                        string topic; byte[] data;
                        if (!Wire.SplitTopic(p, out topic, out data) || !conn.Topics.Contains(topic)) continue;
                        var now = DateTime.UtcNow;   // token bucket: 200 msg/s, burst 400
                        conn.Tokens = Math.Min(400, conn.Tokens + (now - conn.Refill).TotalSeconds * 200);
                        conn.Refill = now;
                        if (conn.Tokens < 1) continue;
                        conn.Tokens -= 1;
                        Conn[] targets;
                        lock (RoomsLock)
                        {
                            HashSet<Conn> room;
                            targets = Rooms.TryGetValue(topic, out room) ? room.Where(x => x != conn).ToArray() : new Conn[0];
                        }
                        var frame = Wire.Frame(Wire.MSG, p);
                        foreach (var t in targets) Send(t, frame);
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                lock (RoomsLock)
                {
                    foreach (var topic in conn.Topics)
                    {
                        HashSet<Conn> room;
                        if (Rooms.TryGetValue(topic, out room)) { room.Remove(conn); if (room.Count == 0) Rooms.Remove(topic); }
                    }
                }
                try { c.Close(); } catch { }
                if (joined)
                    Console.WriteLine("{0} - {1} ({2} connected)", DateTime.Now.ToString("HH:mm:ss"), conn.Addr, Interlocked.Decrement(ref connections));
            }
        }

        static void Send(Conn c, byte[] frame)
        {
            lock (c.WLock)
            {
                try { c.S.Write(frame, 0, frame.Length); }
                catch { try { c.Tcp.Close(); } catch { } }
            }
        }
    }
}
