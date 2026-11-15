using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TJCServiceDesk.Models;
using TJCServiceDesk.Services;
using Microsoft.Win32;

namespace TJCServiceDesk.ViewModels;

public partial class MusicViewModel : ObservableObject
{
    private readonly MusicService _music;
    private readonly MusicSearchService _searchService;
    private readonly AudioPlayerService _player;
    private readonly SettingsService _settings;
    private CancellationTokenSource? _searchCancellation;
    private readonly HashSet<string> _favoriteIds = new(StringComparer.Ordinal);
    public ObservableCollection<Track> Tracks { get; } = new();
    public ObservableCollection<Playlist> Playlists { get; } = new();
    public ObservableCollection<MusicSearchResult> SearchResults { get; } = new();
    public ObservableCollection<MusicSearchResult> RecentSongs { get; } = new();
    public ObservableCollection<MusicSearchResult> FavoriteSongs { get; } = new();
    public ObservableCollection<string> RecentSearches { get; } = new();
    public ICollectionView FilteredTracks { get; }
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string searchQuery = "";
    [ObservableProperty] private string currentSection = "Library";
    [ObservableProperty] private bool isSearching;
    [ObservableProperty] private bool isSearchHistoryOpen;
    public bool CanSearch => !IsSearching;
    [ObservableProperty] private string status = "Scan a folder to add your music.";
    [ObservableProperty] private string newPlaylistName = "";
    [ObservableProperty] private Playlist? selectedPlaylist;
    [ObservableProperty] private Track? selectedTrack;
    public event Action<Track>? TrackStarted;
    public event Action? PlaylistOpened;

    public MusicViewModel(MusicService music, MusicSearchService searchService, AudioPlayerService player, SettingsService settings)
    {
        _music = music; _searchService = searchService; _player = player; _settings = settings;
        FilteredTracks = CollectionViewSource.GetDefaultView(Tracks);
        FilteredTracks.Filter = item => item is Track track && (string.IsNullOrWhiteSpace(SearchText) || $"{track.Title} {track.Artist} {track.Album}".Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSearchTextChanged(string value) => FilteredTracks.Refresh();
    partial void OnSearchQueryChanged(string value) => RefreshRecentSearches();
    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(CanSearch));

    public void ShowSearchHistory()
    {
        RefreshRecentSearches();
        IsSearchHistoryOpen = RecentSearches.Count > 0;
    }

    public async Task InitializeAsync()
    {
        await ReloadAsync();
        var folder = await _settings.GetAsync("MusicFolder");
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) Status = $"Music folder: {folder}";
    }

