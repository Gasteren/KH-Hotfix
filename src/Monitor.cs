using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace KHHotfix
{
    internal sealed class TrackedProcess
    {
        static readonly HashSet<string> GameNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KINGDOM HEARTS FINAL MIX", "KINGDOM HEARTS Re_Chain of Memories", "KINGDOM HEARTS II FINAL MIX",
            "KINGDOM HEARTS Birth by Sleep FINAL MIX", "KINGDOM HEARTS Theater"
        };

        public readonly int Pid;
        public readonly string Name, ImagePath;
        public readonly bool OwnsHandle, IsGame, IsHangWatched;
        public readonly DateTime FirstSeenUtc = DateTime.UtcNow;
        public IntPtr Handle;
        public bool HadWindow, WindowGoneLogged, Exited, KilledByHotfix, FixDone;
        public int FixAttempts;
        public LoadingGuard Loading;
        public DateTime LastVisibleUtc = DateTime.UtcNow, HungSinceUtc = DateTime.MinValue, ExitedUtc;
        public uint ExitCode;
        public string KillReason;
        readonly List<KeyValuePair<DateTime, long>> cpu = new List<KeyValuePair<DateTime, long>>();

        public TrackedProcess(int pid, string imagePath, IntPtr handle, bool ownsHandle)
        {
            Pid = pid;
            ImagePath = imagePath;
            Name = Path.GetFileNameWithoutExtension(imagePath);
            Handle = handle;
            OwnsHandle = ownsHandle;
            IsGame = GameNames.Contains(Name);
            IsHangWatched = IsGame || Name.Equals("KINGDOM HEARTS HD 1.5+2.5 Launcher", StringComparison.OrdinalIgnoreCase);
        }

        public string Label { get { return Name + ".exe (pid " + Pid + ")"; } }

        public void SampleCpu(DateTime now)
        {
            long t = Native.CpuTime(Handle);
            if (t < 0) return;
            cpu.Add(new KeyValuePair<DateTime, long>(now, t));
            cpu.RemoveAll(s => (now - s.Key).TotalSeconds > 60);
        }

        // CPU seconds used during the last `seconds`, or -1 if there is not enough history yet.
        public double CpuSecondsOver(double seconds, DateTime now)
        {
            if (cpu.Count < 2) return -1;
            KeyValuePair<DateTime, long>? baseline = null;
            foreach (var s in cpu) { if ((now - s.Key).TotalSeconds >= seconds) baseline = s; else break; }
            if (baseline == null) return -1;
            return (cpu[cpu.Count - 1].Value - baseline.Value.Value) / 1e7;
        }
    }

    internal static class CrashCodes
    {
        public const uint KilledByHotfix = 0x4B48; // "KH"

        public static bool IsCrash(uint c)
        {
            if (c == 0xC000013A || c == KilledByHotfix) return false;
            return c >= 0xC0000000 || c == 0xE0434352 || c == 0x80000003 || (c & 0xFFFF0000) == 0x887A0000;
        }

        public static string Describe(uint c)
        {
            switch (c)
            {
                case 0x35: return " (Steam could not provide the licence - e.g. locked by Steam Family)";
                case 0xC0000005: return " (access violation)";
                case 0xC0000374: return " (heap corruption)";
                case 0xC0000409: return " (stack buffer overrun / fail-fast)";
                case 0xC00000FD: return " (stack overflow)";
                case 0xC0000017: return " (out of memory)";
                case 0xC000012D: return " (commit limit reached)";
                case 0xC000001D: return " (illegal instruction)";
                case 0xC0000142: return " (DLL initialization failed)";
                case 0xC000041D: return " (fatal exception in a user callback)";
                case 0xC0000602: return " (fail-fast exception)";
                case 0xE0434352: return " (unhandled .NET exception)";
                case 0x80000003: return " (breakpoint)";
                case 0x887A0005: return " (DXGI_ERROR_DEVICE_REMOVED - GPU driver reset)";
                case 0x887A0006: return " (DXGI_ERROR_DEVICE_HUNG - GPU hang)";
                case 0x887A0007: return " (DXGI_ERROR_DEVICE_RESET)";
                case 0x887A0020: return " (DXGI_ERROR_DRIVER_INTERNAL_ERROR)";
                case KilledByHotfix: return " (terminated by KHHotfix)";
                default: return "";
            }
        }
    }

    internal static class Events
    {
        public static string Query(string log, int[] ids, DateTime sinceLocal, string[] needles)
        {
            var sb = new StringBuilder();
            try
            {
                string ts = sinceLocal.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                string idFilter = string.Join(" or ", ids.Select(i => "EventID=" + i.ToString(CultureInfo.InvariantCulture)));
                string xpath = "*[System[(" + idFilter + ") and TimeCreated[@SystemTime>='" + ts + "']]]";
                using (var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath)))
                {
                    for (EventRecord r = reader.ReadEvent(); r != null; r = reader.ReadEvent())
                    {
                        using (r)
                        {
                            string msg = null;
                            try { msg = r.FormatDescription(); } catch { }
                            if (string.IsNullOrEmpty(msg)) msg = string.Join(" | ", r.Properties.Select(p => Convert.ToString(p.Value, CultureInfo.InvariantCulture)));
                            if (!needles.Any(n => msg.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "[{0:yyyy-MM-dd HH:mm:ss}] {1} / event {2}", r.TimeCreated, r.ProviderName, r.Id));
                            sb.AppendLine(msg.Trim());
                            sb.AppendLine();
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Info("Event log '" + log + "' not readable: " + ex.Message); }
            return sb.ToString();
        }
    }

    // Message-only window that receives the global panic hotkey (default Ctrl+Alt+K).
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        const int WM_HOTKEY = 0x0312;
        bool registered;
        public event Action Pressed;

        public HotkeyWindow()
        {
            var cp = new CreateParams();
            cp.Parent = new IntPtr(-3); // HWND_MESSAGE
            CreateHandle(cp);
        }

        public bool Register(string spec)
        {
            uint mods = 0x4000; // MOD_NOREPEAT
            Keys key = Keys.None;
            foreach (string raw in spec.Split('+'))
            {
                string t = raw.Trim().ToLowerInvariant();
                if (t == "ctrl" || t == "control") mods |= 0x2;
                else if (t == "alt") mods |= 0x1;
                else if (t == "shift") mods |= 0x4;
                else if (t == "win") mods |= 0x8;
                else if (!Enum.TryParse(raw.Trim(), true, out key)) return false;
            }
            if (key == Keys.None) return false;
            registered = Native.RegisterHotKey(Handle, 1, mods, (uint)key);
            return registered;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                Action h = Pressed;
                if (h != null) h();
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (registered) Native.UnregisterHotKey(Handle, 1);
            registered = false;
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
