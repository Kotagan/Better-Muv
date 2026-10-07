using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterMuv.Services;

/// <summary>读取 GitHub 仓库最新正式 Release。</summary>
public sealed class GitHubReleaseClient
{
    public const string Owner = "Kotagan";
    public const string Repo = "Better-Muv";
    public static string ReleasesPageUrl => $"https://github.com/{Owner}/{Repo}/releases";
    public static string LatestApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;

    public GitHubReleaseClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? CreateDefaultClient();
    }

    public static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Better-Muv (+https://github.com/Kotagan/Better-Muv)");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    public async Task<GitHubRelease> FetchLatestAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http.GetAsync(LatestApiUrl, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("仓库尚无正式 Release。");
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        GitHubReleaseDto? dto = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(
            stream, JsonOptions, cancellationToken);
        if (dto is null || string.IsNullOrWhiteSpace(dto.TagName))
            throw new InvalidOperationException("GitHub Release 响应无效。");
        if (dto.Draft || dto.Prerelease)
            throw new InvalidOperationException("最新 Release 为草稿或预发布，已跳过。");

        if (!AppVersion.TryParse(dto.TagName, out AppVersion version) &&
            !AppVersion.TryParse(dto.Name, out version))
            throw new InvalidOperationException($"无法解析 Release 版本：{dto.TagName}");

        GitHubReleaseAsset? setup = PickSetupAsset(dto.Assets);
        return new GitHubRelease(
            version,
            dto.TagName,
            dto.Name ?? dto.TagName,
            dto.HtmlUrl ?? ReleasesPageUrl,
            dto.Body,
            setup);
    }

    public async Task DownloadAsync(
        Uri url,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using FileStream output = new(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            readTotal += read;
            if (total is > 0)
                progress?.Report(readTotal / (double)total.Value);
        }

        progress?.Report(1);
    }

    private static GitHubReleaseAsset? PickSetupAsset(List<GitHubAssetDto>? assets)
    {
        if (assets is null || assets.Count == 0)
            return null;

        static bool IsSetup(GitHubAssetDto a) =>
            a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        GitHubAssetDto? hit =
            assets.FirstOrDefault(IsSetup) ??
            assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (hit is null || string.IsNullOrWhiteSpace(hit.BrowserDownloadUrl))
            return null;
        return new GitHubReleaseAsset(hit.Name, new Uri(hit.BrowserDownloadUrl), hit.Size);
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
        public string? Name { get; set; }
        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
        public string? Body { get; set; }
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
        public long Size { get; set; }
    }
}

public sealed record GitHubRelease(
    AppVersion Version,
    string TagName,
    string Name,
    string HtmlUrl,
    string? Body,
    GitHubReleaseAsset? SetupAsset);

public sealed record GitHubReleaseAsset(string Name, Uri DownloadUrl, long SizeBytes);
