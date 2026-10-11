using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace TSDraw
{
    // Keeps TSDraw.exe up to date from the GitHub releases of Repo: the newest release's
    // TSDraw.exe asset replaces the running exe, which then restarts itself.
    static class Updater
    {
        public const string Repo = "spoussa/tsdraw";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";

        public class Release
        {
            public Version Version;
            public string Tag, ExeUrl, Notes;
        }

        // Latest published release, or null if there is none / it has no TSDraw.exe.
        public static Release FetchLatest()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2, which GitHub requires
            var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
            req.UserAgent = "TSDraw/" + Program.Version;
            req.Accept = "application/vnd.github+json";
            req.Timeout = 15000;
            string body;
            try
            {
                using (var resp = req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = r.ReadToEnd();
            }
            catch (WebException e)
            {
                var hr = e.Response as HttpWebResponse;
                if (hr != null && hr.StatusCode == HttpStatusCode.NotFound) return null;   // no releases yet
                throw;
            }
            var d = Json.Obj(body);
            string tag = Json.S1(d, "tag_name");
            Version v;
            if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out v)) return null;
            foreach (var a in Json.Arr(d, "assets"))
            {
                var asset = a as Dictionary<string, object>;
                if (Json.S1(asset, "name") == "TSDraw.exe")
                    return new Release { Version = v, Tag = tag, ExeUrl = Json.S1(asset, "browser_download_url"), Notes = Json.S1(d, "body") ?? "" };
            }
            return null;
        }

        public static bool IsNewer(Release r)
        {
            Version cur;
            return r != null && Version.TryParse(Program.Version, out cur) && r.Version > cur;
        }

        // Puts the new exe in place of the running one. Windows lets a running exe be renamed,
        // so: download beside it, rename ourselves to .old, move the new file in. Returns the
        // exe path to start, or throws (and leaves the current exe in place) on failure.
        public static string Install(Release r)
        {
            string exe = System.Windows.Forms.Application.ExecutablePath;
            string tmp = exe + ".new", old = exe + ".old";
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "TSDraw/" + Program.Version;
                wc.DownloadFile(r.ExeUrl, tmp);
            }
            var bytes = File.ReadAllBytes(tmp);
            if (bytes.Length < 20000 || bytes[0] != 'M' || bytes[1] != 'Z')
            {
                File.Delete(tmp);
                throw new InvalidDataException("downloaded file is not a Windows program");
            }
            TryDelete(old);
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe); throw; }
            Log.W("installed TSDraw {0}", r.Version);
            return exe;
        }

        // Run by the new version: wait for the old process to exit, then clean up after it.
        public static void FinishUpdate(int oldPid)
        {
            try { Process.GetProcessById(oldPid).WaitForExit(15000); } catch (ArgumentException) { }
            string old = System.Windows.Forms.Application.ExecutablePath + ".old";
            new Thread(() =>
            {
                for (int i = 0; i < 20 && File.Exists(old); i++) { TryDelete(old); Thread.Sleep(500); }
            }) { IsBackground = true }.Start();
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
