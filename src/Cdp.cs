using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace TSDraw
{
    // Finds TeamSpeak's UI pages on its DevTools port and keeps one CdpPage per page.
    class CdpManager
    {
        public const string BindingName = "__tsdrawSend";
        readonly int port;
        readonly string modJs;
        readonly Dictionary<string, CdpPage> pages = new Dictionary<string, CdpPage>();
        readonly object lk = new object();
        Thread thread;
        volatile bool stopping;

        public event Action<CdpPage> PageOpened, PageClosed;
        public event Action<CdpPage, string> PageMessage;    // raised on the page's receive thread
        public volatile bool Reachable;

        public CdpManager(int port, string modJs)
        {
            this.port = port;
            this.modJs = modJs;
        }

        public int PageCount { get { lock (lk) return pages.Count; } }

        public void Start()
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "cdp" };
            thread.Start();
        }

        public void Stop(string farewellJs)
        {
            stopping = true;
            List<CdpPage> all;
            lock (lk) all = pages.Values.ToList();
            foreach (var p in all) p.Close(farewellJs);
        }

        public void Broadcast(string js)
        {
            List<CdpPage> all;
            lock (lk) all = pages.Values.ToList();
            foreach (var p in all) p.Evaluate(js);
        }

        void Loop()
        {
            while (!stopping)
            {
                try { Poll(); }
                catch (Exception e)
                {
                    if (Reachable) Log.W("TeamSpeak DevTools port {0} not reachable: {1}", port, e.Message);
                    Reachable = false;
                }
                Thread.Sleep(2000);
            }
        }

        void Poll()
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/json/list");
            req.Timeout = 2000;
            req.Proxy = null;
            string body;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = r.ReadToEnd();
            if (!Reachable) Log.W("connected to TeamSpeak DevTools on port {0}", port);
            Reachable = true;

            var targets = (Json.Parse(body) as object[]) ?? new object[0];
            var live = new HashSet<string>();
            foreach (var o in targets)
            {
                var t = o as Dictionary<string, object>;
                string type = Json.S1(t, "type"), url = Json.S1(t, "url"), id = Json.S1(t, "id"), ws = Json.S1(t, "webSocketDebuggerUrl");
                if (type != "page" || url == null || !url.StartsWith("tsui://default/") || id == null || ws == null) continue;
                live.Add(id);
                CdpPage existing;
                lock (lk) pages.TryGetValue(id, out existing);
                if (existing != null && existing.Alive) continue;
                if (existing != null) Drop(id);
                var page = new CdpPage(id, url, ws, modJs, this);
                if (page.Connect())
                {
                    lock (lk) pages[id] = page;
                    Log.W("attached to TeamSpeak page {0} ({1})", id.Substring(0, 8), url);
                    var h = PageOpened;
                    if (h != null) h(page);
                }
            }
            List<string> gone;
            lock (lk) gone = pages.Keys.Where(k => !live.Contains(k) || !pages[k].Alive).ToList();
            foreach (var id in gone) Drop(id);
        }

        void Drop(string id)
        {
            CdpPage p;
            lock (lk)
            {
                if (!pages.TryGetValue(id, out p)) return;
                pages.Remove(id);
            }
            p.Close(null);
            Log.W("detached from TeamSpeak page {0}", id.Substring(0, 8));
            var h = PageClosed;
            if (h != null) h(p);
        }

        internal void OnBinding(CdpPage page, string payload)
        {
            var h = PageMessage;
            if (h != null) h(page, payload);
        }
    }

    class CdpPage
    {
        public readonly string Id, Url;
        readonly string wsUrl, modJs;
        readonly CdpManager mgr;
        readonly ClientWebSocket ws = new ClientWebSocket();
        readonly BlockingCollection<string> outQ = new BlockingCollection<string>();
        int nextId;
        volatile bool alive;

        public CdpPage(string id, string url, string wsUrl, string modJs, CdpManager mgr)
        {
            Id = id; Url = url; this.wsUrl = wsUrl; this.modJs = modJs; this.mgr = mgr;
        }

        public bool Alive { get { return alive; } }

        public bool Connect()
        {
            try
            {
                if (!ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).Wait(4000)) return false;
            }
            catch (Exception e)
            {
                Log.W("DevTools connect to page failed: {0}", e.GetBaseException().Message);
                return false;
            }
            alive = true;
            new Thread(SendLoop) { IsBackground = true, Name = "cdp-tx" }.Start();
            new Thread(ReceiveLoop) { IsBackground = true, Name = "cdp-rx" }.Start();
            // The binding and the new-document script survive page reloads; Runtime.evaluate covers the page as it is now.
            Call("Runtime.addBinding", new Dictionary<string, object> { { "name", CdpManager.BindingName } });
            Call("Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object> { { "source", modJs } });
            Evaluate(modJs);
            return true;
        }

        public void Evaluate(string js)
        {
            Call("Runtime.evaluate", new Dictionary<string, object> { { "expression", js } });
        }

        void Call(string method, Dictionary<string, object> args)
        {
            if (!alive) return;
            var msg = new Dictionary<string, object> { { "id", Interlocked.Increment(ref nextId) }, { "method", method }, { "params", args } };
            try { outQ.Add(Json.Str(msg)); } catch (InvalidOperationException) { }   // closed meanwhile
        }

        public void Close(string farewellJs)
        {
            if (!alive) return;
            if (farewellJs != null)
            {
                Evaluate(farewellJs);
                // Give the send thread a moment to flush.
                for (int i = 0; i < 20 && outQ.Count > 0; i++) Thread.Sleep(25);
            }
            alive = false;
            outQ.CompleteAdding();
            try { ws.Abort(); } catch { }
        }

        void SendLoop()
        {
            try
            {
                foreach (var s in outQ.GetConsumingEnumerable())
                {
                    var bytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(s));
                    ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).Wait();
                }
            }
            catch (Exception e)
            {
                if (alive) Log.W("DevTools send failed: {0}", e.GetBaseException().Message);
                alive = false;
            }
        }

        void ReceiveLoop()
        {
            var buf = new byte[64 * 1024];
            var ms = new MemoryStream();
            try
            {
                while (alive)
                {
                    var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    string text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    ms.SetLength(0);
                    Handle(text);
                }
            }
            catch (Exception e)
            {
                if (alive) Log.W("DevTools connection closed: {0}", e.GetBaseException().Message);
            }
            alive = false;
        }

        void Handle(string text)
        {
            var d = Json.Obj(text);
            if (d == null) return;
            string method = Json.S1(d, "method");
            if (method == "Runtime.bindingCalled")
            {
                var p = d["params"] as Dictionary<string, object>;
                if (Json.S1(p, "name") == CdpManager.BindingName) mgr.OnBinding(this, Json.S1(p, "payload"));
            }
            else if (method == null && d.ContainsKey("result"))
            {
                var res = d["result"] as Dictionary<string, object>;
                if (res != null && res.ContainsKey("exceptionDetails"))
                    Log.W("page script error: {0}", Json.Str(res["exceptionDetails"]));
            }
            else if (method == null && d.ContainsKey("error"))
            {
                Log.W("DevTools error: {0}", Json.Str(d["error"]));
            }
        }
    }
}
