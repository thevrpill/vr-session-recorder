using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Diagnostics.Tracing;

namespace VrRecorder {
    // This enables only the existing VDXR provider. It does not install an API layer
    // or inject a DLL. Event-name and PID filters keep the consumer's work bounded.
    public sealed class RuntimeTrace : IDisposable {
        public static readonly Guid VdxrProvider = new Guid("CBF3ADCD-42B1-4C38-930B-91980AF201F6");
        public static readonly string[] EventNames = { "EndFrame_Signal", "App_Statistics", "OVR_HmdDesc", "OVR_AswStatus", "OVR_SessionStatus", "BeginFrame" };
        readonly string name;
        readonly Action<string, DateTime, IDictionary<string, object>> receive;
        IntPtr properties;
        ulong handle;
        ETWTraceEventSource source;
        Thread reader;
        public long Delivered;
        public long DecodeErrors;
        public string Error = "";
        public bool NameFilterEnabled;
        public RuntimeTrace(int processId, Action<string, DateTime, IDictionary<string, object>> handler, Guid? provider = null) {
            receive = handler;
            name = "VRSessionRecorder-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");
            int size = Marshal.SizeOf(typeof(TraceProperties));
            properties = Marshal.AllocHGlobal(size + 2048);
            Marshal.Copy(new byte[size + 2048], 0, properties, size + 2048);
            var p = new TraceProperties();
            p.Wnode.BufferSize = (uint)(size + 2048);
            p.Wnode.Flags = 0x20000;
            p.Wnode.ClientContext = 1;
            p.Wnode.Guid = Guid.NewGuid();
            p.BufferSize = 64;
            p.MinimumBuffers = 4;
            p.MaximumBuffers = 32;
            p.LogFileMode = 0x100;
            p.FlushTimer = 1;
            p.LoggerNameOffset = (uint)size;
            Marshal.StructureToPtr(p, properties, false);
            byte[] nameBytes = Encoding.Unicode.GetBytes(name + "\0");
            Marshal.Copy(nameBytes, 0, IntPtr.Add(properties, size), nameBytes.Length);
            try {
                uint result = StartTrace(out handle, name, properties);
                Check(result, "Start VR timing capture");
                source = new ETWTraceEventSource(name, TraceEventSourceType.Session);
                source.Dynamic.All += OnEvent;
                Guid guid = provider ?? VdxrProvider;
                Enable(guid, processId);
                reader = new Thread(delegate() {
                    try { source.Process(); }
                    catch (Exception e) { Error = e.Message; }
                });
                reader.IsBackground = true;
                reader.Name = "VDXR timing reader";
                reader.Start();
            } catch { Dispose(); throw; }
        }
        void Enable(Guid provider, int processId) {
            byte[] names = Encoding.UTF8.GetBytes(string.Join("\0", EventNames) + "\0");
            byte[] filter = new byte[20 + names.Length];
            filter[16] = 5; filter[17] = 1;
            Array.Copy(BitConverter.GetBytes((ushort)EventNames.Length), 0, filter, 18, 2);
            Array.Copy(names, 0, filter, 20, names.Length);
            IntPtr namePtr = Marshal.AllocHGlobal(filter.Length);
            IntPtr pidPtr = Marshal.AllocHGlobal(4);
            IntPtr descPtr = Marshal.AllocHGlobal(32);
            try {
                Marshal.Copy(filter, 0, namePtr, filter.Length);
                Marshal.WriteInt32(pidPtr, processId);
                var nf = new FilterDescriptor { Ptr = (ulong)namePtr.ToInt64(), Size = (uint)filter.Length, Type = 0x80000400 };
                var pf = new FilterDescriptor { Ptr = (ulong)pidPtr.ToInt64(), Size = 4, Type = 0x80000004 };
                Marshal.StructureToPtr(nf, descPtr, false);
                Marshal.StructureToPtr(pf, IntPtr.Add(descPtr, 16), false);
                var ep = new EnableParameters { Version = 2, EnableFilterDesc = descPtr, FilterDescCount = 2 };
                uint result = EnableTraceEx2(handle, ref provider, 1, 5, ulong.MaxValue, 0, 0, ref ep);
                Check(result, "Enable filtered VR timing events");
                NameFilterEnabled = true;
            } finally {
                Marshal.FreeHGlobal(namePtr); Marshal.FreeHGlobal(pidPtr); Marshal.FreeHGlobal(descPtr);
            }
        }
        void OnEvent(TraceEvent e) {
            try {
                string eventName = e.EventName.Split('/')[0];
                if (!EventNames.Contains(eventName)) return;
                var values = new Dictionary<string, object>();
                foreach (string field in e.PayloadNames) values[field] = e.PayloadByName(field);
                Interlocked.Increment(ref Delivered);
                receive(eventName, e.TimeStamp.ToUniversalTime(), values);
            } catch { Interlocked.Increment(ref DecodeErrors); }
        }
        public uint EventsLost {
            get {
                if (handle == 0 || properties == IntPtr.Zero) return 0;
                if (ControlTrace(handle, name, properties, 0) != 0) return 0;
                return ((TraceProperties)Marshal.PtrToStructure(properties, typeof(TraceProperties))).EventsLost;
            }
        }
        public void Dispose() {
            if (handle != 0 && properties != IntPtr.Zero) { ControlTrace(handle, name, properties, 1); handle = 0; }
            if (reader != null && reader != Thread.CurrentThread && !reader.Join(5000)) {
                if (source != null) source.StopProcessing();
                reader.Join(1000);
            }
            if (source != null) { source.Dispose(); source = null; }
            if (properties != IntPtr.Zero) { Marshal.FreeHGlobal(properties); properties = IntPtr.Zero; }
        }
        static void Check(uint code, string action) {
            if (code == 0) return;
            if (code == 5) throw new InvalidOperationException(action + ": Windows administrator permission is required.");
            throw new InvalidOperationException(action + ": " + new System.ComponentModel.Win32Exception((int)code).Message + " (" + code + ")");
        }
        [StructLayout(LayoutKind.Sequential)] struct WnodeHeader { public uint BufferSize, ProviderId; public ulong HistoricalContext, TimeStamp; public Guid Guid; public uint ClientContext, Flags; }
        [StructLayout(LayoutKind.Sequential)] struct TraceProperties {
            public WnodeHeader Wnode;
            public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags, AgeLimit;
            public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset, LoggerNameOffset;
        }
        [StructLayout(LayoutKind.Sequential)] struct FilterDescriptor { public ulong Ptr; public uint Size, Type; }
        [StructLayout(LayoutKind.Sequential)] struct EnableParameters { public uint Version, EnableProperty, ControlFlags; public Guid SourceId; public IntPtr EnableFilterDesc; public uint FilterDescCount; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")] static extern uint StartTrace(out ulong session, string name, IntPtr properties);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")] static extern uint ControlTrace(ulong session, string name, IntPtr properties, uint control);
        [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, ref EnableParameters parameters);
    }

    public sealed class FrameBucket {
        public DateTime Time;
        public int Frames;
        public List<double> Intervals = new List<double>();
        public List<double> Cpu = new List<double>();
        public List<double> RenderCpu = new List<double>();
        public List<double> Gpu = new List<double>();
        public int Discarded;
        public double? Refresh, Reprojection, Mounted, Visible;
        public double? Fps { get { return Intervals.Count > 0 && Intervals.Sum() > 0 ? (double?)(1000 * Intervals.Count / Intervals.Sum()) : null; } }
        public static double? Average(List<double> values) { return values.Count == 0 ? null : (double?)values.Average(); }
        public static double? Percentile(List<double> values, double percentile) { if (values.Count == 0) return null; var sorted = values.OrderBy(v => v).ToArray(); return sorted[Math.Max(0, (int)Math.Ceiling(percentile * sorted.Length) - 1)]; }
    }
    public sealed class FrameAggregator {
        readonly object gate = new object();
        readonly SortedDictionary<long, FrameBucket> pending = new SortedDictionary<long, FrameBucket>();
        DateTime lastFrame;
        double? refresh, reprojection, mounted, visible;
        long lastId = -1;
        public long LateEvents;
        long flushed = -1;
        public void Receive(string name, DateTime utc, IDictionary<string, object> payload) {
            lock (gate) {
                long second = utc.Ticks / TimeSpan.TicksPerSecond;
                if (second <= flushed) { LateEvents++; return; }
                FrameBucket b;
                if (!pending.TryGetValue(second, out b)) { b = new FrameBucket { Time = new DateTime(second * TimeSpan.TicksPerSecond, DateTimeKind.Utc) }; pending[second] = b; }
                if (name == "OVR_HmdDesc") refresh = Number(payload, "DisplayRefreshRate");
                if (name == "OVR_AswStatus") reprojection = Number(payload, "AsyncReprojectionActive");
                if (name == "OVR_SessionStatus") { mounted = Number(payload, "HmdMounted"); visible = Number(payload, "IsVisible"); }
                b.Refresh = refresh; b.Reprojection = reprojection; b.Mounted = mounted; b.Visible = visible;
                if (name == "EndFrame_Signal") {
                    double? id = Number(payload, "FrameCompleted");
                    if (id.HasValue && (long)id.Value != lastId) {
                        if (lastFrame != default(DateTime)) {
                            double ms = (utc - lastFrame).TotalMilliseconds;
                            if (ms > 0) b.Intervals.Add(ms);
                        }
                        b.Frames++; lastFrame = utc; lastId = (long)id.Value;
                    }
                }
                if (name == "App_Statistics") {
                    Add(b.Cpu, Number(payload, "AppFrameCpuTime"), false);
                    Add(b.RenderCpu, Number(payload, "AppRenderCpuTime"), false);
                    Add(b.Gpu, Number(payload, "AppRenderGpuTime"), true);
                }
                if (name == "BeginFrame" && Number(payload, "FrameDiscarded") == 1) b.Discarded++;
            }
        }
        static void Add(List<double> values, double? us, bool zeroIsMissing) { if (us.HasValue && us.Value >= 0 && (!zeroIsMissing || us.Value > 0)) values.Add(us.Value / 1000); }
        public static double? Number(IDictionary<string, object> values, string field) {
            object value;
            if (!values.TryGetValue(field, out value) || value == null) return null;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); } catch { return null; }
        }
        public List<FrameBucket> Drain(DateTime through) {
            lock (gate) {
                long cutoff = through.Ticks / TimeSpan.TicksPerSecond;
                var keys = pending.Keys.Where(k => k < cutoff).ToList();
                var result = keys.Select(k => pending[k]).ToList();
                foreach (long key in keys) { pending.Remove(key); flushed = Math.Max(flushed, key); }
                return result;
            }
        }
    }
}
