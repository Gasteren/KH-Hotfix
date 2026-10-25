using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace KHHotfix
{
    internal static class Commands
    {
        public static int Run(string[] a)
        {
            switch (a[0].ToLowerInvariant())
            {
                case "--version": Console.WriteLine(Program.Version); return 0;
                case "--paths": return ShowPaths();
                case "--selftest": return SelfTest.Run();
                case "--backup":
                    Console.WriteLine("New backups: " + new SaveGuard().SnapshotAll("manual"));
                    return 0;
                case "--check": return Check();
                case "--restore-backup": return RestoreBackup(a);
                case "--display-info": Console.WriteLine(DisplayGuard.Describe(Settings.TargetRefreshHz)); return 0;
                case "--restore-display": DisplayGuard.RecoverStale(); Console.WriteLine("Display state checked/restored."); return 0;
                case "--nv-status": return Nv(delegate { return NvDrs.Status(Console.Out); });
                case "--nv-apply": return Nv(delegate { return NvDrs.Apply(ArgUInt(a, "--fps", 60), !a.Contains("--no-vsync"), !a.Contains("--no-gsync-off"), Console.Out); });
                case "--nv-revert": return Nv(delegate { return NvDrs.Revert(Console.Out); });
                case "--fixes-scan": return FixesScan(a.Length > 1 ? a[1] : null);
                case "--fixes-apply": return FixesApply(a);
                case "--loading-status": return LoadingCommand(a, false);
                case "--loading-rescue": return LoadingCommand(a, true);
                case "--audio-devices": return AudioCommand();
                case "--report-bug": return BugReport.Run(a);
                case "--discord-test": return DiscordTest(a.Contains("--send"));
                case "--analyze-dump":
                    {
                        if (a.Length < 2 || !File.Exists(a[1])) { Console.Error.WriteLine("usage: --analyze-dump <file.dmp> [game folder]"); return 2; }
                        DumpAnalyzer.Report r = DumpAnalyzer.Analyze(a[1]);
                        DumpAnalyzer.Classify(r, a.Length > 2 ? a[2] : null);
                        Console.Write(DumpAnalyzer.Format(r, Path.GetFileName(a[1])));
                        return r.Error == null ? 0 : 1;
                    }
                default:
                    Console.Error.WriteLine("Unknown command " + a[0] + ". Commands: --check --backup --restore-backup <gz> <target> [--force] --display-info "
                        + "--restore-display --nv-status --nv-apply [--fps N] [--no-vsync] [--no-gsync-off] --nv-revert --fixes-scan <game folder> "
                        + "--fixes-apply <pid> --loading-status <pid> --loading-rescue <pid> --audio-devices --report-bug [--send ...] "
                        + "--discord-test [--send] --selftest --paths --version");
                    return 2;
            }
        }

        // Lists the in-memory code fixes and whether they match the installed game version.
        static int FixesScan(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { Console.Error.WriteLine("usage: --fixes-scan <game folder>"); return 2; }
            int usable = 0, broken = 0;
            foreach (string exe in Directory.GetFiles(dir, "*.exe").OrderBy(f => f))
            {
                GameFixes.PeInfo pe;
                foreach (GameFixes.Site s in GameFixes.Locate(exe, out pe))
                {
                    if (s.Problem == null) usable++; else broken++;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-45} {1,-24} {2}", Path.GetFileName(exe), s.Fix.Description,
                        s.Problem ?? ("ready (RVA 0x" + (s.Rva + s.Fix.Offset).ToString("X") + ", build " + pe.TimeDateStamp.ToString("X8") + ")")));
                }
                if (Path.GetFileNameWithoutExtension(exe).Equals(LoadingGuard.GameName, StringComparison.OrdinalIgnoreCase))
                {
                    LoadingGuard.Layout l = LoadingGuard.Locate(exe);
                    if (l.Problem == null) usable++; else broken++;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-45} {1,-24} {2}", Path.GetFileName(exe), "Endless loading screen", l.Describe()));
                }
            }
            Console.WriteLine(usable + " fix(es) ready, " + broken + " not matching this game version");
            return broken == 0 && usable > 0 ? 0 : 7;
        }

        static int FixesApply(string[] a)
        {
            int pid;
            if (a.Length < 2 || !int.TryParse(a[1], out pid)) { Console.Error.WriteLine("usage: --fixes-apply <pid>"); return 2; }
            string path = Native.GetImagePath(pid);
            if (path == null) { Console.Error.WriteLine("process " + pid + " not found"); return 1; }
            uint? marker = null;
            int mi = Array.IndexOf(a, "--exit-code");
            if (mi > 0 && mi + 1 < a.Length) marker = Convert.ToUInt32(a[mi + 1], a[mi + 1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10);
            GameFixes.Result r = GameFixes.Apply(pid, path, marker);
            for (int i = 0; r.Outcome == GameFixes.Outcome.Retry && i < 20; i++) { System.Threading.Thread.Sleep(250); r = GameFixes.Apply(pid, path, marker); }
            Console.WriteLine(r.Outcome + ": " + r.Message);
            Log.Info("fixes-apply " + Path.GetFileName(path) + " pid " + pid + ": " + r.Outcome + " - " + r.Message);
            return r.Outcome == GameFixes.Outcome.Applied || r.Outcome == GameFixes.Outcome.AlreadyApplied ? 0 : 1;
        }

        // Shows (and with --loading-rescue releases) a KH1 loading screen stuck on its voice slots.
        static int LoadingCommand(string[] a, bool rescue)
        {
            int pid;
            if (a.Length < 2 || !int.TryParse(a[1], out pid)) { Console.Error.WriteLine("usage: " + a[0] + " <pid of " + LoadingGuard.GameName + ".exe>"); return 2; }
            string path = Native.GetImagePath(pid);
            if (path == null) { Console.Error.WriteLine("process " + pid + " not found"); return 1; }
            if (!Path.GetFileNameWithoutExtension(path).Equals(LoadingGuard.GameName, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("this command only applies to " + LoadingGuard.GameName + ".exe");
                return 2;
            }
            string text;
            int code = LoadingGuard.Inspect(pid, path, rescue, out text);
            Console.WriteLine(text);
            Log.Info(a[0] + " pid " + pid + ": " + text.Replace(Environment.NewLine, " | "));
            return code;
        }

        // Lists the audio outputs. Exit codes: 0 two or more enabled, 4 exactly one (risky), 5 none, 1 error.
        static int AudioCommand()
        {
            string error;
            List<AudioDevices.Endpoint> all = AudioDevices.Render(out error);
            if (all == null) { Console.WriteLine("Audio outputs could not be listed: " + error); return 1; }
            Console.Write(AudioDevices.Describe(all.Where(e => e.State != AudioDevices.NotPresent).ToList()));
            int active = all.Count(e => e.State == AudioDevices.Active);
            Console.WriteLine(active + " audio output(s) enabled.");
            string warning = AudioDevices.SingleOutputWarning(all);
            if (warning != null) Console.WriteLine("WARNING: " + warning);
            return active == 0 ? 5 : active == 1 ? 4 : 0;
        }

        // Checks the configured Discord webhooks without posting; with --send also posts a short test message to each.
        static int DiscordTest(bool send)
        {
            string[] names = { "Bug reports (DiscordBugReportWebhook)", "Crash and session reports (DiscordLogWebhook)" };
            string[] urls = { Settings.DiscordBugReportWebhook, Settings.DiscordLogWebhook };
            int problems = 0, configured = 0;
            for (int i = 0; i < 2; i++)
            {
                if (string.IsNullOrEmpty(urls[i])) { Console.WriteLine(names[i] + ": not set"); continue; }
                configured++;
                string info;
                string problem = Discord.Check(urls[i], 15000, out info);
                if (problem != null) { Console.WriteLine(names[i] + ": " + Discord.Describe(urls[i]) + " - " + problem); problems++; continue; }
                Console.WriteLine(names[i] + ": " + Discord.Describe(urls[i]) + " OK - " + info);
                if (!send) continue;
                // The test posts go through the same code as real reports, so Discord also checks their format.
                string tmp = Path.Combine(Path.GetTempPath(), "KHHotfix-discordtest-" + Guid.NewGuid().ToString("N"));
                Discord.Message m;
                try
                {
                    SystemInfo sys = SystemInfo.Collect(Reporting.GuessGameDir());
                    if (i == 0)
                    {
                        Directory.CreateDirectory(tmp);
                        using (var b = new Bitmap(640, 360))
                        {
                            using (Graphics g = Graphics.FromImage(b))
                            using (var font = new Font("Segoe UI", 26f))
                            {
                                g.Clear(Color.FromArgb(32, 44, 96));
                                g.DrawString("KH Hotfix test screenshot", font, Brushes.White, 40, 150);
                            }
                            File.WriteAllBytes(Path.Combine(tmp, "screenshot.jpg"), Screenshot.Jpeg(b, 640, 85));
                        }
                        m = BugReport.Compose(tmp, new BugReport.Draft
                        {
                            Game = BugReport.Games[0],
                            Category = "Other",
                            Title = "Test - bug reports arrive here",
                            Description = "Test message from KH Hotfix " + Program.Version + ". Bug reports sent with " + Settings.BugReportHotkey
                                + " or Start menu > KH Hotfix > Report a KINGDOM HEARTS bug appear like this. You can delete this post.",
                            Reporter = Reporting.Who(),
                            Snapshot = false
                        }, sys);
                    }
                    else
                        m = LogReport.Compose("Test - crash and session reports arrive here", "notes", null, new[] { "Test message from KH Hotfix " + Program.Version
                            + ". Crash and freeze reports, and notes about sessions in which the hotfix had to step in, appear here automatically. You can delete this post." },
                            null, null, sys);
                    Discord.Result r = Discord.Send(urls[i], m, 30000);
                    Console.WriteLine("  test post: " + (r.Ok ? "sent" + (r.Link != null ? " - " + r.Link : "") : "FAILED - " + r.Error));
                    if (!r.Ok) problems++;
                }
                finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
            }
            Log.Info("discord-test: " + configured + " webhook(s) configured, " + problems + " problem(s)");
            return problems > 0 ? 1 : configured == 0 ? 3 : 0;
        }

        static uint ArgUInt(string[] a, string name, uint def)
        {
            int i = Array.IndexOf(a, name);
            uint v;
            return i >= 0 && i + 1 < a.Length && uint.TryParse(a[i + 1], out v) ? v : def;
        }

        // Exit codes: 0 ok, 1 error, 3 needs administrator rights, 4 no NVIDIA driver/GPU.
        static int Nv(Func<int> f)
        {
            try { return f(); }
            catch (DllNotFoundException) { Console.WriteLine("NVIDIA driver (nvapi64.dll) not found - skipped."); return 4; }
            catch (EntryPointNotFoundException) { Console.WriteLine("NVIDIA driver API not usable - skipped."); return 4; }
            catch (NvException ex)
            {
                Console.WriteLine("NVIDIA: " + ex.Message);
                Log.Warn("NVIDIA: " + ex.Message);
                if (ex.Status == -137) return 3;
                if (ex.Status == -6 || ex.Status == -2) return 4;
                return 1;
            }
        }

        static int ShowPaths()
        {
            Console.WriteLine("Hotfix folder : " + Paths.Root);
            Console.WriteLine("Saves folder  : " + Paths.SavesRoot);
            Console.WriteLine("Documents     : " + Paths.Documents);
            Console.WriteLine("Documents(A)  : " + Paths.DocumentsAnsi + "   <- what the game sees");
            Console.WriteLine("Backups       : " + Paths.Backups);
            Console.WriteLine("Reports       : " + Paths.Reports);
            return 0;
        }

        static int Check()
        {
            List<string> files = SaveGuard.EnumerateSaveFiles();
            Console.WriteLine("Saves folder: " + Paths.SavesRoot + (Directory.Exists(Paths.SavesRoot) ? "" : "  (does not exist yet - start a game once)"));
            int damaged = 0;
            foreach (string f in files)
            {
                long len = new FileInfo(f).Length;
                string status = "-";
                if (SaveGuard.IsPng(f))
                {
                    PngResult r = PngCheck.Check(SaveGuard.ReadShared(f));
                    status = r.Status + " (" + r.Detail + ")";
                    if (r.Status != SaveStatus.Ok) damaged++;
                }
                List<BackupEntry> hist = SaveGuard.ListBackups(SaveGuard.BackupDirFor(f));
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-60} {1,12:N0} bytes  {2}  backups: {3}{4}", Paths.Rel(f), len, status, hist.Count,
                    hist.Count > 0 ? " (newest " + hist[0].LocalTimeText + ")" : ""));
            }
            if (files.Count == 0) Console.WriteLine("  (no save files)");
            return damaged == 0 ? 0 : 5;
        }

        static int RestoreBackup(string[] a)
        {
            if (a.Length < 3) { Console.Error.WriteLine("usage: --restore-backup <backup.gz> <target save file> [--force]"); return 2; }
            string gz = a[1], target = a[2];
            if (!a.Contains("--force") && Process.GetProcesses().Any(p => { try { return p.ProcessName.StartsWith("KINGDOM HEARTS", StringComparison.OrdinalIgnoreCase); } catch { return false; } }))
            {
                Console.Error.WriteLine("KINGDOM HEARTS is running. Quit the game first (or pass --force).");
                return 6;
            }
            byte[] data = SaveGuard.ReadGz(gz);
            if (File.Exists(target))
            {
                string q = SaveGuard.Quarantine(target, SaveGuard.ReadShared(target), "replaced-by-restore");
                Console.WriteLine("Current file kept at: " + q);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            SaveGuard.AtomicWrite(target, data);
            Log.Info("Restored " + target + " from " + gz);
            Console.WriteLine("Restored " + target + " (" + data.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes).");
            return 0;
        }
    }

    internal static class SelfTest
    {
        static int failures;

        static void Expect(bool cond, string what)
        {
            Console.WriteLine((cond ? "  PASS  " : "  FAIL  ") + what);
            if (!cond) failures++;
        }

        const ulong SyntheticBase = 0x00007FF800000000UL;

        // A tiny local stand-in for Discord's webhook API: answers with Reply(request line, payload_json) = "status json"
        // and records what it received (request line, payload_json, "name:size" of each file).
        sealed class FakeDiscord : IDisposable
        {
            readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            public readonly List<string> Requests = new List<string>(), Payloads = new List<string>();
            public readonly List<List<string>> Files = new List<List<string>>();
            public Func<string, string, string> Reply;
            public readonly int Port;

            public FakeDiscord()
            {
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var t = new Thread(Serve);
                t.IsBackground = true;
                t.Start();
            }

            public string Url(string id) { return "http://127.0.0.1:" + Port + "/api/webhooks/" + id + "/test-token"; }

            public int Posts { get { lock (Requests) return Requests.Count(x => x.StartsWith("POST", StringComparison.Ordinal)); } }

            public string LastPayload { get { lock (Requests) return Payloads.Where((p, i) => Requests[i].StartsWith("POST", StringComparison.Ordinal)).LastOrDefault(); } }

            public List<string> LastFiles { get { lock (Requests) return Files.Where((p, i) => Requests[i].StartsWith("POST", StringComparison.Ordinal)).LastOrDefault() ?? new List<string>(); } }

            public void Clear() { lock (Requests) { Requests.Clear(); Payloads.Clear(); Files.Clear(); } }

            void Serve()
            {
                while (true)
                {
                    TcpClient c;
                    try { c = listener.AcceptTcpClient(); }
                    catch { return; }
                    try { using (c) Handle(c.GetStream()); }
                    catch { }
                }
            }

            void Handle(NetworkStream s)
            {
                s.ReadTimeout = 10000;
                var head = new StringBuilder();
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    int b = s.ReadByte();
                    if (b < 0) return;
                    head.Append((char)b);
                }
                string[] lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                string request = lines[0].Substring(0, lines[0].LastIndexOf(' '));
                int length = 0;
                string type = "";
                foreach (string l in lines)
                {
                    if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(l.Substring(15).Trim(), CultureInfo.InvariantCulture);
                    if (l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)) type = l.Substring(13).Trim();
                }
                var body = new byte[length];
                for (int got = 0; got < length; )
                {
                    int n = s.Read(body, got, length - got);
                    if (n <= 0) break;
                    got += n;
                }
                string payload = null;
                var files = new List<string>();
                Match bm = Regex.Match(type, "boundary=(\\S+)");
                if (bm.Success)
                {
                    Encoding latin1 = Encoding.GetEncoding(28591);   // keeps binary parts byte for byte
                    foreach (string part in latin1.GetString(body).Split(new[] { "--" + bm.Groups[1].Value }, StringSplitOptions.None))
                    {
                        int split = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (split < 0) continue;
                        string ph = part.Substring(0, split), pb = part.Substring(split + 4);
                        if (pb.EndsWith("\r\n", StringComparison.Ordinal)) pb = pb.Substring(0, pb.Length - 2);
                        Match fn = Regex.Match(ph, "filename=\"([^\"]*)\"");
                        if (ph.Contains("name=\"payload_json\"")) payload = Encoding.UTF8.GetString(latin1.GetBytes(pb));
                        else if (fn.Success) files.Add(fn.Groups[1].Value + ":" + pb.Length);
                    }
                }
                string reply;
                lock (Requests)
                {
                    Requests.Add(request);
                    Payloads.Add(payload);
                    Files.Add(files);
                    reply = Reply(request, payload);
                }
                int sp = reply.IndexOf(' ');
                byte[] json = Encoding.UTF8.GetBytes(reply.Substring(sp + 1));
                byte[] hdr = Encoding.ASCII.GetBytes("HTTP/1.1 " + reply.Substring(0, sp) + " Fake\r\nContent-Type: application/json\r\nContent-Length: "
                    + json.Length + "\r\nConnection: close\r\n\r\n");
                s.Write(hdr, 0, hdr.Length);
                s.Write(json, 0, json.Length);
            }

            public void Dispose() { try { listener.Stop(); } catch { } }
        }

        static void DiscordTests()
        {
            Console.WriteLine("Discord reporting");
            string token = new string('x', 34) + new string('Y', 34);   // built at runtime: no webhook token in the source
            string hook = "https://discord.com/api/webhooks/123456789012345678/" + token;
            Expect(Discord.IsWebhook(hook) && Discord.IsWebhook(" " + hook + "/ ") && Discord.IsWebhook(hook.Replace("discord.com", "canary.discord.com"))
                && Discord.IsWebhook(hook.Replace("discord.com", "discordapp.com")) && Discord.IsWebhook(hook.Replace("/api/", "/api/v10/")),
                "webhook URLs accepted (discord.com, canary, discordapp, API version)");
            Expect(!Discord.IsWebhook(hook.Replace("https:", "http:")) && !Discord.IsWebhook(hook.Replace("discord.com", "discord.com.evil.example"))
                && !Discord.IsWebhook("https://discord.com/api/webhooks/123456789012345678/short") && !Discord.IsWebhook(hook + "?thread_id=1")
                && !Discord.IsWebhook("") && !Discord.IsWebhook(null), "anything else is rejected (plain http, other hosts, short tokens, extra parameters)");
            Expect(Discord.Describe(hook) == "webhook 123456789012345678" && !Discord.Describe(hook).Contains(token) && Discord.Describe("") == "not set"
                && Discord.Describe("https://example.com/x") == "invalid webhook URL", "logs show the webhook id, never its token");
            Expect(Json.Str("a\"b\\c\nd\u0001\u2028") == "\"a\\\"b\\\\c\\nd\\u0001\\u2028\"", "JSON strings escaped (quotes, backslashes, control characters)");
            const string reply = "{\"code\": 220001, \"message\": \"say \\\"hi\\\"\\n\", \"retry_after\": 0.25}";
            Expect(Json.Find(reply, "code") == "220001" && Json.Find(reply, "message") == "say \"hi\"\n" && Json.Find(reply, "retry_after") == "0.25"
                && Json.Find(reply, "guild_id") == null, "values read from Discord's replies");

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string pc = Environment.MachineName;
            string clean = Reporting.Sanitize("Log " + profile + "\\Documents\\x.log on " + pc + " (C:\\Users\\" + Environment.UserName + "), Steam id 76561198012345678.");
            Expect(clean.Contains("%USERPROFILE%\\Documents") && !clean.Contains("76561198012345678") && clean.Contains("<steamid>")
                && clean.IndexOf("\\Users\\" + Environment.UserName + ")", StringComparison.OrdinalIgnoreCase) < 0
                && (pc.Length < 3 || !Regex.IsMatch(clean, "(?<![A-Za-z0-9])" + Regex.Escape(pc) + "(?![A-Za-z0-9])", RegexOptions.IgnoreCase)),
                "text leaving the PC is cleaned of the user folder, user name, PC name and Steam id");
            Expect(Reporting.ShortGame(BugReport.Games[0]) == "KH1" && Reporting.ShortGame(BugReport.Games[1]) == "Re:CoM" && Reporting.ShortGame(BugReport.Games[2]) == "KH2"
                && Reporting.ShortGame(BugReport.Games[3]) == "BBS" && Reporting.ShortGame(BugReport.Games[4]) == "Days" && Reporting.ShortGame(BugReport.Games[5]) == "Launcher"
                && Reporting.ShortGame("kh2") == "KH2" && Reporting.ShortGame(BugReport.GameTitle("KINGDOM HEARTS II FINAL MIX")) == "KH2", "game short names for post titles");

            var big = new DiscordEmbed { Title = new string('q', 300), Footer = "f" };
            for (int i = 0; i < 30; i++) big.Add("n", new string('q', 1000), true);
            string ej = big.ToJson(new string[0]);
            Expect(big.FieldCount == 25 && ej.Count(c => c == 'q') <= 5900 && ej.Contains("...") && !ej.Contains("\"image\""),
                "embeds stay within Discord's limits (25 fields, 6000 characters)");

            string stateBackup = Paths.State, tagsBackup = Settings.DiscordBugReportTags;
            int delayBackup = Discord.ServerErrorDelayMs;
            string tmp = Path.Combine(Path.GetTempPath(), "KHHotfix-discordtest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Paths.State = Path.Combine(tmp, "State");
                Discord.ServerErrorDelayMs = 50;
                using (var fake = new FakeDiscord())
                {
                    // webhook 1 posts into a forum channel (a post needs a title), webhook 2 into a text channel (no titles allowed)
                    var script = new Queue<string>();
                    fake.Reply = delegate(string req, string payload)
                    {
                        if (req.StartsWith("GET", StringComparison.Ordinal)) return "200 {\"id\":\"1\",\"name\":\"KH bugs\",\"channel_id\":\"500\",\"guild_id\":\"700\",\"token\":\"secret-token\"}";
                        if (script.Count > 0) return script.Dequeue();
                        bool titled = payload != null && payload.Contains("\"thread_name\"");
                        if (req.Contains("/webhooks/1/") && !titled) return "400 {\"code\": 220001, \"message\": \"Webhooks posted to forum channels must have a thread_name or thread_id\"}";
                        if (req.Contains("/webhooks/2/") && titled) return "400 {\"code\": 220003, \"message\": \"Webhooks can only create threads in forum channels\"}";
                        return "200 {\"id\":\"900\",\"channel_id\":\"" + (titled ? "601" : "500") + "\"}";
                    };
                    Func<Discord.Message> message = delegate
                    {
                        var m = new Discord.Message { Username = "Sora @everyone discord", ThreadName = "[KH1] Test", Content = "Hello <@123> @everyone" };
                        m.Files.Add(new Discord.Attachment("summary.txt", Reporting.Utf8("summary"), false));
                        m.Files.Add(new Discord.Attachment("big dump.dmp.gz", new byte[3000], true));
                        return m;
                    };

                    Discord.Result r = Discord.Send(fake.Url("1"), message(), 10000);
                    string p = fake.LastPayload ?? "";
                    Expect(r.Ok && fake.Posts == 1 && p.Contains("\"thread_name\":\"[KH1] Test\"") && r.Link == "https://discord.com/channels/700/601",
                        "forum channel: the report becomes a titled forum post, with a link to it");
                    Expect(p.Contains("\"allowed_mentions\":{\"parse\":[]}") && p.Contains("\"username\":\"Sora everyone\"")
                        && fake.LastFiles.SequenceEqual(new[] { "summary.txt:7", "big_dump.dmp.gz:3000" }), "never pings anyone, safe sender name, files arrive intact");
                    string info;
                    Expect(Discord.Check(fake.Url("1"), 5000, out info) == null && info.Contains("KH bugs") && info.Contains("forum") && !info.Contains("secret"),
                        "webhook check: name and channel type, without the token");

                    fake.Clear();
                    r = Discord.Send(fake.Url("2"), message(), 10000);
                    Expect(r.Ok && fake.Posts == 2 && !(fake.LastPayload ?? "").Contains("thread_name") && r.Link == "https://discord.com/channels/700/500",
                        "text channel: detected automatically and sent as a normal message");
                    fake.Clear();
                    r = Discord.Send(fake.Url("2"), message(), 10000);
                    Expect(r.Ok && fake.Posts == 1 && File.ReadAllText(Path.Combine(Paths.State, "discord-2.txt")).Contains("kind=text"), "channel type remembered");

                    fake.Clear();
                    script.Enqueue("429 {\"message\": \"You are being rate limited.\", \"retry_after\": 0.3, \"global\": false}");
                    r = Discord.Send(fake.Url("1"), message(), 10000);
                    Expect(r.Ok && fake.Posts == 2, "rate limit: waits retry_after and sends again");
                    fake.Clear();
                    script.Enqueue("502 {\"message\": \"Bad Gateway\"}");
                    r = Discord.Send(fake.Url("1"), message(), 10000);
                    Expect(r.Ok && fake.Posts == 2, "Discord server error: tried again");
                    fake.Clear();
                    script.Enqueue("413 {\"message\": \"Request entity too large\", \"code\": 40005}");
                    r = Discord.Send(fake.Url("1"), message(), 10000);
                    Expect(r.Ok && fake.Posts == 2 && r.Dropped.SequenceEqual(new[] { "big_dump.dmp.gz" }) && fake.LastFiles.SequenceEqual(new[] { "summary.txt:7" }),
                        "too large: the optional dump is left out and the rest is sent");
                    fake.Clear();
                    Discord.Message huge = message();
                    huge.Files.Add(new Discord.Attachment("huge.dmp.gz", new byte[Discord.MaxUploadBytes], true));
                    r = Discord.Send(fake.Url("1"), huge, 10000);
                    Expect(r.Ok && fake.Posts == 1 && r.Dropped.SequenceEqual(new[] { "huge.dmp.gz" }) && fake.LastFiles.Count == 2,
                        "upload limit: the largest optional file is left out before sending");

                    fake.Clear();
                    script.Enqueue("404 {\"message\": \"Unknown Webhook\", \"code\": 10015}");
                    r = Discord.Send(fake.Url("1"), message(), 10000);
                    Expect(!r.Ok && fake.Posts == 1 && (r.Error ?? "").Contains("does not exist"), "deleted webhook: clear error, no retries");
                    fake.Clear();
                    script.Enqueue("400 {\"message\": \"A tag is required to create a forum post in this channel\", \"code\": 40067}");
                    r = Discord.Send(fake.Url("1"), message(), 10000);
                    Expect(!r.Ok && fake.Posts == 1 && (r.Error ?? "").Contains("DiscordBugReportTags"), "forum requiring tags: explains how to fix it");
                    fake.Clear();
                    r = Discord.Send("https://example.com/api/webhooks/1/abc", message(), 1000);
                    Expect(!r.Ok && fake.Posts == 0 && (r.Error ?? "").Contains("not valid"), "invalid webhook URL: nothing is sent");
                }
                var closed = new TcpListener(IPAddress.Loopback, 0);
                closed.Start();
                int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
                closed.Stop();
                Discord.Result off = Discord.Send("http://127.0.0.1:" + closedPort + "/api/webhooks/3/test-token", new Discord.Message { Content = "x" }, 5000);
                Expect(!off.Ok && (off.Error ?? "").StartsWith("no connection to Discord", StringComparison.Ordinal), "offline: reported as no connection");

                string bug = Path.Combine(tmp, "bug-20260101-120000");
                Directory.CreateDirectory(bug);
                string log = Path.Combine(tmp, "session-test.log");
                File.WriteAllText(log, "Starting \"" + profile + "\\Games\\KH\\KINGDOM HEARTS HD 1.5+2.5 ReMIX.exe\"\r\n");
                File.WriteAllLines(Path.Combine(bug, "meta.txt"), new[] { "Game=" + BugReport.Games[0], "GameResponding=no", "SessionLog=" + log });
                File.WriteAllText(Path.Combine(bug, "game-state.txt"), "Sound output       : FAILED - the audio device disappeared\r\n");
                using (var bmp = new Bitmap(64, 36)) File.WriteAllBytes(Path.Combine(bug, "screenshot.jpg"), Screenshot.Jpeg(bmp, 32, 80));
                File.WriteAllBytes(Path.Combine(bug, "snapshot.dmp"), new byte[5000]);
                Settings.DiscordBugReportTags = "1234567890123, nonsense";
                var draft = new BugReport.Draft { Category = BugReport.Categories[3], Title = "No sound after a cutscene", Description = new string('d', 2500), Reporter = "Kairi" };
                Discord.Message bm = BugReport.Compose(bug, draft, new SystemInfo());
                List<string> names = bm.Files.Select(f => f.Name).ToList();
                string bj = bm.Embed.ToJson(names);
                Expect(bm.ThreadName == "[KH1] No sound after a cutscene" && bm.Username == "Kairi (KH Hotfix)" && bm.Tags.SequenceEqual(new[] { "1234567890123" }),
                    "bug report: post title with the game, reporter as sender, forum tag");
                Expect(names.Contains("description.txt") && bm.Content.Length <= 2000 && names.Contains("screenshot.jpg") && bj.Contains("attachment://screenshot.jpg")
                    && names.Contains("system.txt") && names.Contains("session.log") && names.Contains("game-snapshot.dmp.gz"),
                    "bug report: long text, screenshot (shown in the post), system info, log and snapshot attached");
                Expect(bj.Contains("not responding") && bj.Contains("\"Sound\""), "bug report: frozen window and lost sound highlighted");
                string sentLog = Encoding.UTF8.GetString(bm.Files.First(f => f.Name == "session.log").Data);
                Expect(sentLog.Contains("%USERPROFILE%\\Games") && sentLog.IndexOf(profile, StringComparison.OrdinalIgnoreCase) < 0, "bug report: attached log cleaned of the user folder");
                draft.Screenshot = draft.Snapshot = draft.Logs = false;
                draft.Description = "short";
                bm = BugReport.Compose(bug, draft, new SystemInfo());
                Expect(bm.Files.Count == 0 && bm.Embed.ImageFile == null && bm.Content == "short", "bug report: unticked attachments are left out");

                string crash = Path.Combine(tmp, "20260101-120500");
                Directory.CreateDirectory(crash);
                File.WriteAllText(Path.Combine(crash, "summary.txt"), "Signature: KINGDOM HEARTS FINAL MIX.exe+0x1234 | access violation\r\nKnown issue: the Quit Game crash\r\n");
                File.WriteAllBytes(Path.Combine(crash, "KINGDOM HEARTS FINAL MIX - frozen.dmp"), Enumerable.Range(0, 4000).Select(i => (byte)i).ToArray());
                Discord.Message lm = LogReport.Compose("KINGDOM HEARTS FINAL MIX crashed (0xC0000005 access violation)", "crash", crash,
                    new[] { "Saved in " + profile + "\\Documents." }, log, BugReport.Games[0], new SystemInfo());
                List<string> lnames = lm.Files.Select(f => f.Name).ToList();
                string lj = lm.Embed.ToJson(lnames);
                Expect(lm.ThreadName.StartsWith("[KH1] KINGDOM HEARTS FINAL MIX crashed", StringComparison.Ordinal) && lj.Contains("Crash report")
                    && lj.Contains("KINGDOM HEARTS FINAL MIX.exe+0x1234") && lj.Contains("Quit Game crash"), "crash report: titled post with the crash signature and known issue");
                byte[] packed = lm.Files.Where(f => f.Name == "KINGDOM_HEARTS_FINAL_MIX_-_frozen.dmp.gz").Select(f => f.Data).FirstOrDefault();
                byte[] unpacked = null;
                if (packed != null)
                    using (var gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress))
                    using (var ms = new MemoryStream()) { gz.CopyTo(ms); unpacked = ms.ToArray(); }
                Expect(lnames.Contains("summary.txt") && lnames.Contains("session.log") && lnames.Contains("system.txt") && unpacked != null
                    && unpacked.SequenceEqual(Enumerable.Range(0, 4000).Select(i => (byte)i)) && lm.Content.Contains("%USERPROFILE%\\Documents"),
                    "crash report: summary, log, system info and the packed dump attached; notices cleaned");
                lm = LogReport.Compose("The game deleted Steam\\76561198012345678\\KHFM_WW.png at 21:41", "notes", null, new string[0], null, null, null);
                Expect(lm.ThreadName.StartsWith("[KH] The game deleted Steam\\<steamid>\\KHFM_WW.png", StringComparison.Ordinal) && !lm.Content.Contains("76561198012345678")
                    && lm.Files.Count == 0 && lm.Embed.ToJson(new string[0]).Contains("Session report"), "session report: title cleaned of the Steam id, nothing attached that does not exist");
            }
            finally
            {
                Paths.State = stateBackup;
                Settings.DiscordBugReportTags = tagsBackup;
                Discord.ServerErrorDelayMs = delayBackup;
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        sealed class FakeMemory : LoadingGuard.IMemory
        {
            public readonly Dictionary<long, byte[]> Regions = new Dictionary<long, byte[]>();
            public int Writes;

            byte[] Find(long address, int length, out int offset)
            {
                foreach (KeyValuePair<long, byte[]> r in Regions)
                {
                    if (address >= r.Key && address + length <= r.Key + r.Value.Length) { offset = (int)(address - r.Key); return r.Value; }
                }
                offset = 0;
                return null;
            }

            public byte[] Read(long address, int length)
            {
                int offset;
                byte[] region = Find(address, length, out offset);
                return region == null ? null : region.Skip(offset).Take(length).ToArray();
            }

            public bool Write(long address, byte[] data)
            {
                int offset;
                byte[] region = Find(address, data.Length, out offset);
                if (region == null) return false;
                data.CopyTo(region, offset);
                Writes++;
                return true;
            }
        }

        // A tiny 64-bit PE whose .text holds the code shapes the loading guard looks for, wired up like the real game.
        static byte[] MakeLoadingImage(bool ambiguous, bool brokenChain, bool foreignAudioSetter = false)
        {
            const int Raw0 = 0x400, Rva0 = 0x1000, TextSize = 0x1000, Pe = 0x80, Opt = Pe + 24, OptSize = 0xF0, Sec = Opt + OptSize;
            var f = new byte[Raw0 + TextSize];
            for (int i = Raw0; i < f.Length; i++) f[i] = 0xCC;
            f[0] = (byte)'M'; f[1] = (byte)'Z';
            BitConverter.GetBytes(Pe).CopyTo(f, 0x3C);
            BitConverter.GetBytes(0x00004550u).CopyTo(f, Pe);
            BitConverter.GetBytes((ushort)1).CopyTo(f, Pe + 6);
            BitConverter.GetBytes(0x669E37CEu).CopyTo(f, Pe + 8);
            BitConverter.GetBytes((ushort)OptSize).CopyTo(f, Pe + 20);
            BitConverter.GetBytes((ushort)0x20B).CopyTo(f, Opt);
            BitConverter.GetBytes(0x100000u).CopyTo(f, Opt + 56);
            System.Text.Encoding.ASCII.GetBytes(".text").CopyTo(f, Sec);
            BitConverter.GetBytes((uint)TextSize).CopyTo(f, Sec + 8);
            BitConverter.GetBytes((uint)Rva0).CopyTo(f, Sec + 12);
            BitConverter.GetBytes((uint)TextSize).CopyTo(f, Sec + 16);
            BitConverter.GetBytes((uint)Raw0).CopyTo(f, Sec + 20);
            Action<int, string> put = delegate(int rva, string sig)
            {
                int o = rva - Rva0 + Raw0;
                foreach (string tok in sig.Split(' ')) f[o++] = tok == "??" ? (byte)0 : byte.Parse(tok, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            };
            Action<int, int> rel = delegate(int dispRva, int target) { BitConverter.GetBytes(target - (dispRva + 4)).CopyTo(f, dispRva - Rva0 + Raw0); };
            put(0x1000, LoadingGuard.SigVoiceBusy); rel(0x1003, 0x50000); rel(0x100A, 0x500A0);
            put(0x1100, LoadingGuard.SigBankTask.Replace("74 ??", "74 30"));                    // step 0 code at 0x1141
            put(0x1141, LoadingGuard.SigBankWait); rel(0x1144, 0x1200);
            put(0x1200, LoadingGuard.SigVoiceWait); rel(0x1212, brokenChain ? 0x1100 : 0x1000);
            put(0x1300, LoadingGuard.SigBankRequest); rel(0x1306, 0x1100); rel(0x130F, 0x60000);
            put(0x1340, LoadingGuard.SigBankRequest); rel(0x1346, 0x1100); rel(0x134F, ambiguous ? 0x61000 : 0x60000);
            put(0x1400, LoadingGuard.SigLoadPending); rel(0x1407, 0x70000); rel(0x141F, 0x70000);
            // mov byte [flag], imm8 has one byte after the displacement
            Action<int, int> rel8 = delegate(int dispRva, int target) { BitConverter.GetBytes(target - (dispRva + 5)).CopyTo(f, dispRva - Rva0 + Raw0); };
            put(0x1500, LoadingGuard.SigAudioInit); rel8(0x1513, 0x80001);
            put(0x1600, LoadingGuard.SigAudioFailedSetter); rel8(0x1602, foreignAudioSetter ? 0x80002 : 0x80001);
            put(0x1700, LoadingGuard.SigVoiceLookup); rel(0x170D, 0x81000); rel(0x1714, 0x81100); rel(0x1724, 0x81200); rel(0x172B, 0x81300);
            put(0x1780, LoadingGuard.SigVoiceIsPlaying);
            put(0x17A0, LoadingGuard.SigVoiceFinish);
            return f;
        }

        // Minimal minidump: exception (access violation reading 0x1234) in kernel32's code, and a stack holding
        // junk plus one genuine return address (the instruction after a call in kernel32's .text).
        static string WriteSyntheticDump(string dir, bool withException, out ulong rip, out ulong ret)
        {
            string k32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
            byte[] f = File.ReadAllBytes(k32);
            GameFixes.PeInfo pe = GameFixes.ReadPe(f);
            int call = -1;
            for (int i = pe.TextRaw + 0x2000; i < pe.TextRaw + pe.TextRawSize - 16; i++) if (f[i] == 0xE8) { call = i; break; }
            rip = SyntheticBase + (ulong)pe.TextRva + 0x100;
            ret = SyntheticBase + (ulong)(call + 5 - pe.TextRaw + pe.TextRva);
            const ulong rsp = 0x000000AB00001000UL;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(0x504D444Du); w.Write(0xA793u); w.Write(withException ? 3u : 2u); w.Write(32u); w.Write(0u); w.Write(0u); w.Write(0UL);
            long dirPos = ms.Position;
            w.Write(new byte[3 * 12]);
            long ctxPos = ms.Position;
            var ctx = new byte[0x4D0];
            BitConverter.GetBytes(rsp).CopyTo(ctx, 0x98);
            BitConverter.GetBytes(rip).CopyTo(ctx, 0xF8);
            w.Write(ctx);
            long excPos = ms.Position;
            w.Write(1u); w.Write(0u); w.Write(0xC0000005u); w.Write(0u); w.Write(0UL); w.Write(rip); w.Write(2u); w.Write(0u);
            w.Write(0UL); w.Write(0x1234UL);
            for (int i = 2; i < 15; i++) w.Write(0UL);
            w.Write(0x4D0u); w.Write((uint)ctxPos);
            long namePos = ms.Position;
            byte[] name = System.Text.Encoding.Unicode.GetBytes(k32);
            w.Write((uint)name.Length); w.Write(name); w.Write((ushort)0);
            long modPos = ms.Position;
            w.Write(1u); w.Write(SyntheticBase); w.Write(pe.SizeOfImage); w.Write(0u); w.Write(pe.TimeDateStamp); w.Write((uint)namePos); w.Write(new byte[108 - 24]);
            long stackPos = ms.Position;
            w.Write(0x1111UL); w.Write(ret); w.Write(0x2222UL);
            long thrPos = ms.Position;
            w.Write(1u); w.Write(1u); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0UL); w.Write(rsp); w.Write(24u); w.Write((uint)stackPos); w.Write(0x4D0u); w.Write((uint)ctxPos);
            long end = ms.Position;
            ms.Position = dirPos;
            if (withException) { w.Write(6u); w.Write(168u); w.Write((uint)excPos); }
            w.Write(4u); w.Write((uint)(stackPos - modPos)); w.Write((uint)modPos);
            w.Write(3u); w.Write((uint)(end - thrPos)); w.Write((uint)thrPos);
            string path = Path.Combine(dir, withException ? "crash.dmp" : "freeze.dmp");
            File.WriteAllBytes(path, ms.ToArray());
            return path;
        }

        public static byte[] MakePng(int trailing)
        {
            using (var bmp = new Bitmap(16, 16))
            using (var ms = new MemoryStream())
            {
                for (int x = 0; x < 16; x++) bmp.SetPixel(x, x, Color.FromArgb(255, x * 15, 0, 255 - x * 15));
                bmp.Save(ms, ImageFormat.Png);
                byte[] png = ms.ToArray();
                if (trailing <= 0) return png;
                var withData = new byte[png.Length + trailing];
                png.CopyTo(withData, 0);
                for (int i = 0; i < trailing; i++) withData[png.Length + i] = (byte)(i * 7);
                return withData;
            }
        }

        public static int Run()
        {
            failures = 0;
            Console.WriteLine("PNG validator");
            Expect(PngCheck.Crc(System.Text.Encoding.ASCII.GetBytes("IEND"), 0, 4) == 0xAE426082, "CRC32(\"IEND\") = AE426082");
            byte[] png = MakePng(0);
            Expect(PngCheck.Check(png).Status == SaveStatus.Ok, "valid PNG is Ok");
            Expect(PngCheck.Check(MakePng(5000)).Status == SaveStatus.Ok && PngCheck.Check(MakePng(5000)).Trailing == 5000, "PNG with trailing data is Ok and trailing bytes are counted");
            Expect(PngCheck.Check(png.Take(png.Length - 5).ToArray()).Status == SaveStatus.Truncated, "cut inside IEND is Truncated");
            Expect(PngCheck.Check(png.Take(png.Length - 12).ToArray()).Status == SaveStatus.Truncated, "missing IEND is Truncated");
            Expect(PngCheck.Check(png.Take(png.Length / 2).ToArray()).Status == SaveStatus.Truncated, "half file is Truncated");
            byte[] flipped = (byte[])png.Clone();
            flipped[40] ^= 0xFF;
            Expect(PngCheck.Check(flipped).Status == SaveStatus.BadCrc, "flipped byte gives BadCrc");
            Expect(PngCheck.Check(new byte[0]).Status == SaveStatus.Empty, "0 bytes is Empty");
            Expect(PngCheck.Check(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }).Status == SaveStatus.NotPng, "random bytes are NotPng");

            Console.WriteLine("Command line passthrough");
            const string cmd = "\"C:\\a b\\KHHotfix.exe\" \"D:\\x y\\KINGDOM HEARTS HD 1.5+2.5 ReMIX.exe\" -foo \"bar baz\"";
            Expect(CommandLine.Remainder(cmd, 1) == "\"D:\\x y\\KINGDOM HEARTS HD 1.5+2.5 ReMIX.exe\" -foo \"bar baz\"", "skip 1 token");
            Expect(CommandLine.Remainder(cmd, 2) == "-foo \"bar baz\"", "skip 2 tokens keeps game arguments intact");
            Expect(CommandLine.Remainder("x.exe \"D:\\g.exe\"", 2) == "", "no game arguments");

            Console.WriteLine("Crash code classification");
            Expect(CrashCodes.IsCrash(0xC0000005) && CrashCodes.IsCrash(0x887A0005) && CrashCodes.IsCrash(0xE0434352), "crash codes detected");
            Expect(!CrashCodes.IsCrash(0) && !CrashCodes.IsCrash(1) && !CrashCodes.IsCrash(CrashCodes.KilledByHotfix), "normal exit codes ignored");

            Console.WriteLine("In-memory code fixes");
            int off, len;
            byte?[] pat = GameFixes.ParsePattern("AA ?? | BB CC | DD", out off, out len);
            Expect(pat.Length == 5 && !pat[1].HasValue && off == 2 && len == 2, "signature parsing (wildcards, patch region)");
            byte[] hay = { 1, 0xAA, 9, 0xBB, 0xCC, 0xDD, 0xAA, 7, 0xBB, 0xCC, 0xDE };
            List<int> found = GameFixes.FindAll(hay, 0, hay.Length, pat);
            Expect(found.Count == 1 && found[0] == 1, "signature scan finds the single exact match");
            byte[] patch = GameFixes.BuildQuitPatch(0xDA, 0x00007FF912345678UL, 25);
            Expect(patch.Length == 25 && patch[0] == 0x89 && patch[1] == 0xDA && patch[6] == 0x48 && patch[7] == 0xB8
                && BitConverter.ToUInt64(patch, 8) == 0x00007FF912345678UL && patch[16] == 0xFF && patch[17] == 0xD0 && patch.Skip(18).All(b => b == 0xCC),
                "quit patch encodes mov edx,ebx / or rcx,-1 / mov rax,imm64 / call rax");
            Expect(GameFixes.Catalog.Length == 5 && GameFixes.Catalog.All(f => f.Length >= 18 && f.Offset + f.Length <= f.Pattern.Length && f.Pattern[0].HasValue),
                "every catalogued fix has room for the patch");
            GameFixes.PeInfo me = GameFixes.ReadPe(File.ReadAllBytes(System.Windows.Forms.Application.ExecutablePath));
            Expect(me != null && me.TextRawSize > 0 && me.SizeOfImage > 0, "PE reader finds the .text section");

            Console.WriteLine("Endless loading screen guard: locating the game data");
            LoadingGuard.Layout lay = LoadingGuard.Locate(MakeLoadingImage(false, false));
            Expect(lay.Problem == null && lay.VoiceSlots == 0x50000 && lay.BankScheduler == 0x60000 && lay.BankTask == 0x1100 && lay.LoadPending == 0x70000
                && lay.TimeDateStamp == 0x669E37CE, "voice slots, sound-bank task list, task function and load flag resolved from rip-relative code");
            Expect(lay.AudioFailed == 0x80001, "audio-failure flag found through the sound init code and its single OnCriticalError setter");
            Expect(lay.VoiceLink == 0x81000 && lay.VoiceList == 0x81100 && lay.VoiceStride == 0x81200 && lay.VoicePool == 0x81300,
                "voice pool globals resolved from the voice lookup code (with the playing test and the game's own 'finished' write)");
            lay = LoadingGuard.Locate(MakeLoadingImage(false, false, true));
            Expect(lay.Problem == null && lay.AudioFailed == 0 && lay.VoicePool == 0, "audio flag without a matching OnCriticalError setter is not trusted (guard still works)");
            lay = LoadingGuard.Locate(MakeLoadingImage(true, false));
            Expect(lay.Problem != null && lay.Problem.Contains("ambiguous"), "request code using different task lists is rejected");
            lay = LoadingGuard.Locate(MakeLoadingImage(false, true));
            Expect(lay.Problem != null && lay.Problem.Contains("does not wait"), "a task that does not wait for the voice slots is rejected");
            Expect(LoadingGuard.Locate(File.ReadAllBytes(System.Windows.Forms.Application.ExecutablePath)).Problem != null, "a different program is rejected");

            Console.WriteLine("Endless loading screen guard: decisions (simulated game memory)");
            {
                const long imageBase = 0x0000000140000000L, taskAddr = 0x0000000200000000L;
                var layout = new LoadingGuard.Layout { VoiceSlots = 0x1000, BankScheduler = 0x2000, BankTask = 0x3000, LoadPending = 0x4000 };
                var mem = new FakeMemory();
                byte[] image = new byte[0x5000], task = new byte[0x38];
                mem.Regions[imageBase] = image;
                mem.Regions[taskAddr] = task;
                BitConverter.GetBytes(taskAddr).CopyTo(image, 0x2010);           // first task in the sound-bank list ...
                BitConverter.GetBytes(imageBase + 0x3000).CopyTo(task, 0x10);    // ... is a sound-bank task at step 0
                BitConverter.GetBytes(0x7000UL).CopyTo(task, 0x20);
                image[0x1000] = 4; image[0x1003] = 0x21;                         // voice slot 0 "playing" bank 0 sound 0x21
                var core = new LoadingGuard.Core(mem, imageBase, layout) { RescueSeconds = 10 };
                int hangs = 0;
                core.UnknownHang = delegate { hangs++; };
                DateTime t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                for (int s = 0; s <= 300; s++) core.Tick(t0.AddSeconds(s));
                Expect(image[0x1000] == 4 && mem.Writes == 0 && hangs == 0, "no area load pending: nothing is touched, even with a busy slot and a waiting request");
                image[0x4000] = 1;
                DateTime t1 = t0.AddSeconds(400);
                for (int s = 0; s < 10; s++) core.Tick(t1.AddSeconds(s));
                Expect(image[0x1000] == 4 && mem.Writes == 0, "a load waiting less than LoadingRescueSeconds is left alone");
                core.Tick(t1.AddSeconds(10));
                Expect(image[0x1000] == 0 && image[0x1003] == 0x21 && mem.Writes == 1 && core.Rescues == 1, "a load stuck on a busy voice slot is released (only the state byte changes)");
                image[0x1000] = 4; image[0x1028] = 2;
                BitConverter.GetBytes((short)1).CopyTo(task, 0x1A);
                for (int s = 11; s < 40; s++) core.Tick(t1.AddSeconds(s));
                Expect(mem.Writes == 1, "a request that no longer waits for the voices is never touched");
                BitConverter.GetBytes((short)0).CopyTo(task, 0x1A);
                for (int s = 40; s < 60; s++) { BitConverter.GetBytes(0x7000UL + (ulong)s).CopyTo(task, 0x20); core.Tick(t1.AddSeconds(s)); }
                Expect(mem.Writes == 1, "a load that keeps making progress (new requests every second) is never touched");
                for (int s = 60; s <= 70; s++) core.Tick(t1.AddSeconds(s));
                Expect(image[0x1000] == 0 && image[0x1028] == 0 && core.Rescues == 2, "every busy slot is released");
                int writes = mem.Writes;
                for (int s = 71; s < 69 + LoadingGuard.UnknownHangSeconds; s++) core.Tick(t1.AddSeconds(s));
                Expect(hangs == 0, "no unknown-hang report before " + LoadingGuard.UnknownHangSeconds + " s");
                for (int s = 69 + LoadingGuard.UnknownHangSeconds; s <= 100 + LoadingGuard.UnknownHangSeconds; s++) core.Tick(t1.AddSeconds(s));
                Expect(hangs == 1 && mem.Writes == writes, "a load stuck for another reason is reported once and nothing is written");
                image[0x4000] = 0;
                core.Tick(t1.AddSeconds(500));
                image[0x4000] = 1;
                for (int s = 501; s < 505; s++) core.Tick(t1.AddSeconds(s));
                Expect(hangs == 1, "a new load starts with fresh timers");
            }

            Console.WriteLine("Endless loading screen guard: failed sound output (simulated game memory)");
            {
                const long imageBase = 0x0000000140000000L, taskAddr = 0x0000000200000000L;
                var layout = new LoadingGuard.Layout { VoiceSlots = 0x1000, BankScheduler = 0x2000, BankTask = 0x3000, LoadPending = 0x4000, AudioFailed = 0x4800 };
                Func<int, LoadingGuard.Core> setup = null;
                byte[] image = null, task = null;
                FakeMemory mem = null;
                int lost = 0;
                setup = delegate(int rescueSeconds)
                {
                    mem = new FakeMemory();
                    image = new byte[0x5000]; task = new byte[0x38];
                    mem.Regions[imageBase] = image;
                    mem.Regions[taskAddr] = task;
                    BitConverter.GetBytes(taskAddr).CopyTo(image, 0x2010);
                    BitConverter.GetBytes(imageBase + 0x3000).CopyTo(task, 0x10);
                    image[0x1000] = 4; image[0x1003] = 0x21;
                    lost = 0;
                    var c = new LoadingGuard.Core(mem, imageBase, layout) { RescueSeconds = rescueSeconds };
                    c.AudioLost = delegate { lost++; };
                    return c;
                };
                DateTime t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                LoadingGuard.Core core = setup(10);
                core.Tick(t0);
                Expect(lost == 0 && !core.AudioFailed(), "working sound output: nothing reported");
                image[0x4800] = 1;
                core.Tick(t0.AddSeconds(1));
                core.Tick(t0.AddSeconds(2));
                Expect(lost == 1 && core.AudioFailed() && mem.Writes == 0, "failed sound output is reported once, even without a loading screen, and nothing is written");
                image[0x4000] = 1;
                core.Tick(t0.AddSeconds(2.25));
                Expect(image[0x1000] == 0 && mem.Writes == 1 && core.Rescues == 1, "with failed sound output a stuck load is released on the first check (no waiting)");
                core = setup(0);
                image[0x4800] = 1; image[0x4000] = 1;
                for (int s = 0; s < 60; s++) core.Tick(t0.AddSeconds(s));
                Expect(lost == 1 && mem.Writes == 0 && image[0x1000] == 4, "LoadingRescueSeconds=0 only reports and never changes the game");
            }

            Console.WriteLine("Failed sound output: sounds that can never finish (simulated game memory)");
            {
                const long imageBase = 0x0000000140000000L, poolAddr = 0x0000000300000000L, link = 0x100;
                const int stride = 0x200;
                var layout = new LoadingGuard.Layout { VoiceSlots = 0x1000, BankScheduler = 0x2000, BankTask = 0x3000, LoadPending = 0x4000, AudioFailed = 0x4800,
                    VoicePool = 0x4810, VoiceStride = 0x4818, VoiceLink = 0x4820, VoiceList = 0x4830 };
                var mem = new FakeMemory();
                byte[] image = new byte[0x5000], pool = new byte[LoadingGuard.VoicePoolSize * stride];
                mem.Regions[imageBase] = image;
                mem.Regions[poolAddr] = pool;
                BitConverter.GetBytes(poolAddr).CopyTo(image, 0x4810);
                BitConverter.GetBytes((long)stride).CopyTo(image, 0x4818);
                BitConverter.GetBytes(link).CopyTo(image, 0x4820);
                Action<int, uint, long, uint> voice = delegate(int index, uint handle, long member, uint status)
                {
                    BitConverter.GetBytes(handle).CopyTo(pool, index * stride + LoadingGuard.VoiceHandleOffset);
                    BitConverter.GetBytes(member).CopyTo(pool, index * stride + (int)link + 0x10);
                    BitConverter.GetBytes(status).CopyTo(pool, index * stride + LoadingGuard.VoiceStatusOffset);
                };
                Func<int, uint> statusOf = delegate(int index) { return BitConverter.ToUInt32(pool, index * stride + LoadingGuard.VoiceStatusOffset); };
                long list = imageBase + 0x4830;
                voice(3, 0x1203, list, 4);         // playing forever
                voice(7, 0x0807, list + 8, 4);     // not in the active list
                voice(9, 0x0009, list, 0xC);       // already finished
                voice(11, 0x020B, list, 4);        // finishes by itself
                voice(12, 0x000D, list, 4);        // handle of another pool entry (stale)
                var core = new LoadingGuard.Core(mem, imageBase, layout) { RescueSeconds = 10, StuckSoundSeconds = 5 };
                DateTime t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                for (int s = 0; s <= 8; s++) core.Tick(t0.AddSeconds(s));
                Expect(mem.Writes == 0 && statusOf(3) == 4, "working sound output: no sound is ever finished by the hotfix");
                image[0x4800] = 1;
                DateTime t1 = t0.AddSeconds(20);
                for (int s = 0; s <= 4; s++) core.Tick(t1.AddSeconds(s));
                Expect(statusOf(3) == 4 && mem.Writes == 0, "after a sound failure a sound is left alone for StuckSoundSeconds");
                BitConverter.GetBytes(0u).CopyTo(pool, 11 * stride + LoadingGuard.VoiceStatusOffset);
                for (int s = 5; s <= 7; s++) core.Tick(t1.AddSeconds(s));
                Expect(statusOf(3) == LoadingGuard.VoiceFinished && core.SoundsFinished == 1, "a sound stuck after the sound failure is marked finished (the game's own end state)");
                Expect(statusOf(7) == 4 && statusOf(9) == 0xC && statusOf(11) == 0 && statusOf(12) == 4 && mem.Writes == 1,
                    "voices outside the active list, already finished, finished by the game or with a stale handle are never touched");
            }

            Console.WriteLine("Crash dump analysis (synthetic dump)");
            string dumpDir = Path.Combine(Path.GetTempPath(), "KHHotfix-dumptest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dumpDir);
                ulong rip, ret;
                string dmp = WriteSyntheticDump(dumpDir, true, out rip, out ret);
                DumpAnalyzer.Report rep = DumpAnalyzer.Analyze(dmp);
                Expect(rep.Error == null && rep.ExceptionCode == 0xC0000005 && rep.AccessType == 0 && rep.FaultAddress == 0x1234, "exception code, access type and fault address read");
                Expect(rep.Frames.Count == 2 && rep.Frames[0].Rva == (uint)(rip - SyntheticBase) && rep.Frames[1].Rva == (uint)(ret - SyntheticBase)
                    && rep.Frames[1].Module.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase), "faulting instruction and the real return address found, junk ignored");
                Expect(DumpAnalyzer.Format(rep, "x.dmp").Contains("Signature:"), "report formatted with a signature");
                DumpAnalyzer.Report frz = DumpAnalyzer.Analyze(WriteSyntheticDump(dumpDir, false, out rip, out ret));
                Expect(frz.Error == null && frz.IsFreeze && frz.Frames.Count == 2 && frz.Frames[1].Rva == (uint)(ret - SyntheticBase)
                    && DumpAnalyzer.Format(frz, "f.dmp").Contains("Freeze analysis") && DumpAnalyzer.Signature(frz).Contains("| freeze at"),
                    "freeze snapshot (no exception): main thread stack recovered");
            }
            finally { try { Directory.Delete(dumpDir, true); } catch { } }

            Console.WriteLine("Backups, retention and auto-heal (temporary folder)");
            string saveRootBackup = Paths.SavesRoot, backupsBackup = Paths.Backups, quarantineBackup = Paths.Quarantine;
            int keepBackup = Settings.BackupKeep, daysBackup = Settings.BackupDailyDays;
            bool healBackup = Settings.AutoHeal, detailedBackup = Settings.DetailedMessages;
            string tmp = Path.Combine(Path.GetTempPath(), "KHHotfix-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Paths.SavesRoot = Path.Combine(tmp, "saves");
                Paths.Backups = Path.Combine(tmp, "backups");
                Paths.Quarantine = Path.Combine(Paths.Backups, "_quarantine");
                Settings.AutoHeal = true;
                Settings.DetailedMessages = false;
                string save = Path.Combine(Paths.SavesRoot, @"Steam\76561190000000000\KHFM_WW.png");
                Directory.CreateDirectory(Path.GetDirectoryName(save));
                byte[] good = MakePng(3000);
                File.WriteAllBytes(save, good);
                var sg = new SaveGuard();
                Expect(sg.SnapshotAll("test") == 1, "first snapshot written");
                Expect(sg.SnapshotAll("test") == 0, "unchanged file is not backed up twice");
                List<BackupEntry> hist = SaveGuard.ListBackups(SaveGuard.BackupDirFor(save));
                Expect(hist.Count == 1 && hist[0].Status == "Ok" && hist[0].Size == good.Length, "backup entry parsed (status Ok, size)");
                Expect(SaveGuard.ReadGz(hist[0].FullPath).SequenceEqual(good), "gzip round-trip is byte-identical");

                File.WriteAllBytes(save, good.Take(40).ToArray());   // crash while saving: cut inside the PNG chunks
                var notices = new Notices();
                sg.HealBeforeLaunch(notices);
                Expect(File.ReadAllBytes(save).SequenceEqual(good), "save cut inside PNG chunks is restored from the last good backup");
                Expect(notices.Count == 1 && notices[0].Contains("restored") && notices.Shown.Count == 1, "user is told about the restore");
                Expect(Directory.Exists(Paths.Quarantine) && Directory.GetFiles(Paths.Quarantine, "*.bin", SearchOption.AllDirectories).Length == 1, "damaged copy quarantined");

                byte[] cutTail = good.Take(good.Length - 1000).ToArray();   // cut inside data stored after IEND
                File.WriteAllBytes(save, cutTail);
                notices.Clear();
                sg.HealBeforeLaunch(notices);
                Expect(File.ReadAllBytes(save).SequenceEqual(cutTail) && notices.Count == 1 && notices[0].Contains("smaller"),
                    "tail cut with only one known size: not restored automatically, user warned");

                byte[] good2 = (byte[])good.Clone();
                good2[good2.Length - 10] ^= 0x5A;   // a newer save of the same fixed size
                File.WriteAllBytes(save, good2);
                sg.SnapshotAll("test");
                File.WriteAllBytes(save, good2.Take(good2.Length - 1000).ToArray());
                notices.Clear();
                sg.HealBeforeLaunch(notices);
                Expect(File.ReadAllBytes(save).SequenceEqual(good2) && notices.Count == 1 && notices[0].Contains("restored"),
                    "tail cut once the save size is known to be fixed: newest good save restored");

                string other = Path.Combine(Paths.SavesRoot, @"Steam\76561190000000000\KHIIFM_WW.png");
                File.WriteAllBytes(other, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
                notices.Clear();
                sg.HealBeforeLaunch(notices);
                Expect(File.Exists(other) && notices.Count == 0, "uncalibrated non-PNG layout is never touched");

                File.SetAttributes(save, FileAttributes.ReadOnly);
                notices.Clear();
                sg.Preflight(notices);
                Expect((File.GetAttributes(save) & FileAttributes.ReadOnly) == 0 && notices.Any(n => n.Contains("read-only")) && !notices.Shown.Any(n => n.Contains("read-only")),
                    "read-only save flag removed (logged, the player is not bothered)");

                Settings.BackupKeep = 5;
                Settings.BackupDailyDays = 0;
                string dir = SaveGuard.BackupDirFor(save);
                for (int i = 0; i < 12; i++)
                    File.WriteAllBytes(Path.Combine(dir, BackupEntry.MakeName(DateTime.UtcNow.AddMinutes(-100 + i), "aaaaaa" + i.ToString("x6"), 10, "Truncated")), new byte[1]);
                SaveGuard.Prune(dir);
                List<BackupEntry> after = SaveGuard.ListBackups(dir);
                Expect(after.Count <= 7 && after.Any(e => e.Status == "Ok"), "retention keeps newest N plus the newest good backup");
            }
            finally
            {
                Paths.SavesRoot = saveRootBackup;
                Paths.Backups = backupsBackup;
                Paths.Quarantine = quarantineBackup;
                Settings.BackupKeep = keepBackup;
                Settings.BackupDailyDays = daysBackup;
                Settings.AutoHeal = healBackup;
                Settings.DetailedMessages = detailedBackup;
                try { foreach (string f in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(tmp, true); } catch { }
            }

            Console.WriteLine("Messages for the player");
            bool shownBackup = Settings.DetailedMessages;
            string logHookBackup = Settings.DiscordLogWebhook;
            try
            {
                Settings.DetailedMessages = false;
                Settings.DiscordLogWebhook = "";
                var n = new Notices();
                n.Add("save restored");
                n.Detail("loading screen released");
                n.AboutReport("crash report written");
                n.Insert(0, "game crashed", Notices.AutoReported);
                Expect(n.Count == 4 && n.SequenceEqual(new[] { "game crashed", "save restored", "loading screen released", "crash report written" }),
                    "every message is kept in order (for the log and the Discord reports)");
                Expect(n.Shown.SequenceEqual(new[] { "game crashed", "save restored", "crash report written" }) && n.Hidden.SequenceEqual(new[] { "loading screen released" }),
                    "the player sees what needs them, not what the hotfix handled; crash notes are shown when nobody receives the report");
                Settings.DiscordLogWebhook = "https://discord.com/api/webhooks/123456789012345678/" + new string('x', 68);
                var m = new Notices();
                m.Add("save restored");
                m.Detail("loading screen released");
                m.AboutReport("crash report written");
                Expect(m.Shown.SequenceEqual(new[] { "save restored" }) && m.Count == 3, "with Discord crash reporting, crash and freeze notes are only reported (sent automatically)");
                Settings.DetailedMessages = true;
                Expect(m.Shown.Count == 3 && m.Hidden.Count == 0, "DetailedMessages=1 shows everything");
            }
            finally
            {
                Settings.DetailedMessages = shownBackup;
                Settings.DiscordLogWebhook = logHookBackup;
            }

            DiscordTests();

            Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : failures + " TEST(S) FAILED");
            return failures == 0 ? 0 : 1;
        }
    }
}
