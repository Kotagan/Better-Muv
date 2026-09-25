using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterMuv.Services;

namespace BetterMuv.Tests;

public class LogBundleExporterTests
{
    [Fact]
    public void BundlePreservesLogScreenshotAndNestedDiagnostics()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string run = Path.Combine(root, "run-batch");
            string screenshots = Path.Combine(run, "screenshots");
            string task = Path.Combine(run, "task-maze-1");
            Directory.CreateDirectory(screenshots);
            Directory.CreateDirectory(task);
            byte[] pixels = [0, 0, 255, 255];
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
            image.Freeze();
            WritePng(Path.Combine(screenshots, "game-1.png"), image);
            WritePng(Path.Combine(task, "failure.png"), image);
            File.WriteAllText(Path.Combine(task, "unrelated.txt"), "exclude");
            string destination = Path.Combine(root, "bundle.zip");
            Assert.Equal(3, LogBundleExporter.Export(destination, "中文日志\n第二行", run,
                (image, new DateTime(2026, 9, 7, 18, 44, 47))));
            using var zip = ZipFile.OpenRead(destination);
            using var reader = new StreamReader(zip.GetEntry("log.txt")!.Open());
            Assert.Equal("中文日志\n第二行", reader.ReadToEnd());
            Assert.NotNull(zip.GetEntry("screenshots/latest-game.png"));
            Assert.NotNull(zip.GetEntry("diagnostics/screenshots/game-1.png"));
            Assert.NotNull(zip.GetEntry("diagnostics/task-maze-1/failure.png"));
            Assert.Null(zip.GetEntry("diagnostics/task-maze-1/unrelated.txt"));
            using var notes = new StreamReader(zip.GetEntry("说明.txt")!.Open());
            string explanation = notes.ReadToEnd();
            Assert.Contains("screenshots", explanation);
            Assert.Contains("导出", explanation);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingScreenshotsStillExportsLogAndExplanation()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string destination = Path.Combine(root, "bundle.zip");
            Assert.Equal(0, LogBundleExporter.Export(destination, "log", Path.Combine(root, "missing"), null));
            using var zip = ZipFile.OpenRead(destination);
            Assert.NotNull(zip.GetEntry("log.txt"));
            using var reader = new StreamReader(zip.GetEntry("说明.txt")!.Open());
            Assert.Contains("没有可用的内存截图", reader.ReadToEnd());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void WritePng(string path, BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
