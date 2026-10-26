// KH 1.5+2.5 Hotfix - runtime hotfix for KINGDOM HEARTS -HD 1.5+2.5 ReMIX- (Steam).
//
// build.cmd (and Install.cmd) compile the files in this folder twice:
//   KHHotfix.exe     GUI subsystem: Steam launch-option wrapper  ->  "<dir>\KHHotfix.exe" %command%
//   KHHotfixCli.exe  console (/define:CLI): maintenance commands used by the PowerShell scripts
// Language level is C# 5 (the compiler that ships with the .NET Framework 4.x).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("KH 1.5+2.5 Hotfix")]
[assembly: System.Reflection.AssemblyProduct("KHHotfix")]
[assembly: System.Reflection.AssemblyVersion("1.5.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.5.1.0")]

namespace KHHotfix
{
    internal static class Program
    {
        public const string Version = "1.5.1";
        public const string Title = "KH 1.5+2.5 Hotfix";
        public const string RestoreTool = "Start menu > KH Hotfix > Restore a KINGDOM HEARTS save";
        public const string ReportTool = "Start menu > KH Hotfix > Report a KINGDOM HEARTS bug";
#if CLI
        public static readonly bool Cli = true;
#else
        public static readonly bool Cli = false;
#endif

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                // Real pixel coordinates for game screenshots and a sharp report window on scaled displays.
                Native.SetProcessDPIAware();
            }
            catch { }
            try
            {
                Application.EnableVisualStyles();
                Paths.Init();
                Settings.Load();
                Paths.ApplyOverrides();
            }
            catch (Exception ex)
            {
                Log.Early("init: " + ex);
            }

