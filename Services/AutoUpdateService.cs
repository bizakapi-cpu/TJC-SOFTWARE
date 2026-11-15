using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TJCServiceDesk.Services;

public sealed class AutoUpdateService
{
    public const string VersionManifestUrl = "https://raw.githubusercontent.com/bizakapi-cpu/TJC-SOFTWARE/main/version.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Version CurrentVersion => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);
    public bool IsConfigured => Uri.TryCreate(VersionManifestUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !VersionManifestUrl.Contains("OWNER/REPOSITORY", StringComparison.OrdinalIgnoreCase);

    public async Task<UpdateManifest?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            Debug.WriteLine("Auto-update is not configured. Set AutoUpdateService.VersionManifestUrl to the published version.json URL.");
            return null;
        }

        using var response = await Http.GetAsync(VersionManifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var manifest = await response.Content.ReadFromJsonAsync<UpdateManifest>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The update manifest is empty or invalid.");

        if (!Version.TryParse(manifest.ResolvedVersion, out var latestVersion))
            throw new InvalidDataException("The update manifest version must be a valid numeric version, such as 1.2.3.");
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The update manifest downloadUrl must be an absolute HTTPS URL.");

        return latestVersion > CurrentVersion ? manifest : null;
    }

    public async Task StartUpdateAndRestartAsync(UpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The update download URL is not a valid HTTPS URL.");

        var latestVersion = Version.TryParse(manifest.ResolvedVersion, out var parsedVersion) ? parsedVersion : null;
        if (latestVersion is null || latestVersion <= CurrentVersion)
            throw new InvalidDataException("The update version is invalid or is not newer than the installed version.");

        var targetExecutable = Environment.ProcessPath;
        var expectedExecutableName = $"{Assembly.GetEntryAssembly()?.GetName().Name ?? "TJC Service Desk"}.exe";
        if (string.IsNullOrWhiteSpace(targetExecutable) || !File.Exists(targetExecutable)
            || !string.Equals(Path.GetFileName(targetExecutable), expectedExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The application must be running from its own executable before it can update.");

        var updateDirectory = Path.Combine(Path.GetTempPath(), "TJCServiceDesk", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateDirectory);
        var stagedExecutable = Path.Combine(updateDirectory, Path.GetFileName(targetExecutable));
        var scriptPath = Path.Combine(updateDirectory, "apply-update.cmd");

        using (var response = await Http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(stagedExecutable, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await source.CopyToAsync(target, cancellationToken);
        }

        await ValidateExecutableAsync(stagedExecutable, cancellationToken);
        await File.WriteAllTextAsync(scriptPath, UpdateScript, cancellationToken);

        var process = Process.GetCurrentProcess();
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = updateDirectory
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add($"\"{scriptPath}\"");
        startInfo.Environment["TJC_PARENT_PID"] = process.Id.ToString();
        startInfo.Environment["TJC_UPDATE_FILE"] = stagedExecutable;
        startInfo.Environment["TJC_TARGET_EXE"] = targetExecutable;
        startInfo.Environment["TJC_UPDATE_DIR"] = updateDirectory;

        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("Could not start the update helper process.");
    }

    private static async Task ValidateExecutableAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length < 2) throw new InvalidDataException("The downloaded update is empty.");

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 2, useAsync: true);
        var signature = new byte[2];
        var bytesRead = await stream.ReadAsync(signature, cancellationToken);
        if (bytesRead != 2 || signature[0] != (byte)'M' || signature[1] != (byte)'Z')
            throw new InvalidDataException("The downloaded update is not a valid Windows executable.");
    }

    private const string UpdateScript = "@echo off\r\nsetlocal\r\n" +
        ":wait_for_app\r\n" +
        "tasklist /FI \"PID eq %TJC_PARENT_PID%\" /NH | findstr /C:\"%TJC_PARENT_PID%\" >nul\r\n" +
        "if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait_for_app)\r\n" +
        "set /a attempts=0\r\n" +
        "copy /Y \"%TJC_TARGET_EXE%\" \"%TJC_TARGET_EXE%.bak\" >nul\r\n" +
        ":replace_app\r\n" +
        "copy /Y \"%TJC_UPDATE_FILE%\" \"%TJC_TARGET_EXE%\" >nul\r\n" +
        "if errorlevel 1 (set /a attempts+=1 & if %attempts% LSS 30 (timeout /t 1 /nobreak >nul & goto replace_app) & copy /Y \"%TJC_TARGET_EXE%.bak\" \"%TJC_TARGET_EXE%\" >nul & exit /b 1)\r\n" +
        "del /Q \"%TJC_TARGET_EXE%.bak\" >nul 2>nul\r\n" +
        "start \"\" \"%TJC_TARGET_EXE%\"\r\n" +
        "del /Q \"%TJC_UPDATE_FILE%\" >nul 2>nul\r\n" +
        "del /Q \"%~f0\" >nul 2>nul\r\n" +
        "endlocal\r\n";
}

public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("latestVersion")] public string LatestVersion { get; set; } = "";
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("releaseNotes")] public string ReleaseNotes { get; set; } = "";
    [JsonIgnore] public string ResolvedVersion => string.IsNullOrWhiteSpace(Version) ? LatestVersion : Version;
}
