namespace TJCServiceDesk.Models;

public sealed class Track
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "Unknown title";
    public string Artist { get; set; } = "Unknown artist";
    public string Album { get; set; } = "Unknown album";
    public string CoverPath { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string Duration => TimeSpan.FromSeconds(Math.Max(0, DurationSeconds)).ToString(@"m\:ss");
}

public sealed class Playlist
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}
