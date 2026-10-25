using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace KHHotfix
{
    // Helpers shared by bug reports and the automatic crash/freeze reports.
    internal static class Reporting
    {
        // Removes what identifies the player from text that leaves the PC: the Windows user folder and name,
        // the PC name and Steam account ids. (Snapshots of the game's memory can't be cleaned; the dialogs say so.)
        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
            if (profile.Length > 3) s = Regex.Replace(s, Regex.Escape(profile), "%USERPROFILE%", RegexOptions.IgnoreCase);
            string user = Environment.UserName;
            if (!string.IsNullOrEmpty(user) && user.Length >= 2)
                s = Regex.Replace(s, @"(\\Users\\)" + Regex.Escape(user) + @"(?=\\|""|\s|$)", "$1<user>", RegexOptions.IgnoreCase);
            string pc = Environment.MachineName;
            if (!string.IsNullOrEmpty(pc) && pc.Length >= 3) s = Regex.Replace(s, @"(?<![A-Za-z0-9])" + Regex.Escape(pc) + @"(?![A-Za-z0-9])", "<pc>", RegexOptions.IgnoreCase);
            return Regex.Replace(s, @"(?<!\d)7656119\d{10}(?!\d)", "<steamid>");
        }

        // A text file as UTF-8 bytes, cleaned with Sanitize. Keeps the end of files larger than maxChars.
        public static byte[] TextFile(string path, int maxChars)
        {
            string t;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8, true)) t = r.ReadToEnd();
            if (t.Length > maxChars) t = "[... beginning cut, " + (t.Length - maxChars) + " characters ...]\r\n" + t.Substring(t.Length - maxChars);
            return Encoding.UTF8.GetBytes(Sanitize(t));
        }

        public static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s ?? ""); }

        public static byte[] Gzip(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        public static byte[] GzipFile(string path)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) fs.CopyTo(gz);
                return ms.ToArray();
            }
        }

        // Short, stable, anonymous id of this PC (so reports from the same PC can be grouped).
        public static string MachineTag()
        {
            string guid = null;
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    if (k != null) guid = k.GetValue("MachineGuid") as string;
            }
            catch { }
            uint h = 2166136261;
            foreach (char c in guid ?? Environment.MachineName) { h ^= c; h *= 16777619; }
            return "PC-" + (h & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture);
        }

        static string ReporterFile { get { return Path.Combine(Paths.State, "reporter.txt"); } }

        // Name the player entered in the bug report window (shown in Discord); empty when not set.
        public static string ReporterName
        {
            get
            {
                try { return File.Exists(ReporterFile) ? File.ReadAllText(ReporterFile).Trim() : ""; }
                catch { return ""; }
            }
            set
            {
                try
                {
                    Directory.CreateDirectory(Paths.State);
                    File.WriteAllText(ReporterFile, (value ?? "").Trim());
                }
                catch { }
            }
        }

        public static string Who()
        {
            string n = ReporterName;
            return n.Length > 0 ? TextUtil.Clip(n, 40) : MachineTag();
        }

        public static string ShortGame(string name)
        {
            string n = (name ?? "").ToUpperInvariant();
            if (n.Contains("CHAIN OF MEMORIES") || n.Contains("RE:COM") || n == "COM" || n == "RECOM") return "Re:CoM";
            if (n.Contains("BIRTH BY SLEEP") || n == "BBS") return "BBS";
            if (n.Contains("THEATER") || n.Contains("358") || n.Contains("DAYS") || n.Contains("CODED")) return "Days";
            if (n.Contains("II FINAL MIX") || n == "KH2") return "KH2";
            if (n.Contains("LAUNCHER") || n.Contains("OTHER")) return "Launcher";
            if (n.Contains("FINAL MIX") || n == "KH1") return "KH1";
            return "KH";
        }

        public static string LatestSessionLog()
        {
            try
            {
                if (!Directory.Exists(Paths.Logs)) return null;
                FileInfo f = new DirectoryInfo(Paths.Logs).GetFiles("session-*.log").OrderByDescending(x => x.Name).FirstOrDefault();
                return f == null ? null : f.FullName;
            }
            catch { return null; }
        }

        // The game folder, from a running KH process or the latest session log.
        public static string GuessGameDir()
        {
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        if (!p.ProcessName.StartsWith("KINGDOM HEARTS", StringComparison.OrdinalIgnoreCase)) continue;
                        string path = Native.GetImagePath(p.Id);
                        if (path != null) return Path.GetDirectoryName(path);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                string log = LatestSessionLog();
                if (log == null) return null;
                Match m = Regex.Match(Encoding.UTF8.GetString(TextFileRaw(log, 32768)), "Starting \"([^\"]+)\"");
                return m.Success ? Path.GetDirectoryName(m.Groups[1].Value) : null;
            }
            catch { return null; }
        }

        static byte[] TextFileRaw(string path, int maxBytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var b = new byte[Math.Min(maxBytes, (int)Math.Min(fs.Length, int.MaxValue))];
                int n = fs.Read(b, 0, b.Length);
                Array.Resize(ref b, n);
                return b;
            }
        }

        // Newest crash/freeze report (Reports\yyyyMMdd-HHmmss) of the last `maxAge` that was not sent to Discord yet.
        public static string LatestUnsentCrashReport(TimeSpan maxAge)
        {
            try
            {
                if (!Directory.Exists(Paths.Reports)) return null;
                foreach (DirectoryInfo d in new DirectoryInfo(Paths.Reports).GetDirectories().OrderByDescending(x => x.Name))
                {
                    DateTime t;
                    if (!DateTime.TryParseExact(d.Name, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                    if (DateTime.Now - t > maxAge) return null;
                    if (File.Exists(Path.Combine(d.FullName, "summary.txt")) && !File.Exists(Path.Combine(d.FullName, SentMarker))) return d.FullName;
                }
            }
            catch { }
            return null;
        }

        public const string SentMarker = "discord-sent.txt";

        public static void MarkSent(string dir, Discord.Result r)
        {
            try { File.WriteAllText(Path.Combine(dir, SentMarker), DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + (r != null && r.Link != null ? " " + r.Link : "")); }
            catch { }
        }

        // Discord embed color of a crash, freeze or other report.
        public const int Red = 0xE74C3C, Orange = 0xE67E22, Gray = 0x7F8C8D;
    }

    // What helps to reproduce a problem: Windows, hardware, display, audio, game and hotfix versions and settings.
    internal sealed class SystemInfo
    {
        public string Windows = "?", Cpu = "?", Ram = "?", Gpu = "?", Display = "?", Audio = "?", GameBuild = "?", Hotfix = "?";

        static string Safe(Func<string> f)
        {
            try { string v = f(); return string.IsNullOrEmpty(v) ? "?" : v.Trim(); }
            catch { return "?"; }
        }

        static string RegString(string key, string value)
        {
            using (RegistryKey k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(key))
                return k == null ? null : Convert.ToString(k.GetValue(value), CultureInfo.InvariantCulture);
        }

        public static string OsDescription()
        {
            try
            {
                string v = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
                string name = RegString(v, "ProductName") ?? "Windows";
                int build;
                // Windows 11 still reports "Windows 10" as its product name; the build number tells them apart.
                if (int.TryParse(RegString(v, "CurrentBuild"), out build) && build >= 22000) name = name.Replace("Windows 10", "Windows 11");
                return name + " " + RegString(v, "DisplayVersion") + " build " + RegString(v, "CurrentBuild") + "." + RegString(v, "UBR");
            }
            catch { return Environment.OSVersion.ToString(); }
        }

        public static SystemInfo Collect(string gameDir)
        {
            var s = new SystemInfo();
            s.Windows = Safe(OsDescription);
            s.Cpu = Safe(delegate { return Regex.Replace(RegString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "", @"\s+", " "); });
            s.Ram = Safe(delegate
            {
                ulong b = Native.TotalMemory();
                return b == 0 ? null : Math.Round(b / 1073741824.0).ToString(CultureInfo.InvariantCulture) + " GB";
            });
            s.Gpu = Safe(ReadGpu);
            s.Display = Safe(delegate
            {
                Native.DEVMODE cur;
                string dev = DisplayGuard.FindPrimary(out cur);
                if (dev == null) return null;
                return string.Format(CultureInfo.InvariantCulture, "{0}x{1} @ {2} Hz", cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency)
                    + (Settings.TargetRefreshHz > 0 ? " (hotfix switches to " + Settings.TargetRefreshHz + " Hz while playing)" : "");
            });
            s.Audio = Safe(delegate
            {
                string err;
                List<AudioDevices.Endpoint> all = AudioDevices.Render(out err);
                if (all == null) return null;
                List<AudioDevices.Endpoint> active = all.Where(e => e.State == AudioDevices.Active).ToList();
                return active.Count + " enabled: " + string.Join(", ", active.Select(e => e.Name + (e.IsDefault ? " (default)" : "")));
            });
            s.GameBuild = Safe(delegate { return GameBuildOf(gameDir); });
            s.Hotfix = Program.Version + string.Format(CultureInfo.InvariantCulture, " (FixQuitCrash={0}, LoadingRescueSeconds={1}{2})",
                Settings.FixQuitCrash ? 1 : 0, Settings.LoadingRescueSeconds, File.Exists(Settings.LocalFilePath) ? ", with KHHotfix.local.ini" : "");
            return s;
        }

        static string ReadGpu()
        {
            string name = null;
            for (uint i = 0; i < 32; i++)
            {
                Native.DISPLAY_DEVICE dd = Native.NewDisplayDevice();
                if (!Native.EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0) { name = dd.DeviceString; break; }
            }
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                using (RegistryKey cls = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    foreach (string sub in cls == null ? new string[0] : cls.GetSubKeyNames())
                    {
                        if (!Regex.IsMatch(sub, @"^\d{4}$")) continue;
                        using (RegistryKey k = cls.OpenSubKey(sub))
                        {
                            if (k == null || !string.Equals(k.GetValue("DriverDesc") as string, name, StringComparison.OrdinalIgnoreCase)) continue;
                            string v = k.GetValue("DriverVersion") as string;
                            if (string.IsNullOrEmpty(v)) break;
                            string[] p = v.Split('.');
                            // NVIDIA's own version number is hidden in the last digits of the Windows driver version (32.0.15.6094 = 560.94).
                            if (name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 && p.Length == 4 && (p[2] + p[3]).Length >= 5)
                            {
                                string d = (p[2] + p[3]).Substring((p[2] + p[3]).Length - 5);
                                return name + ", driver " + d.Substring(0, 3) + "." + d.Substring(3) + " (" + v + ")";
                            }
                            return name + ", driver " + v;
                        }
                    }
                }
            }
            catch { }
            return name;
        }

        static string GameBuildOf(string gameDir)
        {
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir)) return null;
            var parts = new List<string>();
            try
            {
                string acf = Path.GetFullPath(Path.Combine(gameDir, @"..\..\appmanifest_2552430.acf"));
                if (File.Exists(acf))
                {
                    Match m = Regex.Match(File.ReadAllText(acf), "\"buildid\"\\s+\"(\\d+)\"");
                    if (m.Success) parts.Add("Steam build " + m.Groups[1].Value);
                }
            }
            catch { }
            try
            {
                string kh1 = Path.Combine(gameDir, LoadingGuard.GameName + ".exe");
                if (File.Exists(kh1))
                {
                    byte[] head = new byte[0x1000];
                    using (var fs = new FileStream(kh1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) fs.Read(head, 0, head.Length);
                    GameFixes.PeInfo pe = GameFixes.ReadPe(head.Concat(new byte[0x400]).ToArray());
                    if (pe != null) parts.Add("KH1 exe " + pe.TimeDateStamp.ToString("X8"));
                }
            }
            catch { }
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Windows    : " + Windows);
            sb.AppendLine("CPU        : " + Cpu);
            sb.AppendLine("RAM        : " + Ram);
            sb.AppendLine("GPU        : " + Gpu);
            sb.AppendLine("Display    : " + Display);
            sb.AppendLine("Audio      : " + Audio);
            sb.AppendLine("Game build : " + GameBuild);
            sb.AppendLine("Hotfix     : " + Hotfix);
            sb.AppendLine("PC         : " + Reporting.MachineTag());
            return Reporting.Sanitize(sb.ToString());
        }

        public void AddTo(DiscordEmbed e)
        {
            e.Add("Windows", Reporting.Sanitize(Windows), true);
            e.Add("GPU", Reporting.Sanitize(Gpu), true);
            e.Add("CPU / RAM", Reporting.Sanitize(Cpu + " / " + Ram), true);
            e.Add("Display", Reporting.Sanitize(Display), true);
            e.Add("Audio outputs", Reporting.Sanitize(Audio), true);
            e.Add("Game / hotfix", Reporting.Sanitize(GameBuild + " / KH Hotfix " + Hotfix), true);
        }
    }

    // Screenshots of the game window for bug reports.
    internal static class Screenshot
    {
        // The game window as shown, or null (minimized, not found). Never blocks on a frozen game for long.
        public static Bitmap Capture(IntPtr hwnd, bool hung)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || Native.IsIconic(hwnd)) return null;
            Native.RECT r;
            if (!Native.WindowBounds(hwnd, out r)) return null;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 16 || h < 16 || w > 16384 || h > 16384) return null;
            if (!hung)
            {
                Bitmap printed = null;
                var done = new ManualResetEvent(false);
                var t = new Thread(delegate()
                {
                    try
                    {
                        var b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                        bool ok;
                        using (Graphics g = Graphics.FromImage(b))
                        {
                            IntPtr dc = g.GetHdc();
                            try { ok = Native.PrintWindow(hwnd, dc, 2 /* PW_RENDERFULLCONTENT */); }
                            finally { g.ReleaseHdc(dc); }
                        }
                        if (ok) printed = b; else b.Dispose();
                    }
                    catch { }
                    finally { done.Set(); }
                });
                t.IsBackground = true;
                t.Start();
                if (done.WaitOne(3000) && printed != null)
                {
                    if (!MostlyBlack(printed)) return printed;
                    printed.Dispose();
                }
            }
            var s = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(s)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h));
            return s;
        }

        static bool MostlyBlack(Bitmap b)
        {
            for (int y = 1; y < 8; y++)
                for (int x = 1; x < 8; x++)
                {
                    Color c = b.GetPixel(b.Width * x / 8, b.Height * y / 8);
                    if (c.R + c.G + c.B > 24) return false;
                }
            return true;
        }

        public static byte[] Jpeg(Bitmap b, int maxWidth, long quality)
        {
            Bitmap scaled = null;
            Bitmap src = b;
            if (b.Width > maxWidth)
            {
                int h = Math.Max(1, (int)Math.Round(b.Height * (double)maxWidth / b.Width));
                scaled = new Bitmap(maxWidth, h, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(b, 0, 0, maxWidth, h);
                }
                src = scaled;
            }
            try
            {
                ImageCodecInfo jpg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using (var p = new EncoderParameters(1))
                using (var ms = new MemoryStream())
                {
                    p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                    src.Save(ms, jpg, p);
                    return ms.ToArray();
                }
            }
            finally { if (scaled != null) scaled.Dispose(); }
        }
    }

    // Automatic reports to DiscordLogWebhook: crashes and freezes (with the crash report) and sessions in which the
    // hotfix had to step in (lost sound, released loading screens, deleted saves, ...).
    internal static class LogReport
    {
        public static Discord.Message Compose(string headline, string kind, string reportDir, IList<string> notices, string sessionLog, string gameName, SystemInfo sys)
        {
            string game = string.IsNullOrEmpty(gameName) ? "KINGDOM HEARTS" : gameName;
            headline = Reporting.Sanitize(string.IsNullOrEmpty(headline) ? "KH Hotfix report" : headline);   // notices can name save paths with the Steam id
            var m = new Discord.Message();
            m.Username = Reporting.Who() + " (KH Hotfix)";
            m.ThreadName = TextUtil.Clip("[" + Reporting.ShortGame(game) + "] " + headline, 82) + " - " + DateTime.Now.ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            sb.Append("**").Append(TextUtil.Clip(headline, 300)).Append("**");
            foreach (string n in notices ?? new List<string>()) sb.Append("\n- ").Append(TextUtil.Clip(Reporting.Sanitize(n), 400));
            m.Content = TextUtil.Clip(sb.ToString(), 1900);

            var e = new DiscordEmbed
            {
                Title = kind == "crash" ? "Crash report" : kind == "freeze" ? "Freeze report" : "Session report",
                Color = kind == "crash" ? Reporting.Red : kind == "freeze" ? Reporting.Orange : Reporting.Gray,
                Timestamp = DateTime.Now,
                Footer = "Sent automatically by KH Hotfix " + Program.Version + " | " + Reporting.MachineTag()
            };
            e.Add("Game", game, true);
            e.Add("Reported by", Reporting.Who(), true);
            string summaryPath = reportDir == null ? null : Path.Combine(reportDir, "summary.txt");
            if (summaryPath != null && File.Exists(summaryPath))
            {
                string summary = Encoding.UTF8.GetString(Reporting.TextFile(summaryPath, 200000));
                var sigs = Regex.Matches(summary, @"Signature: (.+)").Cast<Match>().Select(x => x.Groups[1].Value.Trim()).Distinct().ToList();
                if (sigs.Count > 0) e.Add("Signature", "`" + string.Join("`\n`", sigs.Take(3)) + "`", false);
                var known = Regex.Matches(summary, @"Known issue: (.+)").Cast<Match>().Select(x => x.Groups[1].Value.Trim()).Distinct().ToList();
                if (known.Count > 0) e.Add("Known issue", string.Join("\n", known), false);
                m.Files.Add(new Discord.Attachment("summary.txt", Reporting.Utf8(summary), false));
            }
            if (sys != null) sys.AddTo(e);
            m.Embed = e;

            if (sessionLog != null && File.Exists(sessionLog)) m.Files.Add(new Discord.Attachment("session.log", Reporting.TextFile(sessionLog, 2000000), false));
            if (sys != null) m.Files.Add(new Discord.Attachment("system.txt", Reporting.Utf8(sys.ToText()), true));
            if (reportDir != null && Directory.Exists(reportDir))
            {
                foreach (string name in new[] { "events.txt", "controlled-folder-access.txt" })
                {
                    string f = Path.Combine(reportDir, name);
                    if (File.Exists(f)) m.Files.Add(new Discord.Attachment(name, Reporting.TextFile(f, 200000), true));
                }
                foreach (string dmp in Directory.GetFiles(reportDir, "*.dmp").OrderBy(f => new FileInfo(f).Length))
                {
                    try
                    {
                        if (new FileInfo(dmp).Length > 256L * 1024 * 1024) continue;
                        m.Files.Add(new Discord.Attachment(Path.GetFileName(dmp) + ".gz", Reporting.GzipFile(dmp), true));
                    }
                    catch (Exception ex) { Log.Warn("Could not pack " + dmp + " for Discord: " + ex.Message); }
                }
            }
            return m;
        }

        // Sends the report and returns the line for the session summary (null when Discord reporting is off).
        public static string Send(string headline, string kind, string reportDir, IList<string> notices, string sessionLog, string gameName, string gameDir)
        {
            string webhook = Settings.DiscordLogWebhook;
            if (string.IsNullOrEmpty(webhook)) return null;
            if (!Discord.IsWebhook(webhook)) { Log.Warn("DiscordLogWebhook is not a valid Discord webhook URL - nothing was sent"); return null; }
            Discord.Message m = Compose(headline, kind, reportDir, notices, sessionLog, gameName, SystemInfo.Collect(gameDir));
            Discord.Result r = Discord.Send(webhook, m, 45000);
            if (r.Dropped.Count > 0) Log.Warn("Discord: left out (too large): " + string.Join(", ", r.Dropped));
            if (r.Ok)
            {
                Log.Info("Report sent to Discord (" + Discord.Describe(webhook) + ")" + (r.Link != null ? ": " + r.Link : ""));
                if (reportDir != null) Reporting.MarkSent(reportDir, r);
                return kind == "notes" ? "A report about this session was sent to Discord." : "The " + (kind == "crash" ? "crash" : "freeze") + " report was sent to Discord automatically.";
            }
            Log.Warn("Sending the report to Discord failed (" + Discord.Describe(webhook) + "): " + r.Error);
            return "Could not send the report to Discord: " + r.Error + (reportDir != null ? " It is saved in " + reportDir + "; you can send it with Ctrl+Alt+B or Start menu > KH Hotfix > Report a KINGDOM HEARTS bug." : "");
        }
    }
}
