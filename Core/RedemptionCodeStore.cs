using System.Text.Json;
using System.Text.Json.Serialization;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>本地缓存 GameKee 兑换码，并保留「已用」标记。</summary>
public static class RedemptionCodeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string CachePath { get; } =
        Path.Combine(ConfigStore.UserDirectory, "redemption-codes.json");

    public static RedemptionCodeCache Load()
    {
        try
        {
            if (!File.Exists(CachePath))
                return new RedemptionCodeCache();
            string json = File.ReadAllText(CachePath);
            return JsonSerializer.Deserialize<RedemptionCodeCache>(json, JsonOptions)
                   ?? new RedemptionCodeCache();
        }
        catch
        {
            return new RedemptionCodeCache();
        }
    }

    public static void Save(RedemptionCodeCache cache)
    {
        Directory.CreateDirectory(ConfigStore.UserDirectory);
        string json = JsonSerializer.Serialize(cache, JsonOptions);
        File.WriteAllText(CachePath, json);
    }

    /// <summary>
    /// 用远端列表更新本地缓存。返回新增/变更统计；已用标记按 id/code 保留。
    /// </summary>
    public static RedemptionSyncResult MergeFromRemote(
        RedemptionCodeCache cache, IReadOnlyList<GameKeeCdkItem> remote)
    {
        var previousById = cache.Codes.ToDictionary(c => c.Id);
        var previousByCode = cache.Codes
            .Where(c => !string.IsNullOrWhiteSpace(c.Code))
            .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var next = new List<RedemptionCodeEntry>(remote.Count);
        var addedCodes = new List<RedemptionCodeEntry>();
        int updated = 0;

        foreach (GameKeeCdkItem item in remote.OrderByDescending(x => x.CreatedAt))
        {
            if (string.IsNullOrWhiteSpace(item.Code))
                continue;

            bool known = previousById.TryGetValue(item.Id, out RedemptionCodeEntry? old)
                         || previousByCode.TryGetValue(item.Code.Trim(), out old);

            var entry = new RedemptionCodeEntry
            {
                Id = item.Id,
                Code = item.Code.Trim(),
                Content = item.Content?.Trim() ?? "",
                EndAtUnix = item.EndAt,
                CreatedAtUnix = item.CreatedAt,
                MarkedUsed = old?.MarkedUsed ?? false
            };
            next.Add(entry);

            if (!known)
                addedCodes.Add(entry);
            else if (!string.Equals(old!.Content, item.Content, StringComparison.Ordinal)
                     || old.EndAtUnix != item.EndAt)
                updated++;
        }

        cache.Codes = next;
        cache.LastSyncedAt = DateTimeOffset.Now;
        cache.SourceUrl = GameKeeCdkClient.SourceUrl;
        Save(cache);
        return new RedemptionSyncResult(addedCodes.Count, updated, next.Count, addedCodes);
    }

    public static string FormatExpiry(long endAtUnix)
    {
        if (endAtUnix <= 0)
            return "永久";
        var end = DateTimeOffset.FromUnixTimeSeconds(endAtUnix).ToLocalTime();
        if (end < DateTimeOffset.Now)
            return $"已过期 {end:MM-dd}";
        return $"至 {end:yyyy-MM-dd}";
    }
}

public sealed class RedemptionCodeCache
{
    public string? SourceUrl { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public List<RedemptionCodeEntry> Codes { get; set; } = [];
}

public sealed class RedemptionCodeEntry
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Content { get; set; } = "";
    public long EndAtUnix { get; set; }
    public long CreatedAtUnix { get; set; }
    public bool MarkedUsed { get; set; }
}

public readonly record struct RedemptionSyncResult(
    int Added,
    int Updated,
    int Total,
    IReadOnlyList<RedemptionCodeEntry> NewCodes);
