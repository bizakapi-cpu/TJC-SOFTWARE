using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace TJCServiceDesk.Services;

public sealed class HardwareMonitorService : IDisposable
{
    private readonly Computer _computer = new() { IsCpuEnabled = true, IsGpuEnabled = true };
    private readonly Dictionary<string, long> _networkBytes = new();
    private DateTime _networkSample = DateTime.UtcNow;
    private bool _opened;

    public HardwareSnapshot ReadSnapshot()
    {
        var snapshot = new HardwareSnapshot();
        try
        {
            if (!_opened) { _computer.Open(); _opened = true; }
            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
                foreach (var subHardware in hardware.SubHardware) subHardware.Update();
                var sensors = hardware.Sensors.Concat(hardware.SubHardware.SelectMany(h => h.Sensors));
                foreach (var sensor in sensors)
                {
                    if (sensor.Value is not float value) continue;
                    if (sensor.SensorType == SensorType.Load)
                    {
                        var isGpu = hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
                        if (hardware.HardwareType == HardwareType.Cpu && sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase)) snapshot.CpuLoad = value;
                        if (isGpu && snapshot.GpuLoad == 0 && (sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("3D", StringComparison.OrdinalIgnoreCase))) snapshot.GpuLoad = value;
                    }
                    if (sensor.SensorType == SensorType.Temperature)
                    {
                        if (hardware.HardwareType == HardwareType.Cpu && snapshot.CpuTemperature is null) snapshot.CpuTemperature = value;
                        if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel && snapshot.GpuTemperature is null) snapshot.GpuTemperature = value;
                    }
                }
            }
        }
        catch (Exception ex) { snapshot.SensorError = ex.Message; }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            snapshot.RamTotalGb = memory.TotalPhysical / 1024d / 1024d / 1024d;
            snapshot.RamUsedGb = (memory.TotalPhysical - memory.AvailablePhysical) / 1024d / 1024d / 1024d;
        }
        try
        {
            var now = DateTime.UtcNow;
            var elapsed = Math.Max(0.1, (now - _networkSample).TotalSeconds);
            long received = 0, sent = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.OperationalStatus != OperationalStatus.Up) continue;
                var stats = ni.GetIPv4Statistics(); received += stats.BytesReceived; sent += stats.BytesSent;
            }
            if (_networkBytes.TryGetValue("rx", out var oldRx)) snapshot.DownloadMbps = Math.Max(0, received - oldRx) * 8 / elapsed / 1_000_000;
            if (_networkBytes.TryGetValue("tx", out var oldTx)) snapshot.UploadMbps = Math.Max(0, sent - oldTx) * 8 / elapsed / 1_000_000;
            _networkBytes["rx"] = received; _networkBytes["tx"] = sent; _networkSample = now;
        }
        catch (NetworkInformationException ex) { snapshot.SensorError ??= ex.Message; }
        return snapshot;
    }

    public void Dispose() => _computer.Close();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}

public sealed class HardwareSnapshot
{
    public float CpuLoad { get; set; }
    public float? CpuTemperature { get; set; }
    public float GpuLoad { get; set; }
    public float? GpuTemperature { get; set; }
    public double RamUsedGb { get; set; }
    public double RamTotalGb { get; set; }
    public double DownloadMbps { get; set; }
    public double UploadMbps { get; set; }
    public string? SensorError { get; set; }
    public bool HasTemperatures => CpuTemperature.HasValue || GpuTemperature.HasValue;
}
