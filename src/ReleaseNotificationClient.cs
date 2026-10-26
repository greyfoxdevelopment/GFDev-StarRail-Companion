using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace StarRailCompanion;

internal sealed record PublicRelease(
    [property: JsonPropertyName("productId")] string ProductId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("githubReleaseUrl")] string GithubReleaseUrl,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt);

internal sealed record LatestReleaseResponse(
    [property: JsonPropertyName("productId")] string ProductId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("latest")] PublicRelease? Latest);

internal sealed record ReleasePageResponse(
    [property: JsonPropertyName("productId")] string ProductId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("latestSequence")] long? LatestSequence,
    [property: JsonPropertyName("releases")] IReadOnlyList<PublicRelease> Releases,
    [property: JsonPropertyName("hasMore")] bool HasMore);

internal sealed class ReleaseNotificationClient : IAsyncDisposable
{
    private const string ProductId = "star-rail";
    private const string Repository = "greyfoxdevelopment/GFDev-StarRail-Companion";
    private static readonly Uri ApiBaseUri = new("https://gfdcompanion.duckdns.org/api/v1/");
    private static readonly Uri HubUri = new("https://gfdcompanion.duckdns.org/hubs/releases?product=star-rail");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HubConnection _connection;
    private readonly Func<PublicRelease, Task> _onReleaseAvailable;
    private readonly SemaphoreSlim _sequenceGate = new(1, 1);
    private long _lastSeenSequence;

    internal event Action<Exception>? ErrorOccurred;

    internal ReleaseNotificationClient(long lastSeenSequence, Func<PublicRelease, Task> onReleaseAvailable)
    {
        _lastSeenSequence = Math.Max(0, lastSeenSequence);
        _onReleaseAvailable = onReleaseAvailable;

        _connection = new HubConnectionBuilder()
            .WithUrl(HubUri)
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            })
            .Build();

        _connection.On<PublicRelease>("ReleasePublished", HandleLiveReleaseAsync);
        _connection.Reconnected += HandleReconnectedAsync;
    }

    internal async Task<bool> CheckLatestAndConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await TryStartHubAsync(cancellationToken);

            LatestReleaseResponse? latest = await _http.GetFromJsonAsync<LatestReleaseResponse>(
                new Uri(ApiBaseUri, $"products/{ProductId}/releases/latest"), JsonOptions, cancellationToken);
            if (latest == null || latest.ProductId != ProductId ||
                (latest.Status != "enabled" && latest.Status != "preparing"))
            {
                throw new InvalidDataException("The release service returned an invalid latest-release response.");
            }

            if (latest.Latest is not null)
            {
                ValidateRelease(latest.Latest);
                await _onReleaseAvailable(latest.Latest);
            }

            await CatchUpAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (IsServiceUnavailable(ex))
        {
            ErrorOccurred?.Invoke(ex);
            return false;
        }
    }

    internal async Task<bool> ReconnectAndCatchUpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await TryStartHubAsync(cancellationToken);
            await CatchUpAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (IsServiceUnavailable(ex))
        {
            ErrorOccurred?.Invoke(ex);
            return false;
        }
    }

    private async Task TryStartHubAsync(CancellationToken cancellationToken)
    {
        if (_connection.State != HubConnectionState.Disconnected) return;

        try
        {
            await _connection.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (IsServiceUnavailable(ex))
        {
            ErrorOccurred?.Invoke(ex);
        }
    }

    private async Task HandleReconnectedAsync(string? connectionId)
    {
        try
        {
            await CatchUpAsync(CancellationToken.None);
        }
        catch (Exception ex) when (IsServiceUnavailable(ex))
        {
            ErrorOccurred?.Invoke(ex);
        }
    }

    private async Task HandleLiveReleaseAsync(PublicRelease release)
    {
        ValidateRelease(release);
        await _sequenceGate.WaitAsync();
        try
        {
            if (release.Sequence <= _lastSeenSequence) return;

            _lastSeenSequence = release.Sequence;
            await _onReleaseAvailable(release);
        }
        finally
        {
            _sequenceGate.Release();
        }
    }

    private async Task CatchUpAsync(CancellationToken cancellationToken)
    {
        await _sequenceGate.WaitAsync(cancellationToken);
        try
        {
            PublicRelease? newest = null;
            while (true)
            {
                string relativeUrl = $"products/{ProductId}/releases?after={_lastSeenSequence}&limit=50";
                ReleasePageResponse? page = await _http.GetFromJsonAsync<ReleasePageResponse>(
                    new Uri(ApiBaseUri, relativeUrl), JsonOptions, cancellationToken);
                if (page == null || page.ProductId != ProductId ||
                    (page.Status != "enabled" && page.Status != "preparing") ||
                    page.Releases == null)
                {
                    throw new InvalidDataException("The release service returned an invalid release-history response.");
                }

                long previousSequence = _lastSeenSequence;
                foreach (PublicRelease release in page.Releases)
                {
                    ValidateRelease(release);
                    if (release.Sequence <= _lastSeenSequence) continue;

                    _lastSeenSequence = release.Sequence;
                    newest = release;
                }

                if (page.HasMore && _lastSeenSequence == previousSequence)
                    throw new InvalidDataException("The release service reported another page without advancing the sequence.");

                if (!page.HasMore) break;
            }

            if (newest is not null)
                await _onReleaseAvailable(newest);
        }
        finally
        {
            _sequenceGate.Release();
        }
    }

    private static void ValidateRelease(PublicRelease release)
    {
        if (release.ProductId != ProductId || release.Sequence <= 0 ||
            string.IsNullOrWhiteSpace(release.Title) || release.Title.Length > 120 ||
            release.Notes is null || release.Notes.Length > 8000 ||
            !Version.TryParse(release.Version, out _) ||
            !Uri.TryCreate(release.GithubReleaseUrl, UriKind.Absolute, out Uri? releaseUri) ||
            releaseUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(releaseUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !releaseUri.AbsolutePath.StartsWith(
                $"/{Repository}/releases/tag/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The release service returned invalid or unsupported release metadata.");
        }
    }

    private static bool IsServiceUnavailable(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException
            or HubException or InvalidOperationException or IOException;

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _http.Dispose();
        _sequenceGate.Dispose();
    }
}
