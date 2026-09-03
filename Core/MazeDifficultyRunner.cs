using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>迷宫准备界面（第三步）难度选择。</summary>
public sealed class MazeDifficultyRunner
{
    private const int MaxAdjustClicks = 260;
    private const int AdjustSettleMs = 280;
    private const int AfterOpenSliderMs = 600;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;

    public MazeDifficultyRunner(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
    }

    public async Task ApplyAsync(GameWindow window, CancellationToken cancellationToken)
    {
        string mode = NormalizeMode(_config.MazeDifficultyMode);
        if (mode == "keep")
        {
            _log("难度选择：保持不变，跳过。");
            return;
        }

        int target = _config.MazeDifficultyTarget;
        if (target < 1)
        {
            _log($"难度选择：自选目标无效（{target}），跳过。");
            return;
        }

        _log($"难度选择：自选目标 {target}。");
        int? current = await ReadDifficultyAsync(window, cancellationToken);
        if (current is null)
        {
            _log("难度数字 OCR 失败，尝试打开滑动选择界面后再读。");
            window = await _screen.ClickAsync(
                window, _config.DifficultyOpenSliderClick, "打开难度滑动界面", cancellationToken);
            await Task.Delay(AfterOpenSliderMs, cancellationToken);
            current = await ReadDifficultyAsync(window, cancellationToken);
        }

        if (current is null)
        {
            _log("仍无法识别当前难度，放弃调整（请检查 ROI 或系统 OCR）。");
            return;
        }

        _log($"当前难度识别为 {current.Value}。");
        if (current.Value == target)
        {
            _log("已是目标难度，点击确定后继续。");
            await _screen.ClickAsync(window, _config.DifficultyConfirmClick, "难度确定", cancellationToken);
            await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
            return;
        }

        ConfigPoint adjust = current.Value < target
            ? _config.DifficultyIncreaseClick
            : _config.DifficultyDecreaseClick;
        string adjustName = current.Value < target ? "难度+" : "难度-";
        int remaining = Math.Min(Math.Abs(target - current.Value), MaxAdjustClicks);

        for (int i = 0; i < remaining; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = await _screen.ClickAsync(window, adjust, $"{adjustName} {i + 1}/{remaining}", cancellationToken);
            await Task.Delay(AdjustSettleMs, cancellationToken);

            // 每 5 次复核，避免连点过量。
            if ((i + 1) % 5 == 0 || i + 1 == remaining)
            {
                int? again = await ReadDifficultyAsync(window, cancellationToken);
                if (again is int value)
                {
                    _log($"难度复核：{value}（目标 {target}）。");
                    if (value == target)
                        break;
                    if ((value < target && adjustName == "难度-") ||
                        (value > target && adjustName == "难度+"))
                    {
                        adjust = value < target
                            ? _config.DifficultyIncreaseClick
                            : _config.DifficultyDecreaseClick;
                        adjustName = value < target ? "难度+" : "难度-";
                        remaining = Math.Min(i + 1 + Math.Abs(target - value), MaxAdjustClicks);
                    }
                }
            }
        }

        int? finalValue = await ReadDifficultyAsync(window, cancellationToken);
        _log(finalValue is int v
            ? $"难度调整结束，当前识别 {v}（目标 {target}），点击确定。"
            : $"难度调整结束，未能复核数字（目标 {target}），仍点击确定。");
        await _screen.ClickAsync(window, _config.DifficultyConfirmClick, "难度确定", cancellationToken);
        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);
    }

    private async Task<int?> ReadDifficultyAsync(GameWindow window, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        RegionCapture region = _screen.CaptureRegion(
            window, _config.DifficultyDigitTopLeft, _config.DifficultyDigitSize);
        if (_config.SaveDiagnostics)
        {
            try
            {
                string dir = Path.GetFullPath(
                    Path.Combine(AppContext.BaseDirectory, _config.DiagnosticDirectory));
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"difficulty-digit-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                await using var fs = File.Create(path);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(region.Image));
                encoder.Save(fs);
            }
            catch
            {
                // 诊断失败不影响主流程
            }
        }

        return await DigitOcrService.TryReadIntAsync(region.Image, cancellationToken);
    }

    public static string NormalizeMode(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            "custom" or "自选" => "custom",
            _ => "keep"
        };
}
