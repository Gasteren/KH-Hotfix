using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace KHHotfix
{
    internal enum SaveStatus { Ok, Empty, NotPng, Truncated, BadCrc, Unreadable }

    internal sealed class PngResult
    {
        public SaveStatus Status;
        public string Detail = "";
        public int Chunks;
        public long Trailing;
    }

    // Structural PNG validation. KH 1.5+2.5 stores every save slot of a game inside one PNG container
    // (KHFM_WW.png, KHIIFM_WW.png, ...). A crash while the game rewrites it leaves a truncated file.
    internal static class PngCheck
    {
        static readonly uint[] Table = MakeTable();
        static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Crc(byte[] d, int off, int len)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < len; i++) c = Table[(c ^ d[off + i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        static uint BE(byte[] d, long o) { return ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3]; }

        public static PngResult Check(byte[] d)
        {
            var r = new PngResult();
            if (d == null) { r.Status = SaveStatus.Unreadable; r.Detail = "could not read file"; return r; }
            if (d.Length == 0) { r.Status = SaveStatus.Empty; r.Detail = "file is 0 bytes"; return r; }
            if (d.Length < 8 || !d.Take(8).SequenceEqual(Signature)) { r.Status = SaveStatus.NotPng; r.Detail = "missing PNG signature"; return r; }
            long pos = 8;
            string firstBad = null;
            while (true)
            {
                if (pos == d.Length) { r.Status = SaveStatus.Truncated; r.Detail = "file ends without IEND chunk"; return r; }
                if (pos + 12 > d.Length) { r.Status = SaveStatus.Truncated; r.Detail = "file ends inside a chunk header at offset " + pos; return r; }
                uint len = BE(d, pos);
                if (len > int.MaxValue || pos + 12 + len > d.Length)
                {
                    r.Status = SaveStatus.Truncated;
                    r.Detail = string.Format(CultureInfo.InvariantCulture, "chunk at offset {0} needs {1} bytes but the file is {2} bytes", pos, len, d.Length);
                    return r;
                }
                string type = Encoding.ASCII.GetString(d, (int)pos + 4, 4);
                if (BE(d, pos + 8 + len) != Crc(d, (int)pos + 4, (int)len + 4) && firstBad == null) firstBad = type + "@" + pos;
                r.Chunks++;
                pos += 12 + len;
                if (type == "IEND") break;
            }
            r.Trailing = d.Length - pos;
            if (firstBad != null) { r.Status = SaveStatus.BadCrc; r.Detail = "CRC mismatch in chunk " + firstBad; return r; }
            r.Status = SaveStatus.Ok;
            r.Detail = r.Chunks + " chunks" + (r.Trailing > 0 ? ", " + r.Trailing + " trailing bytes" : "");
            return r;
        }
    }

    internal sealed class BackupEntry
    {
        public string FullPath, Hash, Status;
        public DateTime Time;   // UTC (local time repeats an hour when daylight saving time ends)
        public long Size;

        // <yyyyMMdd-HHmmss-fff UTC>_<sha1-12>_<size>_<status>.gz
        public static string MakeName(DateTime utc, string hash, long size, string status)
        {
            return utc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "_" + hash + "_" + size.ToString(CultureInfo.InvariantCulture) + "_" + status + ".gz";
        }

        public string LocalTimeText { get { return Time.ToLocalTime().ToString("g"); } }

        public static BackupEntry Parse(string path)
        {
            string n = Path.GetFileName(path);
            if (!n.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) return null;
            string[] p = n.Substring(0, n.Length - 3).Split('_');
            DateTime t;
            long size;
            if (p.Length != 4) return null;
            if (!DateTime.TryParseExact(p[0], "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t)) return null;
            if (!long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out size)) return null;
            return new BackupEntry { FullPath = path, Time = t, Hash = p[1], Size = size, Status = p[3] };
        }
    }

    internal sealed class SaveGuard
    {
        const long MaxFileBytes = 64L * 1024 * 1024;
        public readonly List<string> SessionNotices = new List<string>();
        public int SnapshotsTaken;
        Dictionary<string, FileSig> lastSeen = new Dictionary<string, FileSig>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);

        struct FileSig { public long Len, Ticks, Created; }
        sealed class Pending { public FileSig Sig; public DateTime Since; }

        public static bool IsPng(string f) { return f.EndsWith(".png", StringComparison.OrdinalIgnoreCase); }

        public static List<string> EnumerateSaveFiles()
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(Paths.SavesRoot)) return list;
                foreach (string f in Directory.GetFiles(Paths.SavesRoot, "*", SearchOption.AllDirectories))
                {
                    if (f.EndsWith(".khhotfix-tmp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".khhotfix-test", StringComparison.OrdinalIgnoreCase)) continue;
                    try { if (new FileInfo(f).Length > MaxFileBytes) continue; } catch { continue; }
                    list.Add(f);
                }
            }
            catch (Exception ex) { Log.Warn("Cannot enumerate saves: " + ex.Message); }
            return list;
        }

        public static string BackupDirFor(string f) { return Path.Combine(Paths.Backups, Paths.Rel(f)); }

        public static List<BackupEntry> ListBackups(string dir)
        {
            if (!Directory.Exists(dir)) return new List<BackupEntry>();
            return Directory.GetFiles(dir, "*.gz").Select(BackupEntry.Parse).Where(e => e != null).OrderByDescending(e => e.Time).ToList();
        }

        public static byte[] ReadShared(string f)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var buf = new byte[fs.Length];
                        int off = 0;
                        while (off < buf.Length) { int n = fs.Read(buf, off, buf.Length - off); if (n <= 0) break; off += n; }
                        if (off != buf.Length) Array.Resize(ref buf, off);
                        return buf;
                    }
                }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                catch (IOException) { if (attempt >= 8) return null; Thread.Sleep(250); }
                catch (UnauthorizedAccessException) { if (attempt >= 2) return null; Thread.Sleep(250); }
            }
        }

        static string Hash12(byte[] d)
        {
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(d);
                var sb = new StringBuilder();
                for (int i = 0; i < 6; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static void WriteGz(string target, byte[] data)
        {
            string tmp = target + ".khhotfix-tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var gz = new GZipStream(fs, CompressionMode.Compress, true)) gz.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
        }

        public static byte[] ReadGz(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                gz.CopyTo(ms);
                return ms.ToArray();
            }
        }

        // Writes data to target via a temp file in the same folder, so a crash never leaves a half-written save.
        public static void AtomicWrite(string target, byte[] data)
        {
            string tmp = target + ".khhotfix-tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            if (File.Exists(target))
            {
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                File.Replace(tmp, target, null);
            }
            else File.Move(tmp, target);
        }

        public static string Quarantine(string f, byte[] data, string why)
        {
            string dir = Path.Combine(Paths.Quarantine, Paths.Rel(f));
            Directory.CreateDirectory(dir);
            string q = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "_" + why + ".bin");
            File.WriteAllBytes(q, data ?? new byte[0]);
            return q;
        }

        public bool SnapshotFile(string f, string reason)
        {
            byte[] data = ReadShared(f);
            if (data == null) { Log.Warn("Backup skipped (unreadable): " + Paths.Rel(f)); return false; }
            string hash = Hash12(data);
            string dir = BackupDirFor(f);
            List<BackupEntry> hist = ListBackups(dir);
            if (hist.Count > 0 && hist[0].Hash == hash) return false;
            string status = IsPng(f) ? PngCheck.Check(data).Status.ToString() : "Raw";
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, BackupEntry.MakeName(DateTime.UtcNow, hash, data.LongLength, status));
            if (File.Exists(target)) return false;
            WriteGz(target, data);
            SnapshotsTaken++;
            Log.Info(string.Format(CultureInfo.InvariantCulture, "Backup [{0}] {1} ({2:N0} bytes, {3})", reason, Paths.Rel(f), data.LongLength, status));
            Prune(dir);
            return true;
        }

        public int SnapshotAll(string reason)
        {
            int n = 0;
            foreach (string f in EnumerateSaveFiles())
            {
                try { if (SnapshotFile(f, reason)) n++; }
                catch (Exception ex) { Log.Warn("Backup failed for " + f + ": " + ex.Message); }
            }
            lastSeen = ScanSigs();
            return n;
        }

        public static void Prune(string dir)
        {
            List<BackupEntry> all = ListBackups(dir);
            if (all.Count <= Settings.BackupKeep) return;
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BackupEntry e in all.Take(Settings.BackupKeep)) keep.Add(e.FullPath);
            DateTime cutoff = DateTime.UtcNow.AddDays(-Settings.BackupDailyDays);
            foreach (var day in all.Where(e => e.Time >= cutoff).GroupBy(e => e.Time.ToLocalTime().Date)) keep.Add(day.First().FullPath);
            BackupEntry newestOk = all.FirstOrDefault(e => e.Status == "Ok");
            if (newestOk != null) keep.Add(newestOk.FullPath);
            foreach (BackupEntry e in all)
            {
                if (!keep.Contains(e.FullPath)) { try { File.Delete(e.FullPath); } catch { } }
            }
        }

        // ---- checks before the game starts ------------------------------------------------------
        public void Preflight(Notices notices)
        {
            string ansi = Paths.DocumentsAnsi;
            if (ansi == null || ansi.IndexOf('?') >= 0 || !Directory.Exists(ansi))
            {
                notices.Add("The game will NOT be able to save. It locates Documents with legacy ANSI file APIs and gets \"" + (ansi ?? "(nothing)")
                    + "\" instead of \"" + Paths.Documents + "\". Fix: Settings > Time & language > Language & region > Administrative language settings > "
                    + "Change system locale > enable 'Beta: Use Unicode UTF-8 for worldwide language support' and reboot, or move Documents to a plain-ASCII path.");
            }

            foreach (string ev in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            {
                string od = Environment.GetEnvironmentVariable(ev);
                if (string.IsNullOrEmpty(od) || !Paths.SavesRoot.StartsWith(od.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                if (Paths.OnceFlag("onedrive"))
                    notices.Add("Your saves are inside a OneDrive-synced folder (" + od + "). OneDrive can lock or replace save files while the game writes them. "
                        + "Pause OneDrive while playing or stop syncing 'Documents\\My Games\\KINGDOM HEARTS HD 1.5+2.5 ReMIX'.");
                break;
            }

            foreach (string f in EnumerateSaveFiles())
            {
                try
                {
                    FileAttributes attr = File.GetAttributes(f);
                    if ((attr & FileAttributes.ReadOnly) == 0) continue;
                    File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                    Log.Warn("Cleared read-only attribute: " + f);
                    notices.Detail("Removed the read-only flag from " + Paths.Rel(f) + " (it makes the game fail with \"Failed to save game data\").");
                }
                catch (Exception ex) { Log.Warn("Attribute check failed for " + f + ": " + ex.Message); }
            }

            long free = Native.FreeBytes(Paths.Documents);
            if (free >= 0 && free < 512L * 1024 * 1024)
                notices.Add("Only " + (free / (1024 * 1024)) + " MB free on the drive that holds Documents. The game refuses to save when space runs out.");

            string probe = new[] { Paths.SavesRoot, Path.GetDirectoryName(Paths.SavesRoot), Paths.Documents }.FirstOrDefault(Directory.Exists);
            string err = WriteTest(probe);
            if (err == null) Log.Info("Write test OK: " + probe);
            else notices.Add("A test write to \"" + probe + "\" failed (" + err + "). The game will show \"Failed to save game data\". Typical causes: "
                + "Windows Security > Ransomware protection (Controlled Folder Access), antivirus, or folder permissions. Run Install.cmd again - it offers to allow the game.");

            int cfa = ControlledFolderAccessState();
            Log.Info("Controlled Folder Access state: " + (cfa < 0 ? "unknown" : cfa.ToString(CultureInfo.InvariantCulture)));
            if (cfa == 1 && !File.Exists(Path.Combine(Paths.State, "cfa-allowed.txt")) && Paths.OnceFlag("cfa"))
                notices.Add("Windows Controlled Folder Access is ON. It silently blocks unknown programs from writing to Documents, which breaks KH saves. "
                    + "Run Install.cmd again and answer Y when it offers to allow the KH executables.");
        }

        static string WriteTest(string dir)
        {
            if (dir == null) return "no existing folder to test";
            string f = Path.Combine(dir, "khhotfix-write-test-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".khhotfix-test");
            try
            {
                var data = new byte[64 * 1024];
                new Random().NextBytes(data);
                using (var fs = new FileStream(f, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                    fs.Position = 0;
                    var back = new byte[data.Length];
                    int n = fs.Read(back, 0, back.Length);
                    if (n != data.Length || !back.SequenceEqual(data)) return "read-back mismatch";
                }
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
            finally { try { File.Delete(f); } catch { } }
        }

        public static int ControlledFolderAccessState()
        {
            string[] keys =
            {
                @"SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access",
                @"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access"
            };
            int result = -1;
            try
            {
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    foreach (string k in keys)
                    {
                        using (RegistryKey key = hklm.OpenSubKey(k))
                        {
                            object v = key == null ? null : key.GetValue("EnableControlledFolderAccess");
                            if (v is int) return (int)v;
                            if (key != null) result = 0;
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        // Restores the newest known-good backup when a save is provably damaged. Every rule is calibrated against
        // this PC's own history of good saves, so an unknown save layout can never trigger a restore:
        //  - structural damage (truncated chunk, missing IEND, not a PNG) once a real save of that file passed validation
        //  - CRC damage once at least two good versions passed and none ever failed the CRC
        //  - a file shorter than the size that every good version had (the payload may sit after IEND)
        //  - an empty file
        public void HealBeforeLaunch(Notices notices)
        {
            foreach (string f in EnumerateSaveFiles())
            {
                if (!IsPng(f)) continue;
                byte[] data = ReadShared(f);
                PngResult r = PngCheck.Check(data);
                if (r.Status == SaveStatus.Unreadable) continue;
                string rel = Paths.Rel(f);
                List<BackupEntry> hist = ListBackups(BackupDirFor(f));
                List<BackupEntry> okHist = hist.Where(e => e.Status == "Ok").ToList();
                bool fixedSize = okHist.Count >= 2 && okHist.All(e => e.Size == okHist[0].Size);
                string damage = null;
                if (r.Status == SaveStatus.Ok)
                {
                    if (fixedSize && data.LongLength < okHist[0].Size)
                        damage = string.Format(CultureInfo.InvariantCulture, "file is {0:N0} bytes but every good save so far was {1:N0} bytes", data.LongLength, okHist[0].Size);
                    else
                    {
                        if (okHist.Count > 0 && data.LongLength < okHist[0].Size * 9 / 10)
                            notices.Add(rel + " is much smaller than your last good save (" + data.LongLength.ToString("N0", CultureInfo.InvariantCulture) + " vs "
                                + okHist[0].Size.ToString("N0", CultureInfo.InvariantCulture) + " bytes). If the game calls it corrupt, quit and run Restore.cmd before saving again.");
                        continue;
                    }
                }
                else if (r.Status == SaveStatus.Empty) damage = r.Detail;
                else if (okHist.Count == 0) { Log.Info(rel + ": validator says " + r.Status + " (" + r.Detail + ") - ignored, no good save of this file seen yet"); continue; }
                else if (r.Status == SaveStatus.BadCrc && (okHist.Count < 2 || hist.Any(e => e.Status == "BadCrc"))) { Log.Info(rel + ": CRC mismatch ignored (not calibrated)"); continue; }
                else damage = r.Detail;

                Log.Warn(rel + " is damaged: " + damage);
                BackupEntry good = okHist.FirstOrDefault();
                if (good == null) { notices.Add(rel + " is damaged (" + damage + ") and there is no known-good backup yet. The game may report it as corrupt and delete it."); continue; }
                if (!Settings.AutoHeal) { notices.Add(rel + " is damaged (" + damage + "). Run Restore.cmd to restore the backup from " + good.LocalTimeText + "."); continue; }
                string q = Quarantine(f, data, r.Status == SaveStatus.Ok ? "Shortened" : r.Status.ToString());
                AtomicWrite(f, ReadGz(good.FullPath));
                Log.Warn("Auto-heal: restored " + rel + " from " + good.FullPath + "; damaged copy kept at " + q);
                notices.Add(rel + " was damaged (" + damage + "), most likely by a crash while saving. The hotfix restored your last good save from "
                    + good.LocalTimeText + " so the game does not delete it. The damaged copy was kept in Backups\\_quarantine.");
            }
        }

        // ---- monitoring while the game runs ------------------------------------------------------
        static Dictionary<string, FileSig> ScanSigs()
        {
            var d = new Dictionary<string, FileSig>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in EnumerateSaveFiles())
            {
                try
                {
                    var fi = new FileInfo(f);
                    d[f] = new FileSig { Len = fi.Length, Ticks = fi.LastWriteTimeUtc.Ticks, Created = fi.CreationTimeUtc.Ticks };
                }
                catch { }
            }
            return d;
        }

        public void Poll()
        {
            DateTime now = DateTime.UtcNow;
            Dictionary<string, FileSig> cur = ScanSigs();
            foreach (var kv in cur)
            {
                FileSig old;
                bool known = lastSeen.TryGetValue(kv.Key, out old);
                if (known && old.Created != kv.Value.Created) Log.Info("Save file was re-created by the game: " + Paths.Rel(kv.Key));
                if (!known || old.Len != kv.Value.Len || old.Ticks != kv.Value.Ticks) pending[kv.Key] = new Pending { Sig = kv.Value, Since = now };
            }
            foreach (string gone in lastSeen.Keys.Where(k => !cur.ContainsKey(k)).ToList())
            {
                pending.Remove(gone);
                Log.Warn("Save file disappeared during the session: " + Paths.Rel(gone));
                if (IsPng(gone))
                    SessionNotices.Add("The game deleted " + Paths.Rel(gone) + " at " + DateTime.Now.ToString("t") + " (it does this when it believes a save is corrupt). "
                        + "The previous version is in the hotfix backups - run Restore.cmd to get it back.");
            }
            lastSeen = cur;
            foreach (var kv in pending.ToList())
            {
                FileSig s;
                if (!cur.TryGetValue(kv.Key, out s)) { pending.Remove(kv.Key); continue; }
                if (s.Len != kv.Value.Sig.Len || s.Ticks != kv.Value.Sig.Ticks) { kv.Value.Sig = s; kv.Value.Since = now; continue; }
                if ((now - kv.Value.Since).TotalSeconds < 3) continue;
                pending.Remove(kv.Key);
                try { SnapshotFile(kv.Key, "in-game save"); }
                catch (Exception ex) { Log.Warn("Backup failed for " + kv.Key + ": " + ex.Message); }
            }
        }

        // Waits (up to maxMs) until no save file changes for one second, then snapshots everything.
        public void WaitForStableAndSnapshot(string reason, int maxMs)
        {
            Dictionary<string, FileSig> a = ScanSigs();
            for (int waited = 0; waited < maxMs; waited += 1000)
            {
                Thread.Sleep(1000);
                Dictionary<string, FileSig> b = ScanSigs();
                bool same = a.Count == b.Count && a.All(kv => { FileSig s; return b.TryGetValue(kv.Key, out s) && s.Len == kv.Value.Len && s.Ticks == kv.Value.Ticks; });
                if (same) break;
                a = b;
            }
            SnapshotAll(reason);
        }
    }
}
