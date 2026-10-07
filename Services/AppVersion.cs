namespace BetterMuv.Services;

/// <summary>语义化版本解析与比较（忽略 v 前缀与 +metadata）。</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    public static bool TryParse(string? raw, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string text = raw.Trim();
        int plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];
        int dash = text.IndexOf('-');
        if (dash > 0)
            text = text[..dash];
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        string[] parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;
        if (!int.TryParse(parts[0], out int major) ||
            !int.TryParse(parts[1], out int minor))
            return false;
        int patch = 0;
        if (parts.Length >= 3 && !int.TryParse(parts[2], out patch))
            return false;

        version = new AppVersion(major, minor, patch);
        return true;
    }

    public static AppVersion Parse(string raw) =>
        TryParse(raw, out AppVersion v)
            ? v
            : throw new FormatException($"无法解析版本号：{raw}");

    public int CompareTo(AppVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        return Patch.CompareTo(other.Patch);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
