using LiberaFx.Services;

internal static class AppUpdateServiceTests
{
    public static void Run()
    {
        Require(AppUpdateService.CompareVersions("2026.11.1", "2026.11.2") > 0, "Newer patch version");
        Require(AppUpdateService.CompareVersions("2026.11.2", "2026.11.2") == 0, "Equal version");
        Require(AppUpdateService.CompareVersions("2026.11.3", "2026.11.2") < 0, "Older version");
        Require(AppUpdateService.CompareVersions("2026.11.2-preview", "2026.11.2") > 0, "Stable newer than prerelease");

        const string json = """
        [
          {
            "tag_name": "v2026.12.0",
            "draft": false,
            "prerelease": false,
            "body": "## Changes\n- Added Reroute",
            "html_url": "https://github.com/Libra-Works/LibraFX/releases/tag/v2026.12.0",
            "assets": [
              {
                "name": "LiberaFX-win-x64-2026.12.0.zip",
                "browser_download_url": "https://example.invalid/LiberaFX-win-x64-2026.12.0.zip"
              }
            ]
          }
        ]
        """;

        var tempDir = Path.Combine(Path.GetTempPath(), "lfx-update-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsPath = Path.Combine(tempDir, "update-settings.json");
            var service = new AppUpdateService(settingsPath);
            Require(service.AutoCheckUpdates, "AutoCheckUpdates should default to true.");
            service.AutoCheckUpdates = false;
            service.SkipVersion("2026.12.0");

            var reloaded = new AppUpdateService(settingsPath);
            Require(!reloaded.AutoCheckUpdates, "AutoCheckUpdates preference did not persist.");
            Require(reloaded.SkippedVersion == "2026.12.0", "SkippedVersion did not persist.");

            var parsed = AppUpdateService.ParseReleasesJson(json, "2026.11.99", reloaded.SkippedVersion);
            Require(parsed is { IsUpdateAvailable: true, IsSkipped: true, Version: "2026.12.0" } &&
                    parsed.ReleaseNotes.Contains("Added Reroute", StringComparison.Ordinal),
                "Release parsing/skip flag mismatch.");

            var script = AppUpdateService.CreateApplyScript(tempDir, Path.Combine(tempDir, "src"),
                Path.Combine(tempDir, "dst"), 12345, "LiberaFx.exe");
            Require(File.Exists(script) && File.ReadAllText(script).Contains("LiberaFx.exe", StringComparison.Ordinal),
                "Update apply script was not generated.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
