using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterMuv.Services;

/// <summary>在整幅逻辑 1080p 画面中定位用户提供的截图模板。</summary>
public sealed class ImageLocator
{
    public ImageLocationResult Locate(
        BitmapSource screen, BitmapSource template, int templateLogicalWidth, int templateLogicalHeight)
    {
        const int screenWidth = 1920;
        const int screenHeight = 1080;
        if (templateLogicalWidth < 2 || templateLogicalHeight < 2 ||
            templateLogicalWidth > screenWidth || templateLogicalHeight > screenHeight)
        {
            throw new InvalidOperationException(
                $"模板逻辑尺寸无效：{templateLogicalWidth}×{templateLogicalHeight}。请使用从游戏画面裁出的较小图像。 ");
        }

        byte[] source = ToGray(screen, screenWidth, screenHeight);
        byte[] target = ToGray(template, templateLogicalWidth, templateLogicalHeight);

        // 整屏穷举会很慢。先按 4 像素步进用最多 20×20 个采样点粗找，再逐像素复核最佳位置附近。
        (double coarseScore, int coarseX, int coarseY) = Search(
            source, screenWidth, screenHeight, target, templateLogicalWidth, templateLogicalHeight,
            candidateStep: 4, sampleGrid: 20,
            0, screenWidth - templateLogicalWidth, 0, screenHeight - templateLogicalHeight);

        int minX = Math.Max(0, coarseX - 5);
        int maxX = Math.Min(screenWidth - templateLogicalWidth, coarseX + 5);
        int minY = Math.Max(0, coarseY - 5);
        int maxY = Math.Min(screenHeight - templateLogicalHeight, coarseY + 5);
        (double score, int x, int y) = Search(
            source, screenWidth, screenHeight, target, templateLogicalWidth, templateLogicalHeight,
            candidateStep: 1, sampleGrid: 32, minX, maxX, minY, maxY);

        return new ImageLocationResult(score, x, y, templateLogicalWidth, templateLogicalHeight, coarseScore);
    }

    private static (double Score, int X, int Y) Search(
        byte[] source, int sourceWidth, int sourceHeight, byte[] template, int templateWidth, int templateHeight,
        int candidateStep, int sampleGrid, int minX, int maxX, int minY, int maxY)
    {
        int sampleX = Math.Min(sampleGrid, templateWidth);
        int sampleY = Math.Min(sampleGrid, templateHeight);
        int samples = sampleX * sampleY;
        var offsets = new (int X, int Y)[samples];
        double templateMean = 0;
        int index = 0;
        for (int y = 0; y < sampleY; y++)
        for (int x = 0; x < sampleX; x++)
        {
            int tx = x * (templateWidth - 1) / Math.Max(1, sampleX - 1);
            int ty = y * (templateHeight - 1) / Math.Max(1, sampleY - 1);
            offsets[index++] = (tx, ty);
            templateMean += template[ty * templateWidth + tx];
        }
        templateMean /= samples;

        double templateVariance = 0;
        foreach ((int x, int y) in offsets)
        {
            double delta = template[y * templateWidth + x] - templateMean;
            templateVariance += delta * delta;
        }
        if (templateVariance <= double.Epsilon)
            throw new InvalidOperationException("模板几乎是纯色，无法可靠定位。请裁入更多图标或文字细节。");

        double bestScore = double.NegativeInfinity;
        int bestX = minX;
        int bestY = minY;
        for (int candidateY = minY; candidateY <= maxY; candidateY += candidateStep)
        for (int candidateX = minX; candidateX <= maxX; candidateX += candidateStep)
        {
            double sourceMean = 0;
            foreach ((int x, int y) in offsets)
                sourceMean += source[(candidateY + y) * sourceWidth + candidateX + x];
            sourceMean /= samples;

            double numerator = 0;
            double sourceVariance = 0;
            foreach ((int x, int y) in offsets)
            {
                double sourceValue = source[(candidateY + y) * sourceWidth + candidateX + x] - sourceMean;
                double templateValue = template[y * templateWidth + x] - templateMean;
                numerator += sourceValue * templateValue;
                sourceVariance += sourceValue * sourceValue;
            }

            double denominator = Math.Sqrt(sourceVariance * templateVariance);
            double score = denominator <= double.Epsilon ? -1 : numerator / denominator;
            if (score > bestScore)
                (bestScore, bestX, bestY) = (score, candidateX, candidateY);
        }
        return (bestScore, bestX, bestY);
    }

    private static byte[] ToGray(BitmapSource image, int targetWidth, int targetHeight)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0);
        int sourceWidth = converted.PixelWidth;
        int sourceHeight = converted.PixelHeight;
        byte[] original = new byte[sourceWidth * sourceHeight];
        converted.CopyPixels(original, sourceWidth, 0);

        byte[] resized = new byte[targetWidth * targetHeight];
        for (int y = 0; y < targetHeight; y++)
        for (int x = 0; x < targetWidth; x++)
        {
            int sourceX = Math.Clamp((int)((x + 0.5) * sourceWidth / targetWidth), 0, sourceWidth - 1);
            int sourceY = Math.Clamp((int)((y + 0.5) * sourceHeight / targetHeight), 0, sourceHeight - 1);
            resized[y * targetWidth + x] = original[sourceY * sourceWidth + sourceX];
        }
        return resized;
    }
}

public sealed record ImageLocationResult(
    double Score, int X, int Y, int Width, int Height, double CoarseScore);
