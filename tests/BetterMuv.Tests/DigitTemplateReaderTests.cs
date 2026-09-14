using System.Windows.Media.Imaging;
using BetterMuv.Services;

namespace BetterMuv.Tests;

public class DigitTemplateReaderTests
{
    private static string DigitsDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Assets", "Templates", "digits"));

    private static string ProbeDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "_logtmp", "probe", "digit-fixed"));

    private static BitmapSource LoadPng(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    [Fact]
    public void AllDigitTemplatesExistAtFixedSize()
    {
        Assert.True(Directory.Exists(DigitsDir), $"missing {DigitsDir}");
        for (int d = 0; d <= 9; d++)
        {
            string path = Path.Combine(DigitsDir, $"{d}.png");
            Assert.True(File.Exists(path), $"missing {path}");
            BitmapSource img = LoadPng(path);
            Assert.Equal(DigitTemplateReader.SlotWidth, img.PixelWidth);
            Assert.Equal(DigitTemplateReader.SlotHeight, img.PixelHeight);
        }
    }

    [Fact]
    public void EachDigitTemplateSelfMatchesAboveNinetyPercent()
    {
        var reader = new DigitTemplateReader(DigitsDir);
        Assert.Equal(10, reader.LoadedCount);

        // 用仅含该数字槽位的合成 ROI：把模板贴到 slot0，其余槽贴 0 模板。
        byte[] zero = File.ReadAllBytes(Path.Combine(DigitsDir, "0.png")); // wrong - need bitmap
        _ = zero;

        for (int digit = 0; digit <= 9; digit++)
        {
            BitmapSource tpl = LoadPng(Path.Combine(DigitsDir, $"{digit}.png"));
            // 构造 840×300 ROI，三槽都放同一数字模板 → 读成 ddd
            var roi = BuildRoiWithDigit(tpl, tpl, tpl);
            var slots = reader.MatchAllSlots(roi);
            Assert.Equal(3, slots.Count);
            foreach ((int? d, double score) in slots)
            {
                Assert.Equal(digit, d);
                Assert.True(score >= DigitTemplateReader.MatchThreshold,
                    $"digit {digit} score {score:F3} < {DigitTemplateReader.MatchThreshold}");
            }
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(199)]
    [InlineData(195)]
    public void KnownFloorRoisReadCorrectlyWithHighScores(int floor)
    {
        string path = Path.Combine(ProbeDir, $"floor-{floor}.png");
        // 优先用 fixed3 新采集
        string alt = Path.GetFullPath(Path.Combine(ProbeDir, "..", "digit-fixed3", $"floor-{floor}.png"));
        if (File.Exists(alt))
            path = alt;
        if (!File.Exists(path))
            return;

        var reader = new DigitTemplateReader(DigitsDir);
        BitmapSource roi = LoadPng(path);
        var slots = reader.MatchAllSlots(roi);
        string expected = floor.ToString("D3");
        for (int i = 0; i < 3; i++)
        {
            int want = expected[i] - '0';
            Assert.True(slots[i].Score >= DigitTemplateReader.MatchThreshold,
                $"floor {floor} slot {i} score {slots[i].Score:F3}");
            Assert.Equal(want, slots[i].Digit);
        }
        Assert.Equal(floor, reader.TryRead(roi));
    }

    private static BitmapSource BuildRoiWithDigit(BitmapSource a, BitmapSource b, BitmapSource c)
    {
        int w = 840, h = 300;
        var dv = new System.Windows.Media.DrawingVisual();
        using (System.Windows.Media.DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, w, h));
            dc.DrawImage(a, new System.Windows.Rect(
                DigitTemplateReader.SlotOriginX,
                DigitTemplateReader.SlotOriginY,
                DigitTemplateReader.SlotWidth,
                DigitTemplateReader.SlotHeight));
            dc.DrawImage(b, new System.Windows.Rect(
                DigitTemplateReader.SlotOriginX + DigitTemplateReader.SlotPitch,
                DigitTemplateReader.SlotOriginY,
                DigitTemplateReader.SlotWidth,
                DigitTemplateReader.SlotHeight));
            dc.DrawImage(c, new System.Windows.Rect(
                DigitTemplateReader.SlotOriginX + DigitTemplateReader.SlotPitch * 2,
                DigitTemplateReader.SlotOriginY,
                DigitTemplateReader.SlotWidth,
                DigitTemplateReader.SlotHeight));
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
