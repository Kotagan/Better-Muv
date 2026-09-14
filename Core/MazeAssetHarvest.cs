using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace BetterMuv.Core;

/// <summary>迷宫跑测时把遗物整卡、关键模板候选落到固定目录，按内容去重。</summary>
public static class MazeAssetHarvest
{
    public static string CatalogRoot => Path.Combine(ResolveDataRoot(), "relic-catalog");

    public static string TemplateHarvestRoot => Path.Combine(ResolveDataRoot(), "template-harvest");

    private static string ResolveDataRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Better-Muv.csproj")))
                return Path.Combine(dir.FullName, "_logtmp");
            dir = dir.Parent;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Better-Muv",
            "harvest");
    }

    /// <summary>把三选一 ROI 均分为三张整卡写入目录。</summary>
    public static int SaveRelicCards(BitmapSource optionsRoi, Action<string>? log = null)
    {
        Directory.CreateDirectory(CatalogRoot);
        int w = optionsRoi.PixelWidth;
        int h = optionsRoi.PixelHeight;
        if (w < 90 || h < 90)
            return 0;

        int cardW = w / 3;
        int saved = 0;
        for (int i = 0; i < 3; i++)
        {
            int x = i * cardW;
            int width = i == 2 ? w - x : cardW;
            var crop = new CroppedBitmap(optionsRoi, new System.Windows.Int32Rect(x, 0, width, h));
            crop.Freeze();
            if (SaveUniquePng(CatalogRoot, $"relic-card", crop, out string path))
            {
                saved++;
                log?.Invoke($"遗物整卡已存：{path}");
            }
        }

        if (SaveUniquePng(CatalogRoot, "relic-strip", optionsRoi, out string stripPath))
        {
            saved++;
            log?.Invoke($"遗物整排已存：{stripPath}");
        }

        return saved;
    }

    /// <summary>保存下一步 / 路线 / メイズ探索等关键模板候选（完整按钮/标题，非碎边）。</summary>
    public static bool SaveTemplateCandidate(string key, BitmapSource image, Action<string>? log = null)
    {
        Directory.CreateDirectory(TemplateHarvestRoot);
        if (SaveUniquePng(TemplateHarvestRoot, key, image, out string path))
        {
            log?.Invoke($"模板候选已存：{path}");
            return true;
        }
        return false;
    }

    private static bool SaveUniquePng(string dir, string prefix, BitmapSource image, out string path)
    {
        path = "";
        byte[] png = EncodePng(image);
        string hash = Convert.ToHexString(SHA1.HashData(png))[..12].ToLowerInvariant();
        path = Path.Combine(dir, $"{prefix}-{hash}.png");
        if (File.Exists(path))
            return false;
        File.WriteAllBytes(path, png);
        return true;
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
