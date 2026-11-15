using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TJCServiceDesk.Models;
using TJCServiceDesk.Services;
using Microsoft.Win32;

namespace TJCServiceDesk.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly MusicViewModel _music;
    private readonly GamesViewModel _games;
    private readonly GameService _gameService;
    private readonly SupabaseService _supabase;
    private bool _isInitializing;
    [ObservableProperty] private string userName = "Guest";
    [ObservableProperty] private string userTag = "";
    [ObservableProperty] private string profilePicturePath = "";
    [ObservableProperty] private string accentColor = "#8B5CF6";
    [ObservableProperty] private bool launchAtStartup;
    [ObservableProperty] private string musicFolder = "";
    [ObservableProperty] private string gameFolder = "";
    [ObservableProperty] private string status = "";
    public ObservableCollection<AccentSwatch> AccentSwatches { get; } = new()
    {
        new("Violet", "#8B5CF6"), new("Blauw", "#3B82F6"), new("Groen", "#10B981"),
        new("Rood", "#EF4444"), new("Roze", "#EC4899"), new("Oranje", "#F59E0B")
    };
    public string UserDisplayTag => $"{UserName}#{UserTag}";

    public SettingsViewModel(SettingsService settings, MusicViewModel music, GamesViewModel games, GameService gameService, SupabaseService supabase)
    {
        _settings = settings; _music = music; _games = games; _gameService = gameService; _supabase = supabase;
        PropertyChanged += HandleChanged;
    }

    public async Task InitializeAsync()
    {
        _isInitializing = true;
        UserName = await _settings.GetAsync("UserName") ?? "Guest";
        var savedTag = await _settings.GetAsync("UserTag");
        UserTag = string.IsNullOrWhiteSpace(savedTag) ? RandomNumberGenerator.GetInt32(0, 10000).ToString("D4") : savedTag;
        ProfilePicturePath = await _settings.GetAsync("ProfilePicturePath") ?? "";
        AccentColor = ResolveAccent(await _settings.GetAsync("AccentColor") ?? "#8B5CF6");
        MusicFolder = await _settings.GetAsync("MusicFolder") ?? "";
        await _settings.SetAsync("UserTag", UserTag);
        OnPropertyChanged(nameof(UserDisplayTag));
        GameFolder = await _settings.GetAsync("GameFolder") ?? "";
        LaunchAtStartup = string.Equals(await _settings.GetAsync("LaunchAtStartup"), "true", StringComparison.OrdinalIgnoreCase);
        ApplyAccent(AccentColor);
        UpdateSelectedSwatch();
        _isInitializing = false;
        if (!_supabase.IsConfigured) Status = "Supabase is not configured. Add its URL and anon key to enable live Connect.";
    }

    private async void HandleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing) return;
        try
        {
            if (e.PropertyName == nameof(UserName))
            {
                UserName = UserName.Trim();
                OnPropertyChanged(nameof(UserDisplayTag));
                await _settings.SetAsync("UserName", UserName);
                await SyncSupabaseProfileAsync();
            }
            if (e.PropertyName == nameof(ProfilePicturePath))
            {
                await _settings.SetAsync("ProfilePicturePath", ProfilePicturePath);
                await SyncSupabaseProfileAsync();
            }
            if (e.PropertyName == nameof(AccentColor)) { ApplyAccent(AccentColor); await _settings.SetAsync("AccentColor", AccentColor); UpdateSelectedSwatch(); }
            if (e.PropertyName == nameof(LaunchAtStartup)) { _settings.SetStartup(LaunchAtStartup); await _settings.SetAsync("LaunchAtStartup", LaunchAtStartup.ToString()); }
        }
        catch (Exception ex) { Status = $"Setting could not be saved: {ex.Message}"; }
    }

    private async Task SyncSupabaseProfileAsync()
    {
        try
        {
            await _supabase.UpsertProfileAsync(UserDisplayTag, UserName, ProfilePicturePath);
        }
        catch (Exception ex)
        {
            Status = $"Supabase profile could not be synchronized: {ex.Message}";
            MessageBox.Show("Supabase fout: " + ex.Message, "Supabase fout", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string ResolveAccent(string value) => value switch
    {
        "Blue" => "#3B82F6", "Green" => "#10B981", "Red" => "#EF4444", "Pink" => "#EC4899", "Orange" => "#F59E0B",
        "#3B82F6" => "#3B82F6", "#10B981" => "#10B981", "#EF4444" => "#EF4444", "#EC4899" => "#EC4899", "#F59E0B" => "#F59E0B", _ => "#8B5CF6"
    };

    private static void ApplyAccent(string hex)
    {
        var colorValue = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        Application.Current.Resources["AccentPrimaryBrush"] = new System.Windows.Media.SolidColorBrush(colorValue);
    }

    partial void OnUserNameChanged(string value) => OnPropertyChanged(nameof(UserDisplayTag));

    private void UpdateSelectedSwatch()
    {
        foreach (var swatch in AccentSwatches) swatch.IsSelected = swatch.Hex == AccentColor;
    }

    [RelayCommand]
    private void SetAccent(string hex)
    {
        if (AccentSwatches.Any(swatch => swatch.Hex == hex)) AccentColor = hex;
    }

    [RelayCommand]
    private async Task UploadProfilePictureAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Profile pictures (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", Title = "Choose a profile picture", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TJCServiceDesk", "profiles");
            Directory.CreateDirectory(folder);
            var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            var path = Path.Combine(folder, "profile" + extension);
            System.IO.File.Copy(dialog.FileName, path, true);
            ProfilePicturePath = path;
            await _settings.SetAsync("ProfilePicturePath", path);
            Status = "Profile picture updated.";
        }
        catch (Exception ex) { Status = $"Could not update profile picture: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ChooseMusicFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose your music library folder", InitialDirectory = Directory.Exists(MusicFolder) ? MusicFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        MusicFolder = dialog.FolderName;
        await _settings.SetAsync("MusicFolder", MusicFolder);
        Status = await _music.ScanFolderAsync(MusicFolder) ? "Music library rescanned." : _music.Status;
    }

    [RelayCommand]
    private async Task RescanMusicAsync()
    {
        if (!Directory.Exists(MusicFolder)) { Status = "Choose a valid music folder first."; return; }
        Status = await _music.ScanFolderAsync(MusicFolder) ? "Music library rescanned." : _music.Status;
    }

    [RelayCommand]
    private async Task ChooseGameFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose an additional game folder", InitialDirectory = Directory.Exists(GameFolder) ? GameFolder : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        GameFolder = dialog.FolderName;
        await _settings.SetAsync("GameFolder", GameFolder);
        await RescanGamesAsync();
    }

    [RelayCommand]
    private async Task RescanGamesAsync()
    {
        try
        {
            var count = await _gameService.ScanAsync();
            if (Directory.Exists(GameFolder)) count += await _gameService.ScanFolderAsync(GameFolder);
            await _games.InitializeAsync();
            Status = $"Found or updated {count} games across your libraries.";
        }
        catch (Exception ex) { Status = $"Game rescan failed: {ex.Message}"; }
    }
}
