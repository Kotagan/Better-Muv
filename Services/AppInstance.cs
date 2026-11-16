namespace BetterMuv.Services;

public enum BetterMuvInstanceType
{
    Root,
    ChildSession
}

/// <summary>解析启动参数，区分主实例 / 桌面分身，以及千机链等外部调度。</summary>
public static class AppInstance
{
    public const string InstanceArgument = "--instance";
    public const string OpenChildSessionArgument = "--open-child-session";

    public static BetterMuvInstanceType Type { get; private set; } = BetterMuvInstanceType.Root;

    public static bool IsRoot => Type == BetterMuvInstanceType.Root;

    public static bool IsChildSession => Type == BetterMuvInstanceType.ChildSession;

    /// <summary>提升权限重启后自动打开桌面分身窗口。</summary>
    public static bool ShouldOpenChildSession { get; private set; }

    /// <summary>
    /// 启动后自动跑一条龙（任务页已启用项）。
    /// 兼容：<c>-o</c> / <c>--onedragon</c> / <c>--autostart</c>。
    /// </summary>
    public static bool AutoStartPipeline { get; private set; }

    /// <summary>一条龙结束后关闭游戏。兼容：<c>-c</c> / <c>--close-game</c>。</summary>
    public static bool CloseGameAfterRun { get; private set; }

    /// <summary>一条龙结束后退出本程序（供千机链「脚本被关闭」判定）。兼容：<c>-q</c> / <c>--quit</c> / <c>--exit</c>。</summary>
    public static bool QuitAfterRun { get; private set; }

    /// <summary>由命令行调度（自动跑），应避免阻塞弹窗。</summary>
    public static bool IsCliScheduled => AutoStartPipeline;

    public static void Initialize(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, OpenChildSessionArgument, StringComparison.OrdinalIgnoreCase))
                ShouldOpenChildSession = true;

            if (IsFlag(a, "-o", "--onedragon", "--autostart"))
                AutoStartPipeline = true;
            else if (IsFlag(a, "-c", "--close-game"))
                CloseGameAfterRun = true;
            else if (IsFlag(a, "-q", "--quit", "--exit"))
                QuitAfterRun = true;
            else if (IsFlag(a, "-h", "--help", "/?"))
                ShowHelpAndExit = true;

            if (i >= args.Length - 1)
                continue;
            if (!string.Equals(a, InstanceArgument, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(args[i + 1], "childSession", StringComparison.OrdinalIgnoreCase))
            {
                Type = BetterMuvInstanceType.ChildSession;
            }
        }
    }

    /// <summary>仅打印/弹窗帮助后退出，不进入主流程。</summary>
    public static bool ShowHelpAndExit { get; private set; }

    public static void ClearOpenChildSessionRequest() => ShouldOpenChildSession = false;

    public static string HelpText { get; } =
        """
        Better-Muv 命令行参数（适配千机链 / 任务计划）：

          -o, --onedragon, --autostart   启动后自动跑一条龙（按任务页开关与顺序）
          -c, --close-game               一条龙结束后关闭游戏
          -q, --quit, --exit             一条龙结束后退出本程序
          -h, --help                     显示本说明

        千机链推荐「脚本启动参数」：
          -o -c -q

        检查完成方式可选「脚本被关闭」；请在本工具设置中配置好游戏路径，
        并建议开启「同时启动游戏」。
        """;

    private static bool IsFlag(string arg, params string[] names)
    {
        foreach (string name in names)
        {
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
