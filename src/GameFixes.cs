using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KHHotfix
{
    // Code fixes applied to a running KH process in memory. The game files on disk are never modified.
    // Every fix is located by a byte signature that must match exactly once, and the running image must be the same
    // build as the file on disk, so a game update simply disables a fix instead of corrupting anything.
    internal static class GameFixes
    {
        internal sealed class Fix
        {
            public string Id, Exe, Description;
            public byte?[] Pattern;
            public int Offset, Length;
            public int ExitReg;   // register holding the exit code: 3 = ebx, 7 = edi
        }

        internal enum Outcome { Applied, AlreadyApplied, NotApplicable, Retry, Failed }

        internal sealed class Result
        {
            public Outcome Outcome;
            public string Message;
            public Result(Outcome o, string m) { Outcome = o; Message = m; }
        }

        // Quit crash/freeze: when the main loop ends, the engine runs the application destructor, which frees an
        // already released buffer (access violation in _aligned_free) or deadlocks. Saves and settings are written
        // while playing, so the destructor call is replaced with TerminateProcess(GetCurrentProcess(), exitCode).
        // First the patch does what the engine's startup code does once axaAppMain has returned: it hands the exit code
        // to the collection's bootstrapper (KINGDOM HEARTS HD 1.5+2.5 ReMIX.exe) through the shared memory "Mare_mem".
        // The bootstrapper reads it when the game has ended and acts on it: 0x100 = quit, 1 = start the game again with
        // -reboot=true (its title screen), 0 = open the collection menu. Without it, going back to the title screen or
        // the menu ends on the endless loading screen of WaitTitleProject.exe. The exit code itself is kept as well.
        const string QuitDescription = "Quit Game crash/freeze";
        internal static readonly Fix[] Catalog =
        {
            Quit("KINGDOM HEARTS FINAL MIX.exe", "0F 49 D9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? | 48 8D 05 ?? ?? ?? ?? 48 8D 8C 24 40 01 00 00 48 89 44 24 20 E8 ?? ?? ?? ?? | 8B C3", 3),
            Quit("KINGDOM HEARTS II FINAL MIX.exe", "0F 49 D9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? | 48 8D 05 ?? ?? ?? ?? 48 8D 8C 24 40 01 00 00 48 89 44 24 20 E8 ?? ?? ?? ?? | 8B C3", 3),
            Quit("KINGDOM HEARTS Birth by Sleep FINAL MIX.exe", "0F 49 D9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? | 90 48 8D 05 ?? ?? ?? ?? 48 89 44 24 20 48 8D 8C 24 ?? ?? ?? ?? E8 ?? ?? ?? ?? | 8B C3", 3),
            Quit("KINGDOM HEARTS Re_Chain of Memories.exe", "0F 49 D9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? | 90 48 8D 05 ?? ?? ?? ?? 48 89 44 24 20 48 8D 4D 50 E8 ?? ?? ?? ?? | 8B C3", 3),
            Quit("KINGDOM HEARTS Theater.exe", "0F 49 F9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? | 48 8D 05 ?? ?? ?? ?? 48 8D 8C 24 40 01 00 00 48 89 44 24 20 E8 ?? ?? ?? ?? | 48 8B B4 24 ?? ?? ?? ?? 8B C7", 7),
        };

        // The engine function that stores a value in "Mare_mem" (CreateFileMappingA, MapViewOfFile, write, unmap, close).
        // It is the same in all five games apart from its rip-relative displacements; the lea at MareNameLea must load
        // the name "Mare_mem".
        internal const string MareWriterSignature = "48 89 5C 24 08 57 48 83 EC 30 45 33 C9 48 8D 05 ?? ?? ?? ?? 33 D2 48 89 44 24 28 8B F9 "
            + "C7 44 24 20 04 00 00 00 45 8D 41 04 48 8D 4A FF FF 15 ?? ?? ?? ?? 48 8B D8 48 85 C0 74 35 45 33 C9 48 C7 44 24 20 "
            + "04 00 00 00 45 33 C0 48 8B C8 41 8D 51 02 FF 15 ?? ?? ?? ?? 48 85 C0 74 0B 48 8B C8 89 38 FF 15 ?? ?? ?? ?? 48 8B CB "
            + "FF 15 ?? ?? ?? ?? 48 8B 5C 24 40 48 83 C4 30 5F C3";
        internal const int MareNameLea = 13;
        static readonly byte?[] MareWriterPattern = Pattern(MareWriterSignature);

        static Fix Quit(string exe, string signature, int exitReg)
        {
            int offset, length;
            byte?[] pattern = ParsePattern(signature, out offset, out length);
            return new Fix { Id = "quit", Exe = exe, Description = QuitDescription, Pattern = pattern, Offset = offset, Length = length, ExitReg = exitReg };
        }

        static byte?[] Pattern(string signature) { int offset, length; return ParsePattern(signature, out offset, out length); }

        // "AA ?? BB | CC DD | EE": ?? is a wildcard, the bytes between the two | markers are the patch region.
        internal static byte?[] ParsePattern(string signature, out int offset, out int length)
        {
            var bytes = new List<byte?>();
            int start = -1, end = -1;
            foreach (string tok in signature.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok == "|") { if (start < 0) start = bytes.Count; else end = bytes.Count; continue; }
                bytes.Add(tok == "??" ? (byte?)null : byte.Parse(tok, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            offset = start < 0 ? 0 : start;
            length = end < 0 ? bytes.Count - offset : end - offset;
            return bytes.ToArray();
        }

        internal static List<int> FindAll(byte[] data, int from, int to, byte?[] pattern)
        {
            var hits = new List<int>();
            byte first = pattern[0].Value;
            int last = Math.Min(to, data.Length) - pattern.Length;
            for (int i = Math.Max(0, from); i <= last; i++)
            {
                if (data[i] != first) continue;
                int k = 1;
                while (k < pattern.Length && (!pattern[k].HasValue || data[i + k] == pattern[k].Value)) k++;
                if (k == pattern.Length) hits.Add(i);
            }
            return hits;
        }

        internal static bool Matches(byte[] data, int at, byte?[] pattern)
        {
            if (at < 0 || at + pattern.Length > data.Length) return false;
            for (int k = 0; k < pattern.Length; k++) if (pattern[k].HasValue && data[at + k] != pattern[k].Value) return false;
            return true;
        }

        // Patch placed at RVA `at`: mov ecx, <exit reg> ; call <Mare_mem writer> ; mov edx, <exit reg> (or mov edx, imm32
        // for a diagnostic marker) ; or rcx, -1 ; call [rip + <TerminateProcess import slot>] ; int3 padding.
        // 19 bytes, 22 with a marker. The writer keeps rbx and rdi, so the exit code survives the call.
        internal static byte[] BuildQuitPatch(int exitReg, int at, int writer, int terminateSlot, int length, uint? markerExitCode = null)
        {
            var p = new List<byte> { 0x89, (byte)(0xC0 | exitReg << 3 | 1), 0xE8 };
            p.AddRange(BitConverter.GetBytes(writer - (at + p.Count + 4)));
            if (markerExitCode.HasValue) { p.Add(0xBA); p.AddRange(BitConverter.GetBytes(markerExitCode.Value)); }
            else { p.Add(0x89); p.Add((byte)(0xC0 | exitReg << 3 | 2)); }
            p.AddRange(new byte[] { 0x48, 0x83, 0xC9, 0xFF, 0xFF, 0x15 });
            p.AddRange(BitConverter.GetBytes(terminateSlot - (at + p.Count + 4)));
            if (p.Count > length) throw new InvalidOperationException("patch does not fit");
            while (p.Count < length) p.Add(0xCC);
            return p.ToArray();
        }

        // RVA of the engine's "Mare_mem" writer, or 0 when that code is missing, ambiguous or loads another name.
        internal static int FindMareWriter(byte[] f, PeInfo pe)
        {
            List<int> hits = FindAll(f, pe.TextRaw, pe.TextRaw + pe.TextRawSize, MareWriterPattern);
            if (hits.Count != 1) return 0;
            int rva = hits[0] - pe.TextRaw + pe.TextRva;
            int name = pe.Raw(rva + MareNameLea + 7 + BitConverter.ToInt32(f, hits[0] + MareNameLea + 3));
            return name >= 0 && AsciiZ(f, name, 16) == "Mare_mem" ? rva : 0;
        }

        // RVA of the import address table slot through which the program calls `function` (imported by name), or 0.
        internal static int ImportSlot(byte[] f, PeInfo pe, string function)
        {
            for (int d = pe.ImportRva == 0 ? -1 : pe.Raw(pe.ImportRva); d >= 0 && d + 20 <= f.Length; d += 20)
            {
                int names = BitConverter.ToInt32(f, d), slots = BitConverter.ToInt32(f, d + 16);
                if (names == 0 && slots == 0) break;
                int t = pe.Raw(names != 0 ? names : slots);
                for (int i = 0; t >= 0 && t + 8 * i + 8 <= f.Length; i++)
                {
                    long entry = BitConverter.ToInt64(f, t + 8 * i);
                    if (entry == 0) break;
                    if (entry < 0 || entry > int.MaxValue) continue;   // imported by ordinal
                    int n = pe.Raw((int)entry + 2);
                    if (n >= 0 && AsciiZ(f, n, 128) == function) return slots + 8 * i;
                }
            }
            return 0;
        }

        static string AsciiZ(byte[] f, int at, int max)
        {
            int end = at;
            while (end < f.Length && end - at < max && f[end] != 0) end++;
            return end < f.Length && f[end] == 0 ? Encoding.ASCII.GetString(f, at, end - at) : null;
        }

        // ---- PE file ---------------------------------------------------------------------------------------
        internal sealed class PeInfo
        {
            public uint TimeDateStamp, SizeOfImage;
            public int TextRaw, TextRawSize, TextRva;
            public int ImportRva;
            public readonly List<int[]> Sections = new List<int[]>();   // { RVA, size in memory, file offset, size in the file }

            // File offset of an RVA, or -1 when the file holds no data for it.
            public int Raw(int rva)
            {
                foreach (int[] s in Sections)
                    if (rva >= s[0] && rva - s[0] < Math.Min(s[1], s[3])) return s[2] + rva - s[0];
                return -1;
            }
        }

        internal static PeInfo ReadPe(byte[] f)
        {
            if (f.Length < 0x400 || f[0] != 'M' || f[1] != 'Z') return null;
            int pe = BitConverter.ToInt32(f, 0x3C);
            if (pe <= 0 || pe + 0x108 > f.Length || BitConverter.ToUInt32(f, pe) != 0x00004550) return null;
            int sections = BitConverter.ToUInt16(f, pe + 6);
            int optSize = BitConverter.ToUInt16(f, pe + 20);
            int opt = pe + 24;
            if (BitConverter.ToUInt16(f, opt) != 0x20B) return null;
            var info = new PeInfo { TimeDateStamp = BitConverter.ToUInt32(f, pe + 8), SizeOfImage = BitConverter.ToUInt32(f, opt + 56) };
            if (optSize >= 128 && BitConverter.ToUInt32(f, opt + 108) > 1) info.ImportRva = BitConverter.ToInt32(f, opt + 120);
            bool text = false;
            for (int s = 0; s < sections; s++)
            {
                int h = opt + optSize + s * 40;
                if (h + 40 > f.Length) break;
                uint vsize = BitConverter.ToUInt32(f, h + 8), rawSize = BitConverter.ToUInt32(f, h + 16);
                int rva = (int)BitConverter.ToUInt32(f, h + 12), raw = (int)BitConverter.ToUInt32(f, h + 20);
                info.Sections.Add(new[] { rva, (int)vsize, raw, (int)rawSize });
                if (text || Encoding.ASCII.GetString(f, h, 8).TrimEnd('\0') != ".text") continue;
                text = true;
                info.TextRva = rva;
                info.TextRawSize = (int)Math.Min(vsize, rawSize);
                info.TextRaw = raw;
            }
            return text ? info : null;
        }

        // WriterRva and TerminateSlot: the "Mare_mem" writer and the TerminateProcess import slot the quit patch calls.
        internal sealed class Site { public Fix Fix; public int Rva, WriterRva, TerminateSlot; public string Problem; }

        static readonly Dictionary<string, KeyValuePair<DateTime, List<Site>>> SiteCache = new Dictionary<string, KeyValuePair<DateTime, List<Site>>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, PeInfo> PeCache = new Dictionary<string, PeInfo>(StringComparer.OrdinalIgnoreCase);

        internal static List<Fix> FixesFor(string exePath)
        {
            string name = Path.GetFileName(exePath);
            return Catalog.Where(f => f.Exe.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Locates every fix for an executable on disk. Problem != null means the fix cannot be applied to this build.
        internal static List<Site> Locate(string exePath, out PeInfo pe)
        {
            DateTime stamp = File.GetLastWriteTimeUtc(exePath);
            KeyValuePair<DateTime, List<Site>> cached;
            if (SiteCache.TryGetValue(exePath, out cached) && cached.Key == stamp) { pe = PeCache[exePath]; return cached.Value; }
            byte[] file = File.ReadAllBytes(exePath);
            pe = ReadPe(file);
            var sites = new List<Site>();
            List<Fix> fixes = FixesFor(exePath);
            int writer = 0, terminate = 0;
            if (pe != null && fixes.Count > 0) { writer = FindMareWriter(file, pe); terminate = ImportSlot(file, pe, "TerminateProcess"); }
            foreach (Fix fix in fixes)
            {
                if (pe == null) { sites.Add(new Site { Fix = fix, Problem = "not a 64-bit executable" }); continue; }
                List<int> hits = FindAll(file, pe.TextRaw, pe.TextRaw + pe.TextRawSize, fix.Pattern);
                if (hits.Count != 1) { sites.Add(new Site { Fix = fix, Problem = hits.Count == 0 ? "code not found (different game version)" : "code found " + hits.Count + " times" }); continue; }
                // Without the hand-over the fix would break going back to the title screen or the collection menu, so it is not used at all.
                if (writer == 0) { sites.Add(new Site { Fix = fix, Problem = "exit code hand-over not found (different game version)" }); continue; }
                if (terminate == 0) { sites.Add(new Site { Fix = fix, Problem = "TerminateProcess import not found (different game version)" }); continue; }
                sites.Add(new Site { Fix = fix, Rva = hits[0] - pe.TextRaw + pe.TextRva, WriterRva = writer, TerminateSlot = terminate });
            }
            SiteCache[exePath] = new KeyValuePair<DateTime, List<Site>>(stamp, sites);
            PeCache[exePath] = pe;
            return sites;
        }

        // ---- running process -------------------------------------------------------------------------------
        const uint PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020, PROCESS_QUERY_INFORMATION = 0x0400;
        const uint PAGE_EXECUTE_READWRITE = 0x40;

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_BASIC_INFORMATION { public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }

        [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PROCESS_BASIC_INFORMATION pbi, int len, out int ret);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, [Out] byte[] buf, IntPtr size, out IntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint prot, out uint old);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, IntPtr size);

        internal static byte[] Read(IntPtr h, long addr, int len)
        {
            var buf = new byte[len];
            IntPtr got;
            return ReadProcessMemory(h, new IntPtr(addr), buf, new IntPtr(len), out got) && got.ToInt64() == len ? buf : null;
        }

        // Writes game data (writable pages only; code is patched by Apply).
        internal static bool WriteData(IntPtr h, long addr, byte[] data)
        {
            IntPtr written;
            return WriteProcessMemory(h, new IntPtr(addr), data, new IntPtr(data.Length), out written) && written.ToInt64() == data.Length;
        }

        // Base address of the main executable in the process and its PE header as mapped; 0 while the process is still starting.
        internal static long ImageBase(IntPtr h, out PeInfo live)
        {
            live = null;
            var pbi = new PROCESS_BASIC_INFORMATION();
            int ret;
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), out ret) != 0) return 0;
            byte[] peb = Read(h, pbi.PebBaseAddress.ToInt64(), 0x18);
            if (peb == null) return 0;
            long imageBase = BitConverter.ToInt64(peb, 0x10);
            byte[] header = imageBase == 0 ? null : Read(h, imageBase, 0x1000);
            live = header == null ? null : ReadPe(header.Concat(new byte[0x400]).ToArray());
            return live == null ? 0 : imageBase;
        }

        // Applies all fixes for the program running as pid. Safe to call repeatedly.
        // markerExitCode (diagnostics only) makes the quit fix exit with that code, proving the patched path ran.
        internal static Result Apply(int pid, string exePath, uint? markerExitCode = null)
        {
            List<Fix> fixes = FixesFor(exePath);
            if (fixes.Count == 0) return new Result(Outcome.NotApplicable, "no code fixes for " + Path.GetFileName(exePath));
            PeInfo pe;
            List<Site> sites;
            try { sites = Locate(exePath, out pe); }
            catch (Exception ex) { return new Result(Outcome.Failed, "cannot read " + exePath + ": " + ex.Message); }
            IntPtr h = Native.OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return new Result(Outcome.Failed, "cannot open process " + pid + " (error " + Marshal.GetLastWin32Error() + ")");
            try
            {
                PeInfo live;
                long imageBase = ImageBase(h, out live);
                if (imageBase == 0) return new Result(Outcome.Retry, "process not initialized yet");
                if (pe == null || live.TimeDateStamp != pe.TimeDateStamp || live.SizeOfImage != pe.SizeOfImage)
                    return new Result(Outcome.NotApplicable, "the running program is a different build than " + exePath);

                var notes = new List<string>();
                bool anyApplied = false, anyFailed = false;
                foreach (Site site in sites)
                {
                    string label = site.Fix.Description + " fix";
                    if (site.Problem != null) { notes.Add(label + ": " + site.Problem); continue; }
                    long addr = imageBase + site.Rva;
                    byte[] patch = BuildQuitPatch(site.Fix.ExitReg, site.Rva + site.Fix.Offset, site.WriterRva, site.TerminateSlot, site.Fix.Length, markerExitCode);
                    byte[] current = Read(h, addr, site.Fix.Pattern.Length);
                    if (current == null) { notes.Add(label + ": code not readable"); anyFailed = true; continue; }
                    if (current.Skip(site.Fix.Offset).Take(patch.Length).SequenceEqual(patch)) { notes.Add(label + ": already active"); continue; }
                    if (!Matches(current, 0, site.Fix.Pattern)) { notes.Add(label + ": code in memory differs from the file - left alone"); anyFailed = true; continue; }
                    byte[] writer = Read(h, imageBase + site.WriterRva, MareWriterPattern.Length);
                    if (writer == null || !Matches(writer, 0, MareWriterPattern)) { notes.Add(label + ": exit code hand-over in memory differs from the file - left alone"); anyFailed = true; continue; }
                    long target = addr + site.Fix.Offset;
                    uint old;
                    if (!VirtualProtectEx(h, new IntPtr(target), new IntPtr(patch.Length), PAGE_EXECUTE_READWRITE, out old)) { notes.Add(label + ": VirtualProtectEx failed (" + Marshal.GetLastWin32Error() + ")"); anyFailed = true; continue; }
                    IntPtr written;
                    bool ok = WriteProcessMemory(h, new IntPtr(target), patch, new IntPtr(patch.Length), out written) && written.ToInt64() == patch.Length;
                    int err = Marshal.GetLastWin32Error();
                    uint ignored;
                    VirtualProtectEx(h, new IntPtr(target), new IntPtr(patch.Length), old, out ignored);
                    FlushInstructionCache(h, new IntPtr(target), new IntPtr(patch.Length));
                    if (!ok) { notes.Add(label + ": WriteProcessMemory failed (" + err + ")"); anyFailed = true; continue; }
                    byte[] verify = Read(h, target, patch.Length);
                    if (verify == null || !verify.SequenceEqual(patch)) { notes.Add(label + ": verification failed"); anyFailed = true; continue; }
                    notes.Add(label + ": applied at RVA 0x" + (site.Rva + site.Fix.Offset).ToString("X") + " (exit code hand-over via RVA 0x" + site.WriterRva.ToString("X") + ")");
                    anyApplied = true;
                }
                string msg = string.Join("; ", notes);
                if (anyFailed) return new Result(Outcome.Failed, msg);
                return new Result(anyApplied ? Outcome.Applied : (sites.Any(s => s.Problem == null) ? Outcome.AlreadyApplied : Outcome.NotApplicable), msg);
            }
            finally { Native.CloseHandle(h); }
        }
    }
}
