using System.Security.Cryptography;
using System.Text;
using BetterMuv.Core;

namespace BetterMuv.Services.ChildSession;

/// <summary>
/// 当前用户桌面分身登录密码的本机持久化（DPAPI，仅当前 Windows 用户可解密）。
/// </summary>
internal static class ChildSessionCredentialStore
{
    private static readonly string CredentialFilePath =
        Path.Combine(ConfigStore.UserDirectory, "child-session-login.dpapi");

    internal static ChildSessionLoginCredentials? TryLoadForCurrentUser()
    {
        try
        {
            if (!File.Exists(CredentialFilePath))
                return null;

            byte[] protectedBytes = File.ReadAllBytes(CredentialFilePath);
            byte[] plain = ProtectedData.Unprotect(
                protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            string text = Encoding.UTF8.GetString(plain);
            string[] parts = text.Split('\n', 3);
            if (parts.Length < 3)
                return null;

            string userName = parts[0].Trim();
            string domain = parts[1].Trim();
            string password = parts[2];
            if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password))
                return null;

            if (!string.Equals(userName, Environment.UserName, StringComparison.OrdinalIgnoreCase))
                return null;

            if (string.IsNullOrWhiteSpace(domain))
                domain = Environment.UserDomainName;
            if (string.IsNullOrWhiteSpace(domain))
                domain = Environment.MachineName;

            return new ChildSessionLoginCredentials(userName, domain, password);
        }
        catch
        {
            return null;
        }
    }

    internal static void Save(ChildSessionLoginCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrEmpty(credentials.Password))
            throw new ArgumentException("密码不能为空。", nameof(credentials));

        Directory.CreateDirectory(ConfigStore.UserDirectory);
        string payload = credentials.UserName + "\n" + credentials.Domain + "\n" + credentials.Password;
        byte[] plain = Encoding.UTF8.GetBytes(payload);
        byte[] protectedBytes = ProtectedData.Protect(
            plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(CredentialFilePath, protectedBytes);
    }

    internal static void Clear()
    {
        try
        {
            if (File.Exists(CredentialFilePath))
                File.Delete(CredentialFilePath);
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    internal static bool HasStoredCredentials() => TryLoadForCurrentUser() is not null;
}
