using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace VrRecorder {
    public sealed class Sensors : IDisposable {
        public static readonly string[] Fields = {
            "gpu_used_mb", "gpu_total_mb", "gpu_load_pct", "gpu_memory_controller_pct", "gpu_temperature_c", "gpu_power_w", "gpu_clock_mhz", "gpu_memory_clock_mhz", "gpu_encoder_load_pct", "gpu_encoder_sessions", "gpu_encoder_fps", "gpu_encoder_latency_ms", "gpu_clock_limit_flags", "gpu_thermal_limited",
            "system_ram_used_mb", "system_ram_total_mb", "system_commit_used_mb", "system_cpu_pct", "game_private_mb", "game_working_set_mb", "game_cpu_pct", "game_handles", "game_gpu_dedicated_mb", "game_gpu_shared_mb", "vd_streamer_private_mb", "network_send_mbps", "network_receive_mbps", "recorder_cpu_pct", "recorder_working_set_mb", "sample_work_ms"
        };
        IntPtr gpu;
        bool nvml;
        public string GpuName = "Unavailable";
        public string Driver = "Unavailable";
        public string GpuStatus = "";
        readonly Dictionary<int, double> cpu = new Dictionary<int, double>();
        readonly Dictionary<string, long[]> net = new Dictionary<string, long[]>();
        readonly List<PerformanceCounter> dedicated = new List<PerformanceCounter>();
        readonly List<PerformanceCounter> shared = new List<PerformanceCounter>();
        DateTime lastCounterDiscovery = DateTime.MinValue;
        int counterPid = -1;
        ulong oldIdle, oldKernel, oldUser;
        long previousTick = Stopwatch.GetTimestamp();
        bool first = true;
        public Sensors() {
            try {
                int code = Nvml.nvmlInit_v2();
                if (code != 0) throw new Exception("NVIDIA sensor initialization returned " + code);
                nvml = true;
                if (Nvml.nvmlDeviceGetHandleByIndex_v2(0, out gpu) != 0) throw new Exception("NVIDIA GPU unavailable");
                var name = new StringBuilder(128); var driver = new StringBuilder(128);
                if (Nvml.nvmlDeviceGetName(gpu, name, 128) == 0) GpuName = name.ToString();
                if (Nvml.nvmlSystemGetDriverVersion(driver, 128) == 0) Driver = driver.ToString();
            } catch (Exception e) { GpuStatus = e.Message; gpu = IntPtr.Zero; }
        }
        public Dictionary<string, double?> Read(Process game) {
            var sw = Stopwatch.StartNew();
            var values = Fields.ToDictionary(k => k, k => (double?)null);
            long tick = Stopwatch.GetTimestamp();
            double seconds = (double)(tick - previousTick) / Stopwatch.Frequency;
            previousTick = tick;
            if (gpu != IntPtr.Zero) {
                try {
                    Nvml.Memory memory; Nvml.Utilization util; uint n, period, sessions, fps, latency; ulong flags;
                    if (Nvml.nvmlDeviceGetMemoryInfo(gpu, out memory) == 0) { values["gpu_used_mb"] = memory.Used / 1048576.0; values["gpu_total_mb"] = memory.Total / 1048576.0; }
                    if (Nvml.nvmlDeviceGetUtilizationRates(gpu, out util) == 0) { values["gpu_load_pct"] = util.Gpu; values["gpu_memory_controller_pct"] = util.Memory; }
                    if (Nvml.nvmlDeviceGetTemperature(gpu, 0, out n) == 0) values["gpu_temperature_c"] = n;
                    if (Nvml.nvmlDeviceGetPowerUsage(gpu, out n) == 0) values["gpu_power_w"] = n / 1000.0;
                    if (Nvml.nvmlDeviceGetClockInfo(gpu, 0, out n) == 0) values["gpu_clock_mhz"] = n;
                    if (Nvml.nvmlDeviceGetClockInfo(gpu, 2, out n) == 0) values["gpu_memory_clock_mhz"] = n;
                    if (Nvml.nvmlDeviceGetEncoderUtilization(gpu, out n, out period) == 0) values["gpu_encoder_load_pct"] = n;
                    if (Nvml.nvmlDeviceGetEncoderStats(gpu, out sessions, out fps, out latency) == 0) {
                        values["gpu_encoder_sessions"] = sessions;
                        if (sessions > 0) { values["gpu_encoder_fps"] = fps; values["gpu_encoder_latency_ms"] = latency / 1000.0; }
                    }
                    if (Nvml.nvmlDeviceGetCurrentClocksThrottleReasons(gpu, out flags) == 0) { values["gpu_clock_limit_flags"] = flags; values["gpu_thermal_limited"] = (flags & (32UL | 64UL)) != 0 ? 1 : 0; }
                } catch (Exception e) { GpuStatus = e.Message; }
            }
            var mem = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (GlobalMemoryStatusEx(ref mem)) {
                values["system_ram_total_mb"] = mem.TotalPhysical / 1048576.0;
                values["system_ram_used_mb"] = (mem.TotalPhysical - mem.AvailablePhysical) / 1048576.0;
                values["system_commit_used_mb"] = (mem.TotalPageFile - mem.AvailablePageFile) / 1048576.0;
            }
            ulong idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user)) {
                if (!first && kernel + user > oldKernel + oldUser) values["system_cpu_pct"] = 100 * (1 - (double)(idle - oldIdle) / (kernel + user - oldKernel - oldUser));
                oldIdle = idle; oldKernel = kernel; oldUser = user;
            }
            if (game != null) {
                try {
                    game.Refresh();
                    values["game_private_mb"] = game.PrivateMemorySize64 / 1048576.0;
                    values["game_working_set_mb"] = game.WorkingSet64 / 1048576.0;
                    values["game_handles"] = game.HandleCount;
                    values["game_cpu_pct"] = ProcessCpu(game, seconds);
                    ReadGpuMemory(game.Id, values);
                } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }
            using (var self = Process.GetCurrentProcess()) {
                values["recorder_cpu_pct"] = ProcessCpu(self, seconds);
                values["recorder_working_set_mb"] = self.WorkingSet64 / 1048576.0;
            }
            var streamers = Process.GetProcessesByName("VirtualDesktop.Streamer");
            double vdRam = 0; bool found = false;
            foreach (var p in streamers) { using (p) { try { vdRam += p.PrivateMemorySize64 / 1048576.0; found = true; } catch { } } }
            if (found) values["vd_streamer_private_mb"] = vdRam;
            try {
                double send = 0, receive = 0; bool measured = false;
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(a => a.OperationalStatus == OperationalStatus.Up && a.NetworkInterfaceType != NetworkInterfaceType.Loopback && a.NetworkInterfaceType != NetworkInterfaceType.Tunnel)) {
                    var stats = adapter.GetIPv4Statistics(); long[] previous;
                    if (net.TryGetValue(adapter.Id, out previous) && !first && seconds > 0 && stats.BytesSent >= previous[0] && stats.BytesReceived >= previous[1]) { send += (stats.BytesSent - previous[0]) * 8 / seconds / 1e6; receive += (stats.BytesReceived - previous[1]) * 8 / seconds / 1e6; measured = true; }
                    net[adapter.Id] = new long[] { stats.BytesSent, stats.BytesReceived };
                }
                if (measured) { values["network_send_mbps"] = send; values["network_receive_mbps"] = receive; }
            } catch { }
            first = false;
            values["sample_work_ms"] = sw.Elapsed.TotalMilliseconds;
            return values;
        }
        double? ProcessCpu(Process p, double seconds) {
            double now = p.TotalProcessorTime.TotalSeconds, previous;
            bool exists = cpu.TryGetValue(p.Id, out previous); cpu[p.Id] = now;
            if (!exists || first || seconds <= 0 || now < previous) return null;
            return 100 * (now - previous) / seconds / Environment.ProcessorCount;
        }
        void ReadGpuMemory(int pid, IDictionary<string, double?> values) {
            if (counterPid != pid || (DateTime.UtcNow - lastCounterDiscovery).TotalSeconds >= 15) {
                foreach (var c in dedicated.Concat(shared)) c.Dispose(); dedicated.Clear(); shared.Clear();
                counterPid = pid; lastCounterDiscovery = DateTime.UtcNow;
                try {
                    var category = new PerformanceCounterCategory("GPU Process Memory");
                    foreach (string instance in category.GetInstanceNames().Where(s => s.StartsWith("pid_" + pid + "_", StringComparison.OrdinalIgnoreCase))) {
                        dedicated.Add(new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance, true));
                        shared.Add(new PerformanceCounter("GPU Process Memory", "Shared Usage", instance, true));
                    }
                } catch { }
            }
            try { if (dedicated.Count > 0) values["game_gpu_dedicated_mb"] = dedicated.Sum(c => (double)c.RawValue) / 1048576; } catch { }
            try { if (shared.Count > 0) values["game_gpu_shared_mb"] = shared.Sum(c => (double)c.RawValue) / 1048576; } catch { }
        }
        public void Dispose() { foreach (var c in dedicated.Concat(shared)) c.Dispose(); if (nvml) { Nvml.nvmlShutdown(); nvml = false; } }
        [StructLayout(LayoutKind.Sequential)] struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    }
    static class Nvml {
        [StructLayout(LayoutKind.Sequential)] public struct Memory { public ulong Total, Free, Used; }
        [StructLayout(LayoutKind.Sequential)] public struct Utilization { public uint Gpu, Memory; }
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlInit_v2();
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlShutdown();
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint size);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlSystemGetDriverVersion(StringBuilder name, uint size);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensor, out uint temperature);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint power);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetClockInfo(IntPtr device, uint type, out uint clock);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetEncoderUtilization(IntPtr device, out uint usage, out uint samplingPeriodUs);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetEncoderStats(IntPtr device, out uint sessions, out uint fps, out uint latencyUs);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong flags);
    }
}
