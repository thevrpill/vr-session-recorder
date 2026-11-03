using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using VrRecorder;

[EventSource(Name = "VRSessionRecorder-Validation", Guid = "0131BD63-0947-41E8-8DE9-2FBE3AEAB353")]
sealed class TestEvents : EventSource {
    public TestEvents() : base(EventSourceSettings.EtwSelfDescribingEventFormat) { }
    public void Frame(long id) {
        Write("EndFrame_Signal", new { FrameCompleted = id });
        Write("App_Statistics", new { FrameId = id, AppFrameCpuTime = 8000L });
        Write("App_Statistics", new { FrameId = id, AppRenderGpuTime = 5500L });
        Write("OVR_HmdDesc", new { DisplayRefreshRate = 90f });
        Write("OVR_SessionStatus", new { HmdMounted = true, IsVisible = true });
        Write("OVR_AswStatus", new { AsyncReprojectionActive = false });
        Write("ExcludedEvent", new { Value = 42 });
    }
}
class RecorderTests {
    static readonly List<string> output = new List<string>();
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); output.Add("PASS: " + label); Console.WriteLine(output.Last()); }
    static int Main(string[] args) {
        string resultPath = Path.Combine(Files.Root, "test-results" + (args.Contains("--trace") ? "-trace" : args.Contains("--auto-refresh") ? "-auto-refresh" : args.Contains("--ci") ? "-ci" : "") + ".txt");
        try {
            if (args.Contains("--trace")) { LiveTrace(); }
            else if (args.Contains("--auto-refresh")) { AutoRefreshChecks(); }
            else if (args.Contains("--ci")) { UnitChecks(); AutoRefreshChecks(); ReportChecks(); } // build server: no GPU, headset or admin rights
            else { UnitChecks(); AutoRefreshChecks(); HardwareChecks(); ReportChecks(); OfflineTrace(); SaveSessionChecks(); }
            output.Add("ALL REQUESTED TESTS PASSED"); return 0;
        } catch (Exception e) { output.Add(e.ToString()); Console.WriteLine(e); return 1; }
        finally { File.WriteAllLines(resultPath, output); }
    }
    static void UnitChecks() {
        var aggregator = new FrameAggregator(); var t = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
        aggregator.Receive("OVR_HmdDesc", t, new Dictionary<string, object> { { "DisplayRefreshRate", 90.0 } });
        for (int i = 0; i < 100; i++) {
            var time = t.AddMilliseconds(i * 20);
            aggregator.Receive("EndFrame_Signal", time, new Dictionary<string, object> { { "FrameCompleted", i } });
            aggregator.Receive("App_Statistics", time, new Dictionary<string, object> { { "AppFrameCpuTime", 5000L } });
            aggregator.Receive("App_Statistics", time, new Dictionary<string, object> { { "AppRenderGpuTime", 7500L } });
        }
        aggregator.Receive("EndFrame_Signal", t.AddMilliseconds(1980), new Dictionary<string, object> { { "FrameCompleted", 99 } });
        var rows = aggregator.Drain(t.AddSeconds(3));
        Check(rows.Sum(r => r.Frames) == 100, "frame accounting rejects duplicate frame IDs");
        Check(rows.All(r => r.Fps.HasValue && Math.Abs(r.Fps.Value - 50) < .01), "VR frame intervals convert to 50 FPS");
        Check(rows.All(r => Math.Abs(r.Gpu.Average() - 7.5) < .01), "runtime microseconds convert to milliseconds");
        Check(rows.All(r => !r.Reprojection.HasValue), "missing metrics remain missing");
        aggregator.Receive("EndFrame_Signal", t, new Dictionary<string, object> { { "FrameCompleted", 101 } });
        Check(aggregator.LateEvents == 1 && aggregator.Drain(DateTime.MaxValue).Count == 0, "late trace data is counted instead of corrupting old samples");
        var good = SyntheticRows(t, false);
        Check(Report.FindSlowdowns(good).Count == 0, "steady performance does not generate slowdown flags");
        var bad = SyntheticRows(t, true); var flags = Report.FindSlowdowns(bad);
        Check(flags.Count == 1 && flags[0]["start_utc"].ToString() == t.AddSeconds(500).ToString("o"), "sustained 60-to-25 FPS drop is located automatically");
        var severe = SyntheticRows(t, true); foreach (var row in severe.Where(r => (r.Time-t).TotalSeconds >= 500)) row.Values["vr_app_fps"] = 2;
        Check(Report.FindSlowdowns(severe).Count == 1, "severe slowdown below five FPS remains detectable");
        foreach (var row in bad.Where(r => (r.Time-t).TotalSeconds >= 500)) row.Values["hmd_mounted"] = 0;
        Check(Report.FindSlowdowns(bad).Count == 0, "headset removal does not generate a slowdown flag");
        var sparse = SyntheticRows(t, true).Where((r, i) => i < 500 || i % 5 == 0).ToList();
        Check(Report.FindSlowdowns(sparse).Count == 0, "gaps in timing data do not become a sustained slowdown");
    }
    static List<ReportRow> SyntheticRows(DateTime t, bool slowdown) {
        var list = new List<ReportRow>();
        for (int i=0;i<600;i++) list.Add(new ReportRow { Time=t.AddSeconds(i), Values=new Dictionary<string,double?> { {"vr_app_fps",slowdown&&i>=500?25:60}, {"hmd_mounted",1}, {"hmd_visible",1} } });
        return list;
    }
    static void HardwareChecks() {
        using(var sensors=new Sensors()) using(var p=Process.GetCurrentProcess()) {
            var first=sensors.Read(p); Thread.Sleep(1000); var second=sensors.Read(p);
            output.Add("GPU: "+sensors.GpuName+"; driver: "+sensors.Driver+"; status: "+sensors.GpuStatus);
            output.Add(Files.Serialize(second));
            Check(second["gpu_total_mb"]>15000 && second["gpu_total_mb"]<18000,"actual RTX 4080 SUPER memory readings");
            Check(second["gpu_temperature_c"]>0&&second["gpu_temperature_c"]<110,"actual GPU temperature");
            Check(second["system_ram_total_mb"]>60000,"actual system memory");
            Check(second["game_private_mb"]>0&&second["game_cpu_pct"].HasValue,"Windows process memory and CPU readings");
            Check(second["sample_work_ms"]<500,"sensor sampling completes within its one-second budget");
            Check(first["game_cpu_pct"]==null,"first CPU sample is not fabricated");
        }
    }
    static void ReportChecks() {
        string folder=Path.Combine(Files.Root,"Validation","Sample report");Directory.CreateDirectory(folder);
        var t=new DateTime(2026,10,28,9,0,0,DateTimeKind.Utc);
        using(var writer=new StreamWriter(Path.Combine(folder,"vr-timing.csv"))) {
            writer.WriteLine("timestamp_utc,"+string.Join(",",Recording.TimingFields));
            for(int i=0;i<600;i++) { var values=new double?[]{i>=500?25:60,i>=500?25:60,i>=500?45:18,i>=500?80:28,8,5,5.5,90,0,1,1,0};writer.WriteLine(t.AddSeconds(i).ToString("o")+","+string.Join(",",values.Select(Files.Number))); }
        }
        using(var writer=new StreamWriter(Path.Combine(folder,"hardware.csv"))) {
            writer.WriteLine("timestamp_utc,"+string.Join(",",Sensors.Fields));
            for(int i=0;i<600;i++) { var v=Sensors.Fields.ToDictionary(k=>k,k=>(double?)null);v["gpu_used_mb"]=9000+i*10;v["gpu_total_mb"]=16376;v["gpu_load_pct"]=i>=500?99:70;v["gpu_temperature_c"]=55+i/60.0;v["game_private_mb"]=12000+i*3;v["system_ram_used_mb"]=32000+i*3;v["recorder_cpu_pct"]=.05;writer.WriteLine(t.AddSeconds(i).ToString("o")+","+string.Join(",",Sensors.Fields.Select(k=>Files.Number(v[k])))); }
        }
        Files.Json(Path.Combine(folder,"session.json"),new {started_utc=t.ToString("o"),finished_utc=t.AddSeconds(600).ToString("o"),gpu="SYNTHETIC VALIDATION — not a real game session",vr_frames=32500,notes=new[]{"Generated test data used to validate the report. Not evidence about GTA performance."}});
        Report.Create(folder);
        string html=File.ReadAllText(Path.Combine(folder,"Report.html"));
        Check(html.Contains("SYNTHETIC VALIDATION")&&!html.Contains("/*SESSION_DATA*/null"),"standalone report embeds its local data");
        Check(Report.Read(Path.Combine(folder,"vr-timing.csv")).Count==600,"saved timing data survives a CSV round trip");
        Check(File.ReadAllText(Path.Combine(folder,"possible-slowdowns.json")).Contains("25"),"report includes detected slowdown period");
        output.Add("Sample report: "+Path.Combine(folder,"Report.html"));
    }
    static void LiveTrace() {
        Check(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),"Windows granted tracing permission");
        using(var p=Process.GetCurrentProcess()) using(var events=new TestEvents()) {
            var recording=new Recording(p,delegate(string s){},Path.Combine(Files.Root,"Validation","Trace capture"),new Guid("0131BD63-0947-41E8-8DE9-2FBE3AEAB353"));
            var thread=new Thread(recording.Run); thread.Start();
            Thread.Sleep(1500);var timer=Stopwatch.StartNew();long count=0;
            while(timer.Elapsed.TotalSeconds<12) { events.Frame(++count); Thread.Sleep(16); }
            recording.Stop();Check(thread.Join(15000),"recorder stops and saves cleanly");
            var rows=Report.Read(Path.Combine(recording.Folder,"vr-timing.csv"));
            Check(recording.Failure==null,"complete capture pipeline reports no failure");
            Check(rows.Sum(r=>Report.Value(r,"vr_frames")??0)>300,"actual Windows trace events reach the recording");
            var gpu=rows.Select(r=>Report.Value(r,"app_gpu_ms")).Where(v=>v.HasValue).Select(v=>v.Value).ToList();
            Check(gpu.Count>0&&gpu.All(v=>Math.Abs(v-5.5)<.01),"TraceLogging payloads decoded with correct GPU units");
            Check(rows.Any(r=>Report.Value(r,"headset_refresh_hz")==90),"runtime state readings captured");
            Check(File.Exists(Path.Combine(recording.Folder,"Report.html")),"live capture creates its report automatically");
            output.Add("Trace recording: "+recording.Folder);
            output.Add(File.ReadAllText(Path.Combine(recording.Folder,"session.json")));
        }
    }
    static void OfflineTrace() {
        string path=Path.GetFullPath(Path.Combine(Files.Root,"..","work","reference","SelfDescribingSingleEvent.etl"));
        if(!File.Exists(path)) { output.Add("SKIP (optional ETW sample trace not present): "+path); Console.WriteLine(output.Last()); return; }
        int decoded=0;
        using(var source=new Microsoft.Diagnostics.Tracing.ETWTraceEventSource(path)) {
            source.Dynamic.All+=e=>{ foreach(string field in e.PayloadNames) { object value=e.PayloadByName(field); } decoded++; };
            source.Process();
        }
        Check(decoded>0,"Microsoft self-describing ETW sample decodes with installed dependencies");
    }
    static void SaveSessionChecks() {
        string sourceLog=Path.Combine(Files.Root,"RealVR64.log");
        if(File.Exists(sourceLog))throw new Exception("Refusing to replace an existing test-source log.");
        File.WriteAllText(sourceLog,"Synthetic preservation test — first session");
        try {
            using(var p=Process.GetCurrentProcess()) {
                var recording=new Recording(p,delegate(string s){},Path.Combine(Files.Root,"Validation","Hardware capture"),new Guid("0131BD63-0947-41E8-8DE9-2FBE3AEAB353"));
                var thread=new Thread(recording.Run);thread.Start();Thread.Sleep(3200);recording.Stop();
                Check(thread.Join(12000),"hardware-only session stops and finalizes");
                Check(recording.Failure==null,"session save completes without an exception");
                Check(Report.Read(Path.Combine(recording.Folder,"hardware.csv")).Count>=2,"live hardware session saves multiple samples");
                Check(recording.FrameCount==0&&File.ReadAllText(Path.Combine(recording.Folder,"session.json")).Contains("No VR application frame data captured"),"absent VR timing is clearly reported, not fabricated");
                File.WriteAllText(sourceLog,"Synthetic second session");
                Check(File.ReadAllText(Path.Combine(recording.Folder,"RealVR64.log")).Contains("first session"),"preserved log survives a later source-log overwrite");
                Check(File.Exists(Path.Combine(recording.Folder,"Report.html")),"hardware-only capture also produces its report");
            }
        } finally { File.Delete(sourceLog); }
    }

    static StreamHealth Feed(DateTime t, int seconds, Func<int, double?> fps, Func<int, double?> gpu, Func<int, double?> visible) {
        var h = new StreamHealth();
        for (int i = 0; i < seconds; i++) { h.AddGpu(t.AddSeconds(i), gpu(i)); h.AddFrame(t.AddSeconds(i), fps(i), 120, 1, visible(i)); }
        return h;
    }
    static void AutoRefreshChecks() {
        var t = new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc);
        var healthy = Feed(t, 1800, i => 119.5, i => 97, i => 1);
        Check(!healthy.Armed, "auto-refresh: healthy 120 fps never triggers");
        var gpuBound = Feed(t, 1800, i => 95, i => 98, i => 1);
        Check(!gpuBound.Armed, "auto-refresh: a GPU-bound drop (GPU 98%) is not treated as a stream slowdown");
        string armedAt = null;
        var decline = new StreamHealth(); decline.Changed += delegate(string what, double f, double g) { if (what == "armed" && armedAt == null) armedAt = f.ToString("0.0"); };
        for (int i = 0; i < 2400; i++) { double k = Math.Max(0, (i - 600) / 1800.0); decline.AddGpu(t.AddSeconds(i), 97 - 42 * Math.Min(1, k)); decline.AddFrame(t.AddSeconds(i), 120 - 54 * Math.Min(1, k), 120, 1, 1); }
        Check(decline.Armed && armedAt != null, "auto-refresh: gradual 120->66 fps decline with falling GPU load is detected (armed at " + armedAt + " fps)");
        var removed = Feed(t, 1800, i => i > 600 ? (double?)60 : 120, i => i > 600 ? (double?)30 : 97, i => i > 600 ? (double?)0 : 1);
        Check(!removed.Armed, "auto-refresh: headset removed/not visible never triggers");
        var startup = Feed(t, 100, i => 60, i => 50, i => 1);
        Check(!startup.Armed, "auto-refresh: first two minutes are ignored");
        decline.Reset(t.AddSeconds(2400));
        for (int i = 2400; i < 2600; i++) { decline.AddGpu(t.AddSeconds(i), 97); decline.AddFrame(t.AddSeconds(i), 120, 120, 1, 1); }
        Check(!decline.Armed && decline.MedianFpsSince(t.AddSeconds(2410), 10) >= 119, "auto-refresh: recovery after a refresh is recognised");
        Check(StreamHealth.LooksPaused(new double[] { 62, 60, 65, 58, 61, 63 }, new double[] { 41, 38 }), "auto-refresh: GPU drop after map press counts as paused (Oct 29 pattern)");
        Check(!StreamHealth.LooksPaused(new double[] { 45, 44, 47, 46, 45 }, new double[] { 60, 70 }), "auto-refresh: GPU rising after a press (closing the menu) is not paused");
        Check(!StreamHealth.LooksPaused(new double[] { 62, 60, 65 }, new double[] { 30 }), "auto-refresh: too little GPU history is not trusted");
        Check(Marshal.SizeOf(typeof(Native.Input)) == 40, "auto-refresh: Windows key-press structure has the 64-bit size SendInput expects");
        if (Process.GetProcessesByName(VdToggle.StreamerProcess).Length > 0) {
            IntPtr streamer = VdToggle.FindStreamerWindow();
            var title = new System.Text.StringBuilder(256); if (streamer != IntPtr.Zero) Native.GetWindowText(streamer, title, title.Capacity);
            Check(streamer != IntPtr.Zero, "auto-refresh: VD Streamer window found (\"" + title + "\", visible=" + (streamer != IntPtr.Zero && Native.IsWindowVisible(streamer)) + ", minimized=" + (streamer != IntPtr.Zero && Native.IsIconic(streamer)) + ")");
        } else { output.Add("SKIP (VD Streamer not running): auto-refresh: VD Streamer window found"); Console.WriteLine(output.Last()); }
        // Replays of real sessions (expected times found by the Python replay of the same rules).
        ReplayCheck("2026-11-02_12-33-27_49720_4c0288", new[] { "06:05" }, "Nov 2 slowdown detected about 30 min before the manual refresh at 13:36");
        ReplayCheck("2026-10-29_14-27-35_16640_dad649", new[] { "08:01", "08:47" }, "Oct 29: both slowdowns detected before the manual refreshes at 15:16 and 15:57");
        ReplayCheck("2026-10-28_20-21-19_54292_84b710", new[] { "13:23" }, "Oct 28: already-slow session detected");
    }
    static void ReplayCheck(string session, string[] expectedUtcMinutes, string label) {
        string folder = Path.Combine(Files.Sessions, session);
        if (!File.Exists(Path.Combine(folder, "vr-timing.csv"))) { output.Add("SKIP (recording not present): " + label); Console.WriteLine(output.Last()); return; }
        var frames = Report.Read(Path.Combine(folder, "vr-timing.csv")); var hardware = Report.Read(Path.Combine(folder, "hardware.csv"));
        var events = frames.Select(r => new { r.Time, Kind = 0, Row = r }).Concat(hardware.Select(r => new { r.Time, Kind = 1, Row = r })).OrderBy(e => e.Time).ThenBy(e => e.Kind).ToList();
        var h = new StreamHealth(); var armed = new List<string>(); DateTime current = DateTime.MinValue;
        h.Changed += delegate(string what, double f, double g) { if (what == "armed") armed.Add(current.ToString("HH:mm")); };
        foreach (var e in events) {
            current = e.Time;
            if (e.Kind == 1) h.AddGpu(e.Time, Report.Value(e.Row, "gpu_load_pct"));
            else h.AddFrame(e.Time, Report.Value(e.Row, "vr_app_fps"), Report.Value(e.Row, "headset_refresh_hz"), Report.Value(e.Row, "hmd_mounted"), Report.Value(e.Row, "hmd_visible"));
        }
        Check(armed.SequenceEqual(expectedUtcMinutes), "auto-refresh replay: " + label + " (UTC " + string.Join(", ", armed) + ")");
    }
}
