using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace StarRailCompanion;

internal sealed record AvailableUpdate(Version Version, string Tag, Uri DownloadUri, Uri ChecksumUri);

internal static class UpdateService
{
    private const string Repository = "greyfoxdevelopment/GFDev-StarRail-Companion";
    private const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const string ExecutableAssetName = "StarRailCompanion.exe";
    private const string ChecksumAssetName = ExecutableAssetName + ".sha256";

    // Release assets must live under THIS repository's release-download path on github.com.
    private const string DownloadPathPrefix = "/" + Repository + "/releases/download/";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StarRailCompanion");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    internal static async Task<AvailableUpdate?> GetAvailableUpdateAsync(
        Version currentVersion,
        Version? expectedVersion = null)
    {
        using HttpResponseMessage response = await Http.GetAsync(LatestReleaseUrl);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        GitHubRelease? release = await response.Content.ReadFromJsonAsync<GitHubRelease>();
        if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            throw new InvalidDataException("GitHub returned release data without a version tag.");

        string tag = release.TagName.Trim();

        // Accept both "1.2.3" and "v1.2.3" tags.
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? releaseVersion))
            throw new InvalidDataException($"GitHub release tag '{tag}' is not a valid version.");

        if (expectedVersion != null &&
            NormalizeVersion(releaseVersion).CompareTo(NormalizeVersion(expectedVersion)) != 0)
        {
            throw new InvalidDataException(
                $"The release server announced version {expectedVersion}, but GitHub's latest release is {tag}. No update was installed.");
        }

        if (NormalizeVersion(releaseVersion).CompareTo(NormalizeVersion(currentVersion)) <= 0) return null;

        Uri downloadUri = FindAssetUri(release, ExecutableAssetName,
            $"Release {tag} does not contain a valid {ExecutableAssetName} download.");

        // Fail closed: no checksum file, no update.
        Uri checksumUri = FindAssetUri(release, ChecksumAssetName,
            $"Release {tag} has no {ChecksumAssetName} file, so the download can't be verified. Nothing was installed.");

        return new AvailableUpdate(releaseVersion, tag, downloadUri, checksumUri);
    }

    private static Uri FindAssetUri(GitHubRelease release, string assetName, string errorMessage)
    {
        GitHubAsset? asset = Array.Find(release.Assets ?? Array.Empty<GitHubAsset>(), candidate =>
            string.Equals(candidate.Name, assetName, StringComparison.Ordinal));

        if (asset == null ||
            !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out Uri? uri) ||
            !IsTrustedDownloadUri(uri))
        {
            throw new InvalidDataException(errorMessage);
        }

        return uri;
    }

    private static bool IsTrustedDownloadUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith(DownloadPathPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads the update, verifies its SHA-256 against the release's checksum file, and returns the temp path.</summary>
    internal static async Task<string> DownloadAsync(AvailableUpdate update)
    {
        string downloadedPath = Path.Combine(Path.GetTempPath(), $"StarRailCompanion-{Guid.NewGuid():N}.exe");
        bool completed = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            // 1) Expected hash first (tiny file) so a bad release fails fast.
            string checksumText = await Http.GetStringAsync(update.ChecksumUri, timeout.Token);
            if (checksumText.Length > 4096)
                throw new InvalidDataException("The checksum file is unexpectedly large.");
            string expectedHash = ParseSha256(checksumText);

            // 2) The executable itself.
            using HttpResponseMessage response = await Http.GetAsync(
                update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();

            await using (var destination = new FileStream(
                downloadedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (Stream source = await response.Content.ReadAsStreamAsync(timeout.Token))
            {
                await source.CopyToAsync(destination, timeout.Token);
            }

            if (new FileInfo(downloadedPath).Length == 0)
                throw new InvalidDataException("The downloaded update is empty.");

            // 3) Verify before anything can run it.
            string actualHash;
            await using (var verify = new FileStream(downloadedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verify, timeout.Token));
            }

            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The downloaded update failed its SHA-256 check and was discarded. Nothing was installed.");

            completed = true;
            return downloadedPath;
        }
        finally
        {
            if (!completed && File.Exists(downloadedPath))
                File.Delete(downloadedPath);
        }
    }

    /// <summary>Accepts "hash" or "hash  filename" (sha256sum style) and returns the 64-char hex hash.</summary>
    private static string ParseSha256(string text)
    {
        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 64 && token.All(Uri.IsHexDigit)) return token;
        }
        throw new InvalidDataException("The checksum file doesn't contain a valid SHA-256 hash.");
    }

    internal static void InstallWhenClosed(string downloadedPath, int processId, string executablePath)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), $"StarRailCompanion-update-{Guid.NewGuid():N}.ps1");
        string script = string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            "try {",
            $"    $running = Get-Process -Id {processId} -ErrorAction SilentlyContinue",
            "    if ($running) { $running.WaitForExit() }",
            $"    Move-Item -LiteralPath {QuoteForPowerShell(downloadedPath)} -Destination {QuoteForPowerShell(executablePath)} -Force",
            $"    Start-Process -FilePath {QuoteForPowerShell(executablePath)}",
            "} catch {",
            "    Add-Type -AssemblyName System.Windows.Forms",
            "    [System.Windows.Forms.MessageBox]::Show(",
            "        \"Update failed: \" + $_.Exception.Message,",
            "        \"Star Rail Companion update\",",
            "        [System.Windows.Forms.MessageBoxButtons]::OK,",
            "        [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null",
            "} finally {",
            "    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue",
            "}");
        File.WriteAllText(scriptPath, script);

        string powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        var startInfo = new ProcessStartInfo(powershellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        using Process? updater = Process.Start(startInfo);
        if (updater == null)
            throw new Win32Exception("Could not start the update installer.");
    }

    private static string QuoteForPowerShell(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; set; } = Array.Empty<GitHubAsset>();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = string.Empty;
    }
}
