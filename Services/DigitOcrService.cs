using System.IO;
using System.Windows;
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
            // OCR 常把三位数拆成多个 word；同一行拼接后抽出全部数字。
            string lineText = string.Concat(words.Select(word => word.Text));
            string digits = new(lineText.Where(char.IsDigit).ToArray());
            if (digits.Length == 0 || !int.TryParse(digits, out int value)) continue;
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

        // 开口 4 / 斜杠 0：系统 OCR 常把 140 读成 100；形态学能分清时优先采用。
        int? morphFirst = TryReadStylizedDigits(image);
        if (morphFirst is int mf && HasNonZeroOneDigit(mf) && mf is >= 10 and <= 999)
            return mf;

        OcrEngine?[] engines =
        [
            OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")),
            OcrEngine.TryCreateFromUserProfileLanguages(),
            OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ja"))
        ];

        // 多路：原图 → 中心裁切 → 放大；避免旁路「100」和高对比把 4 吃成 0。
        BitmapSource[] variants =
        [
            image,
            CenterCrop(image, 0.62),
            Upscale(image, 2.0),
            Upscale(CenterCrop(image, 0.62), 2.0)
        ];

        var candidates = new List<int>();
        foreach (BitmapSource variant in variants)
        {
            IReadOnlyList<int> found = await CollectLineDigitsAsync(variant, engines, cancellationToken);
            candidates.AddRange(found);
            int? preferred = PreferDifficultyReading(found);
            // 已有含 2–9 的可信三位数则早停（如 140）。
            if (preferred is int p && HasNonZeroOneDigit(p) && p is >= 100 and <= 999)
                return p;
        }

        int? best = PreferDifficultyReading(candidates);
        if (best is not null)
            return best;

        // 形态学兜底（含仅 100/110 等宽字符组合）。
        if (morphFirst is int m && !IsSuspiciousDifficultyOcr(m))
            return m;
        int? morph = TryReadStylizedDigits(image);
        if (morph is int m2 && !IsSuspiciousDifficultyOcr(m2))
            return m2;
        return null;
    }

    /// <summary>按行把 word 拼成一行，再抽出数字。</summary>
    private static async Task<IReadOnlyList<int>> CollectLineDigitsAsync(
        BitmapSource image, OcrEngine?[] engines, CancellationToken cancellationToken)
    {
        var candidates = new List<int>();
        foreach (OcrEngine? engine in engines)
        {
            if (engine is null) continue;
            using SoftwareBitmap softwareBitmap = await ToSoftwareBitmapAsync(image, cancellationToken);
            OcrResult result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
            foreach (OcrLine line in result.Lines)
            {
                if (line.Words.Count == 0) continue;
                string lineText = string.Concat(line.Words.Select(word => word.Text));
                string digits = new(lineText.Where(char.IsDigit).ToArray());
                if (digits.Length == 0) continue;
                if (int.TryParse(digits, out int value) && value is >= 1 and <= 999)
                    candidates.Add(value);
            }
        }
        return candidates;
    }

    private static bool HasNonZeroOneDigit(int v) =>
        v.ToString().Any(c => c is >= '2' and <= '9');

    /// <summary>游戏里几乎不会单独显示的数：OCR 常把 140/130/120 误成这些。</summary>
    public static bool IsUnreliableDifficultyReading(int v) =>
        v is 1 or 11 or 100 or 101 or 110 or 111;

    private static bool IsSuspiciousDifficultyOcr(int v) => IsUnreliableDifficultyReading(v);

    /// <summary>
    /// 多候选：优先含 2–9 的三位数；若只剩 100/111 等可疑值则视为失败。
    /// </summary>
    private static int? PreferDifficultyReading(IReadOnlyList<int> candidates)
    {
        if (candidates.Count == 0) return null;

        static int? PickMajority(IEnumerable<int> pool)
        {
            List<int> list = pool.ToList();
            if (list.Count == 0) return null;
            return list.GroupBy(v => v)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => HasNonZeroOneDigit(g.Key) ? 1 : 0)
                .ThenByDescending(g => g.Key)
                .First()
                .Key;
        }

        int? threeRich = PickMajority(candidates.Where(v =>
            v is >= 100 and <= 999 && HasNonZeroOneDigit(v)));
        if (threeRich is not null) return threeRich;

        int? three = PickMajority(candidates.Where(v =>
            v is >= 100 and <= 999 && !IsSuspiciousDifficultyOcr(v)));
        if (three is not null) return three;

        int? twoRich = PickMajority(candidates.Where(v =>
            v is >= 10 and <= 99 && HasNonZeroOneDigit(v)));
        if (twoRich is not null) return twoRich;

        int? two = PickMajority(candidates.Where(v =>
            v is >= 10 and <= 99 && !IsSuspiciousDifficultyOcr(v)));
        if (two is not null) return two;

        // 只剩 100/111/1 等：不可信，返回空让上层重试或走列表。
        return null;
    }

    private static BitmapSource Upscale(BitmapSource source, double scale)
    {
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static BitmapSource CenterCrop(BitmapSource source, double keepRatio)
    {
        keepRatio = Math.Clamp(keepRatio, 0.3, 1.0);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int cropW = Math.Max(8, (int)Math.Round(width * keepRatio));
        int cropH = Math.Max(8, (int)Math.Round(height * keepRatio));
        int x = Math.Max(0, (width - cropW) / 2);
        int y = Math.Max(0, (height - cropH) / 2);
        if (x + cropW > width) cropW = width - x;
        if (y + cropH > height) cropH = height - y;
        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, cropW, cropH));
        cropped.Freeze();
        return cropped;
    }

    /// <summary>
    /// 按列切分 1–3 个数字并做简易形态分类。
    /// 旧逻辑把宽字符一律当 0，会把开口「4」读成 0，从而把 140 读成 100。
    /// </summary>
    private static int? TryReadStylizedDigits(BitmapSource source)
    {
        var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        int width = gray.PixelWidth, height = gray.PixelHeight, stride = width;
        byte[] pixels = new byte[stride * height];
        gray.CopyPixels(pixels, stride, 0);

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
            int? digit = ClassifyStylizedDigit(pixels, stride, left, right, top, bottom);
            if (digit is null) return null;
            value = value * 10 + digit.Value;
        }
        return value;
    }

    private static int? ClassifyStylizedDigit(
        byte[] pixels, int stride, int left, int right, int top, int bottom)
    {
        int w = right - left + 1;
        int h = bottom - top + 1;
        if (w < 4 || h < 8) return null;
        double aspect = w / (double)h;
        if (aspect <= 0.58) return 1;

        // 上沿中部开口 → 开口「4」；带斜杠的「0」上沿是闭合的。
        int topBand = Math.Max(2, h / 6);
        int midY0 = top + (int)(h * 0.42);
        int midY1 = top + (int)(h * 0.62);
        int x0 = left + w / 3;
        int x1 = left + (2 * w) / 3;

        int topCenterInk = 0, topCenterTotal = 0;
        for (int y = top; y < top + topBand; y++)
        for (int x = x0; x <= x1; x++)
        {
            topCenterTotal++;
            if (pixels[y * stride + x] < 105) topCenterInk++;
        }
        double topCenterFill = topCenterTotal == 0 ? 1 : topCenterInk / (double)topCenterTotal;

        int midBarInk = 0, midBarTotal = 0;
        for (int y = midY0; y <= midY1; y++)
        for (int x = left; x <= right; x++)
        {
            midBarTotal++;
            if (pixels[y * stride + x] < 105) midBarInk++;
        }
        double midBarFill = midBarTotal == 0 ? 0 : midBarInk / (double)midBarTotal;

        // 斜杠「0」：主对角线方向墨水偏多。
        int diagInk = 0, antiInk = 0, diagSamples = 0;
        for (int i = 0; i < Math.Min(w, h); i++)
        {
            int y = bottom - i;
            int xDiag = left + i * (w - 1) / Math.Max(1, h - 1);
            int xAnti = right - i * (w - 1) / Math.Max(1, h - 1);
            if (y < top || y > bottom) continue;
            diagSamples++;
            if (pixels[y * stride + xDiag] < 105) diagInk++;
            if (pixels[y * stride + xAnti] < 105) antiInk++;
        }
        double diagFill = diagSamples == 0 ? 0 : diagInk / (double)diagSamples;
        double antiFill = diagSamples == 0 ? 0 : antiInk / (double)diagSamples;

        bool openTop = topCenterFill < 0.40;
        bool hasMidBar = midBarFill > 0.28;
        // 斜杠 0：主对角线墨水明显高于反对角线；开口 4 两条接近。
        bool slashZero = diagFill > 0.55 && diagFill > antiFill + 0.18;

        if (slashZero) return 0;
        if (openTop && hasMidBar) return 4;
        if (!openTop && aspect >= 0.68) return 0;

        // 其余宽字符：仍可能是 2/3/5…，此处不强行猜，交给 OCR。
        return null;
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