    [RelayCommand]
    private async Task SearchYouTubeAsync()
    {
        var query = SearchQuery.Trim();
        IsSearchHistoryOpen = false;
        if (query.Length == 0) { Status = "Enter a song, artist, or album to search YouTube."; return; }

        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _searchCancellation, cancellation);
        previous?.Cancel();
        IsSearching = true;
        CurrentSection = "Search";
        Status = "Searching YouTube…";
        try
        {
            var results = await _searchService.SearchAsync(query, cancellation.Token);
            foreach (var result in results) result.IsFavorite = _favoriteIds.Contains(result.SongId);
            await _music.SaveRecentSongsAsync(results, cancellation.Token);
            SearchResults.Clear();
            foreach (var result in results) SearchResults.Add(result);
            await LoadRecentAndFavoritesAsync(cancellation.Token);
            Status = results.Count == 0 ? "No YouTube results found." : $"Found {results.Count} YouTube results. Select a result to open its official page.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { Status = $"YouTube search failed: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _searchCancellation, null, cancellation), cancellation)) IsSearching = false;
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private void NavigateSection(string? section)
    {
        if (string.IsNullOrWhiteSpace(section)) return;
        CurrentSection = section;
        if (section == "Recent") _ = LoadRecentAndFavoritesAsync();
        else if (section == "Favorites") _ = LoadRecentAndFavoritesAsync();
    }

    [RelayCommand]
    private async Task SearchHistorySelectedAsync(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        SearchQuery = query;
        IsSearchHistoryOpen = false;
        await SearchYouTubeAsync();
    }

    [RelayCommand]
    private async Task OpenResultAsync(MusicSearchResult? result)
    {
        if (result is null) return;
        try
        {
            if (result.IsLocal)
            {
                var track = Tracks.FirstOrDefault(item => item.Path == result.LocalPath)
                    ?? new Track { Path = result.LocalPath, Title = result.Title, Artist = result.Artist, CoverPath = result.ThumbnailUrl, DurationSeconds = result.DurationSeconds };
                PlayTrack(track);
            }
            else if (Uri.TryCreate(result.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            else throw new InvalidOperationException("The selected result does not have a valid HTTPS link.");

            await _music.SaveRecentSongsAsync(new[] { result });
            await LoadRecentAndFavoritesAsync();
        }
        catch (Exception ex) { Status = $"Could not open '{result.Title}': {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(MusicSearchResult? result)
    {
        if (result is null) return;
        await UpdateFavoriteAsync(result);
    }

    [RelayCommand]
    private async Task ToggleTrackFavoriteAsync(Track? track)
    {
        if (track is null) return;
        await UpdateFavoriteAsync(ToLocalResult(track));
    }

    private async Task UpdateFavoriteAsync(MusicSearchResult song)
    {
        try
        {
            var isFavorite = !song.IsFavorite;
            await _music.SetFavoriteAsync(song, isFavorite);
            song.IsFavorite = isFavorite;
            if (isFavorite) _favoriteIds.Add(song.SongId);
            else _favoriteIds.Remove(song.SongId);
            await LoadRecentAndFavoritesAsync();
            Status = isFavorite ? $"Added '{song.Title}' to favorites." : $"Removed '{song.Title}' from favorites.";
        }
        catch (Exception ex) { Status = $"Could not update favorite: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder containing music", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        await ScanFolderAsync(dialog.FolderName);
    }

    public async Task<bool> ScanFolderAsync(string folder)
    {
        try
        {
            Status = "Scanning music…";
            var result = await _music.ScanAsync(folder);
            await _settings.SetAsync("MusicFolder", folder);
            await ReloadAsync();
            Status = result.FirstError is null ? $"Added or updated {result.Count} tracks." : $"Added {result.Count} tracks. {result.FirstError}";
            return true;
        }
        catch (Exception ex) { Status = $"Music scan failed: {ex.Message}"; return false; }
    }

    [RelayCommand]
    private async Task CreatePlaylistAsync()
    {
        var name = NewPlaylistName.Trim();
        if (name.Length == 0) { Status = "Enter a playlist name first."; return; }
        try { await _music.CreatePlaylistAsync(name); NewPlaylistName = ""; await LoadPlaylistsAsync(); Status = $"Playlist '{name}' created."; }
        catch (Exception ex) { Status = $"Could not create playlist: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DeletePlaylistAsync()
    {
        if (SelectedPlaylist is null) return;
        try { await _music.DeletePlaylistAsync(SelectedPlaylist.Id); SelectedPlaylist = null; await LoadPlaylistsAsync(); Status = "Playlist deleted."; }
        catch (Exception ex) { Status = $"Could not delete playlist: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task AddSelectedToPlaylistAsync()
    {
        if (SelectedTrack is null || SelectedPlaylist is null) { Status = "Select a track and playlist first."; return; }
        try { await _music.AddTrackAsync(SelectedPlaylist.Id, SelectedTrack.Path); Status = $"Added '{SelectedTrack.Title}' to {SelectedPlaylist.Name}."; }
        catch (Exception ex) { Status = $"Could not add track: {ex.Message}"; }
    }

    [RelayCommand]
    private void PlaySelected() { if (SelectedTrack is not null) PlayTrack(SelectedTrack); }

    public void PlayTrack(Track track)
    {
        try
        {
            _player.Play(track.Path);
            SelectedTrack = track;
            TrackStarted?.Invoke(track);
            Status = $"Playing {track.Title}";
            _ = RecordLocalPlaybackAsync(track);
        }
        catch (Exception ex) { Status = $"Playback failed: {ex.Message}"; }
    }

    private async Task RecordLocalPlaybackAsync(Track track)
    {
        try
        {
            await _music.SaveRecentSongsAsync(new[] { ToLocalResult(track) });
            await LoadRecentAndFavoritesAsync();
        }
        catch (Exception ex) { Status = $"Could not save recent track: {ex.Message}"; }
    }

    private static MusicSearchResult ToLocalResult(Track track) => new()
    {
        SongId = $"local:{track.Path}", Provider = "Local", Title = track.Title, Artist = track.Artist,
        DurationSeconds = track.DurationSeconds, ThumbnailUrl = track.CoverPath, Url = new Uri(track.Path).AbsoluteUri, LocalPath = track.Path
    };

    public void PlayRelative(int direction)
    {
        if (Tracks.Count == 0) return;
        var index = SelectedTrack is null ? -1 : Tracks.IndexOf(SelectedTrack);
        index = (index + direction + Tracks.Count) % Tracks.Count;
        PlayTrack(Tracks[index]);
    }

    [RelayCommand]
    private async Task OpenPlaylistAsync(long id)
    {
        try
        {
            var tracks = await _music.GetTracksAsync(id); Tracks.Clear(); foreach (var track in tracks) Tracks.Add(track);
            var playlist = Playlists.FirstOrDefault(p => p.Id == id); Status = playlist is null ? "Playlist opened." : $"Showing playlist: {playlist.Name}";
            SearchText = "";
            PlaylistOpened?.Invoke();
        }
        catch (Exception ex) { Status = $"Could not load playlist: {ex.Message}"; }
    }

    public async Task ReloadAsync()
    {
        var tracks = await _music.GetTracksAsync(); Tracks.Clear(); foreach (var track in tracks) Tracks.Add(track);
        await LoadPlaylistsAsync();
        await LoadRecentAndFavoritesAsync();
    }

    private async Task LoadRecentAndFavoritesAsync(CancellationToken cancellationToken = default)
    {
        var favorites = await _music.GetFavoritesAsync(cancellationToken);
        _favoriteIds.Clear();
        foreach (var favorite in favorites) _favoriteIds.Add(favorite.SongId);
        FavoriteSongs.Clear();
        foreach (var favorite in favorites) { favorite.IsFavorite = true; FavoriteSongs.Add(favorite); }
        var recent = await _music.GetRecentSongsAsync(cancellationToken);
        RecentSongs.Clear();
        foreach (var song in recent) { song.IsFavorite = _favoriteIds.Contains(song.SongId); RecentSongs.Add(song); }
        RefreshRecentSearches();
    }

    private void RefreshRecentSearches()
    {
        var query = SearchQuery.Trim();
        var searches = RecentSongs
            .Select(song => song.SearchQuery?.Trim())
            .Where(search => !string.IsNullOrWhiteSpace(search))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(search => query.Length == 0 || search!.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToArray();
        RecentSearches.Clear();
        foreach (var search in searches) RecentSearches.Add(search!);
        if (RecentSearches.Count == 0) IsSearchHistoryOpen = false;
    }

    private async Task LoadPlaylistsAsync()
    {
        var playlists = await _music.GetPlaylistsAsync(); Playlists.Clear(); foreach (var playlist in playlists) Playlists.Add(playlist);
    }
}
