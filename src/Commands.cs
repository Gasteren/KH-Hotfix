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
                case "--loading-status": return LoadingCommand(a, false);
                case "--loading-rescue": return LoadingCommand(a, true);
                case "--audio-devices": return AudioCommand();
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
                        + "--fixes-apply <pid> --loading-status <pid> --loading-rescue <pid> --audio-devices --selftest --paths --version");
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
