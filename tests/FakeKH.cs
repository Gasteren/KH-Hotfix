// Test double for the KH executables, used by Run-Tests.ps1. Copied into a fake game folder as
//   "KINGDOM HEARTS HD 1.5+2.5 ReMIX.exe" (bootstrapper) and "KINGDOM HEARTS FINAL MIX.exe" (game).
// FAKEKH_MODE: normal | crash | hang | hungwindow | corruptsave | deletesave | idle (window stays open until killed)
// FakeKH.exe --fake-discord <dir>: a local stand-in for Discord's webhook API (see FakeDiscord below).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class FakeKH
{
    const int PayloadBytes = 50000; // fixed-size save payload stored after the PNG's IEND chunk

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--fake-discord") return FakeDiscord.Run(args[1]);
        string me = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        string mode = Environment.GetEnvironmentVariable("FAKEKH_MODE") ?? "normal";
        string savesRoot = Environment.GetEnvironmentVariable("KHHOTFIX_SAVES_ROOT");
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(savesRoot), "fake-cmdlines.txt"), me + " | " + Environment.CommandLine + Environment.NewLine);

        if (me.EndsWith("ReMIX", StringComparison.OrdinalIgnoreCase))
        {
            Process p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "KINGDOM HEARTS FINAL MIX.exe")) { UseShellExecute = false });
            p.WaitForExit();
            return 0;
        }

        string save = Path.Combine(savesRoot, @"Steam\76561190000000000\KHFM_WW.png");
        Directory.CreateDirectory(Path.GetDirectoryName(save));
        var form = new Form { Text = "FAKE KINGDOM HEARTS (" + mode + ")", Width = 360, Height = 160, StartPosition = FormStartPosition.Manual, Left = 40, Top = 40, ShowInTaskbar = false };
        int step = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += delegate
        {
            step++;
            if (step == 2) WriteSave(save, 1, mode == "corruptsave" ? 40 : -1);
            if (step == 5 && mode != "corruptsave") WriteSave(save, 2, -1);
            if (step == 5 && mode == "deletesave") File.Delete(save);
            if (step == 12 && mode == "deletesave") WriteSave(save, 3, -1);
            if (step == 6 && mode == "hungwindow") Thread.Sleep(Timeout.Infinite);
            if (mode != "idle" && step == (mode == "deletesave" ? 14 : 8)) { timer.Stop(); form.Close(); }
        };
        form.Shown += delegate { timer.Start(); };
        Application.Run(form);

        if (mode == "hang") Thread.Sleep(Timeout.Infinite);
        if (mode == "crash" || mode == "corruptsave") Environment.Exit(unchecked((int)0xC0000005));
        return 0;
    }

    static void WriteSave(string path, int version, int cutAt)
    {
        byte[] png;
        using (var bmp = new Bitmap(8, 8))
        using (var ms = new MemoryStream())
        {
            bmp.SetPixel(version % 8, 0, Color.Red);
            bmp.Save(ms, ImageFormat.Png);
            png = ms.ToArray();
        }
        var data = new byte[png.Length + PayloadBytes];
        png.CopyTo(data, 0);
        int seed = Process.GetCurrentProcess().Id;   // unique content per session, same size
        for (int i = 0; i < PayloadBytes; i++) data[png.Length + i] = (byte)(i * version + seed);
        int len = cutAt >= 0 ? Math.Min(cutAt, data.Length) : data.Length;
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) fs.Write(data, 0, len); // in-place, like the game
    }
}

