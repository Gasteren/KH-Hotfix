using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KHHotfix
{
    // Audio outputs as Windows' audio service sees them (the registry keeps stale entries for removed devices).
    // The KH games create their XAudio2 output on the Windows default device, so Windows moves their sound to another
    // output when the default one disappears. When no output is left at all (the only device disconnects for a moment),
    // XAudio2 fails for good: the games never rebuild their sound, so they stay silent and, in KH1, loading screens
    // keep waiting for sounds that never finish. A second enabled output prevents that.
    internal static class AudioDevices
    {
        internal const int Active = 1, Disabled = 2, NotPresent = 4, Unplugged = 8;

        internal sealed class Endpoint
        {
            public string Id, Name;
            public int State, Channels, SampleRate;
            public bool IsDefault;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { public Guid FormatId; public int PropertyId; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Pointer;      // VT_LPWSTR
            [FieldOffset(8)] public uint BlobSize;       // VT_BLOB: size, then data pointer
            [FieldOffset(16)] public IntPtr BlobData;
        }

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

        const ushort VT_LPWSTR = 31, VT_BLOB = 65;
        static readonly PropertyKey FriendlyName = new PropertyKey { FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 14 };
        static readonly PropertyKey DeviceFormat = new PropertyKey { FormatId = new Guid("F19F064D-082C-4E27-BC73-6882A1BB8E4C"), PropertyId = 0 };

        // All render endpoints (every state), or null with an error message.
        internal static List<Endpoint> Render(out string error)
        {
            error = null;
            var list = new List<Endpoint>();
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                string defaultId = null;
                IMMDevice def;
                if (en.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out def) == 0 && def != null)
                {
                    def.GetId(out defaultId);
                    Marshal.ReleaseComObject(def);
                }
                IMMDeviceCollection col;
                int hr = en.EnumAudioEndpoints(0, Active | Disabled | NotPresent | Unplugged, out col);
                if (hr != 0) { error = "EnumAudioEndpoints failed (0x" + hr.ToString("X8") + ")"; return null; }
                uint count;
                col.GetCount(out count);
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice d;
                    if (col.Item(i, out d) != 0 || d == null) continue;
                    var e = new Endpoint();
                    d.GetId(out e.Id);
                    d.GetState(out e.State);
                    IPropertyStore ps;
                    if (d.OpenPropertyStore(0 /* STGM_READ */, out ps) == 0 && ps != null)
                    {
                        e.Name = ReadString(ps, FriendlyName);
                        byte[] fmt = ReadBlob(ps, DeviceFormat);
                        if (fmt != null && fmt.Length >= 8) { e.Channels = BitConverter.ToUInt16(fmt, 2); e.SampleRate = BitConverter.ToInt32(fmt, 4); }
                        Marshal.ReleaseComObject(ps);
                    }
                    e.IsDefault = defaultId != null && e.Id == defaultId;
                    list.Add(e);
                    Marshal.ReleaseComObject(d);
                }
                Marshal.ReleaseComObject(col);
                Marshal.ReleaseComObject(en);
                return list;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        static string ReadString(IPropertyStore ps, PropertyKey key)
        {
            PropVariant v;
            if (ps.GetValue(ref key, out v) != 0) return null;
            try { return v.Type == VT_LPWSTR && v.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(v.Pointer) : null; }
            finally { PropVariantClear(ref v); }
        }

        static byte[] ReadBlob(IPropertyStore ps, PropertyKey key)
        {
            PropVariant v;
            if (ps.GetValue(ref key, out v) != 0) return null;
            try
            {
                if (v.Type != VT_BLOB || v.BlobData == IntPtr.Zero || v.BlobSize == 0 || v.BlobSize > 4096) return null;
                var b = new byte[v.BlobSize];
                Marshal.Copy(v.BlobData, b, 0, b.Length);
                return b;
            }
            finally { PropVariantClear(ref v); }
        }

        internal static string StateName(int s)
        {
            switch (s)
            {
                case Active: return "active";
                case Disabled: return "disabled";
                case NotPresent: return "not present";
                case Unplugged: return "unplugged";
                default: return "state " + s;
            }
        }

        internal static string Describe(List<Endpoint> all)
        {
            var sb = new StringBuilder();
            foreach (Endpoint e in all.OrderBy(x => x.State).ThenBy(x => x.Name))
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "  [{0}] {1}", StateName(e.State), e.Name ?? e.Id));
                if (e.State == Active && e.Channels > 0) sb.Append(string.Format(CultureInfo.InvariantCulture, " - {0} ch, {1} Hz", e.Channels, e.SampleRate));
                if (e.IsDefault) sb.Append(" (default)");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // The warning shown when exactly one output is enabled; null when there is nothing to warn about.
        internal static string SingleOutputWarning(List<Endpoint> all)
        {
            List<Endpoint> active = all.Where(e => e.State == Active).ToList();
            if (active.Count != 1) return null;
            return "Only one audio output is enabled: " + (active[0].Name ?? "unknown device") + ". If it disconnects even for a moment "
                + "(Bluetooth dropout, headset turned off, monitor going to sleep), KINGDOM HEARTS loses its sound until you restart it, "
                + "and KH1 loading screens wait for sounds that never finish (the hotfix releases them). To prevent this, enable a second "
                + "output in Windows Settings > System > Sound > All sound devices (for example your monitor or speakers) and keep your "
                + "usual device as the default: Windows then moves the game's sound to the other output and back.";
        }

        // Pre-launch check: logs the outputs and warns once per device when only one output is enabled.
        internal static void Check(List<string> notices)
        {
            string error;
            List<Endpoint> all = Render(out error);
            if (all == null) { Log.Warn("Audio outputs could not be listed: " + error); return; }
            List<Endpoint> active = all.Where(e => e.State == Active).ToList();
            Endpoint def = all.FirstOrDefault(e => e.IsDefault);
            Log.Info("Audio outputs enabled: " + active.Count + (active.Count > 0 ? " (" + string.Join(", ", active.Select(e => e.Name)) + ")" : "")
                + ", default: " + (def != null ? def.Name : "none"));
            string warning = SingleOutputWarning(all);
            if (warning == null) return;
            if (Paths.OnceFlag("single-audio-output-" + StableHash(active[0].Id))) notices.Add(warning);
            else Log.Info("Single audio output (warning already shown once): " + active[0].Name);
        }

        static string StableHash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s ?? "") { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }
    }
}
