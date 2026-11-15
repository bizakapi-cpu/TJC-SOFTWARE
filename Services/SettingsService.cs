using Microsoft.Win32;
using TJCServiceDesk.Models;

namespace TJCServiceDesk.Services;

public sealed class SettingsService(SqliteDataService data)
{
    private readonly SqliteDataService _data = data;
    public Task<string?> GetAsync(string key) => _data.GetSettingAsync(key);
    public Task SetAsync(string key, string value) => _data.SetSettingAsync(key, value);

    public void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
            ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Could not determine the application executable path.");
            key.SetValue("TJC Service Desk", $"\"{executable}\"");
        }
        else key.DeleteValue("TJC Service Desk", throwOnMissingValue: false);
    }
}
