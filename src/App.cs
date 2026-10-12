using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TSDraw
{
    static class Program
    {
        public const string Version = BuildInfo.Version;   // from the VERSION file, via build.cmd

        [STAThread]
        static int Main(string[] args)
        {
            System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;   // TLS 1.2 for GitHub and 7TV
            if (args.Length > 0 && args[0] == "--relay") return RelayServer.Run(args);
            int oldPid;
            bool updated = args.Length > 1 && args[0] == "--updated" && int.TryParse(args[1], out oldPid);
            if (updated) Updater.FinishUpdate(int.Parse(args[1]));

            bool created;
            using (var mutex = new Mutex(true, "Local\\TSDraw-companion", out created))
            {
                if (!created)
                {
                    MessageBox.Show("TSDraw is already running - look for its icon in the system tray.", "TSDraw");
                    return 0;
                }
                Native.EnableDpiAwareness();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.W("UI error: {0}", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.W("fatal: {0}", e.ExceptionObject);
                Log.W("TSDraw {0} starting", Version);
                Application.Run(new App(Config.Load(), updated));
            }
            return 0;
        }
    }

    static class TeamSpeak
    {
        public static Process[] Running() { return Process.GetProcessesByName("TeamSpeak"); }

        public static string FindExe(Config cfg)
        {
            if (!string.IsNullOrEmpty(cfg.TeamSpeakPath) && File.Exists(cfg.TeamSpeakPath)) return cfg.TeamSpeakPath;
            foreach (var p in Running())
            {
                try { return p.MainModule.FileName; } catch { }
            }
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using (var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (k == null) continue;
                    foreach (var name in k.GetSubKeyNames())
                    {
                        using (var e = k.OpenSubKey(name))
                        {
                            var dn = e == null ? null : e.GetValue("DisplayName") as string;
                            var loc = e == null ? null : e.GetValue("InstallLocation") as string;
                            if (dn == null || loc == null || !dn.StartsWith("TeamSpeak") || dn.StartsWith("TeamSpeak 3")) continue;
                            var exe = Path.Combine(loc.Trim('"'), "TeamSpeak.exe");
                            if (File.Exists(exe)) return exe;
                        }
                    }
                }
            }
            foreach (var exe in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TeamSpeak", "TeamSpeak.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TeamSpeak", "TeamSpeak.exe") })
            {
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        public static void Start(string exe, int cdpPort)
        {
            Log.W("starting {0} with DevTools port {1}", exe, cdpPort);
            Process.Start(new ProcessStartInfo(exe, "--remote-debugging-port=" + cdpPort)
            {
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
            });
        }

        public static void Kill()
        {
            foreach (var p in Running())
            {
                try { p.Kill(); p.WaitForExit(5000); } catch (Exception e) { Log.W("could not stop TeamSpeak: {0}", e.Message); }
            }
        }

        // Host of the last server TeamSpeak connected to, from its client log - used to find
        // a relay running next to the TeamSpeak server ("relay": "auto").
        public static string ServerHostFromLog()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeamSpeak", "Logs", "Default");
            if (!Directory.Exists(dir)) return null;
            var file = Directory.GetFiles(dir, "ts*client_*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (file == null) return null;
            string text;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - 4 * 1024 * 1024);
                fs.Seek(start, SeekOrigin.Begin);
                using (var r = new StreamReader(fs)) text = r.ReadToEnd();
            }
            string host = null;
            foreach (Match m in Regex.Matches(text, @"\] Connecting to (?!resolved address)(\S+):(\d+)\s*$", RegexOptions.Multiline))
                host = m.Groups[1].Value;
            return host;
        }
    }

    // Receives global hotkeys.
    class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<int> Pressed;
        readonly HashSet<int> ids = new HashSet<int>();

        public HotkeyWindow() { CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ }); }

        public bool Register(int id, uint mods, uint vk)
        {
            Unregister(id);
            if (!Native.RegisterHotKey(Handle, id, mods | Native.MOD_NOREPEAT, vk)) return false;
            ids.Add(id);
            return true;
        }

        public void Unregister(int id)
        {
            if (ids.Remove(id)) Native.UnregisterHotKey(Handle, id);
        }

        public static bool Parse(string text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            foreach (var raw in (text ?? "").Split('+'))
            {
                var t = raw.Trim().ToLowerInvariant();
                if (t == "ctrl" || t == "control") mods |= Native.MOD_CONTROL;
                else if (t == "alt") mods |= Native.MOD_ALT;
                else if (t == "shift") mods |= Native.MOD_SHIFT;
                else if (t == "win") mods |= Native.MOD_WIN;
                else
                {
                    Keys k;
                    if (!Enum.TryParse<Keys>(raw.Trim(), true, out k)) return false;
                    vk = (uint)k;
                }
            }
            return vk != 0;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && Pressed != null) Pressed((int)m.WParam);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            foreach (var id in ids.ToList()) Unregister(id);
            DestroyHandle();
        }
    }

    class App : ApplicationContext
    {
        // Same order as COLORS in mod.js; the first 8 are the bright ones.
        public static readonly string[] Palette = { "#ff3b30", "#ff9500", "#ffcc00", "#34c759", "#32d4ff", "#0a84ff", "#bf5af2", "#ff2d92", "#ffffff", "#1c1c1e" };
        const int HotkeyDraw = 1, HotkeyEscape = 2;

        public readonly Config Cfg;
        public readonly string InstanceId = RoomKeys.RandomId(6);
        public Color DrawColor;

        readonly Control ui = new Control();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly CdpManager cdp;
        readonly RelayClient relay;      // null in peer-to-peer mode (the default)
        readonly HotkeyWindow hotkeys = new HotkeyWindow();
        readonly System.Windows.Forms.Timer watchdog = new System.Windows.Forms.Timer { Interval = 5000 };
        readonly Dictionary<CdpPage, PageSessions> pageSessions = new Dictionary<CdpPage, PageSessions>();
        readonly Dictionary<string, OverlayController> overlays = new Dictionary<string, OverlayController>();
        readonly Dictionary<string, RoomKeys> roomsByTopic = new Dictionary<string, RoomKeys>();
        readonly Dictionary<string, RoomKeys> roomsBySid = new Dictionary<string, RoomKeys>();
        readonly object roomsLock = new object();
        CdpPage master;                  // the TeamSpeak window that does the peer-to-peer networking
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
        Updater.Release pendingUpdate;
        bool updating;
        int p2pOpen, p2pTotal;
        string tsExe;
        bool warnedNoCdp, restarting;
        int badMessages;

        class OutboundInfo { public string Sid, SrcId, Title; public int Type; }
        class PageLayers { public int Inbound; public bool Drawing; }
        readonly Dictionary<CdpPage, PageLayers> pageLayers = new Dictionary<CdpPage, PageLayers>();
        class PageSessions
        {
            public List<OutboundInfo> Outbound = new List<OutboundInfo>();
            public List<string> Inbound = new List<string>();
        }

        public App(Config cfg, bool justUpdated)
        {
            Cfg = cfg;
            DrawColor = ColorTranslator.FromHtml(Palette[(int)((uint)Cfg.Name.GetHashCode() % 8)]);   // until a TeamSpeak window reports the chosen pen
            ui.CreateControl();

            string modJs;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TSDraw.mod.js"))
            using (var r = new StreamReader(s)) modJs = r.ReadToEnd();
            // Version plus a content hash, so any change to mod.js replaces the copy already injected in TeamSpeak.
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                string hash = RoomKeys.Hex(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(modJs))).Substring(0, 8);
                modJs = modJs.Replace("__TSDRAW_VERSION__", Program.Version + "-" + hash);
            }

            tray.Icon = MakeIcon();
            tray.Text = "TSDraw";
            tray.Visible = true;
            tray.ContextMenuStrip = new ContextMenuStrip();
            tray.ContextMenuStrip.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };
            tray.BalloonTipClicked += (s, e) =>
            {
                if (tray.Tag as string == "restart") RestartTeamSpeak(true);
                else if (tray.Tag as string == "download") Process.Start(Updater.ReleasesPage);
            };

            cdp = new CdpManager(cfg.CdpPort, modJs);
            cdp.PageOpened += p => Post(() =>
            {
                pageSessions[p] = new PageSessions();
                if (master == null) master = p;
                SendHello(p);
            });
            cdp.PageClosed += p => Post(() =>
            {
                pageSessions.Remove(p);
                pageLayers.Remove(p);
                if (master == p)
                {
                    master = pageSessions.Keys.FirstOrDefault();
                    if (master != null) SendHello(master);
                }
                Recompute();
            });
            cdp.PageMessage += (p, json) => Post(() => OnPageMessage(p, json));

            if (!string.IsNullOrWhiteSpace(cfg.Relay))
            {
                relay = new RelayClient(ResolveRelay, cfg.RelayPassword);
                relay.Message += OnRelayMessage;
                relay.StatusChanged += s => Post(() => BroadcastStatus());
            }
            Log.W(relay == null ? "transport: peer-to-peer" : "transport: relay ({0})", cfg.Relay);

            uint mods, vk;
            hotkeys.Pressed += id => { if (id == HotkeyDraw) OnDrawHotkey(); else if (id == HotkeyEscape) SetDrawMode(false); };
            if (!HotkeyWindow.Parse(cfg.DrawHotkey, out mods, out vk) || !hotkeys.Register(HotkeyDraw, mods, vk))
                Log.W("could not register draw hotkey \"{0}\"", cfg.DrawHotkey);

            if (relay != null) relay.Start();
            cdp.Start();

            tsExe = TeamSpeak.FindExe(cfg);
            if (tsExe == null) Log.W("TeamSpeak.exe not found - set \"teamspeakPath\" in {0}", Paths.ConfigFile);
            if (TeamSpeak.Running().Length == 0 && cfg.LaunchTeamSpeak && tsExe != null)
            {
                try { TeamSpeak.Start(tsExe, cfg.CdpPort); } catch (Exception e) { Log.W("could not start TeamSpeak: {0}", e.Message); }
            }
            watchdog.Tick += (s, e) => { CheckTeamSpeak(); AnnouncePresence(); };
            watchdog.Start();
            if (cfg.AutoUpdate)
            {
                updateTimer.Interval = 20000;   // first check shortly after start, then every 6 hours
                updateTimer.Tick += (s, e) => { updateTimer.Interval = 6 * 3600 * 1000; CheckForUpdates(false); };
                updateTimer.Start();
            }
            if (justUpdated) Balloon("TSDraw updated", "You're now on version " + Program.Version + ".", null);
            BuildMenu();
        }

        // ------------------------------------------------------------ updates

        void CheckForUpdates(bool manual)
        {
            if (updating) return;
            new Thread(() =>
            {
                try
                {
                    var r = Updater.FetchLatest();
                    Post(() => OnLatestRelease(r, manual));
                }
                catch (Exception e)
                {
                    Log.W("update check failed: {0}", e.GetBaseException().Message);
                    if (manual) Post(() => Balloon("Couldn't check for updates", e.GetBaseException().Message, null));
                }
            }) { IsBackground = true }.Start();
        }

        void OnLatestRelease(Updater.Release r, bool manual)
        {
            if (!Updater.IsNewer(r))
            {
                pendingUpdate = null;
                if (manual) Balloon("TSDraw is up to date", "You have the latest version (" + Program.Version + ").", null);
                return;
            }
            pendingUpdate = r;
            if (overlays.Count > 0 && !manual)
            {
                // Don't pull the overlay out from under a live stream; look again in 10 minutes.
                Log.W("TSDraw {0} is available; installing after your stream", r.Version);
                updateTimer.Interval = 10 * 60 * 1000;
                return;
            }
            updating = true;
            new Thread(() =>
            {
                try
                {
                    string exe = Updater.Install(r);
                    Post(() =>
                    {
                        Process.Start(new ProcessStartInfo(exe, "--updated " + Process.GetCurrentProcess().Id) { UseShellExecute = false });
                        Shutdown();
                    });
                }
                catch (Exception e)
                {
                    Log.W("update to {0} failed: {1}", r.Version, e.GetBaseException().Message);
                    Post(() =>
                    {
                        updating = false;
                        Balloon("TSDraw " + r.Version + " is available",
                            "It couldn't be installed automatically (" + e.GetBaseException().Message + "). Click to download it.", "download");
                    });
                }
            }) { IsBackground = true }.Start();
        }

        void Post(Action a)
        {
            try
            {
                ui.BeginInvoke((Action)(() =>
                {
                    try { a(); } catch (Exception e) { Log.W("error: {0}", e); }
                }));
            }
            catch (InvalidOperationException) { }   // shutting down
        }

        // ------------------------------------------------------------ pages

        void SendHello(CdpPage p)
        {
            p.Evaluate(RecvJs(new Dictionary<string, object> {
                { "t", "hello" }, { "name", Cfg.Name }, { "relay", PageRelayStatus() }, { "holdMs", (int)(Cfg.FadeSeconds * 1000) },
                { "transport", relay == null ? "p2p" : "relay" }, { "master", p == master }, { "hotkey", Cfg.DrawHotkey },
                { "reactions", ReactionSettings() } }));
        }

        static string RecvJs(Dictionary<string, object> msg)
        {
            return "window.__tsdraw&&window.__tsdraw.recv(" + Json.Str(msg) + ")";
        }

        string PageRelayStatus()
        {
            return relay == null ? "p2p" : relay.Status;
        }

        void BroadcastStatus()
        {
            cdp.Broadcast(RecvJs(new Dictionary<string, object> { { "t", "status" }, { "relay", PageRelayStatus() } }));
            UpdateTrayText();
        }

        void OnPageMessage(CdpPage page, string json)
        {
            var d = Json.Obj(json);
            if (d == null) return;
            switch (Json.S1(d, "t"))
            {
                case "hello":
                    SendHello(page);
                    break;
                case "sessions":
                    var ps = new PageSessions();
                    foreach (var o in Json.Arr(d, "outbound").OfType<Dictionary<string, object>>())
                    {
                        var sid = Json.S1(o, "sid");
                        if (string.IsNullOrEmpty(sid)) continue;
                        ps.Outbound.Add(new OutboundInfo
                        {
                            Sid = sid, Type = (int)Json.D(o, "type", 0), SrcId = Json.S1(o, "srcId") ?? "", Title = Json.S1(o, "title") ?? "",
                        });
                    }
                    foreach (var o in Json.Arr(d, "inbound").OfType<Dictionary<string, object>>())
                    {
                        var sid = Json.S1(o, "sid");
                        if (!string.IsNullOrEmpty(sid)) ps.Inbound.Add(sid);
                    }
                    pageSessions[page] = ps;
                    Recompute();
                    break;
                case "stroke":
                case "clear":
                case "react":
                case "img":
                    var m = DrawMsg.FromDict(d);
                    if (m == null || m.T == "here") return;
                    m.From = InstanceId;
                    m.Name = Cfg.Name;
                    OverlayController ov;
                    if (overlays.TryGetValue(m.Sid, out ov)) ov.Apply(m);   // drawing on your own stream preview
                    if (relay != null) Publish(m);
                    else if (page != master && master != null) master.Evaluate(TxJs(m));   // pop-out window: send via the main one
                    break;
                case "rx":   // received peer-to-peer by the main window
                    var rx = DrawMsg.FromDict(d.ContainsKey("msg") ? d["msg"] as Dictionary<string, object> : null);
                    if (rx == null || rx.T == "here") return;
                    OverlayController rov;
                    if (overlays.TryGetValue(rx.Sid, out rov)) rov.Apply(rx);
                    foreach (var other in pageSessions.Keys.Where(x => x != page).ToList()) other.Evaluate(RecvJs(rx.ToDict()));
                    break;
                case "7tv":         // emote search for the reactions panel
                case "emotebytes":  // an emote's image file, for our overlay
                    AnswerSevenTv(page, d);
                    break;
                case "settings":   // changed in a reactions panel
                    SetReactionSettings(d.ContainsKey("emotes") ? Json.B(d, "emotes") : (bool?)null,
                        d.ContainsKey("images") ? Json.B(d, "images") : (bool?)null,
                        d.ContainsKey("opacity") ? (int)Json.D(d, "opacity", 100) : (int?)null,
                        d.ContainsKey("cooldown") ? Json.D(d, "cooldown", 0) : (double?)null);
                    break;
                case "asset":   // reaction frames decoded by the page, for our overlay
                    ReactionAssets.Add(d);
                    break;
                case "layers":
                    pageLayers[page] = new PageLayers { Inbound = (int)Json.D(d, "inbound", 0), Drawing = Json.B(d, "drawing") };
                    break;
                case "color":
                    var c = Json.S1(d, "c");
                    if (c != null && Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$")) DrawColor = ColorTranslator.FromHtml(c);
                    break;
                case "p2p":
                    p2pOpen = (int)Json.D(d, "open", 0);
                    p2pTotal = (int)Json.D(d, "total", 0);
                    UpdateTrayText();
                    break;
                case "log":
                    Log.W("page: {0}", Json.S1(d, "msg"));
                    break;
            }
        }

        Dictionary<string, object> ReactionSettings()
        {
            return new Dictionary<string, object> {
                { "emotes", Cfg.ReactionEmotes }, { "images", Cfg.ReactionImages },
                { "opacity", Cfg.ReactionOpacity }, { "cooldown", Cfg.ReactionCooldown } };
        }

        // From the tray menu or a reactions panel: save, apply to our overlays, tell every TeamSpeak window.
        void SetReactionSettings(bool? emotes, bool? images, int? opacity, double? cooldown)
        {
            if (emotes.HasValue) Cfg.ReactionEmotes = emotes.Value;
            if (images.HasValue) Cfg.ReactionImages = images.Value;
            if (opacity.HasValue) Cfg.ReactionOpacity = Math.Max(10, Math.Min(100, opacity.Value));
            if (cooldown.HasValue) Cfg.ReactionCooldown = Math.Max(0, Math.Min(60, cooldown.Value));
            Cfg.Save();
            foreach (var o in overlays.Values) o.SetReactionOpacity(Cfg.ReactionOpacity);
            cdp.Broadcast(RecvJs(new Dictionary<string, object> { { "t", "status" }, { "reactions", ReactionSettings() } }));
            AnnouncePresence();
        }

        void AnswerSevenTv(CdpPage page, Dictionary<string, object> d)
        {
            string kind = Json.S1(d, "t");
            int req = (int)Json.D(d, "req", 0);
            string q = Json.S1(d, "q"), id = Json.S1(d, "id");
            new Thread(() =>
            {
                var reply = new Dictionary<string, object> { { "t", "reply" }, { "req", req } };
                try
                {
                    if (kind == "7tv") reply["items"] = SevenTv.Search(q);
                    else reply["b64"] = Convert.ToBase64String(SevenTv.EmoteBytes(id));
                }
                catch (Exception e) { reply["error"] = "7TV: " + e.GetBaseException().Message; }
                page.Evaluate(RecvJs(reply));
            }) { IsBackground = true }.Start();
        }

        // ------------------------------------------------------------ relay

        string ResolveRelay()
        {
            string r = (Cfg.Relay ?? "").Trim();
            string host;
            int port;
            if (r.Length == 0 || r.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                host = TeamSpeak.ServerHostFromLog();
                if (host == null) return null;
                port = Cfg.RelayPort;
            }
            else
            {
                RelayClient.SplitHostPort(r, out host, out port);
                if (!Regex.IsMatch(r, @":\d+$")) port = Cfg.RelayPort;
            }
            return (host.Contains(":") ? "[" + host + "]" : host) + ":" + port;
        }

        RoomKeys Room(string sid)
        {
            lock (roomsLock)
            {
                RoomKeys k;
                if (!roomsBySid.TryGetValue(sid, out k))
                {
                    k = new RoomKeys(sid);
                    roomsBySid[sid] = k;
                    roomsByTopic[k.Topic] = k;
                }
                return k;
            }
        }

        static string TxJs(DrawMsg m)
        {
            return RecvJs(new Dictionary<string, object> { { "t", "tx" }, { "msg", m.ToDict() } });
        }

        void Publish(DrawMsg m)
        {
            if (relay == null) return;
            var k = Room(m.Sid);
            relay.Publish(k.Topic, k.Seal(Json.Str(m.ToDict())));
        }

        // Called by overlays when the streamer draws on their own screen.
        public void PublishOwn(DrawMsg m)
        {
            m.From = InstanceId;
            m.Name = Cfg.Name;
            if (relay != null)
            {
                Publish(m);
                cdp.Broadcast(RecvJs(m.ToDict()));
            }
            else cdp.Broadcast(TxJs(m));   // every window shows it; the main one sends it to the viewers
        }

        void OnRelayMessage(string topic, byte[] data)   // relay thread
        {
            RoomKeys k;
            lock (roomsLock) roomsByTopic.TryGetValue(topic, out k);
            if (k == null) return;
            string json = k.Open(data);
            var m = json == null ? null : DrawMsg.FromDict(Json.Obj(json));
            if (m == null || m.Sid != k.Sid)
            {
                if (Interlocked.Increment(ref badMessages) <= 5) Log.W("dropped an undecryptable or invalid relay message");
                return;
            }
            if (m.From == InstanceId) return;
            Post(() =>
            {
                OverlayController ov;
                if (m.T != "here" && overlays.TryGetValue(m.Sid, out ov)) ov.Apply(m);
                cdp.Broadcast(RecvJs(m.ToDict()));
            });
        }

        // Tells the viewers of our streams that someone is listening (shown on their pen button).
        void AnnouncePresence()
        {
            if (relay == null) return;   // peer-to-peer: an open link is the presence signal
            foreach (var sid in overlays.Keys) Publish(HereMsg(sid));
        }

        DrawMsg HereMsg(string sid)
        {
            return new DrawMsg
            {
                T = "here", Sid = sid, From = InstanceId, Name = Cfg.Name,
                PolEmotes = Cfg.ReactionEmotes, PolImages = Cfg.ReactionImages, PolCooldown = Cfg.ReactionCooldown,
            };
        }

        void Recompute()
        {
            var outbound = pageSessions.Values.SelectMany(p => p.Outbound).GroupBy(o => o.Sid).Select(g => g.First()).ToList();
            var sids = new HashSet<string>(outbound.Select(o => o.Sid).Concat(pageSessions.Values.SelectMany(p => p.Inbound)));
            if (relay != null) relay.SetTopics(sids.Select(s => Room(s).Topic).ToList());

            foreach (var o in outbound)
            {
                OverlayController ov;
                if (!overlays.TryGetValue(o.Sid, out ov))
                {
                    ov = new OverlayController(this, o.Sid);
                    overlays[o.Sid] = ov;
                    Log.W("you started streaming ({0})", OverlayController.Short(o.Sid));
                    Publish(HereMsg(o.Sid));
                }
                ov.UpdateSource(o.Type, o.SrcId, o.Title);
            }
            foreach (var sid in overlays.Keys.Where(s => !outbound.Any(o => o.Sid == s)).ToList())
            {
                Log.W("stream {0} ended", OverlayController.Short(sid));
                overlays[sid].Dispose();
                overlays.Remove(sid);
            }
            if (overlays.Count == 0) hotkeys.Unregister(HotkeyEscape);
            UpdateTrayText();
        }

        // ------------------------------------------------------------ draw mode

        // The draw hotkey: toggles drawing on the stream you're watching, or - when you're
        // only streaming - on your own screen.
        void OnDrawHotkey()
        {
            var ov = overlays.Values.FirstOrDefault();
            if (ov != null && ov.DrawMode) { SetDrawMode(false); return; }
            var page = pageLayers.Where(kv => kv.Value.Inbound > 0).OrderByDescending(kv => kv.Value.Drawing).Select(kv => kv.Key).FirstOrDefault();
            if (page != null) { page.Evaluate(RecvJs(new Dictionary<string, object> { { "t", "toggle" } })); return; }
            if (ov != null) { SetDrawMode(true); return; }
            Balloon("Nothing to draw on", "Watch a stream or start a screen share in TeamSpeak first.", null);
        }

        void ToggleDrawMode()
        {
            var ov = overlays.Values.FirstOrDefault();
            SetDrawMode(ov != null && !ov.DrawMode);
            if (ov == null) Balloon("Nothing to draw on", "Start a screen share in TeamSpeak first.", null);
        }

        void SetDrawMode(bool on)
        {
            var ov = overlays.Values.FirstOrDefault();
            if (ov == null) on = false;
            else ov.SetDrawMode(on);
            if (on) hotkeys.Register(HotkeyEscape, 0, (uint)Keys.Escape);
            else hotkeys.Unregister(HotkeyEscape);
        }

        // ------------------------------------------------------------ TeamSpeak

        void CheckTeamSpeak()
        {
            var procs = TeamSpeak.Running();
            if (procs.Length == 0) { warnedNoCdp = false; return; }
            if (cdp.Reachable || warnedNoCdp || restarting) return;
            // Give a freshly started TeamSpeak time to open its DevTools port.
            try { if (procs.Min(p => p.StartTime) > DateTime.Now.AddSeconds(-20)) return; } catch { }
            warnedNoCdp = true;
            Balloon("Drawing isn't enabled in TeamSpeak yet",
                "TeamSpeak was started without TSDraw. Click here to restart it with drawing enabled (you'll briefly drop from voice).", "restart");
        }

        void RestartTeamSpeak(bool askFirst)
        {
            if (tsExe == null) tsExe = TeamSpeak.FindExe(Cfg);
            if (tsExe == null)
            {
                MessageBox.Show("Couldn't find TeamSpeak.exe. Set \"teamspeakPath\" in " + Paths.ConfigFile, "TSDraw");
                return;
            }
            if (askFirst && TeamSpeak.Running().Length > 0 && MessageBox.Show(
                    "Restart TeamSpeak with drawing enabled? You'll drop out of voice for a few seconds.",
                    "TSDraw", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            restarting = true;
            string exe = tsExe;
            int port = Cfg.CdpPort;
            new Thread(() =>
            {
                try
                {
                    TeamSpeak.Kill();
                    Thread.Sleep(1000);
                    TeamSpeak.Start(exe, port);
                }
                catch (Exception e) { Log.W("TeamSpeak restart failed: {0}", e.Message); }
                Thread.Sleep(10000);
                Post(() => restarting = false);
            }) { IsBackground = true }.Start();
        }

        // ------------------------------------------------------------ tray

        void Balloon(string title, string text, string tag)
        {
            tray.Tag = tag;
            tray.ShowBalloonTip(8000, title, text, ToolTipIcon.Info);
        }

        string TeamSpeakStatus()
        {
            if (TeamSpeak.Running().Length == 0) return "TeamSpeak: not running";
            if (!cdp.Reachable) return "TeamSpeak: running without drawing support";
            int n = cdp.PageCount;
            return "TeamSpeak: drawing enabled" + (n > 1 ? " (" + n + " windows)" : "");
        }

        string RelayStatusText()
        {
            if (relay == null)
            {
                if (p2pTotal == 0) return "Peer-to-peer: no links (opens when a stream is watched)";
                return "Peer-to-peer: " + p2pOpen + " of " + p2pTotal + " link" + (p2pTotal == 1 ? "" : "s") + " open";
            }
            string s = relay.Status;
            if (s == "idle") return "Relay: idle (connects when a stream is open)";
            return "Relay: " + s + (relay.Endpoint != null && !s.Contains("(") ? " (" + relay.Endpoint + ")" : "");
        }

        void UpdateTrayText()
        {
            string t = "TSDraw - " + (overlays.Count > 0 ? "you're streaming" : relay == null ? "peer-to-peer" :
                relay.Status == "connected" ? "relay connected" : relay.Status);
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void BuildMenu()
        {
            var m = tray.ContextMenuStrip;
            m.Items.Clear();
            var head = new ToolStripMenuItem("TSDraw " + Program.Version) { Enabled = false };
            head.Font = new Font(head.Font, FontStyle.Bold);
            m.Items.Add(head);
            m.Items.Add(new ToolStripMenuItem(TeamSpeakStatus()) { Enabled = false });
            m.Items.Add(new ToolStripMenuItem(RelayStatusText()) { Enabled = false });
            var ov = overlays.Values.FirstOrDefault();
            m.Items.Add(new ToolStripMenuItem(ov == null ? "Not streaming" : "Streaming " + ov.Describe()) { Enabled = false });
            m.Items.Add(new ToolStripSeparator());

            var draw = new ToolStripMenuItem("Draw on my screen", null, (s, e) => ToggleDrawMode())
            {
                Enabled = ov != null, Checked = ov != null && ov.DrawMode, ShortcutKeyDisplayString = Cfg.DrawHotkey,
            };
            m.Items.Add(draw);
            m.Items.Add(new ToolStripMenuItem("Clear my drawings", null, (s, e) => { foreach (var o in overlays.Values) o.ClearOwn(); }) { Enabled = ov != null });
            m.Items.Add(ReactionsMenu());
            m.Items.Add(new ToolStripSeparator());

            bool running = TeamSpeak.Running().Length > 0;
            if (!running) m.Items.Add(new ToolStripMenuItem("Start TeamSpeak", null, (s, e) => RestartTeamSpeak(false)));
            else if (!cdp.Reachable) m.Items.Add(new ToolStripMenuItem("Restart TeamSpeak with drawing enabled", null, (s, e) => RestartTeamSpeak(true)));
            m.Items.Add(new ToolStripMenuItem("Display name: " + Cfg.Name + "...", null, (s, e) => AskName()));
            m.Items.Add(new ToolStripMenuItem("Start TSDraw with Windows", null, (s, e) => ToggleAutostart()) { Checked = AutostartEnabled() });
            m.Items.Add(new ToolStripMenuItem(pendingUpdate != null ? "Install update " + pendingUpdate.Version + " now" : "Check for updates", null,
                (s, e) => { if (pendingUpdate != null) OnLatestRelease(pendingUpdate, true); else CheckForUpdates(true); }) { Enabled = !updating });
            m.Items.Add(new ToolStripMenuItem("Open settings file", null, (s, e) => OpenFile(Paths.ConfigFile)));
            m.Items.Add(new ToolStripMenuItem("Open log", null, (s, e) => OpenFile(Paths.LogFile)));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => Shutdown()));
        }

        ToolStripMenuItem ReactionsMenu()
        {
            var menu = new ToolStripMenuItem("Reactions");
            menu.DropDownItems.Add(new ToolStripMenuItem("Show 7TV emotes", null, (s, e) => SetReactionSettings(!Cfg.ReactionEmotes, null, null, null))
                { Checked = Cfg.ReactionEmotes });
            menu.DropDownItems.Add(new ToolStripMenuItem("Show custom images", null, (s, e) => SetReactionSettings(null, !Cfg.ReactionImages, null, null))
                { Checked = Cfg.ReactionImages });
            var opacity = new ToolStripMenuItem("Opacity");
            foreach (var pct in new[] { 100, 75, 50, 25 })
            {
                int p = pct;
                opacity.DropDownItems.Add(new ToolStripMenuItem(p + "%", null, (s, e) => SetReactionSettings(null, null, p, null)) { Checked = Cfg.ReactionOpacity == p });
            }
            menu.DropDownItems.Add(opacity);
            var cooldown = new ToolStripMenuItem("Cooldown per viewer (when streaming)");
            foreach (var sec in new[] { 0, 1, 2, 3, 5, 10, 15, 30, 60 })
            {
                int c = sec;
                cooldown.DropDownItems.Add(new ToolStripMenuItem(c == 0 ? "Off" : c + " s", null, (s, e) => SetReactionSettings(null, null, null, c))
                    { Checked = Math.Abs(Cfg.ReactionCooldown - c) < 0.01 });
            }
            menu.DropDownItems.Add(cooldown);
            return menu;
        }

        static void OpenFile(string path)
        {
            try { Process.Start("notepad.exe", "\"" + path + "\""); } catch (Exception e) { Log.W("open failed: {0}", e.Message); }
        }

        void AskName()
        {
            using (var f = new Form { Text = "TSDraw", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterScreen, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12), TopMost = true })
            {
                var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
                flow.Controls.Add(new Label { Text = "Name shown next to your drawings:", AutoSize = true });
                var box = new TextBox { Text = Cfg.Name, Width = 260, MaxLength = 32 };
                flow.Controls.Add(box);
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
                flow.Controls.Add(ok);
                f.Controls.Add(flow);
                f.AcceptButton = ok;
                if (f.ShowDialog() == DialogResult.OK && box.Text.Trim().Length > 0)
                {
                    Cfg.Name = box.Text.Trim();
                    Cfg.Save();
                    cdp.Broadcast(RecvJs(new Dictionary<string, object> { { "t", "status" }, { "name", Cfg.Name } }));
                }
            }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool AutostartEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("TSDraw") != null;
        }

        static void ToggleAutostart()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (k.GetValue("TSDraw") != null) k.DeleteValue("TSDraw");
                else k.SetValue("TSDraw", "\"" + Application.ExecutablePath + "\"");
            }
        }

        static Icon MakeIcon()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TSDraw.icon"))
                return new Icon(s, SystemInformation.SmallIconSize);   // the app icon, at the tray's size
        }

        void Shutdown()
        {
            watchdog.Stop();
            updateTimer.Stop();
            cdp.Stop("window.__tsdraw&&window.__tsdraw.remove()");
            if (relay != null) relay.Stop();
            foreach (var o in overlays.Values) o.Dispose();
            overlays.Clear();
            hotkeys.Dispose();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }
}
