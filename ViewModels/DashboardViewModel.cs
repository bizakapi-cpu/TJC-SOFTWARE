using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using TJCServiceDesk.Services;

namespace TJCServiceDesk.ViewModels;

public sealed class DashboardViewModel : INotifyPropertyChanged
{
    private readonly HardwareMonitorService _hardware;
    private CancellationTokenSource? _monitoring;
    private string _cpu = "--"; private string _cpuTemp = "--"; private string _gpu = "--"; private string _gpuTemp = "--";
    private string _ram = "Reading…"; private string _network = "--"; private string _notice = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public string CpuTemperature { get => _cpuTemp; private set => Set(ref _cpuTemp, value); }
    public string Gpu { get => _gpu; private set => Set(ref _gpu, value); }
    public string GpuTemperature { get => _gpuTemp; private set => Set(ref _gpuTemp, value); }
    public string Ram { get => _ram; private set => Set(ref _ram, value); }
    public string Network { get => _network; private set => Set(ref _network, value); }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }

    public DashboardViewModel(HardwareMonitorService hardware) => _hardware = hardware;

    public void SetActive(bool active)
    {
        if (active && _monitoring is null) { _monitoring = new CancellationTokenSource(); _ = MonitorAsync(_monitoring.Token); }
        else if (!active && _monitoring is not null) { _monitoring.Cancel(); _monitoring.Dispose(); _monitoring = null; }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1500));
        try
        {
            do
            {
                var snapshot = await Task.Run(_hardware.ReadSnapshot, token);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Cpu = $"{snapshot.CpuLoad:0}%"; CpuTemperature = snapshot.CpuTemperature.HasValue ? $"{snapshot.CpuTemperature:0}°C" : "Unavailable";
                    Gpu = $"{snapshot.GpuLoad:0}%"; GpuTemperature = snapshot.GpuTemperature.HasValue ? $"{snapshot.GpuTemperature:0}°C" : "Unavailable";
                    Ram = $"{snapshot.RamUsedGb:0.0} / {snapshot.RamTotalGb:0.0} GB";
                    Network = $"↓ {snapshot.DownloadMbps:0.00} Mbps   ↑ {snapshot.UploadMbps:0.00} Mbps";
                    Notice = snapshot.HasTemperatures ? "" : "Start als Admin voor sensortemperaturen";
                });
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}
