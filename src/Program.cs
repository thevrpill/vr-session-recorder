using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace VrRecorder {
    public static class Program {
        [STAThread] public static int Main(string[] args) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--report") { Report.Create(Path.GetFullPath(args[1])); return 0; }
            if (args.Length == 1 && args[0] == "--preview") { Application.Run(new RecorderWindow(true)); return 0; }
            bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            if (!admin) {
                try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Files.Root, Arguments = args.Length > 0 ? "\"" + string.Join("\" \"", args) + "\"" : "" }); return 0; }
                catch (System.ComponentModel.Win32Exception e) {
                    if (e.NativeErrorCode == 1223) MessageBox.Show("VR timing capture needs Windows administrator permission. Open the recorder again and accept the Windows prompt when you want to record.", "VR Session Recorder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else MessageBox.Show(e.Message, "VR Session Recorder");
                    return 1;
                }
            }
            if (args.Length == 1 && args[0] == "--test-toggle") { TestToggle(); return 0; }
            bool created;
            using (var mutex = new Mutex(true, @"Local\VRSessionRecorder-v1", out created)) {
                if (!created) { MessageBox.Show("The recorder is already watching for GTA. Open it from the icon next to the Windows clock.", "VR Session Recorder"); return 0; }
                Application.Run(new RecorderWindow());
            }
            return 0;
        }
        // Lets the user confirm that Virtual Desktop reacts to the simulated Shift+Win+D presses.
        static void TestToggle() {
            var answer = MessageBox.Show("This tests the automatic stream refresh.\n\nWith Virtual Desktop streaming (GTA running in VR is ideal), click OK and put the headset on.\n\nAfter 10 seconds the recorder will bring the VD Streamer window forward, press Shift+Win+D, wait 3 seconds, press it again, and give focus back to the window you were in. You should briefly leave VR and come back.", "VR Session Recorder - test stream refresh", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (answer != DialogResult.OK) return;
            string result;
            try { Thread.Sleep(10000); result = VdToggle.Run(3000); }
            catch (Exception e) { result = "Sending the key presses failed: " + e.Message; }
            try { File.AppendAllText(Path.Combine(Files.Root, "toggle-test-log.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + result + Environment.NewLine); } catch { }
            MessageBox.Show(result + "\n\nDid Virtual Desktop leave VR and come back? If not, see \"Troubleshooting\" in README.md.", "VR Session Recorder - test stream refresh");
        }
        public static void Open(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) { MessageBox.Show(e.Message, "VR Session Recorder"); } }
    }
    public sealed class RecorderWindow : Form {
        readonly Label state, details;
        readonly Button reportButton, stopButton;
        readonly NotifyIcon tray;
        readonly Thread watcher;
        readonly ManualResetEvent exit = new ManualResetEvent(false);
        readonly object gate = new object();
        Recording current;
        string latest;
        int skipPid = -1;
        bool exiting;
        volatile bool autoRefreshOn;
        public RecorderWindow(bool preview = false) {
            Text = preview ? "VR Session Recorder — layout preview" : "VR Session Recorder"; ClientSize = new Size(650, 490); MinimumSize = new Size(666, 529); MaximumSize = new Size(666, 529);
            StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            BackColor = Color.FromArgb(16, 24, 32); ForeColor = Color.FromArgb(237, 245, 246); Font = new Font("Segoe UI", 10F); Icon = SystemIcons.Application;
            var title = new Label { Text = "Play normally. We’ll keep the readings.", Location = new Point(28, 25), Size = new Size(600, 40), Font = new Font("Segoe UI", 19F, FontStyle.Bold) }; Controls.Add(title);
            var subtitle = new Label { Text = "GTA V Enhanced (REAL VR) · Virtual Desktop · automatic stream refresh", Location = new Point(29, 72), Size = new Size(600, 25), ForeColor = Color.FromArgb(168, 189, 201) }; Controls.Add(subtitle);
            var panel = new Panel { BackColor = Color.FromArgb(25, 36, 47), Location = new Point(28, 115), Size = new Size(594, 105) }; Controls.Add(panel);
            state = new Label { Text = "Ready — waiting for GTA V", Location = new Point(16, 14), Size = new Size(560, 27), Font = new Font("Segoe UI", 12F, FontStyle.Bold), ForeColor = Color.FromArgb(110, 231, 192) }; panel.Controls.Add(state);
            details = new Label { Text = "Start GTA as usual. Recording begins when the game opens.\nA separate report is saved when you close the game.", Location = new Point(17, 48), Size = new Size(553, 48), ForeColor = Color.FromArgb(168, 189, 201) }; panel.Controls.Add(details);
            Controls.Add(new Label { Text = "Captures frame pacing, GPU memory / load / temperature,\nencoder readings, game memory and available game logs.", Location = new Point(29, 237), Size = new Size(590, 47) });
            var settings = RecorderSettings.Load();
            autoRefreshOn = settings.AutoRefreshStream;
            var refreshOption = new CheckBox { Text = "", AccessibleName = "Refresh the VD stream automatically when it slows down", Checked = autoRefreshOn, Location = new Point(29, 291), Size = new Size(22, 27), ForeColor = ForeColor }; Controls.Add(refreshOption);
            var refreshLabel = new Label { Text = "Auto-refresh the VD stream when it slows (when you open the map)", Location = new Point(53, 295), Size = new Size(566, 23), ForeColor = ForeColor }; Controls.Add(refreshLabel); refreshLabel.Click += delegate { refreshOption.Checked = !refreshOption.Checked; };
            refreshOption.CheckedChanged += delegate { autoRefreshOn = refreshOption.Checked; settings.AutoRefreshStream = autoRefreshOn; try { settings.Save(); } catch (Exception e) { MessageBox.Show(e.Message, "Could not save preference"); } };
            Controls.Add(new Label { Text = "Only after a slowdown is detected: when you open the map, it switches to the VD\nStreamer window, presses Shift+Win+D twice (out of VR and back), then returns to GTA.", Location = new Point(53, 320), Size = new Size(566, 40), ForeColor = Color.FromArgb(168, 189, 201), Font = new Font("Segoe UI", 9F) });
            var hide = ButtonAt("Keep in background", 28, 378, 185); hide.Click += delegate { Hide(); };
            var recordings = ButtonAt("Recordings", 225, 378, 121); recordings.Click += delegate { Directory.CreateDirectory(Files.Sessions); Program.Open(Files.Sessions); };
            reportButton = ButtonAt("Latest report", 358, 378, 126); reportButton.Enabled = false; reportButton.Click += delegate { if (latest != null) Program.Open(Path.Combine(latest, "Report.html")); };
            stopButton = ButtonAt("Stop & save", 496, 378, 126); stopButton.Enabled = false; stopButton.Click += delegate { lock (gate) { if (current != null) current.Stop(); } };
            Controls.Add(new Label { Text = "Closing this window keeps recording in the tray next to the clock.\nAll recordings stay on this PC.", ForeColor = Color.FromArgb(168, 189, 201), Location = new Point(29, 433), Size = new Size(475, 44), Font = new Font("Segoe UI", 9F) });
            var exitButton = ButtonAt("Exit", 528, 433, 94); exitButton.Click += delegate { BeginExit(); };
            var menu = new ContextMenuStrip(); menu.Items.Add("Show recorder", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add("Open recordings", null, delegate { Directory.CreateDirectory(Files.Sessions); Program.Open(Files.Sessions); });
            menu.Items.Add("Stop, save and exit", null, delegate { BeginExit(); });
            tray = new NotifyIcon { Text = "VR Recorder — waiting for GTA", Icon = Icon, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { Show(); Activate(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (preview) exiting = true; if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } else if (!exiting) { exiting = true; exit.Set(); lock (gate) { if (current != null) current.Stop(); } if (watcher.IsAlive) watcher.Join(6000); } };
            FormClosed += delegate { tray.Visible = false; tray.Dispose(); exit.Dispose(); };
            watcher = new Thread(Watch); watcher.IsBackground = true; watcher.Name = "GTA session watcher";
            Shown += delegate { if (!preview && !watcher.IsAlive) watcher.Start(); };
        }
        Button ButtonAt(string text, int x, int y, int width) { var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(width, 38), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(25, 36, 47), ForeColor = ForeColor, Cursor = Cursors.Hand }; b.FlatAppearance.BorderColor = Color.FromArgb(65, 88, 100); Controls.Add(b); return b; }
        void UpdateUi(Action update) { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(update); } catch (InvalidOperationException) { } }
        void Watch() {
            UpdateUi(delegate { state.Text = "Checking VR recording access…"; details.Text = "A short check runs once for this version.\nGTA recording will start automatically when the check finishes."; });
            string verificationError;
            try { verificationError = CaptureValidation.RunIfNeeded(); } catch (Exception e) { verificationError = e.Message; }
            UpdateUi(delegate { state.Text = verificationError == null ? "Ready — waiting for GTA V" : "Hardware ready; VR timing check failed"; details.Text = verificationError == null ? "Start GTA as usual. Recording begins when the game opens.\nA separate report is saved when you close the game." : verificationError; state.ForeColor = verificationError == null ? Color.FromArgb(110, 231, 192) : Color.FromArgb(255, 204, 128); });
            while (!exit.WaitOne(0)) {
                Process target = null;
                try {
                    bool skippedProcessStillRunning = false;
                    foreach (string name in new[] { "GTA5_Enhanced", "GTA5" }) {
                        foreach (var p in Process.GetProcessesByName(name)) { if (p.Id == skipPid) skippedProcessStillRunning = true; if (target == null && p.Id != skipPid) target = p; else p.Dispose(); }
                        if (target != null) break;
                    }
                    if (target == null && !skippedProcessStillRunning) skipPid = -1;
                    if (target != null) {
                        using (target) {
                            var recording = new Recording(target, message => UpdateUi(delegate { bool unavailable = message.StartsWith("VR timing unavailable") || message.StartsWith("VR timing interrupted"); state.Text = unavailable ? "Hardware recording — VR timing unavailable" : message.Replace("Hardware recording; no VDXR frames received yet", "Hardware only; waiting for VR frames"); if (unavailable) details.Text = message; string refreshStatus = current != null ? current.AutoRefreshStatus : ""; if (!unavailable && refreshStatus.Length > 0) details.Text = "Recording automatically. You can leave this window hidden.\n" + refreshStatus; state.ForeColor = message.StartsWith("VR timing +") ? Color.FromArgb(110, 231, 192) : Color.FromArgb(255, 204, 128); }), autoRefreshEnabled: () => autoRefreshOn);
                            lock (gate) { current = recording; }
                            UpdateUi(delegate { stopButton.Enabled = true; state.Text = "Starting session capture…"; details.Text = "Recording automatically. You can leave this window hidden.\nA report will be saved when GTA closes."; tray.Text = "VR Recorder — recording GTA"; });
                            recording.Run(); latest = recording.Folder;
                            if (!target.HasExited) skipPid = target.Id;
                            lock (gate) { current = null; }
                            UpdateUi(delegate { stopButton.Enabled = false; reportButton.Enabled = File.Exists(Path.Combine(latest, "Report.html")); state.Text = recording.Failure == null ? "Saved — ready for your next session" : "Saved with a recording problem"; details.Text = recording.Failure == null ? "Your report and logs are in Recordings.\nRestart GTA to capture another session." : recording.Failure; tray.Text = "VR Recorder — session saved"; });
                        }
                    }
                } catch (Exception e) { UpdateUi(delegate { state.Text = "Recording needs attention"; details.Text = e.Message; }); }
                exit.WaitOne(2000);
            }
            UpdateUi(delegate { if (exiting) Close(); });
        }
        void BeginExit() { exiting = true; exit.Set(); state.Text = "Saving and closing…"; lock (gate) { if (current != null) current.Stop(); } if (!watcher.IsAlive) Close(); }
    }
}
