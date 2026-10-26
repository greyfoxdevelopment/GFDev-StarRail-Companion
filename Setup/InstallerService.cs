using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace StarRailCompanionSetup;

internal static class InstallerService
{
    private const string Repository = "greyfoxdevelopment/GFDev-StarRail-Companion";
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const string ExecutableName = "StarRailCompanion.exe";
    private const string ChecksumName = ExecutableName + ".sha256";
    private const long MaximumDownloadSize = 1_073_741_824;

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StarRailCompanionSetup");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    internal static async Task<string> InstallLatestAsync(string destination)
    {
        string fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(fullDestination);

        (Uri executableUri, Uri checksumUri, long expectedSize) = await GetLatestReleaseAssetsAsync();
        if (expectedSize <= 0 || expectedSize > MaximumDownloadSize)
            throw new InvalidDataException("The release executable has an invalid or unsupported size.");

        string expectedHash = await GetExpectedHashAsync(checksumUri);
        string temporaryPath = Path.Combine(fullDestination, $".{ExecutableName}.{Guid.NewGuid():N}.download");
        string installedPath = Path.Combine(fullDestination, ExecutableName);

        try
        {
            await DownloadAndVerifyAsync(executableUri, temporaryPath, expectedSize, expectedHash);
            File.Move(temporaryPath, installedPath, overwrite: true);
            return installedPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async Task<(Uri Executable, Uri Checksum, long Size)> GetLatestReleaseAssetsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await Http.GetAsync(LatestReleaseUrl, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("There are no published releases to install yet.");
        response.EnsureSuccessStatusCode();

        using JsonDocument document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(), cancellationToken: timeout.Token);
        JsonElement root = document.RootElement;
        string tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
            ? tagElement.GetString() ?? string.Empty
            : string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out _))
            throw new InvalidDataException("The latest GitHub release has an invalid version tag.");

        if (!root.TryGetProperty("assets", out JsonElement assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The latest GitHub release does not contain downloadable assets.");
        }

        Uri? executable = null;
        Uri? checksum = null;
        long size = 0;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            if (name != ExecutableName && name != ChecksumName)
                continue;

            Uri uri = GetTrustedAssetUri(asset);
            if (name == ExecutableName)
            {
                executable = uri;
                if (asset.TryGetProperty("size", out JsonElement sizeElement))
                    size = sizeElement.GetInt64();
            }
            else
            {
                checksum = uri;
            }
        }

        if (executable is null || checksum is null)
            throw new InvalidDataException(
                $"The latest release must include both {ExecutableName} and {ChecksumName}.");

        return (executable, checksum, size);
    }

    private static Uri GetTrustedAssetUri(JsonElement asset)
    {
        string? url = asset.TryGetProperty("browser_download_url", out JsonElement urlElement)
            ? urlElement.GetString()
            : null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(
                $"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub returned an untrusted release download URL.");
        }

        return uri;
    }

    private static async Task<string> GetExpectedHashAsync(Uri checksumUri)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await Http.GetAsync(checksumUri, timeout.Token);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync(timeout.Token);
        if (text.Length > 4096)
            throw new InvalidDataException("The release checksum file is unexpectedly large.");

        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 64 && token.All(Uri.IsHexDigit))
                return token;
        }

        throw new InvalidDataException("The release checksum file does not contain a valid SHA-256 hash.");
    }

    private static async Task DownloadAndVerifyAsync(
        Uri downloadUri, string temporaryPath, long expectedSize, string expectedHash)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using HttpResponseMessage response = await Http.GetAsync(
            downloadUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();

        await using (var output = new FileStream(
            temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (Stream input = await response.Content.ReadAsStreamAsync(timeout.Token))
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer, timeout.Token)) != 0)
            {
                total += bytesRead;
                if (total > expectedSize || total > MaximumDownloadSize)
                    throw new InvalidDataException("The downloaded file is larger than the published release asset.");

                await output.WriteAsync(buffer.AsMemory(0, bytesRead), timeout.Token);
            }

            if (total != expectedSize)
                throw new InvalidDataException("The downloaded file size does not match the published release asset.");
        }

        await using var verify = new FileStream(
            temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verify, timeout.Token));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The downloaded file failed its SHA-256 check and was discarded.");
    }

    internal static void CreateDesktopShortcut(string executablePath)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable; the shortcut could not be created.");
        object shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Windows Script Host could not create a shortcut.");

        try
        {
            dynamic shellObject = shell;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            dynamic shortcut = shellObject.CreateShortcut(
                Path.Combine(desktop, "Star Rail Companion.lnk"));
            shortcut.TargetPath = executablePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executablePath);
            shortcut.IconLocation = executablePath + ",0";
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
