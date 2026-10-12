using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TSDraw
{
    static class Paths
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TSDraw");
        public static readonly string ConfigFile = Path.Combine(Dir, "config.json");
        public static readonly string LogFile = Path.Combine(Dir, "tsdraw.log");
    }

    static class Log
    {
        static readonly object Lock = new object();

        public static void W(string fmt, params object[] args)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") +
                (args.Length > 0 ? string.Format(CultureInfo.InvariantCulture, fmt, args) : fmt);
            lock (Lock)
            {
                try
                {
                    Directory.CreateDirectory(Paths.Dir);
                    var fi = new FileInfo(Paths.LogFile);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        File.Copy(Paths.LogFile, Paths.LogFile + ".old", true);
                        File.WriteAllText(Paths.LogFile, "");
                    }
                    File.AppendAllText(Paths.LogFile, line + Environment.NewLine);
                }
                catch { }
            }
            if (Console.Out != null) { try { Console.WriteLine(line); } catch { } }
        }
    }

    static class Json
    {
        static readonly JavaScriptSerializer S = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        public static Dictionary<string, object> Obj(string json)
        {
            try { return S.DeserializeObject(json) as Dictionary<string, object>; }
            catch { return null; }
        }

        public static object Parse(string json) { return S.DeserializeObject(json); }

        public static string Str(object o) { return S.Serialize(o); }

        public static string S1(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double D(Dictionary<string, object> d, string key, double def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return def; }
        }

        public static bool B(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0; } catch { return false; }
        }

        public static object[] Arr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return new object[0];
            var a = v as object[];
            if (a != null) return a;
            var l = v as System.Collections.ArrayList;
            return l != null ? l.ToArray() : new object[0];
        }
    }

    class Config
    {
        public string Name = Environment.UserName;
        public string Relay = "";              // "" = peer-to-peer; "auto" = relay on the TeamSpeak server's host; or "host[:port]"
        public int RelayPort = 9989;
        public string RelayPassword = "";
        public int CdpPort = 39222;
        public bool LaunchTeamSpeak = true;
        public string TeamSpeakPath = "";
        public string DrawHotkey = "Ctrl+Alt+D";
        public double FadeSeconds = 3;
        public bool ExcludeOverlayFromCapture = true;
        public bool AutoUpdate = true;
        public bool ReactionEmotes = true, ReactionImages = true;   // what you see, and what your stream accepts
        public int ReactionOpacity = 100;                            // percent
        public double ReactionCooldown = 0;                          // seconds per viewer on your stream

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (!File.Exists(Paths.ConfigFile)) { c.Save(); return c; }
                var d = Json.Obj(File.ReadAllText(Paths.ConfigFile));
                if (d == null) { Log.W("config.json is not valid JSON; using defaults"); return c; }
                c.Name = Json.S1(d, "name") ?? c.Name;
                c.Relay = Json.S1(d, "relay") ?? c.Relay;
                c.RelayPort = (int)Json.D(d, "relayPort", c.RelayPort);
                c.RelayPassword = Json.S1(d, "relayPassword") ?? c.RelayPassword;
                c.CdpPort = (int)Json.D(d, "cdpPort", c.CdpPort);
                if (d.ContainsKey("launchTeamSpeak")) c.LaunchTeamSpeak = Json.B(d, "launchTeamSpeak");
                c.TeamSpeakPath = Json.S1(d, "teamspeakPath") ?? c.TeamSpeakPath;
                c.DrawHotkey = Json.S1(d, "drawHotkey") ?? c.DrawHotkey;
                c.FadeSeconds = Json.D(d, "fadeSeconds", c.FadeSeconds);
                if (d.ContainsKey("excludeOverlayFromCapture")) c.ExcludeOverlayFromCapture = Json.B(d, "excludeOverlayFromCapture");
                if (d.ContainsKey("autoUpdate")) c.AutoUpdate = Json.B(d, "autoUpdate");
                if (d.ContainsKey("reactionEmotes")) c.ReactionEmotes = Json.B(d, "reactionEmotes");
                if (d.ContainsKey("reactionImages")) c.ReactionImages = Json.B(d, "reactionImages");
                c.ReactionOpacity = Math.Max(10, Math.Min(100, (int)Json.D(d, "reactionOpacity", c.ReactionOpacity)));
                c.ReactionCooldown = Math.Max(0, Math.Min(60, Json.D(d, "reactionCooldown", c.ReactionCooldown)));
            }
            catch (Exception e) { Log.W("config load failed: {0}", e.Message); }
            return c;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.AppendFormat("  \"name\": {0},\n", Json.Str(Name));
            sb.AppendFormat("  \"relay\": {0},\n", Json.Str(Relay));
            sb.AppendFormat("  \"relayPort\": {0},\n", RelayPort);
            sb.AppendFormat("  \"relayPassword\": {0},\n", Json.Str(RelayPassword));
            sb.AppendFormat("  \"cdpPort\": {0},\n", CdpPort);
            sb.AppendFormat("  \"launchTeamSpeak\": {0},\n", LaunchTeamSpeak ? "true" : "false");
            sb.AppendFormat("  \"teamspeakPath\": {0},\n", Json.Str(TeamSpeakPath));
            sb.AppendFormat("  \"drawHotkey\": {0},\n", Json.Str(DrawHotkey));
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"fadeSeconds\": {0},\n", FadeSeconds);
            sb.AppendFormat("  \"excludeOverlayFromCapture\": {0},\n", ExcludeOverlayFromCapture ? "true" : "false");
            sb.AppendFormat("  \"autoUpdate\": {0},\n", AutoUpdate ? "true" : "false");
            sb.AppendFormat("  \"reactionEmotes\": {0},\n", ReactionEmotes ? "true" : "false");
            sb.AppendFormat("  \"reactionImages\": {0},\n", ReactionImages ? "true" : "false");
            sb.AppendFormat("  \"reactionOpacity\": {0},\n", ReactionOpacity);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"reactionCooldown\": {0}\n", ReactionCooldown);
            sb.Append("}\n");
            try { Directory.CreateDirectory(Paths.Dir); File.WriteAllText(Paths.ConfigFile, sb.ToString()); }
            catch (Exception e) { Log.W("config save failed: {0}", e.Message); }
        }
    }

    // Per-stream keys. The stream's serverSessionId is a random UUID known only to the
    // streamer and to people who can see the stream in TeamSpeak, so we derive the relay
    // topic and the encryption keys from it: the relay only sees opaque names and bytes.
    class RoomKeys
    {
        public readonly string Sid, Topic;
        readonly byte[] encKey, macKey;

        public RoomKeys(string sid)
        {
            Sid = sid;
            Topic = "v1/" + Hex(Sha256("tsdraw-topic|" + sid)).Substring(0, 32);
            encKey = Sha256("tsdraw-enc|" + sid);
            macKey = Sha256("tsdraw-mac|" + sid);
        }

        public byte[] Seal(string json)
        {
            byte[] plain = Encoding.UTF8.GetBytes(json);
            using (var aes = new AesCryptoServiceProvider())
            {
                aes.Key = encKey; aes.GenerateIV(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                byte[] ct;
                using (var enc = aes.CreateEncryptor()) ct = enc.TransformFinalBlock(plain, 0, plain.Length);
                var outb = new byte[16 + ct.Length + 16];
                Buffer.BlockCopy(aes.IV, 0, outb, 0, 16);
                Buffer.BlockCopy(ct, 0, outb, 16, ct.Length);
                byte[] tag;
                using (var h = new HMACSHA256(macKey)) tag = h.ComputeHash(outb, 0, 16 + ct.Length);
                Buffer.BlockCopy(tag, 0, outb, 16 + ct.Length, 16);
                return outb;
            }
        }

        public string Open(byte[] data)
        {
            if (data.Length < 48 || (data.Length - 32) % 16 != 0) return null;
            int ctLen = data.Length - 32;
            byte[] tag;
            using (var h = new HMACSHA256(macKey)) tag = h.ComputeHash(data, 0, 16 + ctLen);
            int diff = 0;
            for (int i = 0; i < 16; i++) diff |= tag[i] ^ data[16 + ctLen + i];
            if (diff != 0) return null;
            using (var aes = new AesCryptoServiceProvider())
            {
                aes.Key = encKey; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                var iv = new byte[16];
                Buffer.BlockCopy(data, 0, iv, 0, 16);
                aes.IV = iv;
                try
                {
                    using (var dec = aes.CreateDecryptor())
                        return Encoding.UTF8.GetString(dec.TransformFinalBlock(data, 16, ctLen));
                }
                catch (CryptographicException) { return null; }
            }
        }

        static byte[] Sha256(string s)
        {
            using (var h = SHA256.Create()) return h.ComputeHash(Encoding.UTF8.GetBytes(s));
        }

        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static string RandomId(int bytes)
        {
            var b = new byte[bytes];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return Hex(b);
        }
    }
}
