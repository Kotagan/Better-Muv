namespace BetterMuv.Core;

/// <summary>
/// 日常商店配额：按配置数量买够后当天不再买，每天 4:00 刷新后重新按数量购买。
/// </summary>
public static class DailyShopSchedule
{
    public const int ResetHour = 4;

    public static DateOnly CurrentShopDay(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime date = local.Date;
        if (local.Hour < ResetHour)
            date = date.AddDays(-1);
        return DateOnly.FromDateTime(date);
    }

    public static string CurrentShopDayKey(DateTime now) =>
        CurrentShopDay(now).ToString("yyyy-MM-dd");

    public static bool QuotaFilledThisShopDay(string? lastShopDayKey, DateTime now) =>
        !string.IsNullOrWhiteSpace(lastShopDayKey) &&
        string.Equals(lastShopDayKey, CurrentShopDayKey(now), StringComparison.Ordinal);

    public static DateTime NextReset(DateTime now)
    {
        DateTime local = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        DateTime todayReset = local.Date.AddHours(ResetHour);
        return local < todayReset ? todayReset : todayReset.AddDays(1);
    }
}
