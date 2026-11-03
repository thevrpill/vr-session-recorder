using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VrRecorder {
    public static class Files {
        public static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        public static string Root { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static string Sessions { get { return Path.Combine(Root, "Recordings"); } }
        public static string Number(double? value) { return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) ? value.Value.ToString("0.###", Invariant) : ""; }
        public static void Json(string path, object value) { File.WriteAllText(path, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(value), new UTF8Encoding(false)); }
        public static string Serialize(object value) { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(value); }
    }
    public sealed class Recording {
        public static readonly string[] TimingFields = { "vr_app_fps", "vr_frames", "frame_interval_p95_ms", "frame_interval_max_ms", "app_cpu_ms", "app_render_cpu_ms", "app_gpu_ms", "headset_refresh_hz", "reprojection_active", "hmd_mounted", "hmd_visible", "discarded_frames" };
        readonly Process game;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly Action<string> status;
        readonly Guid? provider;
        readonly Func<bool> autoRefreshEnabled;
        AutoRefresh autoRefresh;
        public string AutoRefreshStatus { get { var a = autoRefresh; return a == null ? "" : a.Status; } }
        public string Folder;
        public string TimingStatus;
        public string Failure;
        public bool Finished;
        public long FrameCount;
        public readonly DateTime StartedUtc = DateTime.UtcNow;
        readonly Dictionary<string, object> metadata = new Dictionary<string, object>();
        readonly List<string> notes = new List<string>();
        readonly HashSet<string> copyErrors = new HashSet<string>();
        string logPath;
        DateTime processStartUtc;
        public Recording(Process process, Action<string> onStatus, string outputRoot = null, Guid? providerOverride = null, Func<bool> autoRefreshEnabled = null) {
            game = process; status = onStatus; provider = providerOverride;
            this.autoRefreshEnabled = autoRefreshEnabled;
            string name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + game.Id + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Folder = Path.Combine(outputRoot ?? Files.Sessions, name);
            Directory.CreateDirectory(Folder);
            try { processStartUtc = game.StartTime.ToUniversalTime(); } catch { processStartUtc = StartedUtc; }
            try { logPath = Path.Combine(Path.GetDirectoryName(game.MainModule.FileName), "RealVR64.log"); } catch { logPath = @"C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V Enhanced\RealVR64.log"; }
        }
        public void Stop() { stop.Set(); }
        public void Run() {
            RuntimeTrace trace = null;
            var frames = new FrameAggregator();
            StreamWriter sensorsFile = null, timingFile = null;
            Stopwatch elapsed = Stopwatch.StartNew();
            try {
                using (var sensors = new Sensors()) {
                    sensorsFile = new StreamWriter(Path.Combine(Folder, "hardware.csv"), false, new UTF8Encoding(false)); sensorsFile.AutoFlush = true;
                    timingFile = new StreamWriter(Path.Combine(Folder, "vr-timing.csv"), false, new UTF8Encoding(false)); timingFile.AutoFlush = true;
                    sensorsFile.WriteLine("timestamp_utc," + string.Join(",", Sensors.Fields));
                    timingFile.WriteLine("timestamp_utc," + string.Join(",", TimingFields));
                    metadata["format_version"] = 2;
                    metadata["started_utc"] = StartedUtc.ToString("o"); metadata["started_local"] = StartedUtc.ToLocalTime().ToString("o");
                    metadata["process_name"] = game.ProcessName; metadata["process_id"] = game.Id;
                    metadata["process_started_utc"] = processStartUtc.ToString("o");
                    metadata["gpu"] = sensors.GpuName; metadata["nvidia_driver"] = sensors.Driver;
                    metadata["gpu_status"] = sensors.GpuStatus;
                    metadata["sources"] = "VDXR built-in ETW events; NVIDIA NVML; Windows process and memory counters";
                    metadata["limits"] = "VR application submissions are not headset display FPS. VDXR GPU timers can cover only part of a modded game's render path. NVIDIA encoder latency is not Virtual Desktop's complete encoding latency. Network throughput includes other PC traffic. Missing readings are blank, not zero.";
                    if (autoRefreshEnabled != null) { try { autoRefresh = new AutoRefresh(game.Id, Folder, autoRefreshEnabled); } catch (Exception e) { notes.Add("Auto-refresh unavailable: " + e.Message); } }
                    try { trace = new RuntimeTrace(game.Id, frames.Receive, provider); TimingStatus = "Waiting for VR frame data"; }
                    catch (Exception e) { TimingStatus = "VR timing unavailable: " + e.Message; notes.Add(TimingStatus); }
                    metadata["timing_status"] = TimingStatus; SaveMetadata();
                    int samples = 0;
                    while (!stop.WaitOne(0)) {
                        if (game.HasExited) break;
                        long iteration = elapsed.ElapsedMilliseconds;
                        var timestamp = DateTime.UtcNow;
                        var values = sensors.Read(game);
                        if (autoRefresh != null) autoRefresh.AddHardware(timestamp, values);
                        sensorsFile.WriteLine(timestamp.ToString("o") + "," + string.Join(",", Sensors.Fields.Select(f => Files.Number(values[f]))));
                        WriteFrames(timingFile, frames.Drain(DateTime.UtcNow.AddSeconds(-3)));
                        if (trace != null) {
                            if (!string.IsNullOrEmpty(trace.Error)) TimingStatus = "VR timing interrupted: " + trace.Error;
                            else if (FrameCount > 0) TimingStatus = "VR timing + hardware recording";
                            else if (elapsed.Elapsed.TotalSeconds > 30) TimingStatus = "Hardware recording; no VDXR frames received yet";
                        }
                        if (samples % 15 == 0) { CopyLogs(); metadata["timing_status"] = TimingStatus; metadata["last_sample_utc"] = timestamp.ToString("o"); if (autoRefresh != null) metadata["auto_refresh"] = autoRefresh.Snapshot(); SaveMetadata(); }
                        status(TimingStatus + "  |  " + elapsed.Elapsed.ToString(@"hh\:mm\:ss"));
                        samples++;
                        stop.WaitOne((int)Math.Max(1, 1000 - (elapsed.ElapsedMilliseconds - iteration)));
                    }
                    if (trace != null) {
                        metadata["etw_events_lost"] = trace.EventsLost;
                        trace.Dispose();
                        metadata["timing_events_received"] = trace.Delivered;
                        metadata["timing_decode_errors"] = trace.DecodeErrors;
                        metadata["event_name_filter"] = trace.NameFilterEnabled;
                        if (!string.IsNullOrEmpty(trace.Error)) notes.Add(trace.Error);
                        trace = null;
                    }
                    WriteFrames(timingFile, frames.Drain(DateTime.MaxValue));
                    metadata["late_timing_events"] = frames.LateEvents;
                    metadata["gpu_status"] = sensors.GpuStatus;
                }
            } catch (Exception e) { Failure = e.ToString(); notes.Add("Recording interrupted: " + e.Message); }
            finally {
                if (trace != null) { try { trace.Dispose(); } catch (Exception e) { notes.Add(e.Message); } }
                if (autoRefresh != null) { try { autoRefresh.Dispose(); } catch (Exception e) { notes.Add(e.Message); } metadata["auto_refresh"] = autoRefresh.Snapshot(); }
                if (sensorsFile != null) sensorsFile.Dispose(); if (timingFile != null) timingFile.Dispose();
                CopyLogs();
                metadata["finished_utc"] = DateTime.UtcNow.ToString("o"); metadata["duration_seconds"] = elapsed.Elapsed.TotalSeconds;
                metadata["vr_frames"] = FrameCount;
                metadata["timing_status"] = FrameCount > 0 ? "VR timing captured" : "No VR application frame data captured";
                metadata["notes"] = notes.Concat(copyErrors).ToArray();
                SaveMetadata();
                try { Report.Create(Folder); } catch (Exception e) { File.WriteAllText(Path.Combine(Folder, "report-error.txt"), e.ToString()); Failure = "Recording saved, but report generation failed: " + e.Message; }
                Finished = true;
            }
        }
        void SaveMetadata() { Files.Json(Path.Combine(Folder, "session.json"), metadata); }
        void WriteFrames(StreamWriter writer, IEnumerable<FrameBucket> buckets) {
            foreach (var b in buckets) {
                FrameCount += b.Frames;
                if (autoRefresh != null) autoRefresh.AddFrame(b.Time, b.Fps, b.Refresh, b.Mounted, b.Visible);
                var values = new double?[] { b.Fps, b.Frames, FrameBucket.Percentile(b.Intervals, .95), b.Intervals.Count > 0 ? (double?)b.Intervals.Max() : null, FrameBucket.Average(b.Cpu), FrameBucket.Average(b.RenderCpu), FrameBucket.Average(b.Gpu), b.Refresh, b.Reprojection, b.Mounted, b.Visible, b.Discarded };
                writer.WriteLine(b.Time.ToString("o") + "," + string.Join(",", values.Select(Files.Number)));
            }
        }
        void CopyLogs() {
            CopyLog(logPath, "RealVR64.log", true);
            CopyLog(@"C:\ProgramData\Virtual Desktop\OpenXR.log", "VirtualDesktop-OpenXR.log", true);
            // The streamer log is diagnostic context only; it may predate this session.
            CopyLog(@"C:\ProgramData\Virtual Desktop\StreamerLog.txt", "VirtualDesktop-StreamerLog.txt", false);
        }
        void CopyLog(string source, string destination, bool requireCurrentSession) {
            try {
                if (!File.Exists(source)) return;
                var info = new FileInfo(source);
                if (info.Length > 32L * 1024 * 1024) { copyErrors.Add(destination + " exceeded the 32 MB copy limit."); return; }
                if (requireCurrentSession && info.LastWriteTimeUtc < processStartUtc.AddSeconds(-5)) return;
                string path = Path.Combine(Folder, destination);
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write)) input.CopyTo(output);
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            } catch (Exception e) { copyErrors.Add("Could not preserve " + destination + ": " + e.Message); }
        }
    }
    public sealed class ReportRow {
        public DateTime Time;
        public Dictionary<string, double?> Values = new Dictionary<string, double?>();
    }
    public static class Report {
        public static List<ReportRow> Read(string path) {
            var result = new List<ReportRow>();
            if (!File.Exists(path)) return result;
            using (var reader = new StreamReader(path)) {
                string first = reader.ReadLine(); if (first == null) return result;
                string[] headers = first.Split(','); string line;
                while ((line = reader.ReadLine()) != null) {
                    string[] parts = line.Split(','); DateTime time;
                    if (parts.Length != headers.Length || !DateTime.TryParse(parts[0], Files.Invariant, DateTimeStyles.RoundtripKind, out time)) continue;
                    var row = new ReportRow { Time = time.ToUniversalTime() };
                    for (int i = 1; i < headers.Length; i++) { double value; row.Values[headers[i]] = double.TryParse(parts[i], NumberStyles.Float, Files.Invariant, out value) && !double.IsNaN(value) && !double.IsInfinity(value) ? (double?)value : null; }
                    result.Add(row);
                }
            }
            return result;
        }
        public static List<Dictionary<string, object>> FindSlowdowns(List<ReportRow> frames) {
            var result = new List<Dictionary<string, object>>();
            var eligible = frames.Where(r => Value(r, "vr_app_fps").HasValue && Value(r, "vr_app_fps") >= 0 && Value(r, "hmd_mounted") != 0 && Value(r, "hmd_visible") != 0).OrderBy(r => r.Time).ToList();
            if (eligible.Count < 120) return result;
            DateTime first = eligible[0].Time;
            var baseline = eligible.Where(r => (r.Time - first).TotalSeconds >= 120 && (r.Time - first).TotalSeconds < 420 && Value(r, "vr_app_fps") > 5).Select(r => Value(r, "vr_app_fps").Value).ToList();
            if (baseline.Count < 60) return result;
            baseline.Sort(); double reference = baseline[baseline.Count / 2];
            var run = new List<ReportRow>();
            Action flush = delegate {
                if (run.Count >= 25 && (run.Last().Time - run[0].Time).TotalSeconds >= 29) result.Add(new Dictionary<string, object> { { "start_utc", run[0].Time.ToString("o") }, { "end_utc", run.Last().Time.ToString("o") }, { "baseline_fps", Math.Round(reference, 1) }, { "average_fps", Math.Round(run.Average(r => Value(r, "vr_app_fps").Value), 1) } });
                run.Clear();
            };
            foreach (var r in eligible.Where(r => (r.Time - first).TotalSeconds >= 420)) {
                if (run.Count > 0 && (r.Time - run.Last().Time).TotalSeconds > 3) flush();
                if (Value(r, "vr_app_fps") < reference * .65) run.Add(r); else flush();
            }
            flush(); return result;
        }
        public static double? Value(ReportRow row, string key) { double? value; return row.Values.TryGetValue(key, out value) ? value : null; }
        public static void Create(string folder) {
            var hardware = Read(Path.Combine(folder, "hardware.csv")); var frames = Read(Path.Combine(folder, "vr-timing.csv"));
            var overlay = Read(Path.Combine(folder, "vd-overlay.csv"));
            var meta = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(folder, "session.json")));
            var slowdowns = FindSlowdowns(frames);
            Files.Json(Path.Combine(folder, "possible-slowdowns.json"), slowdowns);
            var data = new { metadata = meta, hardware = hardware.Select(r => new object[] { r.Time.ToString("o"), r.Values }).ToArray(), frames = frames.Select(r => new object[] { r.Time.ToString("o"), r.Values }).ToArray(), overlay = overlay.Select(r => new object[] { r.Time.ToString("o"), r.Values }).ToArray(), slowdowns = slowdowns };
            string template = File.ReadAllText(Path.Combine(Files.Root, "report-template.html"));
            string json = Files.Serialize(data).Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026");
            File.WriteAllText(Path.Combine(folder, "Report.html"), template.Replace("/*SESSION_DATA*/null", json), new UTF8Encoding(false));
        }
    }
}
