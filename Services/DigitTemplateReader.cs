using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterMuv.Services;

/// <summary>
/// 大号难度数字：固定裁剪框 + 横向平移槽位 + 固定尺寸 0–9 模板。
/// 每个槽位匹配分数须 ≥ <see cref="MatchThreshold"/>。
/// 支持小范围原点搜索，容忍画布校准带来的数像素漂移。
/// </summary>
public sealed class DigitTemplateReader
{
    public const double MatchThreshold = 0.90;

    /// <summary>相对难度数字 ROI 图像的槽位（与 4K ROI 840×300 对齐）。</summary>
    public const int SlotOriginX = 48;
    public const int SlotOriginY = 40;
    public const int SlotWidth = 232;
    public const int SlotHeight = 240;
    public const int SlotPitch = 280;
    public const int SlotCount = 3;

    /// <summary>相对默认槽位原点的搜索半径（像素，归一化到 840×300 后）。</summary>
    public const int AlignSearchRadiusX = 36;
    public const int AlignSearchRadiusY = 24;
    public const int AlignSearchStep = 4;

    private readonly Dictionary<int, byte[]> _templates = new();

    public DigitTemplateReader(string? templateDirectory = null)
    {
        string dir = templateDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", "digits");
        if (!Directory.Exists(dir))
            return;

        for (int digit = 0; digit <= 9; digit++)
        {
            string path = Path.Combine(dir, $"{digit}.png");
            if (!File.Exists(path))
                continue;

            using var stream = File.OpenRead(path);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource image = decoder.Frames[0];
            byte[] gray = ToGrayFixed(image, SlotWidth, SlotHeight);
            _templates[digit] = gray;
        }
    }

    public int LoadedCount => _templates.Count;

    public int? TryRead(BitmapSource image)
    {
        if (_templates.Count == 0)
            return null;

        BitmapSource normalized = NormalizeRoi(image, 840, 300);
        (int? value, _, _, _) = TryReadAligned(normalized);
        return value;
    }

    /// <summary>返回每个槽位的 (digit, score)；先对齐到最佳原点再匹配。</summary>
    public IReadOnlyList<(int? Digit, double Score)> MatchAllSlots(BitmapSource image)
    {
        BitmapSource normalized = NormalizeRoi(image, 840, 300);
        (_, int originX, int originY, _) = TryReadAligned(normalized);
        var results = new List<(int? Digit, double Score)>(SlotCount);
        for (int slot = 0; slot < SlotCount; slot++)
            results.Add(MatchSlotAt(normalized, originX, originY, slot));
        return results;
    }

    private (int? Value, int OriginX, int OriginY, double MinScore) TryReadAligned(BitmapSource normalized)
    {
        int? bestValue = null;
        double bestMin = double.NegativeInfinity;
        int bestOx = SlotOriginX;
        int bestOy = SlotOriginY;

        // 先试默认原点，命中则免搜索。
        (int? exactValue, double exactMin) = TryReadAt(normalized, SlotOriginX, SlotOriginY);
        if (exactValue is not null && exactMin >= MatchThreshold)
            return (exactValue, SlotOriginX, SlotOriginY, exactMin);

        for (int dy = -AlignSearchRadiusY; dy <= AlignSearchRadiusY; dy += AlignSearchStep)
        {
            for (int dx = -AlignSearchRadiusX; dx <= AlignSearchRadiusX; dx += AlignSearchStep)
            {
                if (dx == 0 && dy == 0)
                    continue;
                int ox = SlotOriginX + dx;
                int oy = SlotOriginY + dy;
                (int? value, double minScore) = TryReadAt(normalized, ox, oy);
                if (value is null || minScore < MatchThreshold)
                    continue;
                if (minScore > bestMin)
                {
                    bestMin = minScore;
                    bestValue = value;
                    bestOx = ox;
                    bestOy = oy;
                }
            }
        }

        if (bestValue is null)
            return (null, SlotOriginX, SlotOriginY, exactMin);

        // 在粗搜最优点附近再精修一步。
        for (int dy = -AlignSearchStep; dy <= AlignSearchStep; dy++)
        {
            for (int dx = -AlignSearchStep; dx <= AlignSearchStep; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                int ox = bestOx + dx;
                int oy = bestOy + dy;
                (int? value, double minScore) = TryReadAt(normalized, ox, oy);
                if (value is null || minScore < MatchThreshold)
                    continue;
                if (minScore > bestMin)
                {
                    bestMin = minScore;
                    bestValue = value;
                    bestOx = ox;
                    bestOy = oy;
                }
            }
        }

        return (bestValue, bestOx, bestOy, bestMin);
    }

