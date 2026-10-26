using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarRailCompanion;

internal sealed class HoyolabService
{
    private const string RolesUrl =
        "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hkrpg_global";
    private const string DailyNoteUrl =
        "https://bbs-api-os.hoyolab.com/game_record/app/hkrpg/api/note";

    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<IReadOnlyList<HoyolabRole>> GetRolesAsync(string cookieHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RolesUrl);
        AddHeaders(request, cookieHeader);
        using HttpResponseMessage response = await Client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        HoyolabApiResponse<HoyolabRoleList>? result = ParseResponse<HoyolabRoleList>(response, body);
        if (result?.Data?.List == null)
            throw new HoyolabRequestException("HoYoLAB returned no Honkai: Star Rail account list. Sign in again and retry.");

        return result.Data.List
            .Where(role => !string.IsNullOrWhiteSpace(role.GameUid) &&
                           !string.IsNullOrWhiteSpace(role.Region))
            .ToList();
    }

    public static async Task<HoyolabDailyNote> GetDailyNoteAsync(
        string cookieHeader, HoyolabRole role)
    {
        string url = $"{DailyNoteUrl}?role_id={Uri.EscapeDataString(role.GameUid)}" +
                     $"&server={Uri.EscapeDataString(role.Region)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request, cookieHeader);
        using HttpResponseMessage response = await Client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        HoyolabApiResponse<HoyolabDailyNote>? result = ParseResponse<HoyolabDailyNote>(response, body);
        return result?.Data
            ?? throw new HoyolabRequestException("HoYoLAB returned an empty real-time notes response.");
    }

    private static void AddHeaders(HttpRequestMessage request, string cookieHeader)
    {
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("x-rpc-app_version", "2.22.0");
        request.Headers.TryAddWithoutValidation("x-rpc-client_type", "2");
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Referer", "https://act.hoyolab.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://act.hoyolab.com");
    }

    private static HoyolabApiResponse<T>? ParseResponse<T>(HttpResponseMessage response, string body)
    {
        if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.MovedPermanently)
            throw new HoyolabRequestException("HoYoLAB redirected the request. Check your sign-in and try again.");

        if (!response.IsSuccessStatusCode)
            throw new HoyolabRequestException(
                $"HoYoLAB request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

        HoyolabApiResponse<T>? result;
        try
        {
            result = JsonSerializer.Deserialize<HoyolabApiResponse<T>>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new HoyolabRequestException("HoYoLAB returned an unreadable response. It may be temporarily unavailable.");
        }

        if (result == null)
            throw new HoyolabRequestException("HoYoLAB returned an empty response.");

        if (result.Retcode != 0)
        {
            string message = result.Retcode switch
            {
                -100 or -101 => "HoYoLAB sign-in expired. Sign in again and retry.",
                1034 => "HoYoLAB temporarily blocked this request. Wait a while before retrying.",
                _ => $"HoYoLAB request failed (code {result.Retcode}): {SafeMessage(result.Message)}"
            };
            throw new HoyolabRequestException(message);
        }

        return result;
    }

    private static string SafeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "unknown API error";
        string cleaned = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }
}

internal sealed class HoyolabRequestException(string message) : Exception(message);

internal sealed class HoyolabApiResponse<T>
{
    [JsonPropertyName("retcode")]
    public int Retcode { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

internal sealed class HoyolabRoleList
{
    [JsonPropertyName("list")]
    public List<HoyolabRole> List { get; set; } = [];
}

internal sealed class HoyolabRole
{
    [JsonPropertyName("game_uid")]
    public string GameUid { get; set; } = "";

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = "";

    [JsonPropertyName("region")]
    public string Region { get; set; } = "";

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonIgnore]
    public string DisplayName => $"{Nickname} · {GameUid} · {Region}";
}

internal sealed class HoyolabDailyNote
{
    [JsonPropertyName("current_stamina")]
    public int CurrentStamina { get; set; }

    [JsonPropertyName("max_stamina")]
    public int MaxStamina { get; set; }

    [JsonPropertyName("stamina_recover_time")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int StaminaRecoveryTime { get; set; }

    [JsonPropertyName("current_train_score")]
    public int CurrentTrainScore { get; set; }

    [JsonPropertyName("max_train_score")]
    public int MaxTrainScore { get; set; }

    [JsonPropertyName("current_rogue_score")]
    public int CurrentRogueScore { get; set; }

    [JsonPropertyName("max_rogue_score")]
    public int MaxRogueScore { get; set; }

    [JsonPropertyName("accepted_epedition_num")]
    public int AcceptedExpeditionNum { get; set; }

    [JsonPropertyName("max_expedition_num")]
    public int MaxExpeditionNum { get; set; } = 4;

    [JsonPropertyName("expeditions")]
    public List<HoyolabExpedition>? Expeditions { get; set; } = [];

}

internal sealed class HoyolabExpedition
{
    [JsonPropertyName("remained_time")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int RemainedTime { get; set; }
}
