using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KHHotfix
{
    internal sealed class NvException : Exception
    {
        public readonly int Status;
        public NvException(string message, int status) : base(message + " (NVAPI status " + status + ")") { Status = status; }
    }

    // NVIDIA driver profile (DRS) access through nvapi64.dll. Function IDs and structure layouts are taken from
    // NVIDIA's public NVAPI headers (nvapi_interface.h, nvapi.h, NvApiDriverSettings.h).
    internal static class NvDrs
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnNoArgs();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnOutHandle(out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnHandle(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnFindApp(IntPtr session, IntPtr appName, out IntPtr profile, IntPtr application);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnFindProfile(IntPtr session, IntPtr profileName, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnCreateProfile(IntPtr session, IntPtr profileInfo, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnProfile(IntPtr session, IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnProfilePtr(IntPtr session, IntPtr profile, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnGetSetting(IntPtr session, IntPtr profile, uint settingId, IntPtr setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnDeleteSetting(IntPtr session, IntPtr profile, uint settingId);

        const uint IdInitialize = 0x0150E828, IdCreateSession = 0x0694D52E, IdDestroySession = 0xDAD9CFF8, IdLoadSettings = 0x375DBD6B,
            IdSaveSettings = 0xFCBC7E14, IdFindApplicationByName = 0xEEE566B2, IdFindProfileByName = 0x7E4A9A0B, IdCreateProfile = 0xCC176068,
            IdDeleteProfile = 0x17093206, IdCreateApplication = 0x4347A9DE, IdGetProfileInfo = 0x61CD6FD6, IdGetSetting = 0x73BF8338,
            IdSetSetting = 0x577DD202, IdDeleteProfileSetting = 0xE4A26362;

        public const uint FRL_FPS = 0x10835002, VSYNCMODE = 0x00A879CF, VRR_APP_OVERRIDE = 0x10A879CF;
        const uint VSYNC_FORCEON = 0x47814940, VRR_FORCE_OFF = 1;
        const int SETTING_NOT_FOUND = -160, PROFILE_NOT_FOUND = -163, EXECUTABLE_NOT_FOUND = -166, INCOMPATIBLE_STRUCT_VERSION = -9;

        const int StrBytes = 4096;                         // NvAPI_UnicodeString = NvU16[2048]
        const int SettingSize = 0x3020, ProfileSize = 0x1014, AppSizeV4 = 0x500C;
        static readonly uint[] AppVersions = { AppSizeV4 | (4u << 16), 0x400C | (3u << 16), 0x4008 | (2u << 16), 0x3008 | (1u << 16) };
        const int OffSettingId = 4100, OffSettingType = 4104, OffLocation = 4108, OffIsCurrentPredefined = 4112, OffCurrentValue = 8220;
        const string OwnProfile = "KINGDOM HEARTS HD 1.5+2.5 ReMIX (KHHotfix)";

        public static readonly string[] Executables =
        {
            "KINGDOM HEARTS HD 1.5+2.5 ReMIX.exe", "KINGDOM HEARTS HD 1.5+2.5 Launcher.exe", "KINGDOM HEARTS FINAL MIX.exe",
            "KINGDOM HEARTS Re_Chain of Memories.exe", "KINGDOM HEARTS II FINAL MIX.exe", "KINGDOM HEARTS Birth by Sleep FINAL MIX.exe",
            "KINGDOM HEARTS Theater.exe"
        };

        static string StateFile { get { return Path.Combine(Paths.State, "nvidia.txt"); } }

        static readonly Dictionary<uint, Delegate> Cache = new Dictionary<uint, Delegate>();

        static T Fn<T>(uint id) where T : class
        {
            Delegate d;
            if (!Cache.TryGetValue(id, out d))
            {
                IntPtr p = QueryInterface(id);
                if (p == IntPtr.Zero) throw new NvException("NVAPI function 0x" + id.ToString("X8") + " is not available", -3);
                d = Marshal.GetDelegateForFunctionPointer(p, typeof(T));
                Cache[id] = d;
            }
            return d as T;
        }

        static void Check(int status, string what) { if (status != 0) throw new NvException(what + " failed", status); }

        sealed class Buf : IDisposable
        {
            public readonly byte[] Data;
            GCHandle handle;
            public Buf(int size) { Data = new byte[size]; handle = GCHandle.Alloc(Data, GCHandleType.Pinned); }
            public IntPtr Ptr { get { return handle.AddrOfPinnedObject(); } }
            public void SetU32(int off, uint v) { BitConverter.GetBytes(v).CopyTo(Data, off); }
            public uint U32(int off) { return BitConverter.ToUInt32(Data, off); }
            public void SetString(int off, string s)
            {
                byte[] b = Encoding.Unicode.GetBytes(s);
                Array.Copy(b, 0, Data, off, Math.Min(b.Length, StrBytes - 2));
            }
            public string GetString(int off)
            {
                int end = off;
                while (end + 1 < off + StrBytes && (Data[end] != 0 || Data[end + 1] != 0)) end += 2;
                return Encoding.Unicode.GetString(Data, off, end - off);
            }
            public void Dispose() { if (handle.IsAllocated) handle.Free(); }
        }

        sealed class DrsSession : IDisposable
        {
            public IntPtr H;
            public DrsSession()
            {
                Check(Fn<FnNoArgs>(IdInitialize)(), "NvAPI_Initialize");
                Check(Fn<FnOutHandle>(IdCreateSession)(out H), "NvAPI_DRS_CreateSession");
                Check(Fn<FnHandle>(IdLoadSettings)(H), "NvAPI_DRS_LoadSettings");
            }
            public void Save() { Check(Fn<FnHandle>(IdSaveSettings)(H), "NvAPI_DRS_SaveSettings"); }
            public void Dispose()
            {
                if (H == IntPtr.Zero) return;
                try { Fn<FnHandle>(IdDestroySession)(H); } catch { }
                H = IntPtr.Zero;
            }
        }

        sealed class SettingInfo { public bool Found, IsPredefined; public uint Location, Value; }

        static int FindApp(DrsSession s, string exe, out IntPtr profile)
        {
            profile = IntPtr.Zero;
            using (var name = new Buf(StrBytes))
            using (var app = new Buf(AppSizeV4))
            {
                name.SetString(0, exe);
                int st = INCOMPATIBLE_STRUCT_VERSION;
                foreach (uint ver in AppVersions)
                {
                    Array.Clear(app.Data, 0, app.Data.Length);
                    app.SetU32(0, ver);
                    st = Fn<FnFindApp>(IdFindApplicationByName)(s.H, name.Ptr, out profile, app.Ptr);
                    if (st != INCOMPATIBLE_STRUCT_VERSION) break;
                }
                return st;
            }
        }

        static int FindProfile(DrsSession s, string name, out IntPtr profile)
        {
            using (var b = new Buf(StrBytes))
            {
                b.SetString(0, name);
                return Fn<FnFindProfile>(IdFindProfileByName)(s.H, b.Ptr, out profile);
            }
        }

        static string ProfileName(DrsSession s, IntPtr profile)
        {
            using (var b = new Buf(ProfileSize))
            {
                b.SetU32(0, ProfileSize | (1u << 16));
                Check(Fn<FnProfilePtr>(IdGetProfileInfo)(s.H, profile, b.Ptr), "NvAPI_DRS_GetProfileInfo");
                return b.GetString(4);
            }
        }

        static IntPtr CreateProfile(DrsSession s, string name)
        {
            using (var b = new Buf(ProfileSize))
            {
                b.SetU32(0, ProfileSize | (1u << 16));
                b.SetString(4, name);
                IntPtr p;
                Check(Fn<FnCreateProfile>(IdCreateProfile)(s.H, b.Ptr, out p), "NvAPI_DRS_CreateProfile");
                return p;
            }
        }

        static void AddApp(DrsSession s, IntPtr profile, string exe)
        {
            using (var b = new Buf(AppSizeV4))
            {
                int st = INCOMPATIBLE_STRUCT_VERSION;
                foreach (uint ver in AppVersions)
                {
                    Array.Clear(b.Data, 0, b.Data.Length);
                    b.SetU32(0, ver);
                    b.SetString(8, exe.ToLowerInvariant()); // appName follows version + isPredefined
                    st = Fn<FnProfilePtr>(IdCreateApplication)(s.H, profile, b.Ptr);
                    if (st != INCOMPATIBLE_STRUCT_VERSION) break;
                }
                Check(st, "NvAPI_DRS_CreateApplication(" + exe + ")");
            }
        }

        static SettingInfo GetSetting(DrsSession s, IntPtr profile, uint id)
        {
            using (var b = new Buf(SettingSize))
            {
                b.SetU32(0, SettingSize | (1u << 16));
                int st = Fn<FnGetSetting>(IdGetSetting)(s.H, profile, id, b.Ptr);
                if (st == SETTING_NOT_FOUND) return new SettingInfo();
                Check(st, "NvAPI_DRS_GetSetting(0x" + id.ToString("X8") + ")");
                return new SettingInfo { Found = true, Location = b.U32(OffLocation), IsPredefined = b.U32(OffIsCurrentPredefined) != 0, Value = b.U32(OffCurrentValue) };
            }
        }

        static void SetDword(DrsSession s, IntPtr profile, uint id, uint value)
        {
            using (var b = new Buf(SettingSize))
            {
                b.SetU32(0, SettingSize | (1u << 16));
                b.SetU32(OffSettingId, id);
                b.SetU32(OffSettingType, 0); // NVDRS_DWORD_TYPE
                b.SetU32(OffCurrentValue, value);
                Check(Fn<FnProfilePtr>(IdSetSetting)(s.H, profile, b.Ptr), "NvAPI_DRS_SetSetting(0x" + id.ToString("X8") + ")");
            }
        }

        static string SettingName(uint id)
        {
            if (id == FRL_FPS) return "Frame rate limiter";
            if (id == VSYNCMODE) return "Vertical sync";
            if (id == VRR_APP_OVERRIDE) return "G-SYNC";
            return "0x" + id.ToString("X8");
        }

        static string ValueText(uint id, uint v)
        {
            if (id == FRL_FPS) return v == 0 ? "off" : v + " FPS";
            if (id == VSYNCMODE) return v == VSYNC_FORCEON ? "force on" : v == 0x60925292 ? "application controlled" : "0x" + v.ToString("X8");
            if (id == VRR_APP_OVERRIDE) return v == 0 ? "allow" : v == 1 ? "force off" : v == 2 ? "disallow" : v.ToString(CultureInfo.InvariantCulture);
            return v.ToString(CultureInfo.InvariantCulture);
        }

        static readonly string[] LocationNames = { "this profile", "global profile", "base profile", "driver default" };

        public static int Status(TextWriter o)
        {
            using (var s = new DrsSession())
            {
                foreach (string exe in Executables)
                {
                    IntPtr p;
                    int st = FindApp(s, exe, out p);
                    if (st == EXECUTABLE_NOT_FOUND) { o.WriteLine(exe + ": no driver profile (global settings apply)"); continue; }
                    Check(st, "NvAPI_DRS_FindApplicationByName");
                    o.WriteLine(exe + ": profile \"" + ProfileName(s, p) + "\"");
                    foreach (uint id in new[] { FRL_FPS, VSYNCMODE, VRR_APP_OVERRIDE })
                    {
                        SettingInfo v = GetSetting(s, p, id);
                        string where = v.Location < LocationNames.Length ? LocationNames[v.Location] : "location " + v.Location;
                        o.WriteLine("    " + SettingName(id).PadRight(20) + (v.Found ? ValueText(id, v.Value) + "  [" + where + (v.Location == 0 && !v.IsPredefined ? ", user value" : "") + "]" : "not set"));
                    }
                }
            }
            o.WriteLine(File.Exists(StateFile) ? "Hotfix NVIDIA settings: APPLIED" : "Hotfix NVIDIA settings: not applied");
            return 0;
        }

        public static int Apply(uint fps, bool vsync, bool gsyncOff, TextWriter o)
        {
            if (File.Exists(StateFile)) { o.WriteLine("Previous hotfix NVIDIA settings found - reverting them first."); Revert(o); }
            var lines = new List<string>();
            using (var s = new DrsSession())
            {
                var profiles = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
                var missing = new List<string>();
                foreach (string exe in Executables)
                {
                    IntPtr p;
                    int st = FindApp(s, exe, out p);
                    if (st == EXECUTABLE_NOT_FOUND) { missing.Add(exe); continue; }
                    Check(st, "NvAPI_DRS_FindApplicationByName(" + exe + ")");
                    string n = ProfileName(s, p);
                    if (!profiles.ContainsKey(n)) profiles[n] = p;
                    o.WriteLine("  " + exe + " -> driver profile \"" + n + "\"");
                }
                if (missing.Count > 0)
                {
                    IntPtr own;
                    if (FindProfile(s, OwnProfile, out own) == 0) Check(Fn<FnProfile>(IdDeleteProfile)(s.H, own), "NvAPI_DRS_DeleteProfile(stale)");
                    own = CreateProfile(s, OwnProfile);
                    lines.Add("created-profile\t" + OwnProfile);
                    foreach (string exe in missing) { AddApp(s, own, exe); o.WriteLine("  " + exe + " -> added to new profile \"" + OwnProfile + "\""); }
                    profiles[OwnProfile] = own;
                }
                var targets = new List<KeyValuePair<uint, uint>>();
                if (fps > 0) targets.Add(new KeyValuePair<uint, uint>(FRL_FPS, fps));
                if (vsync) targets.Add(new KeyValuePair<uint, uint>(VSYNCMODE, VSYNC_FORCEON));
                if (gsyncOff) targets.Add(new KeyValuePair<uint, uint>(VRR_APP_OVERRIDE, VRR_FORCE_OFF));
                foreach (var prof in profiles)
                {
                    foreach (var t in targets)
                    {
                        SettingInfo prev = GetSetting(s, prof.Value, t.Key);
                        bool userValue = prev.Found && prev.Location == 0 && !prev.IsPredefined;
                        lines.Add(string.Format(CultureInfo.InvariantCulture, "setting\t{0}\t{1:X8}\t{2}\t{3}", prof.Key, t.Key, userValue ? "user" : "none", userValue ? prev.Value : 0));
                        SetDword(s, prof.Value, t.Key, t.Value);
                        o.WriteLine("  [" + prof.Key + "] " + SettingName(t.Key) + " = " + ValueText(t.Key, t.Value));
                    }
                }
                s.Save();
            }
            Directory.CreateDirectory(Paths.State);
            File.WriteAllLines(StateFile, lines);
            o.WriteLine("NVIDIA driver settings applied.");
            return 0;
        }

        public static int Revert(TextWriter o)
        {
            if (!File.Exists(StateFile)) { o.WriteLine("No hotfix NVIDIA settings to revert."); return 0; }
            using (var s = new DrsSession())
            {
                foreach (string line in File.ReadAllLines(StateFile).Reverse())
                {
                    string[] f = line.Split('\t');
                    IntPtr p;
                    if (f[0] == "setting" && f.Length >= 5)
                    {
                        if (FindProfile(s, f[1], out p) != 0) continue;
                        uint id = uint.Parse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        if (f[3] == "user") SetDword(s, p, id, uint.Parse(f[4], CultureInfo.InvariantCulture));
                        else
                        {
                            int st = Fn<FnDeleteSetting>(IdDeleteProfileSetting)(s.H, p, id);
                            if (st != 0 && st != SETTING_NOT_FOUND) Check(st, "NvAPI_DRS_DeleteProfileSetting");
                        }
                        o.WriteLine("  [" + f[1] + "] " + SettingName(id) + " restored");
                    }
                    else if (f[0] == "created-profile" && f.Length >= 2 && FindProfile(s, f[1], out p) == 0)
                    {
                        Check(Fn<FnProfile>(IdDeleteProfile)(s.H, p), "NvAPI_DRS_DeleteProfile");
                        o.WriteLine("  removed profile \"" + f[1] + "\"");
                    }
                }
                s.Save();
            }
            File.Delete(StateFile);
            o.WriteLine("NVIDIA driver settings reverted.");
            return 0;
        }
    }
}
