using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("VR Session Recorder")]
[assembly: AssemblyProduct("VR Session Recorder for GTA V Enhanced (REAL VR) + Virtual Desktop")]
[assembly: AssemblyDescription("Records VR performance and automatically refreshes a slowing Virtual Desktop stream. Unofficial; not affiliated with Virtual Desktop, Meta, Rockstar Games or the REAL VR mod.")]
[assembly: AssemblyCopyright("Copyright (c) 2026 VRPill. MIT License.")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: AssemblyInformationalVersion("0.1.0")]

namespace VrRecorder {
    // Saved next to the program as settings.json.
    public sealed class RecorderSettings {
        public bool AutoRefreshStream { get; set; }
        public RecorderSettings() { AutoRefreshStream = true; }
        public static RecorderSettings Load() {
            try { return new JavaScriptSerializer().Deserialize<RecorderSettings>(File.ReadAllText(Path.Combine(Files.Root, "settings.json"))); }
            catch { return new RecorderSettings(); }
        }
        public void Save() { Files.Json(Path.Combine(Files.Root, "settings.json"), this); }
    }
}
