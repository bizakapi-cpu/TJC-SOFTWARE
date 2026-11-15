using System.Runtime.CompilerServices;
using TJCServiceDesk.Models;
using YoutubeExplode;

namespace TJCServiceDesk.Services;

public sealed class MusicSearchService
{
    private readonly YoutubeClient _youtube = new();

    public async Task<IReadOnlyList<MusicSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<MusicSearchResult>();

        var results = new List<MusicSearchResult>(20);
        await foreach (var video in _youtube.Search.GetVideosAsync(query, cancellationToken).WithCancellation(cancellationToken))
        {
            var thumbnail = video.Thumbnails.LastOrDefault()?.Url ?? "";
            results.Add(new MusicSearchResult
            {
                SongId = $"youtube:{video.Id.Value}",
                Provider = "YouTube",
                Title = video.Title,
                Artist = video.Author.ChannelTitle,
                DurationSeconds = video.Duration?.TotalSeconds ?? 0,
                ThumbnailUrl = thumbnail,
                Url = video.Url,
                SearchQuery = query
            });
            if (results.Count == 20) break;
        }
        return results;
    }
}
