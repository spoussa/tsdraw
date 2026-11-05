// Exercises GameSwitcher against real windows: Notepad plays the launcher, Paint the game.
// Build and run: test\run-gameswitch.cmd
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace TSDraw
{
    static class Log   // stand-in for Util.cs's, so the test doesn't write to TSDraw's real log
    {
        public static void W(string fmt, params object[] args) { Console.WriteLine("   log: " + (args.Length > 0 ? string.Format(fmt, args) : fmt)); }
    }

    static class GameSwitchTest
    {
        static readonly List<Dictionary<string, object>> sent = new List<Dictionary<string, object>>();
        static int failures;

        static void Check(string name, bool ok, object detail = null)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok || detail == null ? "" : "  (" + detail + ")"));
            if (!ok) failures++;
        }

        static string Show(Dictionary<string, object> m)
        {
            return m == null ? "none" : string.Join(", ", m.Select(kv => kv.Key + "=" + kv.Value));
        }

        static Process Open(string exe)
        {
            var p = Process.Start(exe);
            for (int i = 0; i < 50; i++) { Thread.Sleep(200); p.Refresh(); if (p.MainWindowHandle != IntPtr.Zero) break; }
            return p;
        }

        static string Hwnd(Process p) { p.Refresh(); return p.MainWindowHandle.ToInt64().ToString(); }

        static Dictionary<string, object> TickAndTake(GameSwitcher g, List<GameSwitcher.Stream> streams)
        {
            int before = sent.Count;
            g.Tick(streams);
            return sent.Count > before ? sent[sent.Count - 1] : null;
        }

        static int Main()
        {
            Native.EnableDpiAwareness();
            var g = new GameSwitcher(true, new List<string[]> { new[] { "notepad.exe", "mspaint.exe" } }, m => sent.Add(m));
            Check("rules: built-in League rule plus the test one", g.Rules.Count == 2 && g.Rules[0].Games.Contains("League of Legends"), string.Join("; ", g.Rules));

            var launcher = Open("notepad.exe");
            var stream = new GameSwitcher.Stream { Sid = "S", Type = 3, SrcId = Hwnd(launcher), Status = 4 };
            var streams = new List<GameSwitcher.Stream> { stream };
            Process game = null, game2 = null;
            try
            {
                var m = TickAndTake(g, streams);
                Check("sharing the launcher, no game: nothing sent", m == null, Show(m));

                game = Open("mspaint.exe");
                m = TickAndTake(g, streams);
                Check("game window just appeared: waits before switching", m == null, Show(m));
                Thread.Sleep(1700);
                m = TickAndTake(g, streams);
                string gameId = Hwnd(game);
                Check("game window settled: switch to it, guarded", m != null && (string)m["to"] == gameId && (string)m["guard"] == gameId, Show(m));
                m = TickAndTake(g, streams);
                Check("no repeat while the page is switching", m == null, Show(m));

                g.OnResult("S", gameId, true, null, "Paint");
                stream.SrcId = gameId;
                m = TickAndTake(g, streams);
                Check("streaming the game: nothing more to send", m == null, Show(m));

                game.CloseMainWindow();
                game.WaitForExit(5000);
                m = TickAndTake(g, streams);
                Check("game closed: back to the launcher, game still guarded", m != null && (string)m["to"] == Hwnd(launcher) && (string)m["guard"] == gameId, Show(m));
                g.OnResult("S", Hwnd(launcher), true, null, "Notepad");
                stream.SrcId = Hwnd(launcher);
                m = TickAndTake(g, streams);
                Check("back on the launcher: guard cleared", m != null && !m.ContainsKey("to") && (string)m["guard"] == "", Show(m));

                game2 = Open("mspaint.exe");
                TickAndTake(g, streams);
                Thread.Sleep(1700);
                m = TickAndTake(g, streams);
                string game2Id = Hwnd(game2);
                Check("next game: switched again", m != null && (string)m["to"] == game2Id, Show(m));
                g.OnResult("S", game2Id, true, null, "Paint");

                // You switch back to the launcher yourself mid-game: TSDraw leaves it there.
                stream.SrcId = Hwnd(launcher);
                TickAndTake(g, streams);
                Thread.Sleep(1700);
                m = TickAndTake(g, streams);
                Check("manual switch back mid-game is respected", m == null || !m.ContainsKey("to"), Show(m));

                // Capture failed while the game window still exists (e.g. it went exclusive fullscreen).
                stream.SrcId = game2Id;
                TickAndTake(g, streams);
                stream.Status = -1;
                m = TickAndTake(g, streams);
                Check("game capture failed: back to the launcher", m != null && (string)m["to"] == Hwnd(launcher), Show(m));
                stream.Status = 4;

                g.Enabled = false;
                stream.SrcId = game2Id;
                m = TickAndTake(g, streams);
                Check("turned off: guard dropped", m != null && (string)m["guard"] == "", Show(m));
                stream.SrcId = Hwnd(launcher);
                m = TickAndTake(g, streams);
                Check("turned off: no switching", m == null, Show(m));

                Check("other stream kinds are ignored", TickAndTake(g, new List<GameSwitcher.Stream> { new GameSwitcher.Stream { Sid = "X", Type = 2, SrcId = "0", Status = 4 } }) == null);
            }
            finally
            {
                foreach (var p in new[] { game, game2, launcher })
                    if (p != null && !p.HasExited) { p.CloseMainWindow(); p.WaitForExit(3000); }
            }
            Console.WriteLine(failures == 0 ? "all passed" : failures + " failed");
            return failures == 0 ? 0 : 1;
        }
    }
}
