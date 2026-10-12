using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TSDraw
{
    // 7TV requests on behalf of the TeamSpeak page, which may only reach TeamSpeak's own
    // servers with fetch(). Only these two fixed kinds of request are possible.
    static class SevenTv
    {
        const string SearchQuery = "query($q: String!) { emotes { search(query: $q, page: 1, perPage: 60, " +
                                   "sort: { sortBy: TOP_ALL_TIME, order: DESCENDING }) { items { id defaultName } } } }";
        static readonly Regex IdRe = new Regex("^[0-9A-Za-z]{20,32}$");
        static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>();
        static readonly LinkedList<string> CacheOrder = new LinkedList<string>();
        static readonly object Lock = new object();

        public static List<Dictionary<string, object>> Search(string q)
        {
            q = (q ?? "").Trim();
            if (q.Length > 100) q = q.Substring(0, 100);
            var body = Encoding.UTF8.GetBytes(Json.Str(new Dictionary<string, object> {
                { "query", SearchQuery }, { "variables", new Dictionary<string, object> { { "q", q } } } }));
            var req = Request("https://7tv.io/v4/gql");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.ContentLength = body.Length;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            string text;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) text = r.ReadToEnd();
            var d = Json.Obj(text);
            var data = d == null ? null : d.ContainsKey("data") ? d["data"] as Dictionary<string, object> : null;
            var emotes = data == null ? null : data["emotes"] as Dictionary<string, object>;
            var search = emotes == null ? null : emotes["search"] as Dictionary<string, object>;
            var items = new List<Dictionary<string, object>>();
            foreach (var o in Json.Arr(search, "items").OfType<Dictionary<string, object>>())
            {
                string id = Json.S1(o, "id"), name = Json.S1(o, "defaultName") ?? "";
                if (id == null || !IdRe.IsMatch(id)) continue;
                items.Add(new Dictionary<string, object> { { "k", "7tv" }, { "ref", id }, { "name", name.Length > 40 ? name.Substring(0, 40) : name } });
            }
            return items;
        }

        // The emote's 4x WebP (animated or still), for the streamer's overlay.
        public static byte[] EmoteBytes(string id)
        {
            if (id == null || !IdRe.IsMatch(id)) throw new ArgumentException("bad emote id");
            lock (Lock)
            {
                byte[] hit;
                if (Cache.TryGetValue(id, out hit)) return hit;
            }
            var req = Request("https://cdn.7tv.app/emote/" + id + "/4x.webp");
            byte[] bytes;
            using (var resp = req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                var buf = new byte[65536];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > 6 * 1024 * 1024) throw new InvalidDataException("emote file too big");
                }
                bytes = ms.ToArray();
            }
            lock (Lock)
            {
                Cache[id] = bytes;
                CacheOrder.AddLast(id);
                while (CacheOrder.Count > 60) { Cache.Remove(CacheOrder.First.Value); CacheOrder.RemoveFirst(); }
            }
            return bytes;
        }

        static HttpWebRequest Request(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "TSDraw/" + Program.Version + " (+https://github.com/" + Updater.Repo + ")";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            return req;
        }
    }
}