    private (int? Value, double MinScore) TryReadAt(BitmapSource roi, int originX, int originY)
    {
        int value = 0;
        double minScore = double.PositiveInfinity;
        for (int slot = 0; slot < SlotCount; slot++)
        {
            (int? digit, double score) = MatchSlotAt(roi, originX, originY, slot);
            if (digit is null || score < MatchThreshold)
                return (null, score);
            value = value * 10 + digit.Value;
            if (score < minScore)
                minScore = score;
        }
        return (value, minScore);
    }

    private (int? Digit, double Score) MatchSlotAt(
        BitmapSource roi, int originX, int originY, int slotIndex)
    {
        int x = originX + slotIndex * SlotPitch;
        if (x < 0 || originY < 0 ||
            x + SlotWidth > roi.PixelWidth || originY + SlotHeight > roi.PixelHeight)
            return (null, 0);

        var cropped = new CroppedBitmap(roi, new Int32Rect(x, originY, SlotWidth, SlotHeight));
        cropped.Freeze();
        byte[] source = ToGrayFixed(cropped, SlotWidth, SlotHeight);

        double bestScore = double.NegativeInfinity;
        int? bestDigit = null;
        foreach ((int digit, byte[] template) in _templates)
        {
            TemplateMatchResult match = TemplateMatcher.MatchGray(
                source, SlotWidth, SlotHeight, template, SlotWidth, SlotHeight);
            if (match.Score > bestScore)
            {
                bestScore = match.Score;
                bestDigit = digit;
            }
        }

        if (bestDigit is null || bestScore < MatchThreshold)
            return (null, bestScore is double.NegativeInfinity ? 0 : bestScore);
        return (bestDigit, bestScore);
    }

    private static BitmapSource NormalizeRoi(BitmapSource source, int targetWidth, int targetHeight)
    {
        if (source.PixelWidth == targetWidth && source.PixelHeight == targetHeight)
            return source;

        var scaled = new TransformedBitmap(
            source,
            new ScaleTransform(
                targetWidth / (double)source.PixelWidth,
                targetHeight / (double)source.PixelHeight));
        scaled.Freeze();
        return scaled;
    }

    private static byte[] ToGrayFixed(BitmapSource source, int width, int height)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        if (converted.PixelWidth == width && converted.PixelHeight == height)
        {
            byte[] pixels = new byte[width * height];
            converted.CopyPixels(pixels, width, 0);
            return pixels;
        }

        byte[] original = new byte[converted.PixelWidth * converted.PixelHeight];
        converted.CopyPixels(original, converted.PixelWidth, 0);
        byte[] resized = new byte[width * height];
        int sw = converted.PixelWidth, sh = converted.PixelHeight;
        for (int y = 0; y < height; y++)
        {
            double sy = (y + 0.5) * sh / height - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, sh - 1);
            int y1 = Math.Min(y0 + 1, sh - 1);
            double fy = sy - Math.Floor(sy);
            for (int x = 0; x < width; x++)
            {
                double sx = (x + 0.5) * sw / width - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, sw - 1);
                int x1 = Math.Min(x0 + 1, sw - 1);
                double fx = sx - Math.Floor(sx);
                double top = original[y0 * sw + x0] * (1 - fx) + original[y0 * sw + x1] * fx;
                double bottom = original[y1 * sw + x0] * (1 - fx) + original[y1 * sw + x1] * fx;
                resized[y * width + x] = (byte)Math.Clamp(Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
            }
        }
        return resized;
    }
}
