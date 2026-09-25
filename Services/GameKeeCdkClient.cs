using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterMuv.Services;

/// <summary>
/// 从 GameKee wiki 拉取 MUV-LUV 少女庭园兑换码列表。
/// 页面：https://www.gamekee.com/mabugaru/redemptionCode/106
/// </summary>
public sealed class GameKeeCdkClient
{
    public const string GameAlias = "mabugaru";
    public const int ServerId = 106;
    public const string SourceUrl = "https://www.gamekee.com/mabugaru/redemptionCode/106";

    private static readonly Uri ListUri =
        new("https://www.gamekee.com/v1/game/cdk/queryByServerIdPageList");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;

    public GameKeeCdkClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? CreateDefaultClient();
    }

    public static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Better-Muv/1.0 (+https://github.com; GameKee CDK sync)");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", SourceUrl);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.gamekee.com");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Game-Alias", GameAlias);
        return client;
    }

    /// <summary>拉取全部有效兑换码（state=2）。</summary>
    public async Task<IReadOnlyList<GameKeeCdkItem>> FetchActiveAsync(
        CancellationToken cancellationToken = default)
    {
        var all = new List<GameKeeCdkItem>();
        int pageNo = 1;
        int pageTotal = 1;
        const int limit = 50;

        while (pageNo <= pageTotal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = new Dictionary<string, string?>
            {
                ["server_id"] = ServerId.ToString(),
                ["state"] = "2",
                ["page_no"] = pageNo.ToString(),
                ["limit"] = limit.ToString(),
                ["nick_name"] = ""
            };
            string url = ListUri + "?" + string.Join("&",
                query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value ?? "")}"));

            using HttpResponseMessage response = await _http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            GameKeeCdkResponse? body = await JsonSerializer.DeserializeAsync<GameKeeCdkResponse>(
                stream, JsonOptions, cancellationToken);
            if (body is null)
                throw new InvalidOperationException("GameKee 返回空响应。");
            if (body.Code != 0)
                throw new InvalidOperationException($"GameKee 错误（{body.Code}）：{body.Msg}");

            if (body.Data is { Count: > 0 })
                all.AddRange(body.Data);

            pageTotal = Math.Max(1, body.Meta?.Pagination?.PageTotal ?? 1);
            pageNo++;
            if (body.Data is null || body.Data.Count == 0)
                break;
        }

        return all;
    }
}

public sealed class GameKeeCdkResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public List<GameKeeCdkItem>? Data { get; set; }

    [JsonPropertyName("meta")]
    public GameKeeCdkMeta? Meta { get; set; }
}

public sealed class GameKeeCdkMeta
{
    [JsonPropertyName("pagination")]
    public GameKeeCdkPagination? Pagination { get; set; }
}

public sealed class GameKeeCdkPagination
{
    [JsonPropertyName("page_no")]
    public int PageNo { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("page_total")]
    public int PageTotal { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

public sealed class GameKeeCdkItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("end_at")]
    public long EndAt { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }
}
