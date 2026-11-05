using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace TSDraw
{
    // Follows the game, like Discord does: while you share a game launcher's window (the League of
    // Legends client) and the game starts, the stream moves to the game's window; when the game
    // closes, it moves back to the launcher. TSDraw.exe decides, because only it can see which
    // program owns a window. The injected page makes the switch through TeamSpeak's own stream
    // object, the way TeamSpeak's stream menu changes a live stream's source, so viewers stay on.
    class GameSwitcher
    {
        public class Rule
        {
            public readonly HashSet<string> Launchers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public override string ToString() { return string.Join("/", Launchers) + " -> " + string.Join("/", Games); }
        }

        public class Stream { public string Sid, SrcId; public int Type, Status; }

        class State
        {
            public Rule Rule;
            public IntPtr Launcher;                    // the launcher window the stream came from
            public string Game;                        // the game window the stream is (or was) on
            public IntPtr Candidate;                   // a game window that just appeared
            public DateTime CandidateSince;
            public readonly HashSet<string> Done = new HashSet<string>();   // windows switched to (or given up on): never again
            public string PendingTo;                   // switch asked of the page, not answered yet
            public DateTime PendingAt;
            public int Fails;
            public string Guard;                       // window the page keeps the stream alive for when it closes
            public bool GuardSent;
        }

        // Built in; config "autoSwitchGames" adds more.
        public static readonly string[][] BuiltIn = {
            new[] { "LeagueClientUx.exe, LeagueClientUxRender.exe", "League of Legends.exe" },
        };

        const int CaptureFailed = -1, WindowSource = 3;
        static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);   // a new game window must stay this long
        static readonly TimeSpan Retry = TimeSpan.FromSeconds(4);

        public readonly List<Rule> Rules = new List<Rule>();
        public bool Enabled;
        readonly Action<Dictionary<string, object>> send;
        readonly Dictionary<string, State> states = new Dictionary<string, State>();

        public GameSwitcher(bool enabled, IEnumerable<string[]> pairs, Action<Dictionary<string, object>> send)
        {
            Enabled = enabled;
            this.send = send;
            foreach (var p in BuiltIn.Concat(pairs))
            {
                var r = MakeRule(p[0], p[1]);
                if (r != null) Rules.Add(r); else Log.W("ignoring autoSwitchGames entry \"{0}\" / \"{1}\"", p[0], p[1]);
            }
        }

        // "LeagueClientUx.exe, LeagueClientUxRender.exe" + "League of Legends.exe"
        public static Rule MakeRule(string launchers, string games)
        {
            var r = new Rule();
            foreach (var n in (launchers ?? "").Split(',')) { var x = ExeName(n); if (x != "") r.Launchers.Add(x); }
            foreach (var n in (games ?? "").Split(',')) { var x = ExeName(n); if (x != "") r.Games.Add(x); }
            return r.Launchers.Count > 0 && r.Games.Count > 0 ? r : null;
        }

        static string ExeName(string s)   // as Process.ProcessName has it: no ".exe"
        {
            s = (s ?? "").Trim();
            return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 4) : s;
        }

        // The page was (re)loaded and forgot which streams to keep alive.
        public void ResendGuards()
        {
            foreach (var st in states.Values) st.GuardSent = false;
        }

        // About once a second, with the streams you're sending.
        public void Tick(IList<Stream> outbound)
        {
            foreach (var sid in states.Keys.Where(s => !outbound.Any(o => o.Sid == s)).ToList()) states.Remove(sid);
            var windows = outbound.Where(o => o.Type == WindowSource).ToList();
            if (windows.Count == 0) return;
            if (!Enabled)
            {
                foreach (var o in windows) SetGuard(o.Sid, Get(o.Sid), null);
                return;
            }

            // pid -> name, for the programs our rules mention
            var wanted = new HashSet<string>(Rules.SelectMany(r => r.Launchers.Concat(r.Games)), StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<int, string>();
            foreach (var p in Process.GetProcesses())
            {
                try { if (wanted.Contains(p.ProcessName)) names[p.Id] = p.ProcessName; } catch { }
                p.Dispose();
            }

            var now = DateTime.UtcNow;
            foreach (var o in windows)
            {
                var st = Get(o.Sid);
                long v;
                var src = long.TryParse(o.SrcId, out v) ? new IntPtr(v) : IntPtr.Zero;
                bool alive = src != IntPtr.Zero && Native.IsWindow(src);
                string exe = null;
                if (alive) names.TryGetValue(Native.WindowPid(src), out exe);
                var asGame = exe == null ? null : Rules.FirstOrDefault(r => r.Games.Contains(exe));
                var asLauncher = exe == null ? null : Rules.FirstOrDefault(r => r.Launchers.Contains(exe));

                if (asGame != null && o.Status != CaptureFailed)
                {
                    // Sharing a game: when its window closes, keep the stream and go back to the launcher.
                    st.Rule = asGame;
                    st.Game = o.SrcId;
                    st.Candidate = IntPtr.Zero;
                    SetGuard(o.Sid, st, o.SrcId);
                }
                else if (asLauncher != null)
                {
                    st.Rule = asLauncher;
                    st.Launcher = src;
                    if (st.Guard != st.PendingTo) SetGuard(o.Sid, st, null);   // not while the page is moving it to the game
                    var game = Biggest(names, asLauncher.Games, true);
                    if (game == IntPtr.Zero || st.Done.Contains(Id(game))) { st.Candidate = IntPtr.Zero; continue; }
                    if (st.Candidate != game) { st.Candidate = game; st.CandidateSince = now; continue; }
                    if (now - st.CandidateSince < Settle) continue;
                    Switch(o.Sid, st, game, true);
                }
                else if (st.Rule != null && st.Game == o.SrcId && (!alive || o.Status == CaptureFailed))
                {
                    // The game closed. Back to the launcher once its window is showing again.
                    var back = st.Launcher != IntPtr.Zero && Native.IsWindow(st.Launcher) && Native.IsWindowVisible(st.Launcher) &&
                        IsOf(names, st.Launcher, st.Rule.Launchers) ? st.Launcher : Biggest(names, st.Rule.Launchers, false);
                    if (back != IntPtr.Zero) Switch(o.Sid, st, back, false);
                }
            }
        }

        // The page's answer to a switch.
        public void OnResult(string sid, string to, bool ok, string err, string title)
        {
            State st;
            if (sid == null || !states.TryGetValue(sid, out st)) return;
            if (ok)
            {
                st.PendingTo = null;
                st.Fails = 0;
                st.Done.Add(to);
                Log.W("stream switched to \"{0}\"", title);
                return;
            }
            Log.W("couldn't switch the stream to window {0}: {1}", to, err);
            if (++st.Fails >= 5) { st.Done.Add(to); st.Fails = 0; Log.W("giving up on window {0}", to); }
        }

        State Get(string sid)
        {
            State st;
            if (!states.TryGetValue(sid, out st)) states[sid] = st = new State();
            return st;
        }

        void SetGuard(string sid, State st, string guard)
        {
            if (st.Guard == guard && (st.GuardSent || guard == null)) return;
            st.Guard = guard;
            st.GuardSent = true;
            send(new Dictionary<string, object> { { "t", "autosrc" }, { "sid", sid }, { "guard", guard ?? "" } });
        }

        void Switch(string sid, State st, IntPtr to, bool toGame)
        {
            string id = Id(to);
            if (st.PendingTo == id && DateTime.UtcNow - st.PendingAt < Retry) return;
            st.PendingTo = id;
            st.PendingAt = DateTime.UtcNow;
            // Going back, the closed game window stays guarded until the stream has left it, so a
            // capture failure TeamSpeak reports mid-switch doesn't end the stream.
            if (toGame) st.Game = id;
            st.Guard = st.Game;
            st.GuardSent = true;
            Log.W(toGame ? "game started: switching the stream to \"{0}\"" : "game closed: switching the stream back to \"{0}\"", Native.WindowTitle(to));
            send(new Dictionary<string, object> { { "t", "autosrc" }, { "sid", sid }, { "to", id }, { "guard", st.Guard ?? "" } });
        }

        static string Id(IntPtr h) { return h.ToInt64().ToString(); }

        static bool IsOf(Dictionary<int, string> names, IntPtr hwnd, HashSet<string> exes)
        {
            string n;
            return names.TryGetValue(Native.WindowPid(hwnd), out n) && exes.Contains(n);
        }

        // The biggest app window of these programs; shownOnly skips minimized and small ones.
        static IntPtr Biggest(Dictionary<int, string> names, HashSet<string> exes, bool shownOnly)
        {
            var pids = new HashSet<int>(names.Where(kv => exes.Contains(kv.Value)).Select(kv => kv.Key));
            if (pids.Count == 0) return IntPtr.Zero;
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;
            foreach (var h in Native.AppWindows(pids))
            {
                bool min = Native.IsIconic(h);
                Native.RECT r;
                Native.GetWindowRect(h, out r);
                int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                if (shownOnly && (min || w < 400 || ht < 300)) continue;
                long area = min ? 0 : (long)Math.Max(0, w) * Math.Max(0, ht);
                if (area > bestArea) { best = h; bestArea = area; }
            }
            return best;
        }
    }
}
