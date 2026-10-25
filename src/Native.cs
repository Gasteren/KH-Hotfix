using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KHHotfix
{
    internal static class Native
    {
        // ---- shell / disk -------------------------------------------------------------------
        [DllImport("shell32.dll", CharSet = CharSet.Ansi)]
        static extern int SHGetFolderPathA(IntPtr hwnd, int csidl, IntPtr token, uint flags, StringBuilder path);

        // The game resolves Documents with SHGetFolderPathA (ANSI). Returns exactly what the game sees.
        public static string GetDocumentsAnsi()
        {
            var sb = new StringBuilder(1024);
            return SHGetFolderPathA(IntPtr.Zero, 0x0005 /* CSIDL_PERSONAL */, IntPtr.Zero, 0, sb) == 0 ? sb.ToString() : null;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeToCaller, out ulong total, out ulong totalFree);

        public static long FreeBytes(string dir)
        {
            ulong a, b, c;
            return GetDiskFreeSpaceEx(dir, out a, out b, out c) ? (long)a : -1;
        }

        // ---- processes ------------------------------------------------------------------------
        public const uint PROCESS_TERMINATE = 0x0001, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x00100000;

        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateProcess(IntPtr h, uint code);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        public static string GetImagePath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        // Total CPU time (kernel + user) in 100 ns units, or -1.
        public static long CpuTime(IntPtr h)
        {
            long c, e, k, u;
            return GetProcessTimes(h, out c, out e, out k, out u) ? k + u : -1;
        }

        // ---- windows --------------------------------------------------------------------------
        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr hwnd);

        public sealed class WinState { public int Visible; public bool Hung; }

        public static Dictionary<int, WinState> SnapshotWindows(ICollection<int> pids)
        {
            var result = new Dictionary<int, WinState>();
            var set = new HashSet<int>(pids);
            EnumWindowsProc cb = delegate(IntPtr hwnd, IntPtr lp)
            {
                int pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (!set.Contains(pid) || !IsWindowVisible(hwnd)) return true;
                WinState w;
                if (!result.TryGetValue(pid, out w)) { w = new WinState(); result[pid] = w; }
                w.Visible++;
                if (IsHungAppWindow(hwnd)) w.Hung = true;
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return result;
        }

        // ---- hotkeys --------------------------------------------------------------------------
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        // ---- display --------------------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        public const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1, DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
        public const int ENUM_CURRENT_SETTINGS = -1;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")] public static extern int ChangeDisplaySettingsExNull(string device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr lParam);

        public static DEVMODE NewDevMode()
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return dm;
        }

        public static DISPLAY_DEVICE NewDisplayDevice()
        {
            var dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            return dd;
        }
    }
}