// Local stand-in for Discord's webhook API. Webhook id 1 behaves like a forum channel (a message needs thread_name,
// error 220001 otherwise), webhook id 2 like a text channel (thread_name is rejected with error 220003).
// Writes <dir>\port.txt when ready; every accepted message is saved as post-N.json (payload_json) and post-N.files
// ("name size" per attachment); every request is listed in requests.txt. Stops when <dir>\stop.txt appears.
static class FakeDiscord
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString();
        File.WriteAllText(Path.Combine(dir, "port.tmp"), port);
        File.Move(Path.Combine(dir, "port.tmp"), Path.Combine(dir, "port.txt"));   // appears complete
        int posts = 0;
        while (!File.Exists(Path.Combine(dir, "stop.txt")))
        {
            if (!listener.Pending()) { Thread.Sleep(20); continue; }
            using (TcpClient c = listener.AcceptTcpClient())
            {
                try { Handle(c.GetStream(), dir, ref posts); }
                catch (Exception ex) { File.AppendAllText(Path.Combine(dir, "errors.txt"), ex + Environment.NewLine); }
            }
        }
        listener.Stop();
        return 0;
    }

    static void Handle(NetworkStream s, string dir, ref int posts)
    {
        s.ReadTimeout = 15000;
        var head = new StringBuilder();
        while (!head.ToString().EndsWith("\r\n\r\n"))
        {
            int b = s.ReadByte();
            if (b < 0) return;
            head.Append((char)b);
        }
        string[] lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
        string request = lines[0].Substring(0, lines[0].LastIndexOf(' '));
        int length = 0;
        string type = "";
        foreach (string l in lines)
        {
            if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(l.Substring(15).Trim());
            if (l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)) type = l.Substring(13).Trim();
        }
        var body = new byte[length];
        for (int got = 0; got < length; )
        {
            int n = s.Read(body, got, length - got);
            if (n <= 0) break;
            got += n;
        }
        string payload = null;
        var files = new List<string>();
        Match bm = Regex.Match(type, "boundary=(\\S+)");
        if (bm.Success)
        {
            Encoding latin1 = Encoding.GetEncoding(28591);
            foreach (string part in latin1.GetString(body).Split(new[] { "--" + bm.Groups[1].Value }, StringSplitOptions.None))
            {
                int split = part.IndexOf("\r\n\r\n");
                if (split < 0) continue;
                string ph = part.Substring(0, split), pb = part.Substring(split + 4);
                if (pb.EndsWith("\r\n")) pb = pb.Substring(0, pb.Length - 2);
                Match fn = Regex.Match(ph, "filename=\"([^\"]*)\"");
                if (ph.Contains("name=\"payload_json\"")) payload = Encoding.UTF8.GetString(latin1.GetBytes(pb));
                else if (fn.Success) files.Add(fn.Groups[1].Value + " " + pb.Length);
            }
        }
        Match id = Regex.Match(request, "/webhooks/(\\d+)/");
        string hook = id.Success ? id.Groups[1].Value : "0";
        bool titled = payload != null && payload.Contains("\"thread_name\"");
        string reply;
        if (request.StartsWith("GET")) reply = "200 {\"id\":\"" + hook + "\",\"name\":\"Fake webhook " + hook + "\",\"channel_id\":\"5" + hook + "\",\"guild_id\":\"777\",\"token\":\"x\"}";
        else if (hook == "1" && !titled) reply = "400 {\"code\": 220001, \"message\": \"Webhooks posted to forum channels must have a thread_name or thread_id\"}";
        else if (hook == "2" && titled) reply = "400 {\"code\": 220003, \"message\": \"Webhooks can only create threads in forum channels\"}";
        else
        {
            posts++;
            File.WriteAllText(Path.Combine(dir, "post-" + posts + ".json"), payload ?? "", Encoding.UTF8);
            File.WriteAllLines(Path.Combine(dir, "post-" + posts + ".files"), files.ToArray());
            File.WriteAllText(Path.Combine(dir, "post-" + posts + ".hook"), hook);
            reply = "200 {\"id\":\"9" + posts + "\",\"channel_id\":\"" + (titled ? "6" + posts : "5" + hook) + "\"}";
        }
        File.AppendAllText(Path.Combine(dir, "requests.txt"), request + " -> " + reply.Substring(0, 3) + Environment.NewLine);
        int sp = reply.IndexOf(' ');
        byte[] json = Encoding.UTF8.GetBytes(reply.Substring(sp + 1));
        byte[] hdr = Encoding.ASCII.GetBytes("HTTP/1.1 " + reply.Substring(0, sp) + " Fake\r\nContent-Type: application/json\r\nContent-Length: " + json.Length + "\r\nConnection: close\r\n\r\n");
        s.Write(hdr, 0, hdr.Length);
        s.Write(json, 0, json.Length);
    }
}
