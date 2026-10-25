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
            public byte ExitRegModRm;   // ModRM of "mov edx, <exit code register>": 0xDA = ebx, 0xFA = edi
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
        // The exit code is kept, so the launcher behaves exactly as before.
        const string QuitDescription = "Quit Game crash/freeze";
        internal static readonly Fix[] Catalog =
        {
            Quit("KINGDOM HEARTS FINAL MIX.exe", "0F 49 D9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? | 48 8D 05 ?? ?? ?? ?? 48 8D 8C 24 40 01 00 00 48 89 44 24 20 E8 ?? ?? ?? ?? | 8B C3", 0xDA),
            Quit("KINGDOM HEARTS II FINAL MIX.exe", "0F 49 D9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? | 48 8D 05 ?? ?? ?? ?? 48 8D 8C 24 40 01 00 00 48 89 44 24 20 E8 ?? ?? ?? ?? | 8B C3", 0xDA),
            Quit("KINGDOM HEARTS Birth by Sleep FINAL MIX.exe", "0F 49 D9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? | 90 48 8D 05 ?? ?? ?? ?? 48 89 44 24 20 48 8D 8C 24 ?? ?? ?? ?? E8 ?? ?? ?? ?? | 8B C3", 0xDA),
            Quit("KINGDOM HEARTS Re_Chain of Memories.exe", "0F 49 D9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? | 90 48 8D 05 ?? ?? ?? ?? 48 89 44 24 20 48 8D 4D 50 E8 ?? ?? ?? ?? | 8B C3", 0xDA),
            Quit("KINGDOM HEARTS Theater.exe", "0F 49 F9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? | 48 8D 05 ?? ?? ?? ?? 48 8D 8C 24 40 01 00 00 48 89 44 24 20 E8 ?? ?? ?? ?? | 48 8B B4 24 ?? ?? ?? ?? 8B C7", 0xFA),
        };

        static Fix Quit(string exe, string signature, byte exitReg)
        {
            int offset, length;
            byte?[] pattern = ParsePattern(signature, out offset, out length);
            return new Fix { Id = "quit", Exe = exe, Description = QuitDescription, Pattern = pattern, Offset = offset, Length = length, ExitRegModRm = exitReg };
        }

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

        // mov edx, <exit reg> (or mov edx, imm32 for a diagnostic marker) ; or rcx, -1 ; mov rax, TerminateProcess ; call rax ; int3 padding
        internal static byte[] BuildQuitPatch(byte exitRegModRm, ulong terminateProcess, int length, uint? markerExitCode = null)
        {
            var p = new List<byte>();
            if (markerExitCode.HasValue) { p.Add(0xBA); p.AddRange(BitConverter.GetBytes(markerExitCode.Value)); }
            else { p.Add(0x89); p.Add(exitRegModRm); }
            p.AddRange(new byte[] { 0x48, 0x83, 0xC9, 0xFF, 0x48, 0xB8 });
            p.AddRange(BitConverter.GetBytes(terminateProcess));
            p.Add(0xFF);
            p.Add(0xD0);
            if (p.Count > length) throw new InvalidOperationException("patch does not fit");
            while (p.Count < length) p.Add(0xCC);
            return p.ToArray();
        }

        // ---- PE file ---------------------------------------------------------------------------------------
        internal sealed class PeInfo
        {
            public uint TimeDateStamp, SizeOfImage;
            public int TextRaw, TextRawSize, TextRva;
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
            for (int s = 0; s < sections; s++)
            {
                int h = opt + optSize + s * 40;
                if (h + 40 > f.Length) break;
                if (Encoding.ASCII.GetString(f, h, 8).TrimEnd('\0') != ".text") continue;
                uint vsize = BitConverter.ToUInt32(f, h + 8);
                info.TextRva = (int)BitConverter.ToUInt32(f, h + 12);
                info.TextRawSize = (int)Math.Min(vsize, BitConverter.ToUInt32(f, h + 16));
                info.TextRaw = (int)BitConverter.ToUInt32(f, h + 20);
                return info;
            }
            return null;
        }

        internal sealed class Site { public Fix Fix; public int Rva; public string Problem; }

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
            foreach (Fix fix in FixesFor(exePath))
            {
                if (pe == null) { sites.Add(new Site { Fix = fix, Problem = "not a 64-bit executable" }); continue; }
                List<int> hits = FindAll(file, pe.TextRaw, pe.TextRaw + pe.TextRawSize, fix.Pattern);
                if (hits.Count != 1) { sites.Add(new Site { Fix = fix, Problem = hits.Count == 0 ? "code not found (different game version)" : "code found " + hits.Count + " times" }); continue; }
                sites.Add(new Site { Fix = fix, Rva = hits[0] - pe.TextRaw + pe.TextRva });
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
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("psapi.dll", SetLastError = true)] static extern bool EnumProcessModulesEx(IntPtr h, [Out] IntPtr[] modules, int cb, out int needed, uint filter);
        [DllImport("psapi.dll", CharSet = CharSet.Unicode)] static extern uint GetModuleBaseName(IntPtr h, IntPtr module, StringBuilder name, int size);

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

        static long FindModule(IntPtr h, string name)
        {
            var mods = new IntPtr[1024];
            int needed;
            if (!EnumProcessModulesEx(h, mods, IntPtr.Size * mods.Length, out needed, 0x02 /* LIST_MODULES_64BIT */)) return -1;
            var sb = new StringBuilder(260);
            for (int i = 0; i < Math.Min(mods.Length, needed / IntPtr.Size); i++)
            {
                sb.Length = 0;
                if (GetModuleBaseName(h, mods[i], sb, sb.Capacity) > 0 && sb.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)) return mods[i].ToInt64();
            }
            return 0;
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
                IntPtr ourKernel32 = GetModuleHandle("kernel32.dll");
                long theirKernel32 = FindModule(h, "kernel32.dll");
                if (theirKernel32 <= 0) return new Result(Outcome.Retry, "kernel32 not loaded yet");
                if (theirKernel32 != ourKernel32.ToInt64()) return new Result(Outcome.Failed, "kernel32 is mapped at a different address in the game");
                ulong terminate = (ulong)GetProcAddress(ourKernel32, "TerminateProcess").ToInt64();

                var notes = new List<string>();
                bool anyApplied = false, anyFailed = false;
                foreach (Site site in sites)
                {
                    string label = site.Fix.Description + " fix";
                    if (site.Problem != null) { notes.Add(label + ": " + site.Problem); continue; }
                    long addr = imageBase + site.Rva;
                    byte[] patch = BuildQuitPatch(site.Fix.ExitRegModRm, terminate, site.Fix.Length, markerExitCode);
                    byte[] current = Read(h, addr, site.Fix.Pattern.Length);
                    if (current == null) { notes.Add(label + ": code not readable"); anyFailed = true; continue; }
                    if (current.Skip(site.Fix.Offset).Take(patch.Length).SequenceEqual(patch)) { notes.Add(label + ": already active"); continue; }
                    if (!Matches(current, 0, site.Fix.Pattern)) { notes.Add(label + ": code in memory differs from the file - left alone"); anyFailed = true; continue; }
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
                    notes.Add(label + ": applied at RVA 0x" + (site.Rva + site.Fix.Offset).ToString("X"));
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
