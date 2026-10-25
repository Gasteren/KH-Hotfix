using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KHHotfix
{
    // Writes a minidump of a frozen game before the hotfix closes it, so freezes can be analyzed like crashes.
    internal static class HangDump
    {
        const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_READ = 0x0010;
        // MiniDumpNormal | MiniDumpWithUnloadedModules | MiniDumpWithProcessThreadData | MiniDumpWithThreadInfo
        const uint DumpType = 0x0020 | 0x0100 | 0x1000;

        [DllImport("dbghelp.dll", SetLastError = true)]
        static extern bool MiniDumpWriteDump(IntPtr process, uint pid, SafeFileHandle file, uint type, IntPtr exception, IntPtr userStream, IntPtr callback);

        public static string Write(TrackedProcess t, string reason)
        {
            try
            {
                Directory.CreateDirectory(Paths.CrashDumps);
                string file = Path.Combine(Paths.CrashDumps, t.Name + " - " + reason + " - " + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".dmp");
                string error = WriteFile(t.Pid, file);
                if (error == null) { Log.Info("Freeze dump of " + t.Label + " written: " + file); return file; }
                Log.Warn("Freeze dump of " + t.Label + " failed (" + error + ")");
                return null;
            }
            catch (Exception ex) { Log.Warn("Freeze dump of " + t.Label + " failed: " + ex.Message); return null; }
        }

        // Writes a snapshot of any running process to `file`. Returns null on success, otherwise the reason it failed.
        public static string WriteFile(int pid, string file)
        {
            IntPtr h = Native.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
            if (h == IntPtr.Zero) return "cannot open process " + pid + ", error " + Marshal.GetLastWin32Error();
            try
            {
                bool ok;
                int err;
                using (var fs = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    ok = MiniDumpWriteDump(h, (uint)pid, fs.SafeFileHandle, DumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    err = Marshal.GetLastWin32Error();
                }
                if (ok) return null;
                try { File.Delete(file); } catch { }
                return "error " + err;
            }
            catch (Exception ex) { return ex.Message; }
            finally { Native.CloseHandle(h); }
        }
    }
}
