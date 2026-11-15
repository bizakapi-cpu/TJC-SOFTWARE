using System.IO;
using TagLib;
using TJCServiceDesk.Models;

namespace TJCServiceDesk.Services;

public sealed class MusicService(SqliteDataService data)
{
    private readonly SqliteDataService _data = data;

    public Task<List<Track>> GetTracksAsync(long? playlistId = null) => _data.GetTracksAsync(playlistId);
    public Task<List<Playlist>> GetPlaylistsAsync() => _data.GetPlaylistsAsync();
    public Task<long> CreatePlaylistAsync(string name) => _data.CreatePlaylistAsync(name);
    public Task DeletePlaylistAsync(long id) => _data.DeletePlaylistAsync(id);
    public Task AddTrackAsync(long id, string path) => _data.AddTrackToPlaylistAsync(id, path);
    public Task SaveRecentSongsAsync(IEnumerable<MusicSearchResult> songs, CancellationToken cancellationToken = default) => _data.SaveRecentSongsAsync(songs, cancellationToken);
    public Task<List<MusicSearchResult>> GetRecentSongsAsync(CancellationToken cancellationToken = default) => _data.GetRecentSongsAsync(cancellationToken: cancellationToken);
    public Task SetFavoriteAsync(MusicSearchResult song, bool isFavorite, CancellationToken cancellationToken = default) => _data.SetFavoriteSongAsync(song, isFavorite, cancellationToken);
    public Task<List<MusicSearchResult>> GetFavoritesAsync(CancellationToken cancellationToken = default) => _data.GetFavoriteSongsAsync(cancellationToken);

    public async Task<(int Count, string? FirstError)> ScanAsync(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Music folder does not exist: {folder}");
        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(p => new[] { ".mp3", ".flac", ".wav" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).ToList();
        var tracks = new List<Track>();
        string? firstError = null;
        var coverFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TJCServiceDesk", "covers");
        Directory.CreateDirectory(coverFolder);
        foreach (var path in files)
        {
            try
            {
                using var media = TagLib.File.Create(path);
                var title = string.IsNullOrWhiteSpace(media.Tag.Title) ? Path.GetFileNameWithoutExtension(path) : media.Tag.Title;
                var artist = media.Tag.FirstPerformer ?? "Unknown artist";
                var album = media.Tag.Album ?? "Unknown album";
                var cover = "";
                if (media.Tag.Pictures.Length > 0)
                {
                    cover = Path.Combine(coverFolder, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))) + ".jpg");
                    if (!System.IO.File.Exists(cover)) await System.IO.File.WriteAllBytesAsync(cover, media.Tag.Pictures[0].Data.Data);
                }
                tracks.Add(new Track { Path = path, Title = title, Artist = artist, Album = album, CoverPath = cover, DurationSeconds = media.Properties.Duration.TotalSeconds });
            }
            catch (Exception ex) { firstError ??= $"Skipped '{Path.GetFileName(path)}': {ex.Message}"; }
        }
        await _data.SaveTracksAsync(tracks);
        return (tracks.Count, firstError);
    }
}
