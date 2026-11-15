using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TJCServiceDesk.Models;
using TJCServiceDesk.Services;

namespace TJCServiceDesk.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AudioPlayerService _player;
    private readonly DispatcherTimer _progressTimer;
    private readonly DashboardViewModel _dashboard;
    private readonly MusicViewModel _music;
    private readonly GamesViewModel _games;
    private readonly ConnectViewModel _connect;
    private readonly SettingsViewModel _settings;
    [ObservableProperty] private object? currentViewModel;
    [ObservableProperty] private bool isPlayerVisible;
    [ObservableProperty] private string playerTitle = "";
    [ObservableProperty] private string playerArtist = "";
    [ObservableProperty] private string playerCover = "";
    [ObservableProperty] private string playPauseGlyph = "▶";
    [ObservableProperty] private double playbackProgress;
    [ObservableProperty] private double volume = 0.7;

    public MusicViewModel Music => _music;
    public ConnectViewModel Connect => _connect;
    public SettingsViewModel Settings => _settings;

    public MainViewModel(DashboardViewModel dashboard, MusicViewModel music, GamesViewModel games, ConnectViewModel connect, SettingsViewModel settings, AudioPlayerService player)
    {
        _dashboard = dashboard; _music = music; _games = games; _connect = connect; _settings = settings; _player = player;
        _music.TrackStarted += OnTrackStarted;
        _music.PlaylistOpened += () => Navigate("Music");
        _progressTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { if (IsPlayerVisible && _player.Duration.TotalSeconds > 0) PlaybackProgress = _player.Position.TotalSeconds / _player.Duration.TotalSeconds * 100; PlayPauseGlyph = _player.IsPlaying ? "❚❚" : "▶"; }, System.Windows.Application.Current.Dispatcher);
        _progressTimer.Start();
        Navigate("Dashboard");
    }

    public async Task InitializeAsync()
    {
        await _settings.InitializeAsync();
        await Task.WhenAll(_music.InitializeAsync(), _games.InitializeAsync(), _connect.InitializeAsync());
    }

    public async Task StopAsync()
    {
        _progressTimer.Stop();
        _dashboard.SetActive(false);
        await _connect.StopAsync();
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        _dashboard.SetActive(false);
        CurrentViewModel = page switch
        {
            "Music" => _music, "Games" => _games, "Connect" => _connect, "Settings" => _settings, _ => _dashboard
        };
        _dashboard.SetActive(ReferenceEquals(CurrentViewModel, _dashboard));
        _connect.SetActive(ReferenceEquals(CurrentViewModel, _connect));
    }

    private void OnTrackStarted(Track track)
    {
        PlayerTitle = track.Title; PlayerArtist = track.Artist; PlayerCover = track.CoverPath; PlaybackProgress = 0; IsPlayerVisible = true; PlayPauseGlyph = "❚❚";
    }

    [RelayCommand]
    private void TogglePlayback() { _player.Toggle(); PlayPauseGlyph = _player.IsPlaying ? "❚❚" : "▶"; }
    [RelayCommand]
    private void NextTrack() => _music.PlayRelative(1);
    [RelayCommand]
    private void PreviousTrack() => _music.PlayRelative(-1);

    partial void OnVolumeChanged(double value) => _player.Volume = (float)value;
    partial void OnPlaybackProgressChanged(double value)
    {
        if (IsPlayerVisible && _player.Duration.TotalSeconds > 0 && Math.Abs(_player.Position.TotalSeconds / _player.Duration.TotalSeconds * 100 - value) > 2)
            _player.Seek(TimeSpan.FromSeconds(_player.Duration.TotalSeconds * value / 100));
    }
}
