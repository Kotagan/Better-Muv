using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace BetterMuv.Services.ChildSession;

/// <summary>
/// 曾尝试为分身创建专用本地账户；Child Session 必须使用当前登录用户，
/// 换用户会导致断开原因 4（Access Denied）。此类方法保留备用，默认启动路径不再调用。
/// </summary>
internal static class ChildSessionLocalAccount
{
    internal const string UserName = "BetterMuv";

    private const int NerrSuccess = 0;
    private const int NerrUserExists = 2224;
    private const int NerrUserNotFound = 2221;
    private const int UfScript = 0x0001;
    private const int UfNormalAccount = 0x0200;
    private const int UfDontExpirePasswd = 0x10000;

    private static readonly string CredentialFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Better-Muv",
        "child-session-account.dpapi");

    internal static bool UserExists()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int result = NetUserGetInfo(null, UserName, 0, out buffer);
            return result == NerrSuccess;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                NetApiBufferFree(buffer);
        }
    }

    /// <summary>创建/重置专用账户需要管理员；账户已在且本机已保存密码则不需要。</summary>
    internal static bool NeedsElevationToPrepare()
    {
        if (Services.ElevationHelper.IsElevated())
            return false;
        if (!UserExists())
            return true;
        return LoadStoredPassword() is null;
    }

    internal static ChildSessionLoginCredentials EnsureReady(Action<string>? log = null)
    {
        string? password = LoadStoredPassword();
        bool existed = UserExists();

        if (!existed)
        {
            password ??= GeneratePassword();
            log?.Invoke($"桌面分身：正在创建专用本地账户 {UserName} …");
            CreateUser(password);
            SavePassword(password);
            HideFromLoginScreen(log);
            log?.Invoke($"桌面分身：专用账户 {UserName} 已创建（已从登录界面隐藏）。");
        }
        else if (password is null)
        {
            password = GeneratePassword();
            log?.Invoke($"桌面分身：专用账户已存在但本地无密码记录，正在重置 {UserName} 密码 …");
            SetPassword(password);
            SavePassword(password);
            HideFromLoginScreen(log);
            log?.Invoke($"桌面分身：专用账户 {UserName} 密码已重置并保存。");
        }
        else
        {
            HideFromLoginScreen(log);
            log?.Invoke($"桌面分身：将使用专用账户 {UserName} 自动登录。");
        }

        return new ChildSessionLoginCredentials(UserName, ".", password);
    }

    private static void CreateUser(string password)
    {
        var info = new UserInfo1
        {
            Name = UserName,
            Password = password,
            PasswordAge = 0,
            Priv = 1, // USER_PRIV_USER
            HomeDir = null,
            Comment = "Better-Muv Child Session",
            Flags = UfScript | UfNormalAccount | UfDontExpirePasswd,
            ScriptPath = null
        };

        int result = NetUserAdd(null, 1, ref info, IntPtr.Zero);
        if (result == NerrUserExists)
        {
            SetPassword(password);
            return;
        }

        if (result != NerrSuccess)
            throw new InvalidOperationException(
                $"创建本地账户 {UserName} 失败（NetUserAdd={result}）。请以管理员权限运行 Better-Muv。");
    }

    private static void SetPassword(string password)
    {
        var info = new UserInfo1003 { Password = password };
        int result = NetUserSetInfo(null, UserName, 1003, ref info, IntPtr.Zero);
        if (result == NerrUserNotFound)
            throw new InvalidOperationException($"本地账户 {UserName} 不存在，无法重置密码。");
        if (result != NerrSuccess)
            throw new InvalidOperationException(
                $"重置本地账户 {UserName} 密码失败（NetUserSetInfo={result}）。请以管理员权限运行 Better-Muv。");
    }

    private static void HideFromLoginScreen(Action<string>? log)
    {
        try
        {
            using RegistryKey localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey key = localMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList",
                writable: true)
                ?? throw new InvalidOperationException("无法写入 SpecialAccounts 注册表。");
            key.SetValue(UserName, 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            log?.Invoke("桌面分身：无法隐藏专用账户登录项（需要管理员），可忽略。");
        }
        catch (Exception ex)
        {
            log?.Invoke("桌面分身：隐藏专用账户登录项失败：" + ex.Message);
        }
    }

    private static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string special = "!@#$%*+-";
        Span<char> chars = stackalloc char[20];
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = special[RandomNumberGenerator.GetInt32(special.Length)];
        string all = upper + lower + digits + special;
        for (int i = 4; i < chars.Length; i++)
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];

        for (int i = chars.Length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    private static string? LoadStoredPassword()
    {
        try
        {
            if (!File.Exists(CredentialFilePath))
                return null;
            byte[] protectedBytes = File.ReadAllBytes(CredentialFilePath);
            byte[] plain = ProtectedData.Unprotect(
                protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    private static void SavePassword(string password)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CredentialFilePath)!);
        byte[] plain = Encoding.UTF8.GetBytes(password);
        byte[] protectedBytes = ProtectedData.Protect(
            plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(CredentialFilePath, protectedBytes);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1
    {
        public string Name;
        public string Password;
        public int PasswordAge;
        public int Priv;
        public string? HomeDir;
        public string? Comment;
        public int Flags;
        public string? ScriptPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1003
    {
        public string Password;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(
        string? serverName,
        string userName,
        int level,
        out IntPtr bufPtr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserAdd(
        string? serverName,
        int level,
        ref UserInfo1 buf,
        IntPtr parmErr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(
        string? serverName,
        string userName,
        int level,
        ref UserInfo1003 buf,
        IntPtr parmErr);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
