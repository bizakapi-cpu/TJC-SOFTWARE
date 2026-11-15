using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TJCServiceDesk.Services;
using TJCServiceDesk.ViewModels;

namespace TJCServiceDesk;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var services = new ServiceCollection();
        services.AddSingleton<SqliteDataService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<MusicService>();
        services.AddSingleton<MusicSearchService>();
        services.AddSingleton<GameService>();
        services.AddSingleton<SupabaseService>();
        services.AddSingleton<AutoUpdateService>();
        services.AddSingleton<IChatBackend, SupabaseChatBackend>();
        services.AddSingleton<ChatService>();
        services.AddSingleton<AudioPlayerService>();
        services.AddSingleton<HardwareMonitorService>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<MusicViewModel>();
        services.AddSingleton<GamesViewModel>();
        services.AddSingleton<ConnectViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();
        try
        {
            await _services.GetRequiredService<SqliteDataService>().InitializeAsync();
            try
            {
                await _services.GetRequiredService<SupabaseService>().InitializeAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Supabase fout: " + ex.Message, "Supabase fout", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            var mainViewModel = _services.GetRequiredService<MainViewModel>();
            await mainViewModel.InitializeAsync();
            var window = _services.GetRequiredService<MainWindow>();
            window.DataContext = mainViewModel;
            window.Show();
            _ = CheckForUpdatesAsync(_services.GetRequiredService<AutoUpdateService>(), window);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"TJC Service Desk could not start: {ex.Message}", "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private async Task CheckForUpdatesAsync(AutoUpdateService updater, MainWindow window)
    {
        UpdateManifest? update;
        try
        {
            update = await updater.CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed; the app will continue with the current version: {ex}");
            return;
        }

        if (update is null || !window.IsVisible) return;
        var notes = string.IsNullOrWhiteSpace(update.ReleaseNotes) ? "No release notes provided." : update.ReleaseNotes;
        var choice = MessageBox.Show(
            $"Version {update.ResolvedVersion} is available (installed: {updater.CurrentVersion}).\n\n{notes}\n\nDownload and restart now?",
            "TJC Service Desk update available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (choice != MessageBoxResult.Yes) return;

        try
        {
            await updater.StartUpdateAndRestartAsync(update);
            window.Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The update could not be installed: {ex.Message}", "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is not null) _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
