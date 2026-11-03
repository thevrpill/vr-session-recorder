using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;

namespace VrRecorder {
    [EventSource(Name="VRSessionRecorder-SetupCheck", Guid="A895DB5C-C364-49E3-938A-CFCF540A4CA8")]
    sealed class ValidationEvents : EventSource {
        public ValidationEvents() : base(EventSourceSettings.EtwSelfDescribingEventFormat) { }
        public void Frame(long frame) {
            Write("EndFrame_Signal", new { FrameCompleted=frame });
            Write("App_Statistics", new { FrameId=frame, AppFrameCpuTime=8000L });
            Write("App_Statistics", new { FrameId=frame, AppRenderGpuTime=5500L });
        }
    }
    public static class CaptureValidation {
        public static string RunIfNeeded() {
            string hash;
            using(var input=File.OpenRead(System.Windows.Forms.Application.ExecutablePath)) using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(input));
            string path=Path.Combine(Files.Root,"capture-verification.json");
            if(File.Exists(path)) {
                try { var prior=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));if(prior.ContainsKey("binary_hash")&&(string)prior["binary_hash"]==hash&&prior.ContainsKey("passed")&&(bool)prior["passed"])return null; } catch { }
            }
            string error=null;long eventCount=0;
            try {
                var frames=new FrameAggregator();
                using(var events=new ValidationEvents()) {
                    using(var trace=new RuntimeTrace(Process.GetCurrentProcess().Id,frames.Receive,new Guid("A895DB5C-C364-49E3-938A-CFCF540A4CA8"))) {
                        Thread.Sleep(300);
                        for(int i=1;i<=100;i++) { events.Frame(i);Thread.Sleep(20); }
                        Thread.Sleep(1200);
                        if(trace.DecodeErrors>0)throw new Exception("Some test timing events could not be decoded.");
                        eventCount=trace.Delivered;
                    }
                }
                var rows=frames.Drain(DateTime.MaxValue);
                if(rows.Sum(r=>r.Frames)<50||!rows.Any(r=>r.Gpu.Any(v=>Math.Abs(v-5.5)<.01)))throw new Exception("Windows did not deliver the expected VR-format timing readings.");
            } catch(Exception e) { error=e.Message; }
            Files.Json(path,new { binary_hash=hash,checked_utc=DateTime.UtcNow.ToString("o"),passed=error==null,events_received=eventCount,error=error });
            return error;
        }
    }
}
