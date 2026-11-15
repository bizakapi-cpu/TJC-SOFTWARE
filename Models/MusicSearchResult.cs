using CommunityToolkit.Mvvm.ComponentModel;

namespace TJCServiceDesk.Models;

public sealed partial class MusicSearchResult : ObservableObject
{
    public string SongId { get; set; } = "";
    public string Provider { get; set; } = "YouTube";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string ThumbnailUrl { get; set; } = "";
    public string Url { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string SearchQuery { get; set; } = "";
    public string Duration => DurationSeconds <= 0 ? "—" : DurationSeconds >= 3600
        ? TimeSpan.FromSeconds(DurationSeconds).ToString(@"h\:mm\:ss")
        : TimeSpan.FromSeconds(DurationSeconds).ToString(@"m\:ss");
    public bool IsLocal => !string.IsNullOrWhiteSpace(LocalPath);

    [ObservableProperty]
    private bool isFavorite;
}
