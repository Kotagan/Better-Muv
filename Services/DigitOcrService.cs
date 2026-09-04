using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using SoftwareBitmap = Windows.Graphics.Imaging.SoftwareBitmap;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace BetterMuv.Services;

/// <summary>用系统 OCR 从截图 ROI 中读取整数（迷宫难度数字）。</summary>
public static class DigitOcrService
{
    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    public sealed record LocatedNumber(int Value, double CenterX, double CenterY);

    public static async Task<IReadOnlyList<LocatedNumber>> LocateNumbersAsync(
        BitmapSource image, CancellationToken cancellationToken)
    {
        OcrEngine? engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ja"))
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return [];
        using SoftwareBitmap bitmap = await ToSoftwareBitmapAsync(image, cancellationToken);
        OcrResult result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        var numbers = new List<LocatedNumber>();
        foreach (OcrLine line in result.Lines)
        {
            OcrWord[] words = line.Words.ToArray();
            if (words.Length == 0) continue;
            // OCR 常把三位数拆成“1”与“70”两个 word；同一行先拼接再取数字。
            string lineText = string.Concat(words.Select(word => word.Text));
            Match match = Digits.Match(lineText);
            if (!match.Success || !int.TryParse(match.Value, out int value)) continue;
            double left = words.Min(word => word.BoundingRect.X);
            double top = words.Min(word => word.BoundingRect.Y);
            double right = words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
            double bottom = words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
            numbers.Add(new LocatedNumber(value, (left + right) / 2, (top + bottom) / 2));
        }
        return numbers;
    }

    public static async Task<int?> TryReadIntAsync(BitmapSource image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 游戏的 0 带有斜杠装饰，系统 OCR 经常完全忽略；先处理最常见的 0/1 组合。
        int? simple = TryReadZeroOneDigits(image);
        if (simple is not null)
            return simple;

        OcrEngine?[] engines =
        [
            OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")),
            OcrEngine.TryCreateFromUserProfileLanguages(),
            OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ja"))
        ];
        BitmapSource[] variants = [CreateHighContrast(image), image];
        foreach (BitmapSource variant in variants)
        foreach (OcrEngine? engine in engines)
        {
            if (engine is null) continue;
            using SoftwareBitmap softwareBitmap = await ToSoftwareBitmapAsync(variant, cancellationToken);
            OcrResult result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
            Match match = Digits.Match(result.Text ?? string.Empty);
            if (match.Success && int.TryParse(match.Value, out int value))
                return value;
        }
        return null;
    }

    private static int? TryReadZeroOneDigits(BitmapSource source)
    {
        var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        int width = gray.PixelWidth, height = gray.PixelHeight, stride = width;
        byte[] pixels = new byte[stride * height];
        gray.CopyPixels(pixels, stride, 0);

        // 每列的深色像素数量。字符主体很深，浅色纹理不会越过此阈值。
        int[] ink = new int[width];
        for (int x = 0; x < width; x++)
        for (int y = 0; y < height; y++)
            if (pixels[y * stride + x] < 105) ink[x]++;

        int minimumInk = Math.Max(3, height / 18);
        var spans = new List<(int Left, int Right)>();
        int start = -1;
        for (int x = 0; x <= width; x++)
        {
            bool active = x < width && ink[x] >= minimumInk;
            if (active && start < 0) start = x;
            if (!active && start >= 0)
            {
                if (x - start >= Math.Max(8, width / 30)) spans.Add((start, x - 1));
                start = -1;
            }
        }
        if (spans.Count is < 1 or > 3) return null;

        int value = 0;
        foreach ((int left, int right) in spans)
        {
            int top = height, bottom = -1;
            for (int x = left; x <= right; x++)
            for (int y = 0; y < height; y++)
            {
                if (pixels[y * stride + x] >= 105) continue;
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
            if (bottom <= top) return null;
            double aspect = (right - left + 1) / (double)(bottom - top + 1);
            int digit;
            if (aspect <= 0.62) digit = 1;
            else if (aspect >= 0.72) digit = 0;
            else return null;
            value = value * 10 + digit;
        }
        return value;
    }

    private static BitmapSource CreateHighContrast(BitmapSource source)
    {
        var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        int width = gray.PixelWidth, height = gray.PixelHeight, stride = width;
        byte[] pixels = new byte[stride * height];
        gray.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = pixels[i] < 150 ? (byte)0 : (byte)255;
        BitmapSource result = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Gray8, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(
        BitmapSource source, CancellationToken cancellationToken)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(WpfBitmapFrame.Create(bgra));
        using var managed = new MemoryStream();
        encoder.Save(managed);
        managed.Position = 0;

        // AsStreamForWrite 返回的包装流会在 Dispose 时关闭底层 WinRT 流；
        // 因此必须让两者都存活到 BitmapDecoder 完成读取之后。
        using var raStream = new InMemoryRandomAccessStream();
        using Stream write = raStream.AsStreamForWrite();
        await managed.CopyToAsync(write, 81920, cancellationToken);
        await write.FlushAsync(cancellationToken);
        raStream.Seek(0);

        WinBitmapDecoder decoder = await WinBitmapDecoder.CreateAsync(raStream).AsTask(cancellationToken);
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
    }
}
