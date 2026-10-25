using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KHHotfix
{
    internal static class Wrapper
    {
        public static int Run(string[] args)
        {
            DateTime start = DateTime.Now;
            Log.Open("session-" + start.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Log.Echo = Program.Cli;
            Log.Info("KHHotfix " + Program.Version + " | " + OsDescription() + " | " + (Environment.Is64BitProcess ? "x64" : "x86"));
            Log.Info("Command line: " + Environment.CommandLine);
            string exe = Path.GetFullPath(args[0]);
            string gameArgs = CommandLine.Remainder(Environment.CommandLine, 2);
            if (Settings.ExtraGameArgs.Length > 0 && gameArgs.IndexOf(Settings.ExtraGameArgs, StringComparison.OrdinalIgnoreCase) < 0)
                gameArgs = (gameArgs + " " + Settings.ExtraGameArgs).Trim();
            if (!File.Exists(exe)) { Ui.Error("Game executable not found:\r\n" + exe); return 1; }

            var session = new Session(exe, gameArgs, start);
            try { return session.Run(); }
            catch (Exception ex)
            {
                Log.Error("Unexpected hotfix failure: " + ex);
                return session.GameStarted ? 1 : Session.LaunchDirect(exe, gameArgs); // never block playing
            }
        }

        static string OsDescription()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    return k.GetValue("ProductName") + " " + k.GetValue("DisplayVersion") + " build " + k.GetValue("CurrentBuild") + "." + k.GetValue("UBR");
            }
            catch { return Environment.OSVersion.ToString(); }
        }
    }

    internal sealed class Session
    {
        readonly string exe, gameArgs, gameDir, gameDirPrefix;
        readonly DateTime startLocal;
        readonly Dictionary<int, TrackedProcess> live = new Dictionary<int, TrackedProcess>();
        readonly List<TrackedProcess> done = new List<TrackedProcess>();
        readonly HashSet<int> unopenable = new HashSet<int>();
        readonly List<string> postNotices = new List<string>();
        public bool GameStarted;
        Process root;
        TrackedProcess rootTracked;
        SaveGuard saves;
        DisplayGuard display;
        ApplicationContext ctx;
        DateTime idleSinceUtc = DateTime.MinValue;
        bool busy, finishing, panicked;
        int tick;

        public Session(string exe, string gameArgs, DateTime startLocal)
        {
            this.exe = exe;
            this.gameArgs = gameArgs;
            this.startLocal = startLocal;
            gameDir = Path.GetDirectoryName(exe);
            gameDirPrefix = gameDir.TrimEnd('\\') + "\\";
        }

        static void Step(string name, Action a)
        {
            try { a(); }
            catch (Exception ex) { Log.Error(name + " failed: " + ex); }
        }

        public int Run()
        {
            bool createdNew;
            using (var mutex = new Mutex(false, @"Local\KHHotfix.Session" + Environment.GetEnvironmentVariable("KHHOTFIX_INSTANCE"), out createdNew))
            {
                if (!createdNew)
                {
                    Log.Warn("Another KHHotfix session is active - starting the game without guards");
                    return LaunchDirect(exe, gameArgs);
                }
                return RunGuarded();
            }
        }

        int RunGuarded()
        {
            var notices = new List<string>();
            saves = new SaveGuard();
            display = new DisplayGuard();
            Log.Info("Saves folder: " + Paths.SavesRoot + " | Documents (ANSI view): " + Paths.DocumentsAnsi);
            Step("display recovery", delegate { DisplayGuard.RecoverStale(); });
            Step("save preflight", delegate { saves.Preflight(notices); });
            Step("save integrity check", delegate { saves.HealBeforeLaunch(notices); });
            Step("pre-launch backup", delegate { saves.SnapshotAll("pre-launch"); });
            Ui.Notice("KINGDOM HEARTS save check (before launch):", notices, MessageBoxIcon.Warning);
            Step("refresh-rate switch", delegate { display.Apply(); });
            string report = null;
            try
            {
                StartGame();
                RunLoop();
            }
            finally
            {
                Step("refresh-rate restore", delegate { display.Restore(); });
                Step("post-exit backup", delegate { saves.SnapshotAll("post-exit"); });
                Step("crash report", delegate { report = CollectReport(); });
                postNotices.AddRange(saves.SessionNotices);
                if (rootTracked != null && rootTracked.Exited && !rootTracked.KilledByHotfix && rootTracked.ExitCode != 0 && !rootTracked.HadWindow
                    && done.Count + live.Count == 1 && (rootTracked.ExitedUtc - rootTracked.FirstSeenUtc).TotalSeconds < 20
                    && rootTracked.Name.Equals("KINGDOM HEARTS HD 1.5+2.5 ReMIX", StringComparison.OrdinalIgnoreCase))
                    postNotices.Add("The game closed immediately (code 0x" + rootTracked.ExitCode.ToString("X8") + CrashCodes.Describe(rootTracked.ExitCode)
                        + ") before its launcher opened. If the game is shared through Steam Families, another family member may be using it (the licence is locked). "
                        + "Otherwise use Steam > Properties > Installed Files > Verify integrity.");
                Log.Info("Session finished. Backups written this session: " + saves.SnapshotsTaken);
                if (report != null) { try { File.Copy(Log.FilePath, Path.Combine(report, "hotfix-session.log"), true); } catch { } }
                Ui.Notice("KINGDOM HEARTS session summary:", postNotices, MessageBoxIcon.Warning);
            }
            return rootTracked != null && rootTracked.Exited ? unchecked((int)rootTracked.ExitCode) : 0;
        }

        void StartGame()
        {
            var psi = new ProcessStartInfo(exe, gameArgs) { UseShellExecute = false, WorkingDirectory = gameDir };
            Log.Info("Starting \"" + exe + "\" " + gameArgs);
            root = Process.Start(psi);
            GameStarted = true;
            rootTracked = new TrackedProcess(root.Id, exe, root.Handle, false);
            live[root.Id] = rootTracked;
            Log.Info("Tracking " + rootTracked.Label);
        }

        void RunLoop()
        {
            ctx = new ApplicationContext();
            var hotkey = new HotkeyWindow();
            if (Settings.PanicHotkey.Length > 0)
            {
                if (hotkey.Register(Settings.PanicHotkey)) Log.Info("Panic hotkey active: " + Settings.PanicHotkey);
                else Log.Warn("Panic hotkey unavailable: " + Settings.PanicHotkey);
            }
            hotkey.Pressed += OnPanic;
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate { SafeTick(); };
            timer.Start();
            Application.Run(ctx);
            timer.Stop();
            timer.Dispose();
            hotkey.Dispose();
            foreach (TrackedProcess t in live.Values) { if (t.OwnsHandle && t.Handle != IntPtr.Zero) Native.CloseHandle(t.Handle); }
        }

        void SafeTick()
        {
            if (busy || finishing) return;
            busy = true;
            try { Tick(); }
            catch (Exception ex) { Log.Error("Monitor tick failed: " + ex); }
            finally { busy = false; }
        }

        void Tick()
        {
            tick++;
            DateTime now = DateTime.UtcNow;
            Discover();
            ApplyFixes();
            PollExits();
            Watch(now);
            if (tick % 2 == 0) saves.Poll();
            if (live.Count > 0) { idleSinceUtc = DateTime.MinValue; return; }
            if (idleSinceUtc == DateTime.MinValue) idleSinceUtc = now;
            if ((now - idleSinceUtc).TotalSeconds >= Settings.ExitGraceSeconds)
            {
                finishing = true;
                Log.Info("No KINGDOM HEARTS process left - ending session");
                ctx.ExitThread();
            }
        }

        void Discover()
        {
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception ex) { Log.Warn("Process enumeration failed: " + ex.Message); return; }
            foreach (Process p in all)
            {
                try
                {
                    int pid = p.Id;
                    if (live.ContainsKey(pid) || unopenable.Contains(pid)) continue;
                    string n = p.ProcessName;
                    if (!n.StartsWith("KINGDOM HEARTS", StringComparison.OrdinalIgnoreCase) && !n.Equals("WaitTitleProject", StringComparison.OrdinalIgnoreCase)) continue;
                    string path = Native.GetImagePath(pid);
                    if (path == null || !path.StartsWith(gameDirPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    IntPtr h = Native.OpenProcess(Native.SYNCHRONIZE | Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_TERMINATE, false, pid);
                    if (h == IntPtr.Zero) h = Native.OpenProcess(Native.SYNCHRONIZE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h == IntPtr.Zero) { unopenable.Add(pid); Log.Warn("Cannot open " + n + " (pid " + pid + "), error " + Marshal.GetLastWin32Error()); continue; }
                    var t = new TrackedProcess(pid, path, h, true);
                    live[pid] = t;
                    Log.Info("Tracking " + t.Label);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        void ApplyFixes()
        {
            if (!Settings.FixQuitCrash) return;
            foreach (TrackedProcess t in live.Values)
            {
                if (t.FixDone || !t.IsGame || t.Handle == IntPtr.Zero) continue;
                t.FixAttempts++;
                GameFixes.Result r;
                try { r = GameFixes.Apply(t.Pid, t.ImagePath); }
                catch (Exception ex) { r = new GameFixes.Result(GameFixes.Outcome.Failed, ex.Message); }
                if (r.Outcome == GameFixes.Outcome.Retry && t.FixAttempts < 20) continue;
                t.FixDone = true;
                if (r.Outcome == GameFixes.Outcome.Applied || r.Outcome == GameFixes.Outcome.AlreadyApplied) Log.Info(t.Label + " code fixes: " + r.Message);
                else Log.Warn(t.Label + " code fixes not applied: " + r.Message);
            }
        }

        void PollExits()
        {
            foreach (TrackedProcess t in live.Values.ToList())
            {
                if (Native.WaitForSingleObject(t.Handle, 0) != 0) continue;
                uint code;
                Native.GetExitCodeProcess(t.Handle, out code);
                t.Exited = true;
                t.ExitCode = code;
                t.ExitedUtc = DateTime.UtcNow;
                if (t.OwnsHandle) Native.CloseHandle(t.Handle);
                t.Handle = IntPtr.Zero;
                live.Remove(t.Pid);
                done.Add(t);
                string hex = "0x" + code.ToString("X8");
                if (t.KilledByHotfix) Log.Info(t.Label + " was terminated by the hotfix (" + t.KillReason + ")");
                else if (CrashCodes.IsCrash(code)) Log.Error(t.Label + " CRASHED, exit code " + hex + CrashCodes.Describe(code));
                else Log.Info(t.Label + " exited, code " + hex + CrashCodes.Describe(code));
            }
        }

        void Watch(DateTime now)
        {
            if (live.Count == 0) return;
            Dictionary<int, Native.WinState> wins = Native.SnapshotWindows(live.Keys.ToList());
            foreach (TrackedProcess t in live.Values.ToList())
            {
                Native.WinState w;
                wins.TryGetValue(t.Pid, out w);
                bool visible = w != null && w.Visible > 0;
                bool hung = visible && w.Hung;
                if (visible)
                {
                    if (!t.HadWindow) Log.Info(t.Label + " opened its window");
                    t.HadWindow = true;
                    t.WindowGoneLogged = false;
                    t.LastVisibleUtc = now;
                }
                else if (t.HadWindow && !t.WindowGoneLogged)
                {
                    t.WindowGoneLogged = true;
                    Log.Info(t.Label + " has no visible window any more");
                }
                if (hung && t.HungSinceUtc == DateTime.MinValue) { t.HungSinceUtc = now; Log.Warn(t.Label + " is not responding"); }
                else if (!hung && t.HungSinceUtc != DateTime.MinValue)
                {
                    Log.Info(t.Label + " is responding again after " + (int)(now - t.HungSinceUtc).TotalSeconds + " s");
                    t.HungSinceUtc = DateTime.MinValue;
                }
                t.SampleCpu(now);
                if (t.KilledByHotfix) continue;

                // Known bug: after "Quit Game" the game closes its window but the process never exits.
                if (Settings.QuitHangKillSeconds > 0 && t.IsGame && t.HadWindow && !visible)
                {
                    double gone = (now - t.LastVisibleUtc).TotalSeconds;
                    double cpuUsed = t.CpuSecondsOver(Math.Min(10, Settings.QuitHangKillSeconds), now);
                    if (gone >= Settings.QuitHangKillSeconds && cpuUsed >= 0 && cpuUsed < 0.5)
                        Kill(t, string.Format(CultureInfo.InvariantCulture, "closed its window {0:0} s ago but never exited (shutdown hang)", gone));
                }
                else if (Settings.HungWindowKillSeconds > 0 && t.IsHangWatched && t.HungSinceUtc != DateTime.MinValue
                    && (now - t.HungSinceUtc).TotalSeconds >= Settings.HungWindowKillSeconds)
                {
                    Kill(t, string.Format(CultureInfo.InvariantCulture, "stopped responding for {0:0} s (frozen)", (now - t.HungSinceUtc).TotalSeconds));
                }
            }
        }

        void Kill(TrackedProcess t, string reason)
        {
            Log.Warn("Watchdog: terminating " + t.Label + " - it " + reason);
            saves.WaitForStableAndSnapshot("before watchdog termination", 5000);
            t.KilledByHotfix = true;
            t.KillReason = reason;
            if (!Native.TerminateProcess(t.Handle, CrashCodes.KilledByHotfix)) Log.Error("TerminateProcess failed, error " + Marshal.GetLastWin32Error());
            postNotices.Add(t.Name + ".exe " + reason + ". The hotfix closed it for you after backing up your saves.");
        }

        void OnPanic()
        {
            if (finishing || panicked) return;
            panicked = true;
            Log.Warn("Panic hotkey pressed - closing all KINGDOM HEARTS processes");
            saves.WaitForStableAndSnapshot("panic hotkey", 3000);
            foreach (TrackedProcess t in live.Values.ToList())
            {
                t.KilledByHotfix = true;
                t.KillReason = "panic hotkey";
                if (!Native.TerminateProcess(t.Handle, CrashCodes.KilledByHotfix)) Log.Error("TerminateProcess failed for " + t.Label);
            }
            postNotices.Add("You pressed the panic hotkey (" + Settings.PanicHotkey + "). The game was closed after your saves were backed up.");
        }

        string CollectReport()
        {
            List<TrackedProcess> procs = done.Concat(live.Values).OrderBy(p => p.FirstSeenUtc).ToList();
            List<TrackedProcess> crashed = procs.Where(p => p.Exited && !p.KilledByHotfix && CrashCodes.IsCrash(p.ExitCode)).ToList();
            List<TrackedProcess> killed = procs.Where(p => p.KilledByHotfix).ToList();
            DateTime since = startLocal.AddSeconds(-10);
            if (crashed.Count > 0) Thread.Sleep(3000); // give Windows Error Reporting time to log the crash

            var dumps = new List<string>();
            AddRecent(dumps, gameDir, "*.dmp", since);
            AddRecent(dumps, Paths.SavesRoot, "*.dmp", since);
            AddRecent(dumps, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"), "KINGDOM HEARTS*.dmp", since);
            AddRecent(dumps, Paths.CrashDumps, "*.dmp", since);
            List<string> wer = RecentWerReports(since);
            string events = Events.Query("Application", new[] { 1000, 1001, 1002, 1026 }, since, new[] { "KINGDOM HEARTS", "WaitTitleProject" });
            string cfa = Events.Query("Microsoft-Windows-Windows Defender/Operational", new[] { 1123, 1124 }, since, new[] { "KINGDOM HEARTS" });
            if (cfa.Length > 0)
                postNotices.Add("Windows Controlled Folder Access BLOCKED the game from writing its saves during this session - that is why saving fails. "
                    + "Run Install.cmd again and answer Y when it offers to allow the game, or allow the KH executables in Windows Security > Ransomware protection.");
            if (crashed.Count == 0 && killed.Count == 0 && dumps.Count == 0 && wer.Count == 0 && events.Length == 0 && cfa.Length == 0) return null;

            string dir = Path.Combine(Paths.Reports, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine("KH 1.5+2.5 Hotfix " + Program.Version + " - crash/hang report");
            sb.AppendLine("Session start : " + startLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("Session end   : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("Game folder   : " + gameDir);
            sb.AppendLine("Saves folder  : " + Paths.SavesRoot);
            sb.AppendLine();
            sb.AppendLine("Processes:");
            foreach (TrackedProcess p in procs)
            {
                string state = !p.Exited ? "still running" : "exit 0x" + p.ExitCode.ToString("X8") + CrashCodes.Describe(p.ExitCode);
                if (p.KilledByHotfix) state += " [terminated by hotfix: " + p.KillReason + "]";
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,-45} pid {1,-6} seen {2:HH:mm:ss}  {3}", p.Name + ".exe", p.Pid, p.FirstSeenUtc.ToLocalTime(), state));
            }
            sb.AppendLine();
            sb.AppendLine("Dumps collected: " + (dumps.Count == 0 ? "none" : string.Join(", ", dumps.Select(Path.GetFileName))));
            sb.AppendLine("WER reports    : " + (wer.Count == 0 ? "none" : string.Join(", ", wer.Select(Path.GetFileName))));
            File.WriteAllText(Path.Combine(dir, "summary.txt"), sb.ToString());
            if (events.Length > 0) File.WriteAllText(Path.Combine(dir, "events.txt"), events);
            if (cfa.Length > 0) File.WriteAllText(Path.Combine(dir, "controlled-folder-access.txt"), cfa);
            foreach (string d in dumps)
            {
                try
                {
                    string target = Path.Combine(dir, Path.GetFileName(d));
                    // The game always reuses "<exe name>.dmp", so move it out before the next crash overwrites it.
                    if (d.StartsWith(gameDirPrefix, StringComparison.OrdinalIgnoreCase)) File.Move(d, target);
                    else File.Copy(d, target, true);
                }
                catch (Exception ex) { Log.Warn("Could not collect " + d + ": " + ex.Message); }
            }
            foreach (string w in wer)
            {
                try
                {
                    string target = Path.Combine(dir, "WER", Path.GetFileName(w));
                    Directory.CreateDirectory(target);
                    foreach (string f in Directory.GetFiles(w)) { if (new FileInfo(f).Length < 512L * 1024 * 1024) File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true); }
                }
                catch (Exception ex) { Log.Warn("Could not collect WER report " + w + ": " + ex.Message); }
            }
            var known = new List<string>();
            foreach (string dmp in Directory.GetFiles(dir, "*.dmp"))
            {
                try
                {
                    DumpAnalyzer.Report r = DumpAnalyzer.Analyze(dmp);
                    DumpAnalyzer.Classify(r, gameDir);
                    File.AppendAllText(Path.Combine(dir, "summary.txt"), Environment.NewLine + DumpAnalyzer.Format(r, Path.GetFileName(dmp)));
                    Log.Info("Crash signature: " + DumpAnalyzer.Signature(r));
                    if (r.KnownIssue != null) known.Add(r.KnownIssue);
                }
                catch (Exception ex) { Log.Warn("Crash dump analysis failed for " + dmp + ": " + ex.Message); }
            }
            if (crashed.Count > 0)
                postNotices.Insert(0, string.Join(", ", crashed.Select(c => c.Name + ".exe (0x" + c.ExitCode.ToString("X8") + CrashCodes.Describe(c.ExitCode) + ")"))
                    + " crashed. Your saves were backed up; a crash report was written to " + dir + ". Use Restore.cmd if the game now reports a save as corrupt."
                    + (known.Count > 0 ? " Recognized: " + string.Join("; ", known.Distinct()) + "." : " Sharing Reports\\" + Path.GetFileName(dir) + "\\summary.txt helps to fix this crash."));
            Log.Info("Crash/hang report written to " + dir);
            return dir;
        }

        static void AddRecent(List<string> list, string dir, string pattern, DateTime since)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir, pattern)) { if (File.GetLastWriteTime(f) >= since) list.Add(f); }
            }
            catch { }
        }

        static List<string> RecentWerReports(DateTime since)
        {
            var list = new List<string>();
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (string r in new[] { pd, la })
            {
                foreach (string sub in new[] { @"Microsoft\Windows\WER\ReportArchive", @"Microsoft\Windows\WER\ReportQueue" })
                {
                    try
                    {
                        string root = Path.Combine(r, sub);
                        if (!Directory.Exists(root)) continue;
                        foreach (string d in Directory.GetDirectories(root))
                        {
                            string n = Path.GetFileName(d);
                            if (n.IndexOf("KINGDOM HEARTS", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("WaitTitle", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (Directory.GetCreationTime(d) >= since) list.Add(d);
                        }
                    }
                    catch { }
                }
            }
            return list;
        }

        public static int LaunchDirect(string exe, string args)
        {
            try
            {
                Process p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
                p.WaitForExit();
                int code = p.ExitCode;
                // The bootstrapper may exit before the game itself; keep Steam's "running" state accurate.
                DateTime idle = DateTime.MinValue;
                while (true)
                {
                    bool any = Process.GetProcesses().Any(x => { try { return x.ProcessName.StartsWith("KINGDOM HEARTS", StringComparison.OrdinalIgnoreCase); } catch { return false; } });
                    if (any) idle = DateTime.MinValue;
                    else if (idle == DateTime.MinValue) idle = DateTime.UtcNow;
                    else if ((DateTime.UtcNow - idle).TotalSeconds >= Settings.ExitGraceSeconds) break;
                    Thread.Sleep(2000);
                }
                return code;
            }
            catch (Exception ex)
            {
                Ui.Error("Could not start the game:\r\n" + ex.Message);
                return 1;
            }
        }
    }
}
