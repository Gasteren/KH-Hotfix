using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace KHHotfix
{
    // The "Report a bug" window. Sending runs in the background; the window stays usable and shows the result.
    internal sealed class BugReportForm : Form
    {
        readonly string dir;
        readonly Dictionary<string, string> meta;
        readonly bool discord;
        readonly string crashDir;
        readonly ComboBox game = new ComboBox(), category = new ComboBox();
        readonly TextBox title = new TextBox(), details = new TextBox(), reporter = new TextBox();
        readonly CheckBox shot = new CheckBox(), logs = new CheckBox(), snapshot = new CheckBox(), crash = new CheckBox();
        readonly PictureBox thumb = new PictureBox();
        readonly Label status = new Label();
        readonly LinkLabel link = new LinkLabel();
        readonly Button send = new Button(), save = new Button(), close = new Button();
        readonly TableLayoutPanel root = new TableLayoutPanel();
        int rows;
        bool finished, busy, discard;

        public BugReportForm(string dir)
        {
            this.dir = dir;
            meta = BugReport.ReadMeta(dir);
            discord = Discord.IsWebhook(Settings.DiscordBugReportWebhook);
            crashDir = Reporting.LatestUnsentCrashReport(TimeSpan.FromHours(24));
            string shotFile = Path.Combine(dir, "screenshot.jpg"), snapFile = Path.Combine(dir, "snapshot.dmp");

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Text = "Report a bug - " + Program.Title;
            Icon = SystemIcons.Information;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(740, 680);
            MinimumSize = new Size(660, 640);
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;

            root.Dock = DockStyle.Fill;
            root.ColumnCount = 2;
            root.Padding = new Padding(14, 12, 14, 10);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            var intro = new Label { AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(3, 0, 3, 10) };
            intro.Text = discord
                ? "Tell us what went wrong. The report is posted to the KH Hotfix Discord, together with the attachments you choose below."
                : "Discord reporting is not set up on this PC. Save the report and attach its files to a GitHub issue, or send them to the person who gave you the hotfix.";
            AddRow(intro, null, false);

            game.DropDownStyle = ComboBoxStyle.DropDownList;
            game.Items.AddRange(BugReport.Games);
            game.Width = 360;
            int gi = Array.IndexOf(BugReport.Games, BugReport.Get(meta, "Game"));
            game.SelectedIndex = gi >= 0 ? gi : 0;
            AddRow(Caption("Game"), game, false);

            category.DropDownStyle = ComboBoxStyle.DropDownList;
            category.Items.AddRange(BugReport.Categories);
            category.Width = 360;
            if (BugReport.Get(meta, "GameResponding") == "no") category.SelectedIndex = 1;
            AddRow(Caption("Problem"), category, false);

            title.MaxLength = 90;
            title.Dock = DockStyle.Fill;
            AddRow(Caption("Title"), title, false);

            AddRow(Caption("What happened? What did you do right before (area, cutscene, menu)? Does it happen again?"), null, false);
            details.Multiline = true;
            details.AcceptsReturn = true;
            details.ScrollBars = ScrollBars.Vertical;
            details.MaxLength = 6000;
            details.Dock = DockStyle.Fill;
            details.MinimumSize = new Size(0, 110);
            AddRow(details, null, true);

            // A TextBox sizes itself to its text inside a TableLayoutPanel; the minimum keeps the field usable.
            reporter.MinimumSize = new Size(240, 0);
            reporter.MaxLength = 40;
            reporter.Text = Reporting.ReporterName;
            AddRow(Caption("Your name"), reporter, false);

            var boxes = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            shot.AutoSize = true;
            shot.Enabled = shot.Checked = File.Exists(shotFile);
            shot.Text = shot.Enabled ? "Screenshot of the game (click the picture to view it)" : "Screenshot of the game (none - no game window was found)";
            logs.AutoSize = true;
            logs.Checked = true;
            logs.Text = "Session log and system info (Windows, CPU, GPU, settings)";
            snapshot.AutoSize = true;
            snapshot.Visible = snapshot.Checked = File.Exists(snapFile);
            if (snapshot.Visible)
                snapshot.Text = "Technical snapshot of the game (" + (new FileInfo(snapFile).Length / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB, for freezes and crashes)";
            crash.AutoSize = true;
            crash.Visible = crashDir != null;
            if (crashDir != null)
            {
                DateTime t = Directory.GetCreationTime(crashDir);
                crash.Text = "Crash/freeze report from " + t.ToString("g", CultureInfo.CurrentCulture) + " (not sent yet)";
                crash.Checked = DateTime.Now - t < TimeSpan.FromHours(2);
            }
            boxes.Controls.AddRange(new Control[] { shot, logs, snapshot, crash });
            var attach = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            attach.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            attach.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            attach.Controls.Add(boxes, 0, 0);
            thumb.Size = new Size(176, 99);
            thumb.SizeMode = PictureBoxSizeMode.Zoom;
            thumb.BorderStyle = BorderStyle.FixedSingle;
            thumb.Cursor = Cursors.Hand;
            thumb.Visible = shot.Enabled;
            if (shot.Enabled)
            {
                try { thumb.Image = Image.FromStream(new MemoryStream(File.ReadAllBytes(shotFile))); }
                catch { thumb.Visible = false; }
            }
            attach.Controls.Add(thumb, 1, 0);
            AddRow(Caption("Attach"), attach, false);

            var privacy = new Label { AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 4) };
            privacy.Text = "Your Windows user name, PC name and Steam ID are removed from the logs. The technical snapshot contains part of "
                + "the game's memory (no personal files). Untick anything you don't want to share.";
            AddRow(privacy, null, false);

            var statusPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            status.AutoSize = true;
            status.MaximumSize = new Size(700, 0);
            link.AutoSize = true;
            link.Visible = false;
            link.Text = "Open the post in Discord";
            statusPanel.Controls.Add(status);
            statusPanel.Controls.Add(link);
            AddRow(statusPanel, null, false);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            foreach (Button b in new[] { close, save, send })
            {
                b.AutoSize = true;
                b.MinimumSize = new Size(110, 30);
                b.Padding = new Padding(8, 0, 8, 0);
                buttons.Controls.Add(b);
            }
            close.Text = "Cancel";
            send.Text = discord ? "Send report" : "Save report";
            save.Text = discord ? "Save only" : "Open GitHub issue";
            AddRow(buttons, null, false);

            Controls.Add(root);
            CancelButton = close;
            ResumeLayout(false);
            PerformLayout();

            Shown += delegate { OnShownFirst(); };
            FormClosing += OnClosingForm;
            FormClosed += delegate { OnClosedForm(); };
            send.Click += delegate { if (discord) SendReport(); else SaveOnly(); };
            save.Click += delegate { if (discord) SaveOnly(); else OpenIssue(); };
            close.Click += delegate { Close(); };
            thumb.Click += delegate { try { Process.Start(shotFile); } catch { } };
            link.LinkClicked += delegate { try { Process.Start((string)link.Tag); } catch { } };
        }

        static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 4) };
        }

        void AddRow(Control left, Control right, bool grow)
        {
            root.RowCount = rows + 1;
            root.RowStyles.Add(grow ? new RowStyle(SizeType.Percent, 100F) : new RowStyle(SizeType.AutoSize));
            root.Controls.Add(left, 0, rows);
            if (right == null) root.SetColumnSpan(left, 2);
            else root.Controls.Add(right, 1, rows);
            rows++;
        }

        void OnShownFirst()
        {
            Activate();
            Native.SetForegroundWindow(Handle);
            Cue(title, "Short summary, e.g. Stuck on a loading screen after a cutscene");
            Cue(reporter, "optional - shown in Discord");
            if (category.SelectedIndex < 0) category.Focus(); else title.Focus();
        }

        static void Cue(TextBox t, string text)
        {
            try { Native.SendMessage(t.Handle, 0x1501 /* EM_SETCUEBANNER */, new IntPtr(1), text); } catch { }
        }

        void Say(string text, Color color)
        {
            status.ForeColor = color;
            status.Text = text;
        }

        BugReport.Draft Collect()
        {
            if (category.SelectedIndex < 0) { Say("Please choose what kind of problem it is.", Color.Firebrick); category.Focus(); return null; }
            if (title.Text.Trim().Length == 0 && details.Text.Trim().Length == 0)
            {
                Say("Please give the report a title or describe what happened.", Color.Firebrick);
                title.Focus();
                return null;
            }
            return new BugReport.Draft
            {
                Game = game.SelectedItem as string,
                Category = category.SelectedItem as string,
                Title = title.Text.Trim(),
                Description = details.Text.Trim(),
                Reporter = reporter.Text.Trim(),
                Screenshot = shot.Enabled && shot.Checked,
                Logs = logs.Checked,
                Snapshot = snapshot.Visible && snapshot.Checked,
                CrashReport = crash.Visible && crash.Checked ? crashDir : null
            };
        }

        void SetBusy(bool on)
        {
            busy = on;
            UseWaitCursor = on;
            foreach (Control c in new Control[] { game, category, title, details, reporter, shot, logs, snapshot, crash, send, save }) c.Enabled = !on && !finished;
            if (!on && !File.Exists(Path.Combine(dir, "screenshot.jpg"))) shot.Enabled = false;
            close.Enabled = !on;
        }

        void SendReport()
        {
            BugReport.Draft d = Collect();
            if (d == null) return;
            SetBusy(true);
            Say("Sending to Discord...", SystemColors.ControlText);
            var worker = new Thread(delegate()
            {
                Discord.Result r = null;
                string error = null;
                try { r = BugReport.Send(dir, d); }
                catch (Exception ex) { error = ex.Message; Log.Error("Bug report: " + ex); }
                try { BeginInvoke((MethodInvoker)delegate { Finish(r, error); }); } catch { }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        void Finish(Discord.Result r, string error)
        {
            if (r != null && r.Ok)
            {
                finished = true;
                SetBusy(false);
                Say("Sent - thank you!" + (r.Dropped.Count > 0 ? " (Too large for Discord and left out: " + string.Join(", ", r.Dropped) + ".)" : ""), Color.DarkGreen);
                if (r.Link != null) { link.Tag = r.Link; link.Visible = true; }
                close.Text = "Close";
                close.Focus();
                return;
            }
            SetBusy(false);
            Say("Not sent: " + (error ?? r.Error) + " The report is saved in " + dir + ". Try again later or use \"Save only\".", Color.Firebrick);
        }

        void SaveOnly()
        {
            BugReport.Draft d = Collect();
            if (d == null) return;
            Reporting.ReporterName = d.Reporter;
            string f = BugReport.SaveText(dir, d);
            try { Process.Start("explorer.exe", "/select,\"" + f + "\""); } catch { }
            finished = true;
            Close();
        }

        void OpenIssue()
        {
            BugReport.Draft d = Collect();
            if (d == null) return;
            Reporting.ReporterName = d.Reporter;
            BugReport.OpenIssue(dir, d);
            finished = true;
            Close();
        }

        void OnClosingForm(object sender, FormClosingEventArgs e)
        {
            if (busy) { e.Cancel = true; return; }
            if (finished || File.Exists(Path.Combine(dir, "report.txt"))) return;
            bool typed = title.Text.Trim().Length > 0 || details.Text.Trim().Length > 0;
            if (typed && MessageBox.Show(this, "Discard this bug report?", Program.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            discard = true;   // nothing was sent or saved: the screenshot and snapshot are removed again
        }

        void OnClosedForm()
        {
            if (thumb.Image != null) { thumb.Image.Dispose(); thumb.Image = null; }
            if (discard)
            {
                try { Directory.Delete(dir, true); Log.Info("Bug report discarded: " + dir); }
                catch (Exception ex) { Log.Warn("Could not remove " + dir + ": " + ex.Message); }
            }
            // Back to the game (KH pauses itself while another window has the focus).
            long h;
            string w = BugReport.Get(meta, "GameWindow");
            if (w != null && long.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out h))
            {
                var hwnd = new IntPtr(h);
                if (Native.IsWindow(hwnd)) Native.SetForegroundWindow(hwnd);
            }
        }
    }
}
