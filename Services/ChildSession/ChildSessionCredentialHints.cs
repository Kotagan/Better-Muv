using Microsoft.Win32;

namespace BetterMuv.Services.ChildSession;

/// <summary>桌面分身登录凭据提示（「你的凭据不工作」多与密码/PIN/空密码有关）。</summary>
internal static class ChildSessionCredentialHints
{
    internal static string ShortHint =>
        " 若仍失败：确认已用管理员创建专用账户 BetterMuv，或改用账户密码（非 PIN）登录。";

    internal static bool IsBlankPasswordRemoteBlocked()
    {
        try
        {
            using RegistryKey localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = localMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Lsa");
            object? value = key?.GetValue("LimitBlankPasswordUse");
            return value is int i && i != 0;
        }
        catch
        {
            return true;
        }
    }

    internal static string BuildPreConnectMessage()
    {
        string user = Environment.UserName;
        string domain = Environment.UserDomainName;
        string sam = string.IsNullOrWhiteSpace(domain) ? user : $@"{domain}\{user}";

        string blankHint = IsBlankPasswordRemoteBlocked()
            ? "\n本机禁止空密码远程登录：若账户从未设过密码，必须先设置密码再进分身。\n"
            : "\n";

        return
            "即将连接桌面分身。若弹出登录框，必须使用「当前已登录的同一账户」：\n\n"
            + $"  用户名：{user}\n"
            + $"  也可填：.\\{user}  或  {sam}\n"
            + "  密码：账户「密码」（不要用 PIN / Windows Hello）\n"
            + blankHint
            + "注意：不要用新建的其他用户（会导致断开原因 4）。\n\n"
            + "若「凭据不工作」：\n"
            + "1. 设置 → 账户 → 登录选项 → 密码：为当前账户添加/更改密码\n"
            + "2. 微软账户：关闭「仅允许 Windows Hello 登录」\n"
            + "3. 改完后重启电脑再试\n\n"
            + "选「确定」继续连接。";
    }
}
