using System.Windows.Media.Imaging;

namespace BetterMuv.Services;

/// <summary>
/// 错误自动停止时截取界面；只落盘到诊断目录，不打包。
/// 打包统一走手动「导出」（<see cref="LogBundleExporter"/>）。
/// </summary>
public static class AutoStopCapture
{
    /// <summary>
    /// 将错误停止时的游戏界面写入诊断目录。
    /// </summary>
    /// <returns>保存路径；跳过或失败时为 null。</returns>
    public static string? SaveErrorUi(
        string? diagnosticDirectory,
        BitmapSource? image,
        string reason,
        Action<string>? log = null)
    {
        if (image is null)
        {
            log?.Invoke("错误自动停止截图跳过：无可用画面。");
            return null;
        }

        if (string.IsNullOrWhiteSpace(diagnosticDirectory))
        {
            log?.Invoke("错误自动停止截图跳过：无诊断目录。");
            return null;
        }

        try
        {
            Directory.CreateDirectory(diagnosticDirectory);
            string safe = SanitizeFileToken(reason);
            string path = Path.Combine(
                diagnosticDirectory,
                $"error-stop-{safe}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            using var stream = File.Create(path);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(stream);
            log?.Invoke($"错误自动停止截图：{Path.GetFileName(path)}");
            return path;
        }
        catch (Exception ex)
        {
            log?.Invoke($"错误自动停止截图失败：{ex.Message}");
            return null;
        }
    }

    private static string SanitizeFileToken(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "error";
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = reason.Trim().Select(c => invalid.Contains(c) || c is ' ' or '/' or '\\' ? '-' : c).ToArray();
        string token = new string(chars);
        if (token.Length > 40)
            token = token[..40];
        return string.IsNullOrWhiteSpace(token) ? "error" : token;
    }
}
