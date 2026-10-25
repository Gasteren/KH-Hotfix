using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KHHotfix
{
    // KINGDOM HEARTS FINAL MIX "infinite loading screen": the game keeps running, but the loading icon spins forever.
    // Root cause: the game's XAudio2 sound output fails for good when the audio device disappears and no other output is
    // left (for example the only output is a Bluetooth headset that drops out). The game's sound library only records that
    // error and never rebuilds its sound, so the game stays silent and no sound ever "finishes" again. When an area loads,
    // each character sound bank is registered by a task that first waits until the four character voice slots are idle,
    // so that wait - and the whole area load queued behind it - never ends.
    // While a load is pending, the guard looks for exactly this state (the sound-bank request blocked on busy voice slots)
    // and marks those slots idle; the load then completes at once and the sounds themselves are left alone. When the game's
    // audio-failure flag is set, the slots can never become idle by themselves, so they are released right away; otherwise
    // only after LoadingRescueSeconds. Nothing is written in any other situation. All addresses come from code signatures in
    // the executable, the code that uses them is verified too, and the running program must be the same build as the file.
    internal sealed class LoadingGuard : IDisposable
    {
        internal const string GameName = "KINGDOM HEARTS FINAL MIX";
        internal const int SlotCount = 4, SlotSize = 0x28;
        internal const int UnknownHangSeconds = 120;
        const uint ReadAccess = 0x0010 | 0x0400, WriteAccess = 0x0008 | 0x0020;   // VM_READ | QUERY_INFORMATION, VM_OPERATION | VM_WRITE

        // VoicesIdle check: lea rax,[slots] ; lea rcx,[slots+4*0x28] ; loop { cmp byte [rax],0 ; jne busy ; add rax,0x28 }
        internal const string SigVoiceBusy = "48 8D 05 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? 66 90 80 38 00 75 0C 48 83 C0 28 48 3B C1 7C F2 33 C0 C3 B8 01 00 00 00 C3";
        // Sound-bank request: lea r8,[task function] ; lea rcx,[bank task list] ; call AddTask ; inc [count] ; mov word [task+0x1A],0
        internal const string SigBankRequest = "45 33 C9 4C 8D 05 ?? ?? ?? ?? 33 D2 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? FF 05 ?? ?? ?? ?? 66 44 89 70 1A";
        // Sound-bank task (step = word [task+0x1A]): step 0 waits for the voices, step 1 for the sound system, then calls back.
        internal const string SigBankTask = "40 53 48 83 EC 20 0F BF 51 1A 48 8B D9 85 D2 74 ?? 83 FA 01 0F 85 ?? ?? ?? ?? E8 ?? ?? ?? ?? 85 C0 0F 85 ?? ?? ?? ?? 48 8B 43 28 48 85 C0 74 06 48 8B 4B 30 FF D0 B8 04 00 00 00";
        internal const string SigBankWait = "33 C9 E8 ?? ?? ?? ?? 85 C0 0F 85";                                     // step 0: if (VoicesBusy(0)) wait
        internal const string SigVoiceWait = "48 89 5C 24 18 57 41 56 41 57 48 83 EC 20 48 8B F9 E8 ?? ?? ?? ?? 85 C0";  // VoicesBusy: checks the slots first
        // Area load start: pending = (check == 0) ? 1 : pending ; ... ; store pending
        internal const string SigLoadPending = "E8 ?? ?? ?? ?? 8B 0D ?? ?? ?? ?? 85 C0 0F 44 CB 48 83 3D ?? ?? ?? ?? 00 48 8B 5C 24 20 89 0D ?? ?? ?? ??";
        // Sound output init: clears the audio-failure flag (mov byte [flag],0), then loads xaudio2_9.dll (LOAD_LIBRARY_SEARCH_SYSTEM32).
        internal const string SigAudioInit = "48 83 3D ?? ?? ?? ?? 00 8B 5E 58 8B 3D ?? ?? ?? ?? C6 05 ?? ?? ?? ?? 00 75 ?? 33 D2 48 8D 0D ?? ?? ?? ?? 41 B8 00 08 00 00";
        // IXAudio2EngineCallback::OnCriticalError of the sound library: mov byte [flag],1 ; ret
        internal const string SigAudioFailedSetter = "C6 05 ?? ?? ?? ?? 01 C3";
        // Voice handle lookup: voice = pool + (handle & 0x1FF) * stride, valid when [voice + link + 0x10] == &active list.
        internal const string SigVoiceLookup = "40 53 48 83 EC 20 85 C9 78 ?? 48 8B 05 ?? ?? ?? ?? 48 8D 15 ?? ?? ?? ?? 8B D9 81 E3 FF 01 00 00 48 0F AF 1D ?? ?? ?? ?? 48 03 1D ?? ?? ?? ?? 48 39 54 18 10 75 ??";
        internal const string SigVoiceIsPlaying = "F6 81 24 01 00 00 07 0F 95 C0 C3";   // test byte [voice+0x124],7 ; setne al ; ret
        internal const string SigVoiceFinish = "C7 83 24 01 00 00 08 00 00 00";       // mov dword [voice+0x124],8 (sound finished)
        internal const int VoicePoolSize = 512, VoiceHandleOffset = 0xC, VoiceStatusOffset = 0x124;
        internal const uint VoiceFinished = 8;

        // ---- locating the game data ------------------------------------------------------------------------------
        internal sealed class Layout
        {
            public uint VoiceSlots, BankScheduler, BankTask, LoadPending, AudioFailed, TimeDateStamp, SizeOfImage;
            public uint VoicePool, VoiceStride, VoiceLink, VoiceList;   // globals of the sound voice pool, 0 = not found
            public string Problem;

            public string Describe()
            {
                return Problem ?? string.Format(CultureInfo.InvariantCulture, "ready (voice slots RVA 0x{0:X}, sound-bank tasks RVA 0x{1:X}, load flag RVA 0x{2:X}, {3}, {4}, build {5:X8})",
                    VoiceSlots, BankScheduler, LoadPending, AudioFailed != 0 ? "audio-failure flag RVA 0x" + AudioFailed.ToString("X") : "audio-failure flag not found",
                    VoicePool != 0 ? "voice pool RVA 0x" + VoicePool.ToString("X") : "voice pool not found", TimeDateStamp);
            }
        }

        sealed class Scanner
        {
            readonly byte[] f;
            readonly GameFixes.PeInfo pe;
            public Scanner(byte[] f, GameFixes.PeInfo pe) { this.f = f; this.pe = pe; }
            static byte?[] P(string sig) { int o, n; return GameFixes.ParsePattern(sig, out o, out n); }
            public List<int> All(string sig) { return GameFixes.FindAll(f, pe.TextRaw, pe.TextRaw + pe.TextRawSize, P(sig)); }
            public uint Rva(int raw) { return (uint)(raw - pe.TextRaw + pe.TextRva); }
            public int Raw(uint rva)
            {
                long r = (long)rva - pe.TextRva + pe.TextRaw;
                return r < pe.TextRaw || r >= pe.TextRaw + pe.TextRawSize ? -1 : (int)r;
            }
            public bool At(uint rva, string sig) { int r = Raw(rva); return r >= 0 && GameFixes.Matches(f, r, P(sig)); }
            public sbyte SByteAt(uint rva) { return unchecked((sbyte)f[Raw(rva)]); }
            // Target of an instruction at raw offset `at` whose 32-bit displacement is at `at + disp` and whose length is `len`.
            public uint Target(int at, int disp, int len) { return unchecked((uint)(Rva(at) + len + BitConverter.ToInt32(f, at + disp))); }
        }

        static readonly Dictionary<string, KeyValuePair<DateTime, Layout>> Cache = new Dictionary<string, KeyValuePair<DateTime, Layout>>(StringComparer.OrdinalIgnoreCase);

        internal static Layout Locate(string exePath)
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(exePath); }
            catch { stamp = DateTime.MinValue; }
            KeyValuePair<DateTime, Layout> cached;
            if (Cache.TryGetValue(exePath, out cached) && cached.Key == stamp) return cached.Value;
            Layout l;
            try { l = Locate(File.ReadAllBytes(exePath)); }
            catch (Exception ex) { l = new Layout { Problem = "cannot read " + Path.GetFileName(exePath) + ": " + ex.Message }; }
            Cache[exePath] = new KeyValuePair<DateTime, Layout>(stamp, l);
            return l;
        }

        static Layout Fail(Layout l, string what)
        {
            l.Problem = what + " (different game version)";
            return l;
        }

        static string Found(string what, int count) { return what + (count == 0 ? " not found" : " found " + count + " times"); }

        internal static Layout Locate(byte[] f)
        {
            GameFixes.PeInfo pe = GameFixes.ReadPe(f);
            if (pe == null) return new Layout { Problem = "not a 64-bit executable" };
            var l = new Layout { TimeDateStamp = pe.TimeDateStamp, SizeOfImage = pe.SizeOfImage };
            var x = new Scanner(f, pe);

            List<int> hits = x.All(SigVoiceBusy);
            if (hits.Count != 1) return Fail(l, Found("voice slot check", hits.Count));
            l.VoiceSlots = x.Target(hits[0], 3, 7);
            if (x.Target(hits[0] + 7, 3, 7) - l.VoiceSlots != SlotCount * SlotSize) return Fail(l, "voice slot table has an unexpected size");
            uint voiceBusy = x.Rva(hits[0]);

            // The same request code exists in several functions; all of them must use the same task and task list.
            hits = x.All(SigBankRequest);
            if (hits.Count == 0) return Fail(l, Found("sound-bank request code", 0));
            l.BankTask = x.Target(hits[0] + 3, 3, 7);
            l.BankScheduler = x.Target(hits[0] + 12, 3, 7);
            if (hits.Any(hit => x.Target(hit + 3, 3, 7) != l.BankTask || x.Target(hit + 12, 3, 7) != l.BankScheduler)) return Fail(l, "sound-bank request code is ambiguous");
            if (!x.At(l.BankTask, SigBankTask)) return Fail(l, "sound-bank task code differs");
            uint step0 = unchecked((uint)(l.BankTask + 0x11 + x.SByteAt(l.BankTask + 0x10)));
            if (!x.At(step0, SigBankWait)) return Fail(l, "sound-bank wait code differs");
            uint wait = x.Target(x.Raw(step0) + 2, 1, 5);
            if (!x.At(wait, SigVoiceWait) || x.Target(x.Raw(wait) + 0x11, 1, 5) != voiceBusy) return Fail(l, "sound-bank task does not wait for the voice slots");

            hits = x.All(SigLoadPending);
            if (hits.Count != 1) return Fail(l, Found("load flag code", hits.Count));
            l.LoadPending = x.Target(hits[0] + 5, 2, 6);
            if (x.Target(hits[0] + 29, 2, 6) != l.LoadPending) return Fail(l, "load flag code differs");

            if (new[] { l.VoiceSlots, l.BankScheduler, l.LoadPending }.Any(rva => rva < 0x1000 || (long)rva + 0x100 > l.SizeOfImage)) return Fail(l, "game data outside the program image");

            // Optional: without it the guard still works, it just cannot tell that the sound has failed.
            hits = x.All(SigAudioInit);
            if (hits.Count == 1)
            {
                uint flag = x.Target(hits[0] + 17, 2, 7);
                int setters = x.All(SigAudioFailedSetter).Count(s => x.Target(s, 2, 7) == flag);
                if (setters == 1 && flag >= 0x1000 && flag < l.SizeOfImage) l.AudioFailed = flag;
            }

            // Optional: the voice pool, used to finish sounds that can never end after the sound output failed.
            hits = x.All(SigVoiceLookup);
            if (l.AudioFailed != 0 && hits.Count == 1 && x.All(SigVoiceIsPlaying).Count == 1 && x.All(SigVoiceFinish).Count >= 1)
            {
                uint link = x.Target(hits[0] + 10, 3, 7), list = x.Target(hits[0] + 17, 3, 7), stride = x.Target(hits[0] + 32, 4, 8), pool = x.Target(hits[0] + 40, 3, 7);
                if (new[] { link, list, stride, pool }.All(rva => rva >= 0x1000 && (long)rva + 8 <= l.SizeOfImage))
                {
                    l.VoiceLink = link; l.VoiceList = list; l.VoiceStride = stride; l.VoicePool = pool;
                }
            }
            return l;
        }

        // ---- decision logic (separate from process access so it can be tested) -------------------------------------
        internal interface IMemory
        {
            byte[] Read(long address, int length);
            bool Write(long address, byte[] data);
        }

        internal sealed class ProcessMemory : IMemory
        {
            readonly IntPtr h;
            public ProcessMemory(IntPtr h) { this.h = h; }
            public byte[] Read(long address, int length) { return GameFixes.Read(h, address, length); }
            public bool Write(long address, byte[] data) { return GameFixes.WriteData(h, address, data); }
        }

        internal sealed class Request { public long Task; public ulong Data, Arg; }

        internal sealed class Core
        {
            static readonly string[] StateNames = { "idle", "delayed", "starting", "started", "playing" };
            readonly IMemory mem;
            readonly long imageBase;
            readonly Layout l;
            public int RescueSeconds = 10;
            public int StuckSoundSeconds = 5;
            public int Rescues, SoundsFinished, SlotsReleased;
            public Action<string> Rescued = delegate { };
            public Action<string> UnknownHang = delegate { };
            public Action<string> AudioLost = delegate { };
            long reqTask;
            ulong reqData, reqArg;
            DateTime reqSince = DateTime.MinValue, loadSince = DateTime.MinValue, lastScan = DateTime.MinValue;
            bool hangReported, audioReported;
            readonly Dictionary<uint, DateTime> playingSince = new Dictionary<uint, DateTime>();
            readonly string[] slotKey = new string[SlotCount];
            readonly DateTime[] slotSince = new DateTime[SlotCount];

            public Core(IMemory mem, long imageBase, Layout layout)
            {
                this.mem = mem;
                this.imageBase = imageBase;
                l = layout;
            }

            public bool LoadPending()
            {
                byte[] v = mem.Read(imageBase + l.LoadPending, 4);
                return v != null && BitConverter.ToInt32(v, 0) != 0;
            }

            // True once the game's XAudio2 output has failed (it never recovers by itself); false when unknown.
            public bool AudioFailed()
            {
                if (l.AudioFailed == 0) return false;
                byte[] v = mem.Read(imageBase + l.AudioFailed, 1);
                return v != null && v[0] != 0;
            }

            // The first sound-bank task if it is still waiting for idle voice slots (step 0), else null.
            public Request WaitingRequest()
            {
                byte[] head = mem.Read(imageBase + l.BankScheduler + 0x10, 8);
                long task = head == null ? 0 : BitConverter.ToInt64(head, 0);
                if (task == 0) return null;
                byte[] d = mem.Read(task, 0x38);
                if (d == null || BitConverter.ToInt64(d, 0x10) != imageBase + l.BankTask || BitConverter.ToInt16(d, 0x1A) != 0) return null;
                return new Request { Task = task, Data = BitConverter.ToUInt64(d, 0x20), Arg = BitConverter.ToUInt64(d, 0x30) };
            }

            public byte[] Slots() { return mem.Read(imageBase + l.VoiceSlots, SlotCount * SlotSize); }

            public static string DescribeSlot(byte[] s, int i)
            {
                int o = i * SlotSize;
                string state = s[o] < StateNames.Length ? StateNames[s[o]] : "state " + s[o];
                if (s[o] == 0) return "voice slot " + i + " idle";
                if (s[o + 1] == 1) return string.Format(CultureInfo.InvariantCulture, "voice slot {0} ({1}, voice stream)", i, state);
                return string.Format(CultureInfo.InvariantCulture, "voice slot {0} ({1}, bank {2} sound 0x{3:X2})", i, state, unchecked((sbyte)s[o + 2]), s[o + 3]);
            }

            string DescribeSlots()
            {
                byte[] s = Slots();
                if (s == null) return "voice slots unreadable";
                return string.Join(", ", Enumerable.Range(0, SlotCount).Select(i => DescribeSlot(s, i)));
            }

            public string DescribeState()
            {
                Request r = WaitingRequest();
                var sb = new StringBuilder();
                sb.AppendLine("Sound output       : " + (l.AudioFailed == 0 ? "unknown (audio-failure flag not found)"
                    : AudioFailed() ? "FAILED - the audio device disappeared; no sound until the game is restarted" : "working"));
                sb.AppendLine("Area load pending  : " + (LoadPending() ? "yes (loading screen)" : "no"));
                sb.AppendLine("Sound-bank request : " + (r == null ? "none waiting for the voice slots"
                    : string.Format(CultureInfo.InvariantCulture, "waiting for idle voice slots (task 0x{0:X}, data 0x{1:X})", r.Task, r.Data)));
                byte[] s = Slots();
                for (int i = 0; i < SlotCount; i++) sb.AppendLine("  " + (s == null ? "voice slot " + i + " unreadable" : DescribeSlot(s, i)));
                return sb.ToString();
            }

            // Marks every busy voice slot idle. Returns what was released, or null when no slot was busy.
            public string ReleaseBusySlots()
            {
                byte[] s = Slots();
                if (s == null) return null;
                var released = new List<string>();
                for (int i = 0; i < SlotCount; i++)
                {
                    if (s[i * SlotSize] == 0) continue;
                    if (mem.Write(imageBase + l.VoiceSlots + i * SlotSize, new byte[] { 0 })) released.Add(DescribeSlot(s, i));
                }
                return released.Count == 0 ? null : string.Join(", ", released);
            }

            // Manual rescue (CLI): same conditions as the automatic one, without the waiting time.
            public string RescueNow()
            {
                if (!LoadPending() || WaitingRequest() == null) return null;
                string what = ReleaseBusySlots();
                if (what != null) Rescues++;
                return what;
            }

            long ReadPointer(long address)
            {
                byte[] v = mem.Read(address, 8);
                return v == null ? 0 : BitConverter.ToInt64(v, 0);
            }

            // After the sound output failed, no sound ever finishes, so cutscenes and events that wait for one stop forever.
            // Sounds that have been "playing" for StuckSoundSeconds are marked finished - the state the game itself sets when
            // a sound ends - and the game then cleans them up and continues as usual. Only used while the output has failed.
            void FinishStuckSounds(DateTime now)
            {
                if (l.VoicePool == 0 || (now - lastScan).TotalSeconds < 1) return;
                lastScan = now;
                long pool = ReadPointer(imageBase + l.VoicePool), stride = ReadPointer(imageBase + l.VoiceStride), link = ReadPointer(imageBase + l.VoiceLink);
                if (pool == 0 || stride < VoiceStatusOffset + 4 || stride > 0x4000 || link < 0 || link + 0x18 > stride) return;
                byte[] all = mem.Read(pool, (int)(VoicePoolSize * stride));
                if (all == null) return;
                long list = imageBase + l.VoiceList;
                var seen = new HashSet<uint>();
                for (int i = 0; i < VoicePoolSize; i++)
                {
                    int o = (int)(i * stride);
                    uint handle = BitConverter.ToUInt32(all, o + VoiceHandleOffset);
                    uint status = BitConverter.ToUInt32(all, o + VoiceStatusOffset);
                    if ((handle & 0x1FF) != i || BitConverter.ToInt64(all, o + (int)link + 0x10) != list || (status & 7) == 0 || (status & VoiceFinished) != 0) continue;
                    seen.Add(handle);
                    DateTime first;
                    if (!playingSince.TryGetValue(handle, out first)) { playingSince[handle] = now; continue; }
                    if ((now - first).TotalSeconds < StuckSoundSeconds) continue;
                    long at = pool + o + VoiceStatusOffset;
                    byte[] current = mem.Read(at, 4);
                    if (current != null && BitConverter.ToUInt32(current, 0) == status && mem.Write(at, BitConverter.GetBytes(VoiceFinished))) SoundsFinished++;
                    playingSince.Remove(handle);
                }
                foreach (uint gone in playingSince.Keys.Where(k => !seen.Contains(k)).ToList()) playingSince.Remove(gone);
            }

            // Fallback for the character voice slots: a slot that stays "playing" after its sound should have been finished.
            void ReleaseStuckSlots(DateTime now)
            {
                byte[] s = Slots();
                if (s == null) return;
                for (int i = 0; i < SlotCount; i++)
                {
                    int o = i * SlotSize;
                    string key = s[o] == 4 ? BitConverter.ToString(s, o + 1, 15) : null;
                    if (key == null || key != slotKey[i]) { slotKey[i] = key; slotSince[i] = now; continue; }
                    if ((now - slotSince[i]).TotalSeconds < StuckSoundSeconds + 3) continue;
                    if (mem.Write(imageBase + l.VoiceSlots + o, new byte[] { 0 })) SlotsReleased++;
                    slotKey[i] = null;
                }
            }

            // Called several times per second. Returns true when stuck voice slots were released.
            public bool Tick(DateTime now)
            {
                bool audioFailed = AudioFailed();
                if (audioFailed && !audioReported)
                {
                    audioReported = true;
                    AudioLost("its sound output failed (the audio device disappeared while no other output was available) - "
                        + "the game cannot recover its sound by itself, so it stays silent until it is restarted");
                }
                if (audioFailed && RescueSeconds > 0)
                {
                    FinishStuckSounds(now);
                    ReleaseStuckSlots(now);
                }
                if (!LoadPending())
                {
                    reqTask = 0;
                    reqSince = loadSince = DateTime.MinValue;
                    hangReported = false;
                    return false;
                }
                if (loadSince == DateTime.MinValue) loadSince = now;
                Request r = WaitingRequest();
                if (r == null || r.Task != reqTask || r.Data != reqData || r.Arg != reqArg || reqSince == DateTime.MinValue)
                {
                    reqTask = r == null ? 0 : r.Task;
                    reqData = r == null ? 0 : r.Data;
                    reqArg = r == null ? 0 : r.Arg;
                    reqSince = now;
                }
                bool released = false;
                double waited = (now - reqSince).TotalSeconds;
                // With failed sound output the slots can never become idle by themselves: no reason to wait.
                if (r != null && RescueSeconds > 0 && (audioFailed || waited >= RescueSeconds))
                {
                    string what = ReleaseBusySlots();
                    if (what != null)
                    {
                        released = true;
                        Rescues++;
                        loadSince = now;
                        Rescued(audioFailed
                            ? string.Format(CultureInfo.InvariantCulture, "loading screen waiting for {0} - released at once because the game's sound output has failed (known game bug)", what)
                            : string.Format(CultureInfo.InvariantCulture, "loading screen stuck for {0:0} s waiting for {1} - marked idle, the load continues (known game bug)", waited, what));
                    }
                    reqSince = now;
                }
                if (!hangReported && (now - loadSince).TotalSeconds >= UnknownHangSeconds)
                {
                    hangReported = true;
                    UnknownHang(string.Format(CultureInfo.InvariantCulture, "loading screen has not finished for {0:0} s (sound-bank request {1}; {2})",
                        (now - loadSince).TotalSeconds, r == null ? "not waiting" : "waiting", DescribeSlots()));
                }
                return released;
            }
        }

        // ---- one guarded game process ----------------------------------------------------------------------------
        readonly TrackedProcess t;
        readonly Action<string> notice;
        readonly Action<string, string> alert;
        IntPtr h;
        Core core;
        bool disabled, audioLost;
        DateTime audioLostLocal;
        int attempts, loggedFinished;

        // notice: shown after the session; alert(title, text): shown right away (a Windows notification).
        public LoadingGuard(TrackedProcess t, Action<string> notice, Action<string, string> alert)
        {
            this.t = t;
            this.notice = notice;
            this.alert = alert;
        }

        public static bool Applies(TrackedProcess t) { return t.IsGame && t.Name.Equals(GameName, StringComparison.OrdinalIgnoreCase); }

        public int Rescues { get { return core == null ? 0 : core.Rescues; } }

        public void Tick(DateTime now)
        {
            if (disabled || (core == null && !Attach())) return;
            try
            {
                core.Tick(now);
                int done = core.SoundsFinished + core.SlotsReleased;
                if (done != loggedFinished && (loggedFinished == 0 || done - loggedFinished >= 50))
                {
                    Log.Info(t.Label + ": sounds stuck because of the failed sound output finished so far: " + core.SoundsFinished + " (voice slots released: " + core.SlotsReleased + ")");
                    loggedFinished = done;
                }
            }
            catch (Exception ex) { disabled = true; Log.Warn(t.Label + " loading guard stopped: " + ex.Message); }
        }

        bool Attach()
        {
            attempts++;
            Layout l = Locate(t.ImagePath);
            if (l.Problem != null) return Disable("loading guard not available: " + l.Problem);
            int rescueSeconds = Settings.LoadingRescueSeconds;
            if (h == IntPtr.Zero) h = Native.OpenProcess(ReadAccess | (rescueSeconds > 0 ? WriteAccess : 0), false, t.Pid);
            if (h == IntPtr.Zero) return Disable("loading guard cannot open the game (error " + Marshal.GetLastWin32Error() + ")");
            GameFixes.PeInfo live;
            long imageBase = GameFixes.ImageBase(h, out live);
            if (imageBase == 0) return attempts < 120 ? false : Disable("loading guard: the game image is not readable");
            if (live.TimeDateStamp != l.TimeDateStamp || live.SizeOfImage != l.SizeOfImage)
                return Disable("loading guard not available: the running program is a different build than " + t.ImagePath);
            core = new Core(new ProcessMemory(h), imageBase, l) { RescueSeconds = rescueSeconds };
            core.Rescued = delegate(string what) { Log.Warn(t.Label + ": " + what); };
            core.UnknownHang = delegate(string what)
            {
                Log.Warn(t.Label + ": " + what + " - cause unknown, writing a snapshot for analysis");
                HangDump.Write(t, "loading screen stuck");
                if (notice != null)
                    notice(t.Name + ".exe stayed on a loading screen for over " + (UnknownHangSeconds / 60) + " minutes for a reason the hotfix does not know yet. "
                        + "A snapshot was saved; please report it with Reports\\<date>\\summary.txt (see the README).");
            };
            core.AudioLost = delegate(string what)
            {
                audioLost = true;
                audioLostLocal = DateTime.Now;
                Log.Warn(t.Label + ": " + what);
                if (alert != null)
                    alert("KINGDOM HEARTS lost its sound", "Your audio output disconnected and the game cannot get its sound back by itself. "
                        + "The hotfix keeps loading screens and cutscenes going. Save at the next save point, then restart the game.");
            };
            Log.Info(t.Label + " loading guard active (" + (rescueSeconds > 0 ? "stuck loads are released after " + rescueSeconds + " s; after a sound failure loads at once and sounds stuck for "
                + core.StuckSoundSeconds + " s are finished" : "LoadingRescueSeconds=0: only reports, never changes the game") + "): " + l.Describe());
            return true;
        }

        bool Disable(string why)
        {
            disabled = true;
            Log.Warn(t.Label + " " + why);
            return false;
        }

        public void Dispose()
        {
            if (notice != null && audioLost)
                notice(t.Name + ".exe lost its sound at " + audioLostLocal.ToString("HH:mm", CultureInfo.InvariantCulture) + ": its audio output disappeared while no "
                    + "other output was enabled, and the game cannot recover its sound by itself (restarting it does). "
                    + (core != null && core.SoundsFinished + core.SlotsReleased > 0 ? "Until then the hotfix finished " + (core.SoundsFinished + core.SlotsReleased)
                        + " sounds that would have blocked cutscenes and events forever. " : "")
                    + "To prevent this, keep a second audio output enabled in Windows Settings > System > Sound > All sound devices; "
                    + "Windows then moves the game's sound there and back.");
            if (core != null && core.Rescues > 0 && notice != null)
                notice(t.Name + ".exe got stuck on a loading screen " + core.Rescues + " time(s) - a known bug in the game"
                    + (audioLost ? ", caused by its failed sound output" : "") + ". The hotfix released it each time; nothing else was changed.");
            core = null;
            disabled = true;
            if (h != IntPtr.Zero) { Native.CloseHandle(h); h = IntPtr.Zero; }
        }

        // CLI: shows the loading state of a running game and optionally releases a stuck load right away.
        internal static int Inspect(int pid, string exePath, bool rescue, out string text)
        {
            Layout l = Locate(exePath);
            if (l.Problem != null) { text = "Loading guard not available: " + l.Problem; return 7; }
            IntPtr ph = Native.OpenProcess(ReadAccess | (rescue ? WriteAccess : 0), false, pid);
            if (ph == IntPtr.Zero) { text = "Cannot open process " + pid + " (error " + Marshal.GetLastWin32Error() + ")"; return 1; }
            try
            {
                GameFixes.PeInfo live;
                long imageBase = GameFixes.ImageBase(ph, out live);
                if (imageBase == 0) { text = "The game image is not readable yet"; return 1; }
                if (live.TimeDateStamp != l.TimeDateStamp || live.SizeOfImage != l.SizeOfImage) { text = "The running program is a different build than " + exePath; return 7; }
                var c = new Core(new ProcessMemory(ph), imageBase, l);
                var sb = new StringBuilder("Layout: " + l.Describe() + Environment.NewLine + c.DescribeState());
                int code = 0;
                if (rescue)
                {
                    string what = c.RescueNow();
                    sb.AppendLine(what != null ? "RELEASED: " + what + " - the load continues" : "Nothing to release: no area load is waiting for the voice slots.");
                    if (what == null) code = 3;
                }
                text = sb.ToString().TrimEnd();
                return code;
            }
            finally { Native.CloseHandle(ph); }
        }
    }
}
