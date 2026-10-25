using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;

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
                default:
                    Console.Error.WriteLine("Unknown command " + a[0] + ". Commands: --check --backup --restore-backup <gz> <target> [--force] --display-info "
                        + "--restore-display --nv-status --nv-apply [--fps N] [--no-vsync] [--no-gsync-off] --nv-revert --selftest --paths --version");
                    return 2;
            }
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

            Console.WriteLine("Backups, retention and auto-heal (temporary folder)");
            string saveRootBackup = Paths.SavesRoot, backupsBackup = Paths.Backups, quarantineBackup = Paths.Quarantine;
            int keepBackup = Settings.BackupKeep, daysBackup = Settings.BackupDailyDays;
            bool healBackup = Settings.AutoHeal;
            string tmp = Path.Combine(Path.GetTempPath(), "KHHotfix-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Paths.SavesRoot = Path.Combine(tmp, "saves");
                Paths.Backups = Path.Combine(tmp, "backups");
                Paths.Quarantine = Path.Combine(Paths.Backups, "_quarantine");
                Settings.AutoHeal = true;
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
                var notices = new List<string>();
                sg.HealBeforeLaunch(notices);
                Expect(File.ReadAllBytes(save).SequenceEqual(good), "save cut inside PNG chunks is restored from the last good backup");
                Expect(notices.Count == 1 && notices[0].Contains("restored"), "user is told about the restore");
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
                Expect((File.GetAttributes(save) & FileAttributes.ReadOnly) == 0 && notices.Any(n => n.Contains("read-only")), "read-only save flag removed");

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
                try { foreach (string f in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(tmp, true); } catch { }
            }

            Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : failures + " TEST(S) FAILED");
            return failures == 0 ? 0 : 1;
        }
    }
}
