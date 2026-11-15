using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TJCServiceDesk.Models;

namespace TJCServiceDesk.Services;

public sealed class GameService(SqliteDataService data)
{
    private readonly SqliteDataService _data = data;
    private static readonly HttpClient Http = new();

    public Task<List<Game>> GetGamesAsync() => _data.GetGamesAsync();

    public async Task<int> ScanAsync()
    {
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (!string.IsNullOrWhiteSpace(steam))
        {
            steam = steam.Replace('/', Path.DirectorySeparatorChar);
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (System.IO.File.Exists(vdf))
            {
                var text = await System.IO.File.ReadAllTextAsync(vdf);
                foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"")) libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
            }
            foreach (var library in libraries)
            {
                var manifests = Path.Combine(library, "steamapps");
                if (!Directory.Exists(manifests)) continue;
                foreach (var file in Directory.EnumerateFiles(manifests, "appmanifest_*.acf"))
                {
                    var data = await System.IO.File.ReadAllTextAsync(file);
                    var id = Regex.Match(data, "\\\"appid\\\"\\s+\\\"?(\\d+)\\\"?").Groups[1].Value;
                    var name = Regex.Match(data, "\\\"name\\\"\\s+\\\"([^\\\"]+)\\\"").Groups[1].Value;
                    if (id.Length == 0 || name.Length == 0) continue;
                    var cover = await CacheSteamCoverAsync(id);
                    games["steam:" + id] = new Game { Name = name, AppId = id, ExecutablePath = "steam://rungameid/" + id, CoverPath = cover, Source = "Steam" };
                }
            }
        }

        var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(epic))
        {
            foreach (var file in Directory.EnumerateFiles(epic, "*.item"))
            {
                try
                {
                    using var document = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(file));
                    var root = document.RootElement;
                    var name = ReadJson(root, "DisplayName") ?? ReadJson(root, "AppName") ?? Path.GetFileNameWithoutExtension(file);
                    var install = ReadJson(root, "InstallLocation") ?? "";
                    var launch = ReadJson(root, "LaunchExecutable") ?? "";
                    var executable = Path.IsPathRooted(launch) ? launch : Path.Combine(install, launch.Replace('/', Path.DirectorySeparatorChar));
                    var iconSource = ReadJson(root, " canRunOffline") ?? "";
                    var cover = ExtractIcon(executable, "epic-" + (ReadJson(root, "AppName") ?? name));
                    if (executable.Length > 0) games["epic:" + executable] = new Game { Name = name, ExecutablePath = executable, CoverPath = cover, Source = "Epic Games" };
                }
                catch (JsonException) { }
            }
        }
        await _data.SaveGamesAsync(games.Values);
        return games.Count;
    }

    public async Task<int> ScanFolderAsync(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Game folder does not exist: {folder}");
        var games = new List<Game>();
        foreach (var executable in Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(executable);
            if (fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || fileName.Contains("crash", StringComparison.OrdinalIgnoreCase)) continue;
            games.Add(new Game
            {
                Name = Path.GetFileNameWithoutExtension(executable),
                ExecutablePath = executable,
                CoverPath = ExtractIcon(executable, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(executable)))),
                Source = "Folder"
            });
        }
        await _data.SaveGamesAsync(games);
        return games.Count;
    }

    public async Task<Game> AddLocalAsync(string executable)
    {
        if (!System.IO.File.Exists(executable)) throw new FileNotFoundException("Selected game executable was not found.", executable);
        var game = new Game { Name = Path.GetFileNameWithoutExtension(executable), ExecutablePath = executable, CoverPath = ExtractIcon(executable, Guid.NewGuid().ToString("N")), Source = "Local" };
        await _data.SaveGamesAsync(new[] { game });
        return game;
    }

    public async Task SetCustomCoverAsync(Game game, string selectedPath)
    {
        if (!System.IO.File.Exists(selectedPath)) throw new FileNotFoundException("The selected cover file could not be found.", selectedPath);
        var extension = Path.GetExtension(selectedPath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".ico" or ".exe"))
            throw new InvalidDataException("Choose a PNG, JPG, ICO, or executable file.");

        var cacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TJCServiceDesk", "game-covers");
        Directory.CreateDirectory(cacheFolder);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(game.ExecutablePath)));
        string persistedPath;
        if (extension == ".ico")
        {
            persistedPath = Path.Combine(cacheFolder, "custom-" + key + ".png");
            using var icon = new Icon(selectedPath);
            using var bitmap = icon.ToBitmap();
            bitmap.Save(persistedPath, ImageFormat.Png);
        }
        else if (extension == ".exe")
        {
            persistedPath = ExtractIcon(selectedPath, "custom-" + key);
            if (string.IsNullOrEmpty(persistedPath)) throw new InvalidOperationException("Could not extract an icon from the selected executable.");
        }
        else
        {
            persistedPath = Path.Combine(cacheFolder, "custom-" + key + extension);
            System.IO.File.Copy(selectedPath, persistedPath, true);
        }

        await _data.SetGameCustomCoverAsync(game.ExecutablePath, persistedPath);
    }

    public void Launch(Game game)
    {
        if (game.Source == "Steam" && game.AppId.Length > 0)
            Process.Start(new ProcessStartInfo("steam://rungameid/" + game.AppId) { UseShellExecute = true });
        else if (System.IO.File.Exists(game.ExecutablePath))
            Process.Start(new ProcessStartInfo(game.ExecutablePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath) ?? "" });
        else throw new FileNotFoundException($"Game executable was not found: {game.ExecutablePath}");
    }

    private static string? ReadJson(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<string> CacheSteamCoverAsync(string id)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TJCServiceDesk", "game-covers");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, id + ".jpg");
        if (System.IO.File.Exists(path)) return path;
        try
        {
            using var response = await Http.GetAsync($"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/header.jpg");
            if (!response.IsSuccessStatusCode) return "";
            await System.IO.File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync());
            return path;
        }
        catch (HttpRequestException) { return ""; }
    }

    private static string ExtractIcon(string executable, string key)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TJCServiceDesk", "game-covers");
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, key + ".png");
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(executable);
            if (icon is null) return "";
            using var bitmap = icon.ToBitmap();
            bitmap.Save(output, ImageFormat.Png);
            return output;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return ""; }
    }
}
