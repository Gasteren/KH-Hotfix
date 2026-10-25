using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KHHotfix
{
    // Temporarily switches the primary display to TargetRefreshHz (60) while the game runs.
    // The change is dynamic (not written to the registry), so a reboot or the restore below returns
    // the monitor to its normal refresh rate even if the hotfix is killed.
    internal sealed class DisplayGuard
    {
        const uint CDS_TEST = 0x2;
        const int DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
        string device;
        int originalHz;
        bool changed;

        static string StateFile { get { return Path.Combine(Paths.State, "display.txt"); } }

        public static string FindPrimary(out Native.DEVMODE current)
        {
            current = Native.NewDevMode();
            for (uint i = 0; i < 32; i++)
            {
                Native.DISPLAY_DEVICE dd = Native.NewDisplayDevice();
                if (!Native.EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) == 0) continue;
                if (!Native.EnumDisplaySettings(dd.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref current)) return null;
                return dd.DeviceName;
            }
            return null;
        }

        public static List<int> RatesAt(string device, int w, int h)
        {
            var rates = new SortedSet<int>();
            Native.DEVMODE m = Native.NewDevMode();
            for (int i = 0; Native.EnumDisplaySettings(device, i, ref m); i++)
                if (m.dmPelsWidth == w && m.dmPelsHeight == h) rates.Add(m.dmDisplayFrequency);
            return new List<int>(rates);
        }

        static bool FindMode(string device, Native.DEVMODE cur, int hz, out Native.DEVMODE found)
        {
            found = Native.NewDevMode();
            Native.DEVMODE m = Native.NewDevMode();
            for (int i = 0; Native.EnumDisplaySettings(device, i, ref m); i++)
            {
                if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight && m.dmBitsPerPel == cur.dmBitsPerPel && m.dmDisplayFrequency == hz)
                {
                    found = m;
                    found.dmFields = DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                    return true;
                }
            }
            return false;
        }

        // Returns a description of what Apply would do; uses CDS_TEST only, never changes the mode.
        public static string Describe(int target)
        {
            Native.DEVMODE cur;
            string dev = FindPrimary(out cur);
            if (dev == null) return "Primary display not found";
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "Primary display {0}: {1}x{2} @ {3} Hz (available at this resolution: {4})",
                dev, cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency, string.Join(", ", RatesAt(dev, cur.dmPelsWidth, cur.dmPelsHeight)));
            if (target <= 0) { sb.Append("\r\nRefresh switching is disabled (TargetRefreshHz=0)."); return sb.ToString(); }
            if (cur.dmDisplayFrequency <= target) { sb.AppendFormat("\r\nAlready at or below {0} Hz - nothing to switch.", target); return sb.ToString(); }
            Native.DEVMODE want;
            if (!FindMode(dev, cur, target, out want)) { sb.AppendFormat("\r\nNo {0} Hz mode at this resolution - the hotfix will leave the display alone.", target); return sb.ToString(); }
            int test = Native.ChangeDisplaySettingsEx(dev, ref want, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            sb.AppendFormat("\r\nWhile the game runs the hotfix will switch it to {0} Hz (driver test result: {1}).", target, test == 0 ? "OK" : "rejected, code " + test);
            return sb.ToString();
        }

        public void Apply()
        {
            int target = Settings.TargetRefreshHz;
            if (target <= 0) { Log.Info("Display: refresh switching disabled"); return; }
            Native.DEVMODE cur;
            string dev = FindPrimary(out cur);
            if (dev == null) { Log.Warn("Display: primary display not found"); return; }
            Log.Info(string.Format(CultureInfo.InvariantCulture, "Display: primary {0} is {1}x{2} @ {3} Hz", dev, cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency));
            if (cur.dmDisplayFrequency <= target) return;
            Native.DEVMODE want;
            if (!FindMode(dev, cur, target, out want)) { Log.Warn("Display: no " + target + " Hz mode at the current resolution"); return; }
            int test = Native.ChangeDisplaySettingsEx(dev, ref want, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            if (test != 0) { Log.Warn("Display: driver rejected " + target + " Hz (CDS_TEST " + test + ")"); return; }
            Directory.CreateDirectory(Paths.State);
            File.WriteAllText(StateFile, dev + "\r\n" + cur.dmDisplayFrequency.ToString(CultureInfo.InvariantCulture));
            int r = Native.ChangeDisplaySettingsEx(dev, ref want, IntPtr.Zero, 0, IntPtr.Zero);
            if (r != 0) { Log.Warn("Display: switching to " + target + " Hz failed (code " + r + ")"); TryDelete(StateFile); return; }
            changed = true;
            device = dev;
            originalHz = cur.dmDisplayFrequency;
            Log.Info("Display: switched " + dev + " from " + originalHz + " Hz to " + target + " Hz for the session");
        }

        public void Restore()
        {
            if (!changed) return;
            RestoreDevice(device, originalHz);
            changed = false;
            TryDelete(StateFile);
        }

        public static void RecoverStale()
        {
            if (!File.Exists(StateFile)) return;
            try
            {
                string[] lines = File.ReadAllLines(StateFile);
                int hz;
                if (lines.Length >= 2 && int.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out hz))
                {
                    Log.Warn("Display: a previous session did not restore " + lines[0] + " - restoring " + hz + " Hz now");
                    RestoreDevice(lines[0], hz);
                }
            }
            finally { TryDelete(StateFile); }
        }

        static void RestoreDevice(string dev, int hz)
        {
            // NULL DEVMODE = "use the mode stored in the registry", i.e. the user's normal setting.
            int r = Native.ChangeDisplaySettingsExNull(dev, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            Native.DEVMODE now = Native.NewDevMode();
            Native.EnumDisplaySettings(dev, Native.ENUM_CURRENT_SETTINGS, ref now);
            if (now.dmDisplayFrequency != hz)
            {
                Native.DEVMODE want;
                if (FindMode(dev, now, hz, out want)) r = Native.ChangeDisplaySettingsEx(dev, ref want, IntPtr.Zero, 0, IntPtr.Zero);
                Native.EnumDisplaySettings(dev, Native.ENUM_CURRENT_SETTINGS, ref now);
            }
            Log.Info("Display: restored " + dev + " to " + now.dmDisplayFrequency + " Hz (result " + r + ")");
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    }
}
