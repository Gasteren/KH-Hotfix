using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace KHHotfix
{
    internal static class TextUtil
    {
        // Shortens s to at most max characters (ending with "..."), never splitting a surrogate pair.
        public static string Clip(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            int cut = Math.Max(0, max - 3);
            if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
            return s.Substring(0, cut) + "...";
        }

        public static string OrDash(string s) { return string.IsNullOrEmpty(s) || s.Trim().Length == 0 ? "-" : s; }
    }

    // Minimal JSON support: writing strings, and reading single values from Discord's small replies.
    internal static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // First value of "name": "text" or "name": 123 in a JSON document, or null.
        public static string Find(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Match m = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*(?:\"((?:[^\"\\\\]|\\\\.)*)\"|(-?[0-9][0-9.eE+-]*))");
            if (!m.Success) return null;
            if (!m.Groups[1].Success) return m.Groups[2].Value;
            try { return Regex.Unescape(m.Groups[1].Value); }
            catch (ArgumentException) { return m.Groups[1].Value; }
        }
    }

    // One Discord embed, serialized within Discord's limits (6000 characters in total, 25 fields, ...).
    internal sealed class DiscordEmbed
    {
        public string Title, Footer, ImageFile;
        public int Color = -1;
        public DateTime? Timestamp;
        readonly List<string[]> fields = new List<string[]>();

        public void Add(string name, string value, bool inline)
        {
            if (fields.Count < 25) fields.Add(new[] { name, value, inline ? "1" : "0" });
        }

        public int FieldCount { get { return fields.Count; } }

        // files: names of the attachments that are really sent (the image is only referenced when present).
        public string ToJson(ICollection<string> files)
        {
            int budget = 5900;
            var p = new List<string>();
            Func<string, int, string> take = delegate(string s, int max)
            {
                string v = TextUtil.Clip(s ?? "", Math.Max(0, Math.Min(max, budget)));
                budget -= v.Length;
                return v;
            };
            if (!string.IsNullOrEmpty(Title)) p.Add("\"title\":" + Json.Str(take(Title, 256)));
            if (Color >= 0) p.Add("\"color\":" + Color.ToString(CultureInfo.InvariantCulture));
            if (Timestamp.HasValue) p.Add("\"timestamp\":" + Json.Str(Timestamp.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)));
            string footer = string.IsNullOrEmpty(Footer) ? null : take(Footer, 2048);
            var f = new List<string>();
            foreach (string[] x in fields)
            {
                if (budget < 40) break;
                string name = take(TextUtil.OrDash(x[0]), 256);
                string value = take(TextUtil.OrDash(x[1]), 1024);
                f.Add("{\"name\":" + Json.Str(TextUtil.OrDash(name)) + ",\"value\":" + Json.Str(TextUtil.OrDash(value)) + ",\"inline\":" + (x[2] == "1" ? "true" : "false") + "}");
            }
            if (f.Count > 0) p.Add("\"fields\":[" + string.Join(",", f) + "]");
            if (footer != null) p.Add("\"footer\":{\"text\":" + Json.Str(footer) + "}");
            if (ImageFile != null && files != null && files.Contains(ImageFile)) p.Add("\"image\":{\"url\":" + Json.Str("attachment://" + ImageFile) + "}");
            return "{" + string.Join(",", p) + "}";
        }
    }

    // Posts messages with attachments to a Discord webhook. Works with forum channels (a message there creates a post
    // and needs a title) and with normal text channels; which one a webhook uses is detected and remembered.
    internal static class Discord
    {
        // Attachments per message are kept below Discord's 10 MiB upload limit for servers without boosts.
        public const int MaxUploadBytes = 9 * 1024 * 1024 + 512 * 1024;
        const string Agent = "KH-Hotfix (https://github.com/Gasteren/KH-Hotfix)";
        internal static int ServerErrorDelayMs = 2000;   // the self-test shortens it

        static readonly Regex WebhookRx = new Regex(@"^https://(?:(?:ptb|canary)\.)?discord(?:app)?\.com/api(?:/v\d+)?/webhooks/(\d{5,25})/[A-Za-z0-9_\-]{20,128}/?$", RegexOptions.IgnoreCase);
        // Loopback endpoints are accepted so the tests can use a local fake Discord server.
        static readonly Regex LocalRx = new Regex(@"^http://(?:127\.0\.0\.1|localhost):\d{1,5}/api(?:/v\d+)?/webhooks/(\d{1,25})/[A-Za-z0-9_\-]{1,128}/?$", RegexOptions.IgnoreCase);
        // The project's report relay (relay\worker.js): /bug and /log; plain http only on this PC (tests).
        static readonly Regex RelayRx = new Regex(@"^(?:https://[A-Za-z0-9.\-]+(?::\d{1,5})?|http://(?:127\.0\.0\.1|localhost):\d{1,5})(?:/[A-Za-z0-9._~\-]+)*/(bug|log)/?$", RegexOptions.IgnoreCase);

        public sealed class Attachment
        {
            public readonly string Name;
            public readonly byte[] Data;
            public readonly bool Optional;   // dropped first (largest first) when the message would be too large

            public Attachment(string name, byte[] data, bool optional)
            {
                Name = SafeFileName(name);
                Data = data ?? new byte[0];
                Optional = optional;
            }
        }

        public sealed class Message
        {
            public string Username, ThreadName, Content;
            public DiscordEmbed Embed;
            public readonly List<string> Tags = new List<string>();
            public readonly List<Attachment> Files = new List<Attachment>();
        }

        public sealed class Result
        {
            public bool Ok;
            public string Error, Link;
            public readonly List<string> Dropped = new List<string>();
        }

        public static bool IsWebhook(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            string u = url.Trim();
            return WebhookRx.IsMatch(u) || LocalRx.IsMatch(u) || RelayRx.IsMatch(u);
        }

        static bool IsRelay(string url) { return url != null && !WebhookRx.IsMatch(url.Trim()) && !LocalRx.IsMatch(url.Trim()) && RelayRx.IsMatch(url.Trim()); }

        static string WebhookId(string url)
        {
            Match m = WebhookRx.Match(url.Trim());
            if (!m.Success) m = LocalRx.Match(url.Trim());
            if (m.Success) return m.Groups[1].Value;
            m = RelayRx.Match(url.Trim());
            return m.Success ? "relay-" + m.Groups[1].Value.ToLowerInvariant() : null;
        }

        // Safe to log or show: the webhook id without its secret token, or the relay endpoint (which holds no secret).
        public static string Describe(string url)
        {
            if (string.IsNullOrEmpty(url) || url.Trim().Length == 0) return "not set";
            if (IsRelay(url))
            {
                Uri u = new Uri(url.Trim());
                return "relay " + u.Authority + u.AbsolutePath.TrimEnd('/');
            }
            string id = WebhookId(url);
            return id == null ? "invalid webhook URL" : "webhook " + id;
        }

        static string SafeFileName(string name)
        {
            string n = Regex.Replace(name ?? "file", @"[^A-Za-z0-9._\-]", "_");
            return n.Length == 0 ? "file" : TextUtil.Clip(n, 120);
        }

        static string SafeUsername(string name)
        {
            string n = Regex.Replace(name ?? "", "(?i)discord|clyde|```|[@#:]", "").Trim();
            return n.Length == 0 ? "KH Hotfix" : TextUtil.Clip(n, 80);
        }

        static void PrepareNetwork()
        {
            // Discord only accepts TLS 1.2+; programs built without a target framework default to older protocols.
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); }
            catch { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
            ServicePointManager.Expect100Continue = false;
        }

        static string ContentType(string name)
        {
            string e = Path.GetExtension(name).ToLowerInvariant();
            if (e == ".txt" || e == ".log") return "text/plain; charset=utf-8";
            if (e == ".jpg" || e == ".jpeg") return "image/jpeg";
            if (e == ".png") return "image/png";
            if (e == ".gz") return "application/gzip";
            return "application/octet-stream";
        }

        static int Http(string method, string url, string contentType, byte[] body, int timeoutMs, out string response, out string error)
        {
            response = null;
            error = null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.UserAgent = Agent + " " + Program.Version;
                req.Accept = "application/json";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.KeepAlive = false;
                if (body != null)
                {
                    req.ContentType = contentType;
                    req.ContentLength = body.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    response = ReadBody(resp);
                    return (int)resp.StatusCode;
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    using (resp)
                    {
                        response = ReadBody(resp);
                        return (int)resp.StatusCode;
                    }
                }
                error = ex.Status == WebExceptionStatus.Timeout ? "the connection timed out" : ex.Message;
                return 0;
            }
            catch (Exception ex) { error = ex.Message; return 0; }
        }

        static string ReadBody(HttpWebResponse resp)
        {
            try
            {
                using (Stream s = resp.GetResponseStream())
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
            catch { return null; }
        }

        // ---- what the hotfix remembers per webhook: channel kind (forum or text) and server id -----------------
        static string StateFile(string id) { return Path.Combine(Paths.State, "discord-" + id + ".txt"); }

        static Dictionary<string, string> ReadState(string id)
        {
            var d = new Dictionary<string, string>();
            try
            {
                string f = StateFile(id);
                if (!File.Exists(f)) return d;
                foreach (string line in File.ReadAllLines(f))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) d[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            catch { }
            return d;
        }

        static void WriteState(string id, Dictionary<string, string> d)
        {
            try
            {
                Directory.CreateDirectory(Paths.State);
                File.WriteAllLines(StateFile(id), d.Select(kv => kv.Key + "=" + kv.Value).ToArray());
            }
            catch { }
        }

        // Checks a webhook without posting anything. Returns null when it works, otherwise the problem.
        public static string Check(string url, int timeoutMs, out string info)
        {
            info = null;
            if (!IsWebhook(url)) return "the report address is not valid";
            PrepareNetwork();
            string resp, err;
            int status = Http("GET", url.Trim().TrimEnd('/'), null, null, timeoutMs, out resp, out err);
            if (status == 200)
            {
                string id = WebhookId(url), guild = Json.Find(resp, "guild_id");
                Dictionary<string, string> st = ReadState(id);
                if (!string.IsNullOrEmpty(guild)) { st["guild"] = guild; WriteState(id, st); }
                info = "\"" + Json.Find(resp, "name") + "\" in channel " + Json.Find(resp, "channel_id") + (st.ContainsKey("kind") ? " (" + st["kind"] + " channel)" : "");
                return null;
            }
            return Explain(status, Json.Find(resp, "code"), Json.Find(resp, "message"), err, IsRelay(url));
        }

        static string Explain(int status, string code, string message, string error, bool relay)
        {
            string server = relay ? "the KH Hotfix report server" : "Discord";
            if (status == 0) return "no connection to " + server + " (" + error + ")";
            if (relay && status != 400 && status != 413 && status != 429)
                return "the KH Hotfix report server answered HTTP " + status + (message != null ? ": " + message : "");
            if (status == 401 || status == 403 || status == 404)
                return "the Discord webhook does not exist any more or may not post (HTTP " + status + "). Ask for a new webhook URL.";
            if (status == 400 && code == "40067")
                return "the Discord forum requires a tag on new posts: turn off \"Require tags\" for the forum or set DiscordBugReportTags in KHHotfix.local.ini.";
            if (status == 413) return "the report is too large for Discord, even without the optional files.";
            return server + " answered HTTP " + status + (code != null ? ", code " + code : "") + (message != null ? ": " + message : "");
        }

        static string Payload(Message m, bool thread, ICollection<string> files)
        {
            var p = new List<string>();
            if (!string.IsNullOrEmpty(m.Username)) p.Add("\"username\":" + Json.Str(SafeUsername(m.Username)));
            p.Add("\"content\":" + Json.Str(TextUtil.Clip(m.Content ?? "", 2000)));
            if (thread)
            {
                p.Add("\"thread_name\":" + Json.Str(TextUtil.Clip(string.IsNullOrEmpty(m.ThreadName) ? "KH Hotfix report" : m.ThreadName, 100)));
                if (m.Tags.Count > 0) p.Add("\"applied_tags\":[" + string.Join(",", m.Tags.Take(5).Select(t => Json.Str(t))) + "]");
            }
            if (m.Embed != null) p.Add("\"embeds\":[" + m.Embed.ToJson(files) + "]");
            p.Add("\"allowed_mentions\":{\"parse\":[]}");   // never ping @everyone, roles or users from report text
            return "{" + string.Join(",", p) + "}";
        }

        static byte[] Multipart(string payload, List<Attachment> files, out string contentType)
        {
            string boundary = "KHHotfix" + Guid.NewGuid().ToString("N");
            contentType = "multipart/form-data; boundary=" + boundary;
            var ms = new MemoryStream();
            Action<string> w = delegate(string s) { byte[] b = Encoding.UTF8.GetBytes(s); ms.Write(b, 0, b.Length); };
            w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"payload_json\"\r\nContent-Type: application/json\r\n\r\n");
            w(payload);
            w("\r\n");
            for (int i = 0; i < files.Count; i++)
            {
                w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"files[" + i.ToString(CultureInfo.InvariantCulture) + "]\"; filename=\""
                    + files[i].Name + "\"\r\nContent-Type: " + ContentType(files[i].Name) + "\r\n\r\n");
                ms.Write(files[i].Data, 0, files[i].Data.Length);
                w("\r\n");
            }
            w("--" + boundary + "--\r\n");
            return ms.ToArray();
        }

        public static Result Send(string url, Message m, int timeoutMs)
        {
            var r = new Result();
            if (!IsWebhook(url)) { r.Error = "the report address is not valid"; return r; }
            PrepareNetwork();
            string endpoint = url.Trim().TrimEnd('/');
            string id = WebhookId(endpoint);

            var files = m.Files.Where(f => f != null).Take(10).ToList();
            foreach (Attachment extra in m.Files.Where(f => f != null).Skip(10)) r.Dropped.Add(extra.Name);
            while (files.Sum(f => (long)f.Data.Length) > MaxUploadBytes)
            {
                Attachment big = files.Where(f => f.Optional).OrderByDescending(f => f.Data.Length).FirstOrDefault();
                if (big == null) break;
                files.Remove(big);
                r.Dropped.Add(big.Name);
            }

            Dictionary<string, string> st = ReadState(id);
            string kind;
            st.TryGetValue("kind", out kind);
            bool thread = kind != "text";             // unknown or forum: try a forum post first
            bool toggled = false;
            int serverErrors = 0;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                string contentType;
                byte[] body = Multipart(Payload(m, thread, files.Select(f => f.Name).ToList()), files, out contentType);
                string resp, err;
                int status = Http("POST", endpoint + "?wait=true", contentType, body, timeoutMs, out resp, out err);
                string code = Json.Find(resp, "code");
                if (status >= 200 && status < 300)
                {
                    st["kind"] = thread ? "forum" : "text";
                    string channel = Json.Find(resp, "channel_id"), guild;
                    if (!st.TryGetValue("guild", out guild) || string.IsNullOrEmpty(guild))
                    {
                        string info;
                        if (Check(endpoint, Math.Min(timeoutMs, 15000), out info) == null) { st = ReadState(id); st["kind"] = thread ? "forum" : "text"; st.TryGetValue("guild", out guild); }
                    }
                    WriteState(id, st);
                    if (!string.IsNullOrEmpty(guild) && !string.IsNullOrEmpty(channel)) r.Link = "https://discord.com/channels/" + guild + "/" + channel;
                    r.Ok = true;
                    return r;
                }
                if (status == 400 && code == "220001" && !thread) { thread = true; continue; }   // forum channel: a post needs a title
                if (status == 400 && code == "220003" && thread) { thread = false; continue; }  // text channel: no posts/threads
                if (status == 400 && kind == null && !toggled && code != "40067" && code != "50035") { toggled = true; thread = !thread; continue; }
                if (status == 429)
                {
                    double wait;
                    if (!double.TryParse(Json.Find(resp, "retry_after"), NumberStyles.Float, CultureInfo.InvariantCulture, out wait)) wait = 2;
                    Thread.Sleep((int)(Math.Min(Math.Max(wait, 0.25), 15) * 1000));
                    continue;
                }
                if (status == 413)
                {
                    Attachment big = files.Where(f => f.Optional).OrderByDescending(f => f.Data.Length).FirstOrDefault();
                    if (big != null) { files.Remove(big); r.Dropped.Add(big.Name); continue; }
                }
                if (status >= 500 && serverErrors++ < 2) { Thread.Sleep(ServerErrorDelayMs); continue; }
                r.Error = Explain(status, code, Json.Find(resp, "message"), err, IsRelay(endpoint));
                return r;
            }
            r.Error = "Discord did not accept the report after several attempts";
            return r;
        }
    }
}
