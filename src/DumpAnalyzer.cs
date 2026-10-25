using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace KHHotfix
{
    // Reads the minidump the game writes when it crashes and turns it into a crash signature:
    // exception, faulting instruction and the call stack as module+offset, mapped to function starts.
    // Return addresses are recovered by scanning the faulting thread's stack and checking each candidate against
    // the module file on disk (it must follow a call instruction), so no debugger or symbols are needed.
    internal static class DumpAnalyzer
    {
        sealed class Module
        {
            public ulong Base;
            public uint Size, TimeDateStamp;
            public string Path;
            public string Name { get { return System.IO.Path.GetFileName(Path); } }
            public ModuleFile File;
            public bool FileLoaded;
        }

        sealed class ModuleFile
        {
            public byte[] Bytes;
            public List<uint[]> Sections = new List<uint[]>();   // VirtualAddress, VirtualSize, PointerToRawData, SizeOfRawData, Characteristics
            public List<uint> FuncStarts = new List<uint>(), FuncEnds = new List<uint>();

            public int RawOffset(uint rva, out bool exec)
            {
                exec = false;
                foreach (uint[] s in Sections)
                {
                    if (rva < s[0] || rva >= s[0] + Math.Max(s[1], s[3])) continue;
                    exec = (s[4] & 0x20000000) != 0;
                    uint off = rva - s[0];
                    return off < s[3] ? (int)(s[2] + off) : -1;
                }
                return -1;
            }

            public bool FunctionOf(uint rva, out uint start)
            {
                start = 0;
                int i = FuncStarts.BinarySearch(rva);
                if (i < 0) i = ~i - 1;
                if (i < 0 || rva >= FuncEnds[i]) return false;
                start = FuncStarts[i];
                return true;
            }
        }

        public sealed class Frame { public string Module; public uint Rva, FunctionStart; public bool HasFunction; public bool IsGame; }

        public sealed class Report
        {
            public uint ExceptionCode, ThreadId;
            public ulong FaultAddress;
            public int AccessType = -1;
            public string GameModule, GameBuild;
            public List<Frame> Frames = new List<Frame>();
            public string Error;
            public string KnownIssue;
        }

        static uint U32(byte[] d, long o) { return BitConverter.ToUInt32(d, (int)o); }
        static ulong U64(byte[] d, long o) { return BitConverter.ToUInt64(d, (int)o); }

        public static Report Analyze(string dumpPath)
        {
            var rep = new Report();
            byte[] d = System.IO.File.ReadAllBytes(dumpPath);
            if (d.Length < 32 || U32(d, 0) != 0x504D444D) { rep.Error = "not a minidump"; return rep; }
            uint streams = U32(d, 8), dir = U32(d, 12);
            long exc = -1, mods = -1, threads = -1;
            for (uint i = 0; i < streams; i++)
            {
                long e = dir + i * 12;
                if (e + 12 > d.Length) break;
                uint type = U32(d, e), rva = U32(d, e + 8);
                if (type == 6) exc = rva; else if (type == 4) mods = rva; else if (type == 3) threads = rva;
            }
            if (exc < 0 || mods < 0 || threads < 0) { rep.Error = "the dump has no exception, module or thread information"; return rep; }

            var modules = new List<Module>();
            uint nMods = U32(d, mods);
            for (uint i = 0; i < nMods; i++)
            {
                long m = mods + 4 + i * 108;
                uint nameRva = U32(d, m + 20);
                int len = (int)U32(d, nameRva);
                modules.Add(new Module { Base = U64(d, m), Size = U32(d, m + 8), TimeDateStamp = U32(d, m + 16), Path = Encoding.Unicode.GetString(d, (int)nameRva + 4, len) });
            }

            rep.ThreadId = U32(d, exc);
            rep.ExceptionCode = U32(d, exc + 8);
            ulong excAddress = U64(d, exc + 24);
            uint nParams = U32(d, exc + 32);
            if (rep.ExceptionCode == 0xC0000005 && nParams >= 2) { rep.AccessType = (int)U64(d, exc + 40); rep.FaultAddress = U64(d, exc + 48); }
            uint ctxRva = U32(d, exc + 160 + 4);
            ulong rsp = U64(d, ctxRva + 0x98), rip = U64(d, ctxRva + 0xF8);
            if (rip == 0) rip = excAddress;

            Module game = modules.FirstOrDefault(m => TrackedName(m.Name));
            if (game != null) { rep.GameModule = game.Name; rep.GameBuild = game.TimeDateStamp.ToString("X8"); }
            AddFrame(rep, modules, rip, false);

            uint nThreads = U32(d, threads);
            for (uint i = 0; i < nThreads; i++)
            {
                long t = threads + 4 + i * 48;
                if (U32(d, t) != rep.ThreadId) continue;
                ulong stackStart = U64(d, t + 24);
                uint stackSize = U32(d, t + 32), stackRva = U32(d, t + 36);
                ulong from = rsp >= stackStart && rsp < stackStart + stackSize ? rsp - stackStart : 0;
                for (ulong off = from; off + 8 <= stackSize && off - from < 256 * 1024 && rep.Frames.Count < 40; off += 8)
                    AddFrame(rep, modules, U64(d, stackRva + (long)off), true);
            }
            return rep;
        }

        static bool TrackedName(string name)
        {
            return name.StartsWith("KINGDOM HEARTS", StringComparison.OrdinalIgnoreCase);
        }

        static void AddFrame(Report rep, List<Module> modules, ulong address, bool mustFollowCall)
        {
            Module m = modules.FirstOrDefault(x => address >= x.Base && address < x.Base + x.Size);
            if (m == null) return;
            uint rva = (uint)(address - m.Base);
            ModuleFile f = Load(m);
            if (mustFollowCall && (f == null || !FollowsCall(f, rva))) return;
            var fr = new Frame { Module = m.Name, Rva = rva, IsGame = TrackedName(m.Name) };
            uint start;
            if (f != null && f.FunctionOf(rva, out start)) { fr.HasFunction = true; fr.FunctionStart = start; }
            rep.Frames.Add(fr);
        }

        static ModuleFile Load(Module m)
        {
            if (m.FileLoaded) return m.File;
            m.FileLoaded = true;
            try
            {
                if (!System.IO.File.Exists(m.Path)) return null;
                byte[] b = System.IO.File.ReadAllBytes(m.Path);
                int pe = BitConverter.ToInt32(b, 0x3C);
                if (BitConverter.ToUInt32(b, pe) != 0x4550 || BitConverter.ToUInt32(b, pe + 8) != m.TimeDateStamp) return null;
                int sections = BitConverter.ToUInt16(b, pe + 6), optSize = BitConverter.ToUInt16(b, pe + 20), opt = pe + 24;
                if (BitConverter.ToUInt16(b, opt) != 0x20B) return null;
                var f = new ModuleFile { Bytes = b };
                for (int s = 0; s < sections; s++)
                {
                    int h = opt + optSize + s * 40;
                    f.Sections.Add(new[] { BitConverter.ToUInt32(b, h + 12), BitConverter.ToUInt32(b, h + 8), BitConverter.ToUInt32(b, h + 20), BitConverter.ToUInt32(b, h + 16), BitConverter.ToUInt32(b, h + 36) });
                }
                uint pdataRva = BitConverter.ToUInt32(b, opt + 112 + 3 * 8), pdataSize = BitConverter.ToUInt32(b, opt + 112 + 3 * 8 + 4);
                bool exec;
                int raw = pdataRva == 0 ? -1 : f.RawOffset(pdataRva, out exec);
                if (raw >= 0)
                {
                    var entries = new List<KeyValuePair<uint, uint>>();
                    for (uint k = 0; k + 12 <= pdataSize && raw + k + 12 <= b.Length; k += 12)
                        entries.Add(new KeyValuePair<uint, uint>(BitConverter.ToUInt32(b, (int)(raw + k)), BitConverter.ToUInt32(b, (int)(raw + k + 4))));
                    foreach (var e in entries.OrderBy(x => x.Key)) { f.FuncStarts.Add(e.Key); f.FuncEnds.Add(e.Value); }
                }
                m.File = f;
            }
            catch { m.File = null; }
            return m.File;
        }

        // True if the instruction just before rva is a call (so rva is a genuine return address).
        static bool FollowsCall(ModuleFile f, uint rva)
        {
            bool exec;
            int o = f.RawOffset(rva, out exec);
            if (o < 7 || !exec) return false;
            byte[] b = f.Bytes;
            if (b[o - 5] == 0xE8) return true;                                        // call rel32
            if (b[o - 6] == 0xFF && b[o - 5] == 0x15) return true;                    // call [rip+disp32]
            if (b[o - 2] == 0xFF && (b[o - 1] & 0xF8) == 0xD0) return true;           // call reg
            if (b[o - 2] == 0xFF && (b[o - 1] & 0xF8) == 0x10 && (b[o - 1] & 7) != 4 && (b[o - 1] & 7) != 5) return true;   // call [reg]
            if (b[o - 3] == 0xFF && (b[o - 2] & 0xF8) == 0x50 && (b[o - 2] & 7) != 4) return true;   // call [reg+disp8]
            if (b[o - 6] == 0xFF && (b[o - 5] & 0xF8) == 0x90 && (b[o - 5] & 7) != 4) return true;   // call [reg+disp32]
            if (b[o - 3] == 0xFF && b[o - 2] == 0x14) return true;                    // call [sib]
            if (b[o - 4] == 0xFF && b[o - 3] == 0x54) return true;                    // call [sib+disp8]
            if (b[o - 7] == 0xFF && b[o - 6] == 0x94) return true;                    // call [sib+disp32]
            return false;
        }

        public static string Format(Report r, string dumpName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Crash analysis of " + dumpName);
            if (r.Error != null) { sb.AppendLine("  " + r.Error); return sb.ToString(); }
            sb.Append("  Exception 0x" + r.ExceptionCode.ToString("X8") + CrashCodes.Describe(r.ExceptionCode));
            if (r.AccessType >= 0) sb.Append(string.Format(CultureInfo.InvariantCulture, " {0} address 0x{1:X16}", r.AccessType == 1 ? "writing" : r.AccessType == 8 ? "executing" : "reading", r.FaultAddress));
            sb.AppendLine();
            if (r.GameModule != null) sb.AppendLine("  Game: " + r.GameModule + ", build " + r.GameBuild);
            if (r.KnownIssue != null) sb.AppendLine("  Known issue: " + r.KnownIssue);
            sb.AppendLine("  Call stack (most recent first):");
            for (int i = 0; i < r.Frames.Count; i++)
            {
                Frame f = r.Frames[i];
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "    #{0,-2} {1}+0x{2:X}{3}", i, f.Module, f.Rva,
                    f.HasFunction ? string.Format(CultureInfo.InvariantCulture, "   (function 0x{0:X}+0x{1:X})", f.FunctionStart, f.Rva - f.FunctionStart) : ""));
            }
            sb.AppendLine("  Signature: " + Signature(r));
            return sb.ToString();
        }

        // Stable identifier of a crash: game build plus the first game functions on the stack.
        public static string Signature(Report r)
        {
            var fns = r.Frames.Where(f => f.IsGame).Select(f => f.HasFunction ? f.FunctionStart.ToString("X") : "?" + f.Rva.ToString("X")).Take(6);
            Frame top = r.Frames.FirstOrDefault();
            return (r.GameModule ?? "?") + " " + (r.GameBuild ?? "?") + " | 0x" + r.ExceptionCode.ToString("X8") + " at " + (top == null ? "?" : top.Module + "+0x" + top.Rva.ToString("X")) + " | " + string.Join(" ", fns);
        }

        // Recognizes crashes that a code fix addresses: the stack runs through the instruction after a fix site.
        public static void Classify(Report r, string gameDir)
        {
            if (r.GameModule == null || gameDir == null) return;
            string exe = Path.Combine(gameDir, r.GameModule);
            if (!System.IO.File.Exists(exe)) return;
            GameFixes.PeInfo pe;
            foreach (GameFixes.Site s in GameFixes.Locate(exe, out pe))
            {
                if (s.Problem != null || pe.TimeDateStamp.ToString("X8") != r.GameBuild) continue;
                uint after = (uint)(s.Rva + s.Fix.Offset + s.Fix.Length);
                if (r.Frames.Any(f => f.IsGame && f.Rva == after))
                    r.KnownIssue = s.Fix.Description + " - prevented by the hotfix's code fix (FixQuitCrash=1)";
            }
        }
    }
}
