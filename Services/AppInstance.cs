namespace BetterMuv.Services;

public enum BetterMuvInstanceType
{
    Root,
    ChildSession
}

/// <summary>解析启动参数，区分主实例与桌面分身实例。</summary>
public static class AppInstance
{
    public const string InstanceArgument = "--instance";
    public const string OpenChildSessionArgument = "--open-child-session";

    public static BetterMuvInstanceType Type { get; private set; } = BetterMuvInstanceType.Root;

    public static bool IsRoot => Type == BetterMuvInstanceType.Root;

    public static bool IsChildSession => Type == BetterMuvInstanceType.ChildSession;

    /// <summary>提升权限重启后自动打开桌面分身窗口。</summary>
    public static bool ShouldOpenChildSession { get; private set; }

    public static void Initialize(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], OpenChildSessionArgument, StringComparison.OrdinalIgnoreCase))
                ShouldOpenChildSession = true;

            if (i >= args.Length - 1)
                continue;
            if (!string.Equals(args[i], InstanceArgument, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(args[i + 1], "childSession", StringComparison.OrdinalIgnoreCase))
            {
                Type = BetterMuvInstanceType.ChildSession;
                return;
            }
        }
    }

    public static void ClearOpenChildSessionRequest() => ShouldOpenChildSession = false;
}
