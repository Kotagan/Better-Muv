using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterMuv.Services;

public sealed record TemplateMatchResult(double Score, int X, int Y, int Width, int Height);

public sealed class TemplateMatcher
{
    private readonly byte[] _template;
    private readonly int _templateWidth;
    private readonly int _templateHeight;

    public TemplateMatcher(string templatePath)
    {
        using var stream = File.OpenRead(templatePath);
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource image = decoder.Frames[0];

        ReferenceWidth = image.PixelWidth;
        ReferenceHeight = image.PixelHeight;
        // User templates are captured at 4K; match them in the 1920x1080 logical space.
        _templateWidth = Math.Max(1, ReferenceWidth / 2);
        _templateHeight = Math.Max(1, ReferenceHeight / 2);
        _template = ToGray(image, _templateWidth, _templateHeight);
    }

    public int ReferenceWidth { get; }
    public int ReferenceHeight { get; }
    public int LogicalWidth => _templateWidth;
    public int LogicalHeight => _templateHeight;

    public TemplateMatchResult Match(BitmapSource searchImage, int logicalWidth, int logicalHeight)
    {
        byte[] source = ToGray(searchImage, logicalWidth, logicalHeight);
        return MatchGray(source, logicalWidth, logicalHeight, _template, _templateWidth, _templateHeight);
    }

    /// <summary>将搜索图预处理为逻辑分辨率灰度，供多模板并行匹配复用。</summary>
    public static byte[] PrepareGray(BitmapSource searchImage, int logicalWidth, int logicalHeight) =>
        ToGray(searchImage, logicalWidth, logicalHeight);

    public TemplateMatchResult MatchPrepared(byte[] sourceGray, int logicalWidth, int logicalHeight) =>
        MatchGray(sourceGray, logicalWidth, logicalHeight, _template, _templateWidth, _templateHeight);

    public TemplateMatchResult MatchMultiScale(
        BitmapSource searchImage, int logicalWidth, int logicalHeight, params double[] scales)
    {
        byte[] source = ToGray(searchImage, logicalWidth, logicalHeight);
        TemplateMatchResult? best = null;
        foreach (double scale in scales)
        {
            int width = Math.Clamp((int)Math.Round(_templateWidth * scale), 1, logicalWidth);
            int height = Math.Clamp((int)Math.Round(_templateHeight * scale), 1, logicalHeight);
            byte[] template = width == _templateWidth && height == _templateHeight
                ? _template
                : ResizeGray(_template, _templateWidth, _templateHeight, width, height);
            TemplateMatchResult match = MatchGray(
                source, logicalWidth, logicalHeight, template, width, height);
            if (best is null || match.Score > best.Score)
                best = match;
        }
        return best ?? MatchGray(
            source, logicalWidth, logicalHeight, _template, _templateWidth, _templateHeight);
    }

    public static TemplateMatchResult MatchGray(
        byte[] source, int sourceWidth, int sourceHeight,
        byte[] template, int templateWidth, int templateHeight)
    {
        if (source.Length != sourceWidth * sourceHeight || template.Length != templateWidth * templateHeight)
            throw new ArgumentException("灰度图数据长度与尺寸不一致。");
        if (templateWidth > sourceWidth || templateHeight > sourceHeight)
            throw new ArgumentException(
                $"模板尺寸不能大于搜索图（模板 {templateWidth}×{templateHeight}，搜索 {sourceWidth}×{sourceHeight}）。");

        (_, int coarseX, int coarseY) =
            Search(source, sourceWidth, sourceHeight, template, templateWidth, templateHeight, 2,
                0, sourceWidth - templateWidth, 0, sourceHeight - templateHeight);

        int minX = Math.Max(0, coarseX - 2);
        int maxX = Math.Min(sourceWidth - templateWidth, coarseX + 2);
        int minY = Math.Max(0, coarseY - 2);
        int maxY = Math.Min(sourceHeight - templateHeight, coarseY + 2);
        (double score, int x, int y) =
            Search(source, sourceWidth, sourceHeight, template, templateWidth, templateHeight, 1,
                minX, maxX, minY, maxY);

        return new TemplateMatchResult(score, x, y, templateWidth, templateHeight);
    }

    private static (double Score, int X, int Y) Search(
        byte[] source, int sourceWidth, int sourceHeight,
        byte[] template, int templateWidth, int templateHeight,
        int sampleStep, int minX, int maxX, int minY, int maxY)
    {
        double templateMean = 0;
        int samples = 0;
        for (int y = 0; y < templateHeight; y += sampleStep)
        for (int x = 0; x < templateWidth; x += sampleStep)
        {
            templateMean += template[y * templateWidth + x];
            samples++;
        }
        templateMean /= samples;

        double templateVariance = 0;
        for (int y = 0; y < templateHeight; y += sampleStep)
        for (int x = 0; x < templateWidth; x += sampleStep)
        {
            double value = template[y * templateWidth + x] - templateMean;
            templateVariance += value * value;
        }

        double bestScore = double.NegativeInfinity;
        int bestX = minX;
        int bestY = minY;
        for (int candidateY = minY; candidateY <= maxY; candidateY++)
        for (int candidateX = minX; candidateX <= maxX; candidateX++)
        {
            double sourceMean = 0;
            for (int y = 0; y < templateHeight; y += sampleStep)
            for (int x = 0; x < templateWidth; x += sampleStep)
                sourceMean += source[(candidateY + y) * sourceWidth + candidateX + x];
            sourceMean /= samples;

            double numerator = 0;
            double sourceVariance = 0;
            for (int y = 0; y < templateHeight; y += sampleStep)
            for (int x = 0; x < templateWidth; x += sampleStep)
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

    private static byte[] ToGray(BitmapSource source, int targetWidth, int targetHeight)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        int sourceWidth = converted.PixelWidth;
        int sourceHeight = converted.PixelHeight;
        byte[] original = new byte[sourceWidth * sourceHeight];
        converted.CopyPixels(original, sourceWidth, 0);

        byte[] resized = new byte[targetWidth * targetHeight];
        for (int y = 0; y < targetHeight; y++)
        {
            double sourceY = (y + 0.5) * sourceHeight / targetHeight - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1);
            int y1 = Math.Min(y0 + 1, sourceHeight - 1);
            double fy = sourceY - Math.Floor(sourceY);
            for (int x = 0; x < targetWidth; x++)
            {
                double sourceX = (x + 0.5) * sourceWidth / targetWidth - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1);
                int x1 = Math.Min(x0 + 1, sourceWidth - 1);
                double fx = sourceX - Math.Floor(sourceX);
                double top = original[y0 * sourceWidth + x0] * (1 - fx) + original[y0 * sourceWidth + x1] * fx;
                double bottom = original[y1 * sourceWidth + x0] * (1 - fx) + original[y1 * sourceWidth + x1] * fx;
                resized[y * targetWidth + x] = (byte)Math.Clamp(Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
            }
        }
        return resized;
    }

    private static byte[] ResizeGray(
        byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        byte[] resized = new byte[targetWidth * targetHeight];
        for (int y = 0; y < targetHeight; y++)
        {
            int sourceY = Math.Clamp(
                (int)Math.Round((y + 0.5) * sourceHeight / targetHeight - 0.5),
                0, sourceHeight - 1);
            for (int x = 0; x < targetWidth; x++)
            {
                int sourceX = Math.Clamp(
                    (int)Math.Round((x + 0.5) * sourceWidth / targetWidth - 0.5),
                    0, sourceWidth - 1);
                resized[y * targetWidth + x] = source[sourceY * sourceWidth + sourceX];
            }
        }
        return resized;
    }
}
