using System.Runtime.InteropServices;

namespace BetterMuv.Services.ChildSession;

/// <summary>为本地 Windows 账户设置/重置密码（桌面分身需要账户密码，PIN 无效）。</summary>
internal static class WindowsLocalAccountPassword
{
    private const int NerrSuccess = 0;
    private const int NerrUserNotFound = 2221;
    private const int ErrorAccessDenied = 5;

    internal static void SetPassword(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName))
            throw new ArgumentException("用户名不能为空。", nameof(userName));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("新密码不能为空。", nameof(password));

        var info = new UserInfo1003 { Password = password };
        int result = NetUserSetInfo(null, userName, 1003, ref info, IntPtr.Zero);
        if (result == NerrUserNotFound)
            throw new InvalidOperationException($"找不到本地账户「{userName}」。");
        if (result == ErrorAccessDenied)
            throw new UnauthorizedAccessException(
                "设置账户密码需要管理员权限。请以管理员身份运行 Better-Muv 后再试。");
        if (result != NerrSuccess)
            throw new InvalidOperationException(
                $"设置账户「{userName}」密码失败（错误码 {result}）。若是微软账户，请到系统设置中自行设置密码。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1003
    {
        public string Password;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(
        string? serverName,
        string userName,
        int level,
        ref UserInfo1003 buf,
        IntPtr parmErr);
}
