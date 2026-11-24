using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiberaFx.Services;

public sealed record AppUpdateInfo(
    string TagName,
    string Version,
    string ReleaseNotes,
    string ReleaseUrl,
    string? AssetName,
    string? AssetDownloadUrl,
    bool IsPrerelease,
    bool IsUpdateAvailable,
    bool IsSkipped);

public sealed class AppUpdateService
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/Libra-Works/LibraFX/releases?per_page=10";
    private readonly string _settingsPath;
    private bool _autoCheckUpdates = true;
    private string? _skippedVersion;

    public static AppUpdateService Shared { get; } = new(
        Path.Combine(AppDataDirectory.Root, "LiberaFx", "update-settings.json"));

    internal AppUpdateService(string settingsPath)
    {
        _settingsPath = settingsPath;
        LoadSettings();
    }

    public bool AutoCheckUpdates
    {
        get => _autoCheckUpdates;
        set
        {
            if (_autoCheckUpdates == value) return;
            _autoCheckUpdates = value;
            SaveSettings();
        }
    }

    public string? SkippedVersion => _skippedVersion;

    public void SkipVersion(string version)
    {
        _skippedVersion = NormalizeVersion(version);
        SaveSettings();
    }

    public void ClearSkippedVersion()
    {
        _skippedVersion = null;
        SaveSettings();
    }

    public static string GetCurrentVersion()
    {
        var assembly = typeof(App).Assembly;
        var raw = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        return raw.Split('+')[0];
    }

    public async Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LiberaFx", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var json = await client.GetStringAsync(ReleasesApiUrl, cancellationToken);
        var current = GetCurrentVersion();
        var includePrerelease = current.Contains('-', StringComparison.Ordinal);
        return ParseReleasesJson(json, current, _skippedVersion, includePrerelease);
    }

    internal static AppUpdateInfo? ParseReleasesJson(
        string json,
        string currentVersion,
        string? skippedVersion,
        bool includePrerelease = true)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;
            var isPrerelease = release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
            if (isPrerelease && !includePrerelease)
                continue;

            var tagName = release.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
            var version = NormalizeVersion(tagName);
            if (string.IsNullOrWhiteSpace(version)) continue;

            var notes = release.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
            var htmlUrl = release.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
            var (assetName, assetUrl) = SelectPlatformAsset(release);
            var available = CompareVersions(currentVersion, version) > 0;
            var skipped = !string.IsNullOrWhiteSpace(skippedVersion) &&
                          string.Equals(NormalizeVersion(skippedVersion), version, StringComparison.OrdinalIgnoreCase);

            return new AppUpdateInfo(
                tagName, version, notes, htmlUrl, assetName, assetUrl, isPrerelease, available, skipped);
        }

        return null;
    }

    private static (string? Name, string? Url) SelectPlatformAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null);

        var ridHint = OperatingSystem.IsWindows() ? "win-x64"
            : OperatingSystem.IsMacOS() ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
            : "linux-x64";

        string? fallbackName = null;
        string? fallbackUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            if (name.Contains("LiberaFX", StringComparison.OrdinalIgnoreCase) &&
                name.Contains(ridHint, StringComparison.OrdinalIgnoreCase))
            {
                return (name, url);
            }
            if (fallbackName is null && name.Contains(ridHint, StringComparison.OrdinalIgnoreCase))
            {
                fallbackName = name;
                fallbackUrl = url;
            }
        }
        return (fallbackName, fallbackUrl);
    }

    internal static string NormalizeVersion(string raw) => raw.Trim().TrimStart('v', 'V');

    internal static int CompareVersions(string currentRaw, string candidateRaw)
    {
        var (cNums, cPre) = ParseVersionParts(currentRaw);
        var (nNums, nPre) = ParseVersionParts(candidateRaw);
        var max = Math.Max(cNums.Length, nNums.Length);
        for (var i = 0; i < max; i++)
        {
            var a = i < cNums.Length ? cNums[i] : 0;
            var b = i < nNums.Length ? nNums[i] : 0;
            if (a != b) return b.CompareTo(a);
        }
        if (string.IsNullOrEmpty(cPre) && !string.IsNullOrEmpty(nPre)) return -1;
        if (!string.IsNullOrEmpty(cPre) && string.IsNullOrEmpty(nPre)) return 1;
        return string.Compare(nPre, cPre, StringComparison.OrdinalIgnoreCase);
    }

    private static (int[] Numbers, string Pre) ParseVersionParts(string raw)
    {
        var clean = NormalizeVersion(raw).Split('+')[0];
        var dash = clean.IndexOf('-');
        var core = dash >= 0 ? clean[..dash] : clean;
        var pre = dash >= 0 ? clean[(dash + 1)..] : string.Empty;
        var numbers = Regex.Matches(core, @"\d+")
            .Select(m => int.TryParse(m.Value, out var v) ? v : 0)
            .ToArray();
        return (numbers, pre);
    }

    public async Task<bool> DownloadAndApplyUpdateAsync(AppUpdateInfo info, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(info.AssetDownloadUrl) || string.IsNullOrWhiteSpace(info.AssetName))
        {
            OpenReleasePage(info.ReleaseUrl);
            return false;
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), "liberafx-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(stagingRoot);
        var downloadPath = Path.Combine(stagingRoot, Path.GetFileName(info.AssetName));
        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LiberaFx", "1.0"));
            await using var remote = await client.GetStreamAsync(info.AssetDownloadUrl, cancellationToken);
            await using var local = File.Create(downloadPath);
            await remote.CopyToAsync(local, cancellationToken);
        }

        if (downloadPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(downloadPath) { UseShellExecute = true });
            return true;
        }

        var extractDir = Path.Combine(stagingRoot, "extracted");
        ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
        var sourceDir = Directory.GetDirectories(extractDir) is [var singleSub] &&
                        !File.Exists(Path.Combine(extractDir, "LiberaFx.exe"))
            ? singleSub
            : extractDir;

        var targetDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var exeName = Path.GetFileName(Environment.ProcessPath ?? Path.Combine(targetDir, "LiberaFx.exe"));
        var scriptPath = CreateApplyScript(stagingRoot, sourceDir, targetDir, Environment.ProcessId, exeName);
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return true;
        }

        OpenReleasePage(info.ReleaseUrl);
        return false;
    }

    internal static string CreateApplyScript(
        string stagingRoot, string sourceDir, string targetDir, int pid, string exeName)
    {
        var scriptPath = Path.Combine(stagingRoot, "apply-update.cmd");
        var exePath = Path.Combine(targetDir, exeName);
        var script = $"""
            @echo off
            :waitloop
            tasklist /FI "PID eq {pid}" 2>NUL | find /I "{pid}" >NUL
            if not errorlevel 1 (
              timeout /t 1 /nobreak >NUL
              goto waitloop
            )
            xcopy "{sourceDir}\*" "{targetDir}\" /E /Y /I /Q >NUL
            start "" "{exePath}"
            """;
        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    public static void OpenReleasePage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            if (doc.RootElement.TryGetProperty("autoCheckUpdates", out var auto) &&
                auto.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _autoCheckUpdates = auto.GetBoolean();
            }
            if (doc.RootElement.TryGetProperty("skippedVersion", out var skip) &&
                skip.ValueKind == JsonValueKind.String)
            {
                _skippedVersion = skip.GetString();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var json = JsonSerializer.Serialize(new
            {
                autoCheckUpdates = _autoCheckUpdates,
                skippedVersion = _skippedVersion
            });
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