            if (args.Length == 0)
            {
                string usage = "KHHotfix " + Version + "\r\n\r\nSet this as the Steam launch option of KINGDOM HEARTS -HD 1.5+2.5 ReMIX-:\r\n\r\n    \""
                    + Paths.ExePath + "\" %command%\r\n\r\nDetails: https://github.com/Gasteren/KH-Hotfix";
                if (Cli) Console.WriteLine(usage);
                else MessageBox.Show(usage, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }
            if (args[0].StartsWith("--", StringComparison.Ordinal))
            {
                Log.Open("cli");
                try { return Commands.Run(args); }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                    Console.Error.WriteLine("ERROR: " + ex.Message);
                    return 1;
                }
            }
            return Wrapper.Run(args);
        }
    }

    internal static class Paths
    {
        public static string ExePath, Root, Backups, Quarantine, Reports, Logs, State, CrashDumps;
        public static string Documents, DocumentsAnsi, SavesRoot;

        public static void Init()
        {
            ExePath = Path.GetFullPath(Application.ExecutablePath);
            Root = Path.GetDirectoryName(ExePath);
            Backups = Path.Combine(Root, "Backups");
            Quarantine = Path.Combine(Backups, "_quarantine");
            Reports = Path.Combine(Root, "Reports");
            Logs = Path.Combine(Root, "Logs");
            State = Path.Combine(Root, "State");
            CrashDumps = Path.Combine(Root, "CrashDumps");
            Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            DocumentsAnsi = Native.GetDocumentsAnsi();
            SavesRoot = Path.Combine(Documents, @"My Games\KINGDOM HEARTS HD 1.5+2.5 ReMIX");
        }

        public static void ApplyOverrides()
        {
            string env = Environment.GetEnvironmentVariable("KHHOTFIX_SAVES_ROOT");
            if (!string.IsNullOrEmpty(env)) SavesRoot = env;
            else if (!string.IsNullOrEmpty(Settings.SavesRoot)) SavesRoot = Environment.ExpandEnvironmentVariables(Settings.SavesRoot);
            SavesRoot = SavesRoot.TrimEnd('\\');
        }

        // Path of a save file relative to the saves root (used for backup folder names and messages).
        public static string Rel(string full)
        {
            if (full.StartsWith(SavesRoot + "\\", StringComparison.OrdinalIgnoreCase)) return full.Substring(SavesRoot.Length + 1);
            return Path.GetFileName(full);
        }

        // Returns true the first time it is called for a given name (persisted in State\).
        public static bool OnceFlag(string name)
        {
            try
            {
                Directory.CreateDirectory(State);
                string f = Path.Combine(State, "once-" + name + ".flag");
                if (File.Exists(f)) return false;
                File.WriteAllText(f, DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
                return true;
            }
            catch { return true; }
        }
    }

    internal static class Settings
    {
        public static int BackupKeep = 40;
        public static int BackupDailyDays = 30;
        public static bool AutoHeal = true;
        public static int TargetRefreshHz = 0;
        public static int QuitHangKillSeconds = 20;
        public static int HungWindowKillSeconds = 120;
        public static int ExitGraceSeconds = 8;
        public static int WaitScreenCloseSeconds = 15;
        public static string PanicHotkey = "Ctrl+Alt+K";
        public static bool ShowDialogs = true;
        public static bool DetailedMessages = false;
        public static string SavesRoot = "";
        public static string ExtraGameArgs = "";
        public static bool FixQuitCrash = true;
        public static int LoadingRescueSeconds = 10;
        public static string BugReportHotkey = "Ctrl+Alt+B";
        // The project's report relay (relay\worker.js): forwards reports to the maintainer's Discord. Set by relay\Deploy-Relay.ps1.
        public static string ReportRelay = "https://kh-hotfix-reports.gasteren.workers.dev";
        public static bool AutoReports = true;
        public static string DiscordBugReportWebhook = "";
        public static string DiscordLogWebhook = "";
        public static string DiscordBugReportTags = "";

        // Where reports go: the maintainer's own webhooks when set (KHHotfix.local.ini), otherwise the report relay.
        public static string BugReportTarget { get { return DiscordBugReportWebhook.Length > 0 ? DiscordBugReportWebhook : RelayEndpoint("bug"); } }

        // Crash, freeze and session reports; empty with AutoReports=0.
        public static string LogReportTarget { get { return !AutoReports ? "" : DiscordLogWebhook.Length > 0 ? DiscordLogWebhook : RelayEndpoint("log"); } }

        static string RelayEndpoint(string kind) { return ReportRelay.Length > 0 ? ReportRelay.TrimEnd('/') + "/" + kind : ""; }

        public static string FilePath { get { return Path.Combine(Paths.Root, "KHHotfix.ini"); } }
        // Personal overrides on top of KHHotfix.ini (ignored by git, so a clone's defaults stay untouched).
        public static string LocalFilePath { get { return Path.Combine(Paths.Root, "KHHotfix.local.ini"); } }

        public static void Load()
        {
            LoadFile(FilePath);
            LoadFile(LocalFilePath);
        }

        static void LoadFile(string path)
        {
            if (!File.Exists(path)) return;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "backupkeep": BackupKeep = Int(val, BackupKeep, 3, 100000); break;
                    case "backupdailydays": BackupDailyDays = Int(val, BackupDailyDays, 0, 3650); break;
                    case "autoheal": AutoHeal = Bool(val, AutoHeal); break;
                    case "targetrefreshhz": TargetRefreshHz = Int(val, TargetRefreshHz, 0, 1000); break;
                    case "quithangkillseconds": QuitHangKillSeconds = Int(val, QuitHangKillSeconds, 0, 3600); break;
                    case "hungwindowkillseconds": HungWindowKillSeconds = Int(val, HungWindowKillSeconds, 0, 3600); break;
                    case "exitgraceseconds": ExitGraceSeconds = Int(val, ExitGraceSeconds, 2, 120); break;
                    case "waitscreencloseseconds": WaitScreenCloseSeconds = Int(val, WaitScreenCloseSeconds, 0, 3600); break;
                    case "panichotkey": PanicHotkey = val; break;
                    case "showdialogs": ShowDialogs = Bool(val, ShowDialogs); break;
                    case "detailedmessages": DetailedMessages = Bool(val, DetailedMessages); break;
                    case "savesroot": SavesRoot = val; break;
                    case "extragameargs": ExtraGameArgs = val; break;
                    case "fixquitcrash": FixQuitCrash = Bool(val, FixQuitCrash); break;
                    case "loadingrescueseconds": LoadingRescueSeconds = Int(val, LoadingRescueSeconds, 0, 3600); break;
                    case "bugreporthotkey": BugReportHotkey = val; break;
                    case "reportrelay": ReportRelay = val.Trim('"', '\'').TrimEnd('/'); break;
                    case "autoreports": AutoReports = Bool(val, AutoReports); break;
                    case "discordbugreportwebhook": DiscordBugReportWebhook = val.Trim('"', '\''); break;
                    case "discordlogwebhook": DiscordLogWebhook = val.Trim('"', '\''); break;
                    case "discordbugreporttags": DiscordBugReportTags = val; break;
                }
            }
        }

        static int Int(string s, int def, int min, int max)
        {
            int v;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return def;
            return Math.Max(min, Math.Min(max, v));
        }

        static bool Bool(string s, bool def)
        {
            s = s.ToLowerInvariant();
            if (s == "1" || s == "true" || s == "yes" || s == "on") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off") return false;
            return def;
        }
    }

    internal static class Log
    {
        static readonly object Sync = new object();
        static string file;
        public static bool Echo;
        public static string FilePath { get { return file; } }

        public static void Open(string name)
        {
            try
            {
                Directory.CreateDirectory(Paths.Logs);
                file = Path.Combine(Paths.Logs, name + ".log");
                if (name == "cli" && File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.Delete(file);
                if (name.StartsWith("session-", StringComparison.Ordinal))
                {
                    foreach (FileInfo old in new DirectoryInfo(Paths.Logs).GetFiles("session-*.log").OrderByDescending(f => f.Name).Skip(40))
                    {
                        try { old.Delete(); } catch { }
                    }
                }
            }
            catch { file = null; }
        }

        public static void Info(string m) { Write("INFO ", m); }
        public static void Warn(string m) { Write("WARN ", m); }
        public static void Error(string m) { Write("ERROR", m); }

        public static void Early(string m)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "KHHotfix-early.log"), DateTime.Now.ToString("s") + " " + m + Environment.NewLine); }
            catch { }
        }

        static void Write(string level, string m)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + level + " " + m;
            lock (Sync)
            {
                if (file != null) { try { File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8); } catch { } }
                if (Echo) { try { Console.WriteLine(line); } catch { } }
            }
        }
    }

    // Messages for the player. Important ones (something to do, or something that happened to the saves) are shown;
    // details about what the hotfix handled by itself are only logged and sent with the Discord reports, unless
    // DetailedMessages=1. Enumerating returns all of them, in order.
    internal sealed class Notices : IEnumerable<string>
    {
        sealed class Entry
        {
            public string Text;
            public bool Detail;
        }

        readonly List<Entry> items = new List<Entry>();

        public int Count { get { return items.Count; } }
        public string this[int index] { get { return items[index].Text; } }

        public void Add(string text) { Insert(items.Count, text, false); }
        public void Detail(string text) { Insert(items.Count, text, true); }

        // About a crash or freeze report: a detail when Discord reporting sends the report by itself, otherwise shown
        // because the player has to pass the report on.
        public void AboutReport(string text) { Insert(items.Count, text, AutoReported); }

        public void Insert(int index, string text, bool detail) { items.Insert(index, new Entry { Text = text, Detail = detail }); }

        public void AddRange(IEnumerable<string> texts) { foreach (string t in texts) Add(t); }

        public void Clear() { items.Clear(); }

        public static bool AutoReported { get { return Discord.IsWebhook(Settings.LogReportTarget); } }

        public List<string> Shown { get { return items.Where(i => Settings.DetailedMessages || !i.Detail).Select(i => i.Text).ToList(); } }

        public List<string> Hidden { get { return items.Where(i => !Settings.DetailedMessages && i.Detail).Select(i => i.Text).ToList(); } }

        public IEnumerator<string> GetEnumerator() { return items.Select(i => i.Text).GetEnumerator(); }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }

    internal static class Ui
    {
        public static void Notice(string heading, Notices notices, MessageBoxIcon icon)
        {
            if (notices == null || notices.Count == 0) return;
            List<string> hidden = notices.Hidden, shown = notices.Shown;
            if (hidden.Count > 0) Log.Info("Not shown to the player (DetailedMessages=0): " + string.Join(" | ", hidden));
            if (shown.Count == 0) return;
            var sb = new StringBuilder(heading);
            foreach (string i in shown) { sb.AppendLine(); sb.AppendLine(); sb.Append("- " + i); }
            Log.Info("NOTICE: " + sb.ToString().Replace("\r\n", " | ").Replace("\n", " | "));
            if (Program.Cli || !Settings.ShowDialogs) return;
            try
            {
                MessageBox.Show(sb.ToString(), Program.Title, MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
            }
            catch (Exception ex) { Log.Warn("Dialog failed: " + ex.Message); }
        }

        public static void Error(string text)
        {
            Log.Error(text);
            if (Program.Cli || !Settings.ShowDialogs) return;
            try { MessageBox.Show(text, Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly); }
            catch { }
        }
    }

    internal static class CommandLine
    {
        // Returns the raw command line after skipping the first N tokens, preserving the original quoting.
        public static string Remainder(string cmd, int skipTokens)
        {
            int i = 0;
            for (int t = 0; t < skipTokens; t++)
            {
                while (i < cmd.Length && char.IsWhiteSpace(cmd[i])) i++;
                if (i >= cmd.Length) return "";
                bool quoted = false;
                while (i < cmd.Length)
                {
                    char c = cmd[i];
                    if (c == '"') quoted = !quoted;
                    else if (!quoted && char.IsWhiteSpace(c)) break;
                    i++;
                }
            }
            while (i < cmd.Length && char.IsWhiteSpace(cmd[i])) i++;
            return cmd.Substring(i);
        }
    }
}
