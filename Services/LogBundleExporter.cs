using System.IO.Compression;
using System.Windows.Media.Imaging;

namespace BetterMuv.Services;

public static class LogBundleExporter
{
    public static int Export(string destination, string log, string diagnosticDirectory,
        (BitmapSource Image, DateTime CapturedAt)? screenshot)
    {
        // 先完整写临时包，失败时保留用户原有文件。
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        int imageCount = 0;
        var notes = new List<string>();
        var packedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                WriteText(archive, "log.txt", log);
                if (screenshot is { } captured)
                {
                    PackBitmap(archive, "screenshots/latest-game.png", captured.Image);
                    packedRelativePaths.Add("screenshots/latest-game.png");
                    imageCount++;
                    notes.Add($"最近运行截图时间：{captured.CapturedAt:yyyy-MM-dd HH:mm:ss.fff}（非导出时实时截图）。");
                }
                else notes.Add("没有可用的内存截图；若运行中已归档，仍会打包 diagnostics 下的整批次截图。");

                if (Directory.Exists(diagnosticDirectory))
                {
                    string root = Path.GetFullPath(diagnosticDirectory);
                    foreach (string file in Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories)
                                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                        string entryName = "diagnostics/" + relative;
                        if (!packedRelativePaths.Add(entryName))
                            continue;

                        try
                        {
                            // 打开成功后才建立条目，正在写入的图跳过并记录。
                            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                            using Stream output = archive.CreateEntry(entryName).Open();
                            input.CopyTo(output);
                            imageCount++;
                        }
                        catch (IOException ex)
                        {
                            notes.Add($"诊断图未能完整打包：{relative}：{ex.Message}");
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            notes.Add($"诊断图无法读取：{relative}：{ex.Message}");
                        }
                    }
                }

                notes.Add("diagnostics 包含本次运行批次（run）下的全部截图：screenshots 为运行中按间隔归档的客户区整图，task-* 为各任务诊断图。");
                notes.Add("一条龙会保留批次内每个任务的目录；开始下一次新运行前会清空上一批次目录。");
                WriteText(archive, "说明.txt", string.Join(Environment.NewLine, notes));
            }
            File.Move(temporary, destination, overwrite: true);
            return imageCount;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void PackBitmap(ZipArchive archive, string entryName, BitmapSource image)
    {
        using Stream output = archive.CreateEntry(entryName).Open();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        // WPF 编码器需要可定位流，ZIP 条目流不支持 Seek。
        using var encoded = new MemoryStream();
        encoder.Save(encoded);
        encoded.Position = 0;
        encoded.CopyTo(output);
    }

    private static void WriteText(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }
}
