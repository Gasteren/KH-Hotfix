using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace KHHotfix
{
    // "Report a bug": Ctrl+Alt+B while playing (or Start menu > KH Hotfix > Report a KINGDOM HEARTS bug) captures the moment
    // (screenshot, state of the game, technical snapshot) into Reports\bug-<time>\ and opens a window to describe the
    // problem. The report is posted to DiscordBugReportWebhook, or saved for a GitHub issue when Discord is not set up.
    internal static class BugReport
    {
        public static readonly string[] Categories = { "Crash (the game closed)", "Freeze or stuck", "Endless loading screen", "Sound", "Graphics",
            "Saving or save data", "Controls", "Performance or stutter", "Other" };
        static readonly int[] Colors = { 0xE74C3C, 0xE67E22, 0xF1C40F, 0x9B59B6, 0x3498DB, 0x1ABC9C, 0x2ECC71, 0x95A5A6, 0x7F8C8D };
        public static readonly string[] Games = { "KINGDOM HEARTS FINAL MIX", "KINGDOM HEARTS Re:Chain of Memories", "KINGDOM HEARTS II FINAL MIX",
            "KINGDOM HEARTS Birth by Sleep FINAL MIX", "KINGDOM HEARTS 358/2 Days and Re:coded (Theater)", "Launcher or other" };
        public const string IssueUrl = "https://github.com/Gasteren/KH-Hotfix/issues/new?template=crash-report.md";

        public sealed class Draft
        {
            public string Game, Category, Title, Description, Reporter;
            public bool Screenshot = true, Logs = true, Snapshot = true;
            public string CrashReport;   // folder of a crash report to attach, or null
        }

        public static string GameTitle(string processName)
        {
            switch ((processName ?? "").ToUpperInvariant())
            {
                case "KINGDOM HEARTS FINAL MIX": return Games[0];
                case "KINGDOM HEARTS RE_CHAIN OF MEMORIES": return Games[1];
                case "KINGDOM HEARTS II FINAL MIX": return Games[2];
                case "KINGDOM HEARTS BIRTH BY SLEEP FINAL MIX": return Games[3];
                case "KINGDOM HEARTS THEATER": return Games[4];
                default: return Games[5];
            }
        }

        // ---- the report folder ------------------------------------------------------------------------------
        public static Dictionary<string, string> ReadMeta(string dir)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string line in File.ReadAllLines(Path.Combine(dir, "meta.txt")))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) d[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            catch { }
            return d;
        }

        public static string Get(Dictionary<string, string> meta, string key)
        {
            string v;
            return meta.TryGetValue(key, out v) && v.Length > 0 ? v : null;
        }

        // Captures the moment of the problem. pid = 0 when no game is running.
        public static string Prepare(int pid, string exePath, string gameDir, string sessionLog, string trigger)
        {
            Cleanup();
            string dir = Path.Combine(Paths.Reports, "bug-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(Paths.Reports, "bug-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + i);
            Directory.CreateDirectory(dir);
            var meta = new Dictionary<string, string>();
            meta["Created"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
            meta["Trigger"] = trigger;
            if (!string.IsNullOrEmpty(gameDir)) meta["GameDir"] = gameDir;
            if (!string.IsNullOrEmpty(sessionLog) && File.Exists(sessionLog)) meta["SessionLog"] = sessionLog;
            if (pid > 0 && !string.IsNullOrEmpty(exePath))
            {
                string name = Path.GetFileNameWithoutExtension(exePath);
                meta["GamePid"] = pid.ToString(CultureInfo.InvariantCulture);
                meta["Game"] = GameTitle(name);
                IntPtr hwnd = Native.MainWindowOf(pid);
                bool hung = hwnd != IntPtr.Zero && Native.IsHung(hwnd);
                if (hwnd != IntPtr.Zero) meta["GameWindow"] = hwnd.ToInt64().ToString(CultureInfo.InvariantCulture);
                meta["GameResponding"] = hwnd == IntPtr.Zero ? "no window" : hung ? "no" : "yes";
                try
                {
                    using (Bitmap b = Screenshot.Capture(hwnd, hung))
                        if (b != null) File.WriteAllBytes(Path.Combine(dir, "screenshot.jpg"), Screenshot.Jpeg(b, 1920, 85));
                }
                catch (Exception ex) { Log.Warn("Bug report: screenshot failed: " + ex.Message); }
                File.WriteAllText(Path.Combine(dir, "game-state.txt"), GameState(pid, exePath, name, meta["GameResponding"], trigger), Encoding.UTF8);
                string err = HangDump.WriteFile(pid, Path.Combine(dir, "snapshot.dmp"));
                if (err != null) Log.Warn("Bug report: snapshot of the game failed: " + err);
            }
            File.WriteAllLines(Path.Combine(dir, "meta.txt"), meta.Select(kv => kv.Key + "=" + kv.Value).ToArray());
            Log.Info("Bug report prepared in " + dir + (pid > 0 ? " (game pid " + pid + ")" : " (no game running)"));
            return dir;
        }

        static string GameState(int pid, string exePath, string name, string responding, string trigger)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Report started : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " (" + trigger + ")");
            sb.AppendLine("Game process   : " + name + ".exe, pid " + pid + ", window responding: " + responding);
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    sb.AppendLine("Running since  : " + p.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    sb.AppendLine("Memory in use  : " + (p.WorkingSet64 / 1048576) + " MB");
                    sb.AppendLine("CPU time used  : " + (int)p.TotalProcessorTime.TotalSeconds + " s");
                }
            }
            catch { }
            if (name.Equals(LoadingGuard.GameName, StringComparison.OrdinalIgnoreCase))
            {
                string text;
                try { LoadingGuard.Inspect(pid, exePath, false, out text); }
                catch (Exception ex) { text = "not readable: " + ex.Message; }
                sb.AppendLine();
                sb.AppendLine("KH1 loading and sound state:");
                sb.AppendLine(text);
            }
            return sb.ToString();
        }

        // Reports started outside a game session (Start menu, command line) still capture a running game.
        public static string PrepareFromRunningGame(string trigger)
        {
            int pid = 0;
            string exe = null;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (pid != 0 || !TrackedProcess.IsGameName(p.ProcessName)) continue;
                    string path = Native.GetImagePath(p.Id);
                    if (path != null) { pid = p.Id; exe = path; }
                }
                catch { }
                finally { p.Dispose(); }
            }
            return Prepare(pid, exe, exe != null ? Path.GetDirectoryName(exe) : Reporting.GuessGameDir(), Reporting.LatestSessionLog(), trigger);
        }

        // Report folder for a report started from the Start menu or command line: captures the process given with --pid, else the running game.
        static string PrepareFor(string[] a, string trigger)
        {
            int pid;
            if (!int.TryParse(Arg(a, "--pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) || pid <= 0) return PrepareFromRunningGame(trigger);
            string exe = Native.GetImagePath(pid);
            return Prepare(pid, exe, exe != null ? Path.GetDirectoryName(exe) : Reporting.GuessGameDir(), Reporting.LatestSessionLog(), trigger);
        }

        // Keeps Reports\ small: sent bug reports older than 30 days and anything beyond the newest 30 are removed.
        static void Cleanup()
        {
            try
            {
                if (!Directory.Exists(Paths.Reports)) return;
                List<DirectoryInfo> dirs = new DirectoryInfo(Paths.Reports).GetDirectories("bug-*").OrderByDescending(x => x.Name).ToList();
                for (int i = 0; i < dirs.Count; i++)
                {
                    bool old = DateTime.Now - dirs[i].CreationTime > TimeSpan.FromDays(30);
                    if (i >= 30 || (old && File.Exists(Path.Combine(dirs[i].FullName, Reporting.SentMarker)))) dirs[i].Delete(true);
                }
            }
            catch { }
        }

        public static string SaveText(string dir, Draft d)
        {
            var sb = new StringBuilder();
            sb.AppendLine("KH Hotfix " + Program.Version + " - bug report");
            sb.AppendLine("Date     : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("Game     : " + d.Game);
            sb.AppendLine("Problem  : " + d.Category);
            sb.AppendLine("Title    : " + d.Title);
            sb.AppendLine("Reporter : " + d.Reporter);
            sb.AppendLine();
            sb.AppendLine(d.Description);
            string f = Path.Combine(dir, "report.txt");
            File.WriteAllText(f, sb.ToString(), Encoding.UTF8);
            return f;
        }

        public static Discord.Message Compose(string dir, Draft d, SystemInfo sys)
        {
            Dictionary<string, string> meta = ReadMeta(dir);
            string game = !string.IsNullOrEmpty(d.Game) ? d.Game : Get(meta, "Game") ?? "KINGDOM HEARTS";
            string category = string.IsNullOrEmpty(d.Category) ? "Other" : d.Category;
            string desc = (d.Description ?? "").Trim();
            string title = (d.Title ?? "").Trim();
            if (title.Length == 0) title = desc.Length > 0 ? desc.Split('\n')[0].Trim() : category;
            string reporter = string.IsNullOrEmpty(d.Reporter) ? Reporting.MachineTag() : d.Reporter.Trim();

            var m = new Discord.Message();
            m.Username = reporter + " (KH Hotfix)";
            m.ThreadName = TextUtil.Clip("[" + Reporting.ShortGame(game) + "] " + title, 100);
            string body = desc.Length > 0 ? desc : "(no description)";
            if (body.Length > 1900)
            {
                m.Content = TextUtil.Clip(body, 1850) + "\n(full text in description.txt)";
                m.Files.Add(new Discord.Attachment("description.txt", Reporting.Utf8(body), false));
            }
            else m.Content = body;

            int ci = Array.IndexOf(Categories, category);
            var e = new DiscordEmbed
            {
                Title = TextUtil.Clip(title, 256),
                Color = ci >= 0 ? Colors[ci] : Reporting.Gray,
                Timestamp = DateTime.Now,
                Footer = "KH Hotfix " + Program.Version + " bug report | " + Reporting.MachineTag()
            };
            e.Add("Game", game, true);
            e.Add("Problem", category, true);
            e.Add("Reported by", reporter, true);
            if (Get(meta, "GameResponding") == "no") e.Add("Game window", "not responding when the report was started", true);
            string stateFile = Path.Combine(dir, "game-state.txt");
            string state = File.Exists(stateFile) ? File.ReadAllText(stateFile) : "";
            if (state.Contains("Sound output       : FAILED")) e.Add("Sound", "failed - the audio device disappeared during the session", true);
            if (sys != null) sys.AddTo(e);

            string shot = Path.Combine(dir, "screenshot.jpg");
            if (d.Screenshot && File.Exists(shot))
            {
                m.Files.Add(new Discord.Attachment("screenshot.jpg", File.ReadAllBytes(shot), true));
                e.ImageFile = "screenshot.jpg";
            }
            if (d.Logs)
            {
                string info = (sys != null ? sys.ToText() : "") + (state.Length > 0 ? Environment.NewLine + Reporting.Sanitize(state) : "");
                m.Files.Add(new Discord.Attachment("system.txt", Reporting.Utf8(info), false));
                string log = Get(meta, "SessionLog");
                if (log != null && File.Exists(log)) m.Files.Add(new Discord.Attachment("session.log", Reporting.TextFile(log, 2000000), true));
            }
            if (!string.IsNullOrEmpty(d.CrashReport) && Directory.Exists(d.CrashReport))
            {
                string summary = Path.Combine(d.CrashReport, "summary.txt");
                if (File.Exists(summary)) m.Files.Add(new Discord.Attachment("crash-summary.txt", Reporting.TextFile(summary, 200000), false));
                e.Add("Crash report", "Reports\\" + Path.GetFileName(d.CrashReport) + " attached", false);
                foreach (string dmp in Directory.GetFiles(d.CrashReport, "*.dmp").Where(f => new FileInfo(f).Length < 256L * 1024 * 1024))
                    m.Files.Add(new Discord.Attachment(Path.GetFileName(dmp) + ".gz", Reporting.GzipFile(dmp), true));
            }
            string snap = Path.Combine(dir, "snapshot.dmp");
            if (d.Snapshot && File.Exists(snap)) m.Files.Add(new Discord.Attachment("game-snapshot.dmp.gz", Reporting.GzipFile(snap), true));
            foreach (string tag in (Settings.DiscordBugReportTags ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (Regex.IsMatch(tag, @"^\d{5,25}$")) m.Tags.Add(tag);
            m.Embed = e;
            return m;
        }

        public static Discord.Result Send(string dir, Draft d)
        {
            SaveText(dir, d);
            if (d.Reporter != null) Reporting.ReporterName = d.Reporter;
            SystemInfo sys = SystemInfo.Collect(Get(ReadMeta(dir), "GameDir") ?? Reporting.GuessGameDir());
            Discord.Result r = Discord.Send(Settings.DiscordBugReportWebhook, Compose(dir, d, sys), 60000);
            if (r.Dropped.Count > 0) Log.Warn("Bug report: left out because Discord's size limit was reached: " + string.Join(", ", r.Dropped));
            if (r.Ok)
            {
                Reporting.MarkSent(dir, r);
                if (!string.IsNullOrEmpty(d.CrashReport)) Reporting.MarkSent(d.CrashReport, r);
                Log.Info("Bug report sent to Discord (" + Discord.Describe(Settings.DiscordBugReportWebhook) + ")" + (r.Link != null ? ": " + r.Link : ""));
            }
            else Log.Warn("Bug report not sent (" + Discord.Describe(Settings.DiscordBugReportWebhook) + "): " + r.Error);
            return r;
        }

        // Saves the report, shows its folder and opens a prefilled GitHub issue (used when Discord is not set up).
        public static void OpenIssue(string dir, Draft d)
        {
            string f = SaveText(dir, d);
            try { Process.Start("explorer.exe", "/select,\"" + f + "\""); } catch { }
            string title = TextUtil.Clip("[" + Reporting.ShortGame(d.Game) + "] " + (string.IsNullOrEmpty(d.Title) ? d.Category : d.Title), 120);
            try { Process.Start(IssueUrl + "&title=" + Uri.EscapeDataString(title)); } catch { }
        }

        static string Arg(string[] a, string name)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }

        // --report-bug [folder]                      opens the report window (the folder comes from the hotkey)
        // --report-bug --send --title T --text D [--game KH1] [--category Sound] [--name N] [--no-screenshot] [--no-logs] [--no-snapshot]
        public static int Run(string[] a)
        {
            string dir = a.Length > 1 && !a[1].StartsWith("--", StringComparison.Ordinal) && Directory.Exists(a[1]) ? a[1] : null;
            if (!a.Contains("--send"))
            {
                if (dir == null) dir = PrepareFor(a, "Start menu");
                Application.Run(new BugReportForm(dir));
                return 0;
            }
            string title = (Arg(a, "--title") ?? "").Trim(), text = (Arg(a, "--text") ?? "").Replace("\\n", "\n").Trim();
            if (title.Length == 0 && text.Length == 0)
            {
                Console.Error.WriteLine("usage: --report-bug [folder] --send --title <title> --text <what happened> [--game KH1|Re:CoM|KH2|BBS|Days] "
                    + "[--category <problem>] [--name <name>] [--pid <game pid>] [--no-screenshot] [--no-logs] [--no-snapshot]");
                return 2;
            }
            if (dir == null) dir = PrepareFor(a, "command line");
            string game = Arg(a, "--game"), category = Arg(a, "--category");
            var d = new Draft
            {
                Title = title,
                Description = text,
                Reporter = Arg(a, "--name") ?? Reporting.ReporterName,
                Game = game == null ? Get(ReadMeta(dir), "Game") ?? Games[5] : Games.FirstOrDefault(g => Reporting.ShortGame(g) == Reporting.ShortGame(game)) ?? game,
                Category = category == null ? "Other" : Categories.FirstOrDefault(c => c.StartsWith(category, StringComparison.OrdinalIgnoreCase)) ?? "Other",
                Screenshot = !a.Contains("--no-screenshot"),
                Logs = !a.Contains("--no-logs"),
                Snapshot = !a.Contains("--no-snapshot")
            };
            if (!Discord.IsWebhook(Settings.DiscordBugReportWebhook))
            {
                Console.WriteLine("Discord reporting is not set up (DiscordBugReportWebhook). The report was saved: " + SaveText(dir, d));
                return 4;
            }
            Discord.Result r = Send(dir, d);
            Console.WriteLine(r.Ok ? "Sent" + (r.Link != null ? ": " + r.Link : "") : "Not sent: " + r.Error);
            if (r.Dropped.Count > 0) Console.WriteLine("Left out (too large): " + string.Join(", ", r.Dropped));
            return r.Ok ? 0 : 1;
        }
    }
}
