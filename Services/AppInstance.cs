using System.Security.Principal;

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

    public static BetterMuvInstanceType Type { get; private set; } = BetterMuvInstanceType.Root;

    public static bool IsRoot => Type == BetterMuvInstanceType.Root;

    public static bool IsChildSession => Type == BetterMuvInstanceType.ChildSession;

    public static string RelativeMousePipeName { get; } =
        $"Better-Muv.v1.user-{GetUserSid()}.relativeMouse";

    public static void Initialize(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], InstanceArgument, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(args[i + 1], "childSession", StringComparison.OrdinalIgnoreCase))
            {
                Type = BetterMuvInstanceType.ChildSession;
                return;
            }
        }
    }

    private static string GetUserSid()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
