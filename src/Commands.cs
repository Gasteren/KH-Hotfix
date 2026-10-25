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
                case "--fixes-scan": return FixesScan(a.Length > 1 ? a[1] : null);
                case "--fixes-apply": return FixesApply(a);
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
                        + "--fixes-apply <pid> --selftest --paths --version");
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
