using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VrRecorder {
    // Detects the gradual Virtual Desktop stream slowdown from the recorder's own per-second data.
    // Signature (seen in the Oct 28, Oct 29 and Nov 2 recordings): the game's VR frame rate falls well
    // below the headset refresh rate while the GPU becomes LESS busy, i.e. the PC is being held back
    // rather than being overloaded. Pure logic so it can be unit-tested and replayed on saved recordings.
    public sealed class StreamHealth {
        public const double WindowSeconds = 90;     // rolling window for medians
        public const int MinValidSeconds = 60;      // need at least this many usable seconds in the window
        public const double ArmRatio = 0.90;        // slowdown: median FPS below 90% of refresh (108 at 120 Hz)
        public const double GpuBusyLimit = 90;      // ...while median GPU load is below 90% (not GPU-bound)
        public const double ClearRatio = 0.97;      // healthy again: median FPS at or above 97% of refresh
        public const double WarmupSeconds = 120;    // ignore the first two minutes after (re)start

        sealed class Sample { public DateTime Time; public double Value; }
        readonly List<Sample> fps = new List<Sample>();
        readonly List<Sample> refresh = new List<Sample>();
        readonly List<Sample> gpu = new List<Sample>();
        DateTime? firstFrame;
        public bool Armed { get; private set; }
        public DateTime? ArmedAt { get; private set; }
        public double? LastMedianFps { get; private set; }
        public double? LastMedianGpu { get; private set; }
        public double LastRefresh { get; private set; }
        public event Action<string, double, double> Changed; // "armed"/"cleared", median fps, median GPU

        public StreamHealth() { LastRefresh = 120; }

        public void AddGpu(DateTime utc, double? load) {
            if (!load.HasValue || double.IsNaN(load.Value)) return;
            gpu.Add(new Sample { Time = utc, Value = load.Value });
            Prune(gpu, utc);
        }

        // One call per one-second timing bucket.
        public void AddFrame(DateTime utc, double? frameRate, double? refreshHz, double? mounted, double? visible) {
            if (!firstFrame.HasValue) firstFrame = utc;
            if (refreshHz.HasValue && refreshHz.Value > 30) refresh.Add(new Sample { Time = utc, Value = refreshHz.Value });
            bool usable = frameRate.HasValue && frameRate.Value > 0 && mounted != 0 && visible != 0;
            if (usable) fps.Add(new Sample { Time = utc, Value = frameRate.Value });
            Prune(fps, utc); Prune(refresh, utc); Prune(gpu, utc);
            if ((utc - firstFrame.Value).TotalSeconds < WarmupSeconds) return;
            if (fps.Count < MinValidSeconds) return;
            double r = refresh.Count > 0 ? Median(refresh.Select(s => s.Value)) : 120;
            LastRefresh = r;
            double f = Median(fps.Select(s => s.Value));
            LastMedianFps = f;
            double? g = gpu.Count >= MinValidSeconds / 2 ? (double?)Median(gpu.Select(s => s.Value)) : null;
            LastMedianGpu = g;
            if (!Armed && f < ArmRatio * r && g.HasValue && g.Value < GpuBusyLimit) {
                Armed = true; ArmedAt = utc;
                if (Changed != null) Changed("armed", f, g.Value);
            } else if (Armed && f >= ClearRatio * r) {
                Armed = false; ArmedAt = null;
                if (Changed != null) Changed("cleared", f, g ?? -1);
            }
        }

        // Median FPS of usable seconds after a given time (used to check whether a refresh worked).
        public double? MedianFpsSince(DateTime utc, int minimumSeconds) {
            var values = fps.Where(s => s.Time > utc).Select(s => s.Value).ToList();
            return values.Count >= minimumSeconds ? (double?)Median(values) : null;
        }

        // After a successful refresh, forget the old slow history and start a fresh warm-up.
        public void Reset(DateTime utc) {
            fps.Clear(); Armed = false; ArmedAt = null; firstFrame = utc; LastMedianFps = null;
        }

        static void Prune(List<Sample> list, DateTime now) {
            int remove = 0;
            while (remove < list.Count && (now - list[remove].Time).TotalSeconds >= WindowSeconds) remove++;
            if (remove > 0) list.RemoveRange(0, remove);
        }

        public static double Median(IEnumerable<double> values) {
            var sorted = values.OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return double.NaN;
            int mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }

        // True when GPU load after a map/pause press dropped clearly below its level just before the press.
        // Real pauses (Oct 29 x2, Nov 2) dropped GPU load to ~0.6x within 2 s; ordinary gameplay matched this 2-5% of the time.
        public static bool LooksPaused(IList<double> before, IList<double> after) {
            if (before == null || after == null || before.Count < 4 || after.Count < 1) return false;
            double pre = Median(before); double post = after.Min();
            return post <= pre * 0.75 && pre - post >= 10;
        }
    }

    // Watches for the map/pause press while a slowdown is detected, then presses Shift+Win+D twice
    // (Virtual Desktop's "toggle VR / desktop" shortcut) to restart the stream - the same manual fix.
    public sealed class AutoRefresh : IDisposable {
        const double PressDecisionSeconds = 2.6;     // wait for GPU samples after the press
        const double MinGapBetweenPresses = 4;       // a press soon after another is menu navigation/closing
        const double CooldownSeconds = 300;          // at most one refresh per five minutes
        const int ToggleGapMs = 3000;                // wait between leaving and re-entering VR
        const double VerifyAfterSeconds = 40;        // judge the result this long after the refresh
        const int MaxFailures = 2;                   // stop trying for this session after two refreshes that did not help

        readonly int gamePid;
        readonly string logPath;
        readonly Func<bool> enabled;
        readonly object gate = new object();
        readonly object logGate = new object();
        readonly StreamHealth health = new StreamHealth();
        readonly List<KeyValuePair<DateTime, double>> gpuHistory = new List<KeyValuePair<DateTime, double>>();
        readonly List<KeyValuePair<DateTime, double>> encoderHistory = new List<KeyValuePair<DateTime, double>>();
        readonly Thread worker;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        string status = "";
        int presses, refreshes, successes, failures, armedEvents;
        bool disabledForSession;
        DateTime? lastRefresh, pendingPress, verifyAt, lastPress;
        string pendingSource;
        double? fpsBeforeRefresh;
        readonly bool[] keyWasDown = new bool[256];
        readonly bool[] startWasDown = new bool[4];
        readonly DateTime[] padRetry = new DateTime[4];

        public AutoRefresh(int gamePid, string folder, Func<bool> enabled) {
            this.gamePid = gamePid; this.enabled = enabled;
            logPath = Path.Combine(folder, "auto-refresh.jsonl");
            health.Changed += delegate(string what, double f, double g) {
                if (what == "armed") armedEvents++;
                Log(what, new Dictionary<string, object> { { "median_fps", Math.Round(f, 1) }, { "median_gpu_load_pct", Math.Round(g, 1) }, { "refresh_hz", health.LastRefresh } });
            };
            Log("started", new Dictionary<string, object> { { "enabled", SafeEnabled() }, { "rule", "arm when 90-s median FPS < 90% of refresh while median GPU load < 90%; refresh at next map/pause press (Esc, P or controller Start) that drops GPU load" } });
            UpdateStatus();
            worker = new Thread(Loop); worker.IsBackground = true; worker.Name = "VD auto-refresh"; worker.Start();
        }

        public string Status { get { lock (gate) return status; } }

        public void AddFrame(DateTime utc, double? fps, double? refresh, double? mounted, double? visible) {
            lock (gate) { health.AddFrame(utc, fps, refresh, mounted, visible); UpdateStatus(); }
        }

        public void AddHardware(DateTime utc, IDictionary<string, double?> values) {
            lock (gate) {
                double? load; values.TryGetValue("gpu_load_pct", out load);
                health.AddGpu(utc, load);
                if (load.HasValue) gpuHistory.Add(new KeyValuePair<DateTime, double>(utc, load.Value));
                double? enc; values.TryGetValue("gpu_encoder_fps", out enc);
                if (enc.HasValue) encoderHistory.Add(new KeyValuePair<DateTime, double>(utc, enc.Value));
                gpuHistory.RemoveAll(p => (utc - p.Key).TotalSeconds > 60);
                encoderHistory.RemoveAll(p => (utc - p.Key).TotalSeconds > 60);
            }
        }

        public Dictionary<string, object> Snapshot() {
            lock (gate) {
                return new Dictionary<string, object> {
                    { "enabled", SafeEnabled() }, { "status", status }, { "slowdowns_detected", armedEvents },
                    { "map_or_pause_presses_seen", presses }, { "refreshes_sent", refreshes }, { "refreshes_that_helped", successes },
                    { "refreshes_that_did_not_help", failures }, { "stopped_for_session", disabledForSession },
                    { "log", "auto-refresh.jsonl" }
                };
            }
        }

        void Loop() {
            try { ReadInputs(DateTime.UtcNow, false); } catch { }
            while (!stop.WaitOne(33)) {
                try { Tick(); } catch (Exception e) { Log("error", new Dictionary<string, object> { { "message", e.Message } }); stop.WaitOne(2000); }
            }
        }

        void Tick() {
            DateTime now = DateTime.UtcNow;
            string source = ReadInputs(now, true);
            if (source != null) OnPress(now, source);

            DateTime? decideAt = null; bool verifyDue = false;
            lock (gate) {
                if (pendingPress.HasValue) decideAt = pendingPress.Value.AddSeconds(PressDecisionSeconds);
                verifyDue = verifyAt.HasValue && now >= verifyAt.Value;
            }
            if (decideAt.HasValue && now >= decideAt.Value) Decide(now);
            if (verifyDue) Verify(now);
        }

        void OnPress(DateTime now, string source) {
            lock (gate) {
                presses++;
                double sinceLast = lastPress.HasValue ? (now - lastPress.Value).TotalSeconds : double.MaxValue;
                lastPress = now;
                if (pendingPress.HasValue) {
                    Log("cancelled", new Dictionary<string, object> { { "reason", "another map/pause press within " + PressDecisionSeconds + " s (menu probably closed)" }, { "input", source } });
                    pendingPress = null; return;
                }
                if (!health.Armed || disabledForSession || verifyAt.HasValue || !SafeEnabled()) return;
                if (lastRefresh.HasValue && (now - lastRefresh.Value).TotalSeconds < CooldownSeconds) return;
                if (sinceLast < MinGapBetweenPresses) return;
                pendingPress = now; pendingSource = source;
                Log("press", new Dictionary<string, object> { { "input", source }, { "median_fps", Round(health.LastMedianFps) } });
            }
        }

        void Decide(DateTime now) {
            DateTime press; string source; List<double> before, after;
            lock (gate) {
                if (!pendingPress.HasValue) return;
                press = pendingPress.Value; source = pendingSource; pendingPress = null;
                before = gpuHistory.Where(p => p.Key >= press.AddSeconds(-12) && p.Key <= press.AddSeconds(-0.5)).Select(p => p.Value).ToList();
                after = gpuHistory.Where(p => p.Key > press.AddSeconds(0.4)).Select(p => p.Value).ToList();
            }
            bool paused = StreamHealth.LooksPaused(before, after);
            bool foreground = GameIsForeground();
            var details = new Dictionary<string, object> { { "input", source }, { "gpu_before_median", before.Count > 0 ? (object)Math.Round(StreamHealth.Median(before), 1) : null }, { "gpu_after_min", after.Count > 0 ? (object)after.Min() : null }, { "game_in_foreground", foreground } };
            if (!paused || !foreground) {
                details["reason"] = !foreground ? "GTA is not the active window" : "no pause signature (GPU load did not drop) - probably closing the menu";
                Log("skipped", details); return;
            }
            Refresh(now, "map/pause via " + source, details);
        }

        void Refresh(DateTime now, string why, Dictionary<string, object> details) {
            lock (gate) { fpsBeforeRefresh = health.LastMedianFps; refreshes++; lastRefresh = now; }
            details["why"] = why; details["median_fps_before"] = Round(fpsBeforeRefresh);
            Log("refreshing", details);
            string result = VdToggle.Run(ToggleGapMs);
            Log("toggle_result", new Dictionary<string, object> { { "result", result } });
            ReadInputs(DateTime.UtcNow, false); // re-baseline key states; ignore anything pressed meanwhile
            lock (gate) {
                verifyAt = DateTime.UtcNow.AddSeconds(VerifyAfterSeconds);
                status = "Auto-refresh: refreshed the VD stream at " + DateTime.Now.ToString("HH:mm") + " - checking the result";
            }
        }

        void Verify(DateTime now) {
            lock (gate) {
                DateTime sent = lastRefresh ?? now;
                verifyAt = null;
                double? after = health.MedianFpsSince(sent.AddSeconds(8), 10);
                bool restartSeen = encoderHistory.Any(p => p.Key >= sent.AddSeconds(-1) && p.Key <= sent.AddSeconds(12) && p.Value < 30);
                bool helped = after.HasValue && after.Value >= 0.95 * health.LastRefresh;
                var d = new Dictionary<string, object> { { "median_fps_before", Round(fpsBeforeRefresh) }, { "median_fps_after", Round(after) }, { "encoder_restart_seen", restartSeen } };
                if (!after.HasValue) {
                    Log("inconclusive", d);
                    status = "Auto-refresh: refreshed at " + DateTime.Now.ToString("HH:mm") + " (no VR frames afterwards to check it)";
                } else if (helped) {
                    successes++; failures = 0; health.Reset(now);
                    Log("helped", d);
                    status = "Auto-refresh: refreshed at " + DateTime.Now.ToString("HH:mm") + " (" + Round(fpsBeforeRefresh) + " -> " + Round(after) + " fps)";
                } else {
                    failures++;
                    Log("did_not_help", d);
                    if (failures >= MaxFailures) {
                        disabledForSession = true;
                        Log("stopped", new Dictionary<string, object> { { "reason", "two refreshes in a row did not restore the frame rate" } });
                        status = "Auto-refresh stopped for this session - it did not help twice. Use Shift+Win+D yourself.";
                    } else status = "Auto-refresh: last refresh did not help - will retry at a later map press";
                }
            }
        }

        // Returns the input that looks like "open map/pause": Esc or P on the keyboard, Start on a controller.
        string ReadInputs(DateTime now, bool report) {
            string found = null;
            foreach (int vk in new[] { 0x1B, 0x50 }) {
                bool down = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
                if (down && !keyWasDown[vk] && report && found == null) found = vk == 0x1B ? "Esc" : "P";
                keyWasDown[vk] = down;
            }
            for (uint i = 0; i < 4; i++) {
                if (now < padRetry[i]) continue;
                Native.XInputState state = new Native.XInputState();
                int result = -1;
                try { result = Native.XInputGetState(i, out state); } catch (Exception) { padRetry[i] = now.AddMinutes(10); continue; }
                if (result != 0) { padRetry[i] = now.AddSeconds(2); startWasDown[i] = false; continue; }
                bool start = (state.Buttons & 0x0010) != 0;
                if (start && !startWasDown[i] && report && found == null) found = "controller Start";
                startWasDown[i] = start;
            }
            if (found != null && !GameIsForeground()) return null;
            return found;
        }

        bool GameIsForeground() {
            try {
                IntPtr window = Native.GetForegroundWindow();
                if (window == IntPtr.Zero) return false;
                uint pid; Native.GetWindowThreadProcessId(window, out pid);
                return pid == (uint)gamePid;
            } catch { return false; }
        }

        // Shift + Windows + D: Virtual Desktop's default "toggle VR / desktop" shortcut.
        public static void SendToggle() {
            const ushort Shift = 0xA0, Win = 0x5B, D = 0x44;
            Key(Shift, false); Thread.Sleep(30);
            Key(Win, false); Thread.Sleep(30);
            Key(D, false); Thread.Sleep(40);
            Key(D, true); Thread.Sleep(30);
            Key(Win, true); Thread.Sleep(30);
            Key(Shift, true);
        }

        internal static void Key(ushort vk, bool up) {
            var input = new Native.Input { Type = 1 };
            input.Data.Keyboard.VirtualKey = vk;
            input.Data.Keyboard.ScanCode = (ushort)Native.MapVirtualKey(vk, 0);
            input.Data.Keyboard.Flags = (up ? 2u : 0u) | (vk == 0x5B ? 1u : 0u);
            if (Native.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Native.Input))) != 1)
                throw new InvalidOperationException("Windows refused the simulated key press (error " + Marshal.GetLastWin32Error() + ").");
        }

        bool SafeEnabled() { try { return enabled == null || enabled(); } catch { return false; } }

        void UpdateStatus() {
            if (verifyAt.HasValue || disabledForSession) return;
            if (!SafeEnabled()) { status = "Auto-refresh is off"; return; }
            if (lastRefresh.HasValue && (DateTime.UtcNow - lastRefresh.Value).TotalSeconds < CooldownSeconds && status.StartsWith("Auto-refresh: refreshed")) return;
            status = health.Armed
                ? "Auto-refresh: slowdown detected (" + Round(health.LastMedianFps) + " fps) - will refresh when you open the map"
                : "Auto-refresh: VD stream looks healthy";
        }

        static object Round(double? value) { return value.HasValue && !double.IsNaN(value.Value) ? (object)Math.Round(value.Value, 1) : null; }

        void Log(string evt, Dictionary<string, object> data) {
            try {
                var row = new Dictionary<string, object> { { "timestamp_utc", DateTime.UtcNow.ToString("o") }, { "local_time", DateTime.Now.ToString("HH:mm:ss") }, { "event", evt } };
                foreach (var pair in data) row[pair.Key] = pair.Value;
                lock (logGate) File.AppendAllText(logPath, Files.Serialize(row) + Environment.NewLine, new UTF8Encoding(false));
            } catch { }
        }

        public void Dispose() {
            stop.Set();
            if (worker.IsAlive) worker.Join(5000);
            Log("finished", Snapshot());
        }
    }

    // Virtual Desktop only reacts to Shift+Win+D while the VD Streamer window has keyboard focus
    // (confirmed in testing: presses sent while GTA had focus were ignored).
    // So: remember the active window (GTA), bring the Streamer window forward, press the shortcut,
    // wait, press it again, then give focus back to the original window and re-minimize the Streamer.
    public static class VdToggle {
        public const string StreamerProcess = "VirtualDesktop.Streamer";

        public static IntPtr FindStreamerWindow() {
            var pids = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName(StreamerProcess)) { pids.Add((uint)p.Id); p.Dispose(); }
            if (pids.Count == 0) return IntPtr.Zero;
            IntPtr visible = IntPtr.Zero, titled = IntPtr.Zero, other = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr unused) {
                uint pid; Native.GetWindowThreadProcessId(hwnd, out pid);
                if (!pids.Contains(pid) || Native.GetWindow(hwnd, 4) != IntPtr.Zero) return true; // skip other processes and owned windows
                int length = Native.GetWindowTextLength(hwnd);
                if (length == 0) return true;
                var title = new StringBuilder(length + 1); Native.GetWindowText(hwnd, title, title.Capacity);
                if (title.ToString().IndexOf("Virtual Desktop", StringComparison.OrdinalIgnoreCase) < 0) { if (other == IntPtr.Zero && Native.IsWindowVisible(hwnd)) other = hwnd; return true; }
                if (Native.IsWindowVisible(hwnd) && visible == IntPtr.Zero) visible = hwnd;
                if (titled == IntPtr.Zero) titled = hwnd;
                return true;
            }, IntPtr.Zero);
            return visible != IntPtr.Zero ? visible : titled != IntPtr.Zero ? titled : other;
        }

        public static string Run(int gapMs) {
            IntPtr returnTo = Native.GetForegroundWindow();
            IntPtr streamer = FindStreamerWindow();
            if (streamer == IntPtr.Zero) return "VD Streamer window not found - nothing sent";
            bool wasMinimized = Native.IsIconic(streamer), wasHidden = !Native.IsWindowVisible(streamer);
            var steps = new List<string>();
            steps.Add(Focus(streamer) ? "streamer focused" : "COULD NOT focus streamer");
            Thread.Sleep(250);
            AutoRefresh.SendToggle(); steps.Add("press 1 sent");
            Thread.Sleep(gapMs);
            if (Native.GetForegroundWindow() != streamer) steps.Add(Focus(streamer) ? "streamer re-focused" : "COULD NOT re-focus streamer");
            Thread.Sleep(150);
            AutoRefresh.SendToggle(); steps.Add("press 2 sent");
            Thread.Sleep(400);
            if (returnTo != IntPtr.Zero && returnTo != streamer) steps.Add(Focus(returnTo) ? "focus returned to previous window" : "COULD NOT return focus");
            if (wasMinimized || wasHidden) { Native.ShowWindow(streamer, 6); steps.Add("streamer minimized again"); } // SW_MINIMIZE
            return string.Join("; ", steps);
        }

        // Windows restricts which process may change the foreground window; attaching to the current
        // foreground thread's input (and, as a fallback, holding Alt) are the standard ways to be allowed.
        public static bool Focus(IntPtr hwnd) {
            if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, 9);          // SW_RESTORE
            else if (!Native.IsWindowVisible(hwnd)) Native.ShowWindow(hwnd, 5); // SW_SHOW
            for (int attempt = 0; attempt < 3; attempt++) {
                IntPtr current = Native.GetForegroundWindow();
                if (current == hwnd) return true;
                uint unused; uint foregroundThread = Native.GetWindowThreadProcessId(current, out unused);
                uint me = Native.GetCurrentThreadId();
                bool attached = foregroundThread != 0 && foregroundThread != me && Native.AttachThreadInput(me, foregroundThread, true);
                try { Native.BringWindowToTop(hwnd); Native.SetForegroundWindow(hwnd); }
                finally { if (attached) Native.AttachThreadInput(me, foregroundThread, false); }
                Thread.Sleep(120);
                if (Native.GetForegroundWindow() == hwnd) return true;
                AutoRefresh.Key(0x12, false); Native.SetForegroundWindow(hwnd); AutoRefresh.Key(0x12, true); // Alt
                Thread.Sleep(120);
                if (Native.GetForegroundWindow() == hwnd) return true;
            }
            return false;
        }
    }

    static class Native {
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [StructLayout(LayoutKind.Sequential)] public struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public IntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
        [StructLayout(LayoutKind.Sequential)] public struct Input { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Sequential)] public struct XInputState { public uint PacketNumber; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short ThumbLX, ThumbLY, ThumbRX, ThumbRY; }
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] public static extern int XInputGetState(uint index, out XInputState state);
    }
}
