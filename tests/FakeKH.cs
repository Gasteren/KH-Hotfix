// Test double for the KH executables, used by Run-Tests.ps1. Copied into a fake game folder as
//   "KINGDOM HEARTS HD 1.5+2.5 ReMIX.exe" (bootstrapper) and "KINGDOM HEARTS FINAL MIX.exe" (game).
// FAKEKH_MODE: normal | crash | hang | hungwindow | corruptsave | deletesave
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

static class FakeKH
{
    const int PayloadBytes = 50000; // fixed-size save payload stored after the PNG's IEND chunk

    [STAThread]
    static int Main()
    {
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
            if (step == (mode == "deletesave" ? 14 : 8)) { timer.Stop(); form.Close(); }
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
