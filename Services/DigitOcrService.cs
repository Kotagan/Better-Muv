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

    public static async Task<int?> TryReadIntAsync(BitmapSource image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ja"))
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans"));
        if (engine is null)
            return null;

        SoftwareBitmap softwareBitmap = await ToSoftwareBitmapAsync(image, cancellationToken);
        OcrResult result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
        string text = result.Text ?? string.Empty;
        Match match = Digits.Match(text);
        if (!match.Success)
            return null;
        return int.TryParse(match.Value, out int value) ? value : null;
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

        var raStream = new InMemoryRandomAccessStream();
        using (var write = raStream.AsStreamForWrite())
        {
            await managed.CopyToAsync(write, 81920, cancellationToken);
            await write.FlushAsync(cancellationToken);
        }
        raStream.Seek(0);

        WinBitmapDecoder decoder = await WinBitmapDecoder.CreateAsync(raStream).AsTask(cancellationToken);
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
    }
}
