using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>迷宫准备界面（第三步）难度选择。</summary>
public sealed class MazeDifficultyRunner
{
    private const int MaxListSearchAttempts = 80;
    private const int ListSettleMs = 800;
    private const int AfterOpenListMs = 600;
    private const int NearbyArrowLimit = 10;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;

    public MazeDifficultyRunner(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
    }

    public async Task<bool> ApplyAsync(GameWindow window, CancellationToken cancellationToken)
    {
        string mode = NormalizeMode(_config.MazeDifficultyMode);
        if (mode == "keep")
        {
            _log("难度选择：保持不变，跳过。");
            return true;
        }

        int target = _config.MazeDifficultyTarget;
        if (target < 1)
        {
            _log($"难度选择：自选目标无效（{target}），跳过。");
            return false;
        }

        int? current = await ReadDifficultyAsync(window, cancellationToken);
        if (current == target)
        {
            _log($"难度选择：当前区域已经是 {target}，无需调整。");
            return true;
        }

        if (current is >= 1 and <= 999 && Math.Abs(target - current.Value) <= NearbyArrowLimit)
        {
            int difference = target - current.Value;
            ConfigPoint arrow = difference > 0
                ? _config.DifficultyIncreaseClick
                : _config.DifficultyDecreaseClick;
            string direction = difference > 0 ? "右" : "左";
            int clicks = Math.Abs(difference);
            _log($"难度选择：当前 {current}，目标 {target}，距离较近，点击{direction}箭头 {clicks} 次。");
            for (int click = 1; click <= clicks; click++)
            {
                window = await _screen.ClickAsync(
                    window, arrow, $"区域{direction}箭头 {click}/{clicks}", cancellationToken);
                if (click < clicks)
                    await Task.Delay(50, cancellationToken);
            }

            await Task.Delay(300, cancellationToken);
            int? adjusted = await ReadDifficultyAsync(window, cancellationToken);
            if (adjusted == target)
            {
                _log($"左右箭头调整完成，当前区域 {target}。");
                return true;
            }

            _log($"箭头调整后识别结果为 {(adjusted?.ToString() ?? "失败")}，改用区域选择框定位 {target}。");
        }

        _log($"难度选择：打开区域选择框，目标 {target}。");
        window = await _screen.ClickAsync(
            window, _config.DifficultyOpenSliderClick, "打开区域选择", cancellationToken);
        await Task.Delay(AfterOpenListMs, cancellationToken);

        if (!await SelectFromListAsync(window, target, cancellationToken))
            return false;

        window = await _screen.ClickAsync(
            window, _config.DifficultyConfirmClick, "区域选择确定", cancellationToken);
        await Task.Delay(_config.DetectionPollIntervalMs, cancellationToken);

        int? selected = await ReadVerifiedDifficultyAsync(window, cancellationToken);
        if (selected == target)
        {
            _log($"区域选择结果复核成功：当前区域 {target}。");
            return true;
        }

        if (selected is >= 1 and <= 999 && Math.Abs(target - selected.Value) <= NearbyArrowLimit)
        {
            int difference = target - selected.Value;
            ConfigPoint arrow = difference > 0
                ? _config.DifficultyIncreaseClick
                : _config.DifficultyDecreaseClick;
            string direction = difference > 0 ? "右" : "左";
            int clicks = Math.Abs(difference);
            _log($"区域选择结果为 {selected}，目标为 {target}，自动点击{direction}箭头修正 {clicks} 次。");
            for (int click = 1; click <= clicks; click++)
            {
                window = await _screen.ClickAsync(
                    window, arrow, $"复核修正{direction}箭头 {click}/{clicks}", cancellationToken);
                if (click < clicks)
                    await Task.Delay(50, cancellationToken);
            }

            await Task.Delay(300, cancellationToken);
            int? corrected = await ReadVerifiedDifficultyAsync(window, cancellationToken);
            if (corrected == target)
            {
                _log($"区域选择结果已修正并复核成功：当前区域 {target}。");
                return true;
            }

            _log($"区域修正后复核失败：识别结果 {(corrected?.ToString() ?? "失败")}，目标 {target}。");
            return false;
        }

        _log($"区域选择后复核失败：识别结果 {(selected?.ToString() ?? "失败")}，目标 {target}。");
        return false;
    }

    private async Task<int?> ReadVerifiedDifficultyAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            int? value = await ReadDifficultyAsync(window, cancellationToken);
            if (value is >= 1 and <= 999)
                return value;
            if (attempt < 3)
                await Task.Delay(200, cancellationToken);
        }
        return null;
    }

    private async Task<bool> SelectFromListAsync(
        GameWindow window, int target, CancellationToken cancellationToken)
    {
        int scrollDirection = -1; // 列表初始从 200 开始；默认向下寻找较小区域。
        for (int attempt = 1; attempt <= MaxListSearchAttempts; attempt++)
        {
            window = _screen.Refresh(window);
            int bandHeight = Math.Max(80, _config.DifficultyListSize.Height / 4);
            ConfigSize bandSize = new(_config.DifficultyListSize.Width, bandHeight);
            ConfigPoint bottomTopLeft = new(
                _config.DifficultyListTopLeft.X,
                _config.DifficultyListTopLeft.Y + _config.DifficultyListSize.Height - bandHeight);
            RegionCapture topRegion = _screen.CaptureRegion(
                window, _config.DifficultyListTopLeft, bandSize);
            RegionCapture bottomRegion = _screen.CaptureRegion(window, bottomTopLeft, bandSize);
            IReadOnlyList<DigitOcrService.LocatedNumber> topNumbers =
                await DigitOcrService.LocateNumbersAsync(topRegion.Image, cancellationToken);
            IReadOnlyList<DigitOcrService.LocatedNumber> bottomNumbers =
                await DigitOcrService.LocateNumbersAsync(bottomRegion.Image, cancellationToken);
            var readings = topNumbers.Select(n => (Number: n, Region: topRegion))
                .Concat(bottomNumbers.Select(n => (Number: n, Region: bottomRegion)))
                .ToList();
            IReadOnlyList<DigitOcrService.LocatedNumber> numbers = readings.Select(r => r.Number).ToList();
            IReadOnlyList<DigitOcrService.LocatedNumber> areas = PickAreaNumberCluster(numbers);
            string rawText = string.Join(',', numbers.Select(n => n.Value));
            string areaText = string.Join(',', areas.Select(n => n.Value));
            _log($"区域列表顶部/底部 OCR：原始 [{rawText}]，有效区域 [{areaText}]。");
            DigitOcrService.LocatedNumber? hit = areas.FirstOrDefault(n => n.Value == target);
            if (hit is not null)
            {
                RegionCapture region = readings.First(r => r.Number == hit).Region;
                double x = region.ScreenRect.Left + hit.CenterX * region.ScreenRect.Width / region.Image.PixelWidth;
                double y = region.ScreenRect.Top + hit.CenterY * region.ScreenRect.Height / region.Image.PixelHeight;
                _log($"区域列表找到 {target}，点击目标行（第 {attempt} 次探测）。");
                await _screen.ClickScreenAsync(window, new System.Windows.Point(x, y), cancellationToken);
                await Task.Delay(ListSettleMs, cancellationToken);
                return true;
            }

            int[] visible = areas.Select(n => n.Value).ToArray();
            if (visible.Length == 0)
            {
                _log($"区域列表第 {attempt} 次未识别到数字，停止，避免盲目滚动。");
                return false;
            }
            int min = visible.Min(), max = visible.Max();
            if (target >= min && target <= max)
            {
                _log($"目标 {target} 已进入可见区间 {min}–{max}，切换为整个列表搜索。");
                RegionCapture fullRegion = _screen.CaptureRegion(
                    window, _config.DifficultyListTopLeft, _config.DifficultyListSize);
                IReadOnlyList<DigitOcrService.LocatedNumber> fullNumbers =
                    await DigitOcrService.LocateNumbersAsync(fullRegion.Image, cancellationToken);
                DigitOcrService.LocatedNumber? fullHit = fullNumbers.FirstOrDefault(n => n.Value == target);
                _log($"整个列表 OCR：[{string.Join(',', fullNumbers.Select(n => n.Value))}]。");
                if (fullHit is not null)
                {
                    double x = fullRegion.ScreenRect.Left +
                        fullHit.CenterX * fullRegion.ScreenRect.Width / fullRegion.Image.PixelWidth;
                    double y = fullRegion.ScreenRect.Top +
                        fullHit.CenterY * fullRegion.ScreenRect.Height / fullRegion.Image.PixelHeight;
                    _log($"整个列表找到区域 {target}，点击目标行。");
                    await _screen.ClickScreenAsync(window, new System.Windows.Point(x, y), cancellationToken);
                    await Task.Delay(ListSettleMs, cancellationToken);
                    return true;
                }

                // 游戏字体中的 0 带斜杠，系统 OCR 常把 100 只读成 1。
                // 若这个“1”在垂直行序上正好夹在 101 与 99 之间，它就是目标行本身；
                // 直接使用该文字所在行中心，比跨行插值更准确。
                DigitOcrService.LocatedNumber? truncatedHundred = fullNumbers
                    .Where(n => n.Value == target / 100 && target % 100 == 0)
                    .FirstOrDefault(candidate =>
                        fullNumbers.Any(n => n.Value == target + 1 && n.CenterY < candidate.CenterY) &&
                        fullNumbers.Any(n => n.Value == target - 1 && n.CenterY > candidate.CenterY));
                if (truncatedHundred is not null)
                {
                    double x = fullRegion.ScreenRect.Left + fullRegion.ScreenRect.Width / 2.0;
                    double y = fullRegion.ScreenRect.Top +
                        truncatedHundred.CenterY * fullRegion.ScreenRect.Height / fullRegion.Image.PixelHeight;
                    _log($"OCR 将 {target} 读成 {truncatedHundred.Value}，已通过上下相邻行 " +
                         $"{target + 1}/{target - 1} 确认并点击其实际行中心。");
                    await _screen.ClickScreenAsync(window, new System.Windows.Point(x, y), cancellationToken);
                    await Task.Delay(ListSettleMs, cancellationToken);
                    return true;
                }

                IReadOnlyList<DigitOcrService.LocatedNumber> fullAreas =
                    PickAreaNumberCluster(fullNumbers);
                DigitOcrService.LocatedNumber? nearestAbove = fullAreas
                    .Where(n => n.Value > target)
                    .OrderBy(n => n.Value)
                    .FirstOrDefault();
                DigitOcrService.LocatedNumber? nearestBelow = fullAreas
                    .Where(n => n.Value < target)
                    .OrderByDescending(n => n.Value)
                    .FirstOrDefault();
                if (nearestAbove is not null && nearestBelow is not null &&
                    nearestAbove.Value - nearestBelow.Value <= 10 &&
                    nearestBelow.CenterY > nearestAbove.CenterY)
                {
                    // 使用目标上下最近两行的中心插值，相当于按可见行框定位；
                    // 避免用列表最远边界跨多行计算导致误差落到相邻行。
                    double ratio = (nearestAbove.Value - target) /
                        (double)(nearestAbove.Value - nearestBelow.Value);
                    double logicalY = nearestAbove.CenterY +
                        ratio * (nearestBelow.CenterY - nearestAbove.CenterY);
                    double x = fullRegion.ScreenRect.Left + fullRegion.ScreenRect.Width / 2.0;
                    double y = fullRegion.ScreenRect.Top +
                        logicalY * fullRegion.ScreenRect.Height / fullRegion.Image.PixelHeight;
                    _log($"整块 OCR 漏读 {target}，按相邻行 {nearestAbove.Value}/{nearestBelow.Value} 的行框中心定位。");
                    await _screen.ClickScreenAsync(window, new System.Windows.Point(x, y), cancellationToken);
                    await Task.Delay(ListSettleMs, cancellationToken);
                    return true;
                }

                // 整块 OCR 可能恰好漏掉中间某一行。区域编号按行连续递减，
                // 因此只要上下边界可靠地夹住目标，就可以用两条边界行的位置插值。
                var upper = readings
                    .Where(r => areas.Contains(r.Number) && r.Number.Value == max)
                    .Select(r => (r.Number.Value, Y: ToScreenY(r.Number, r.Region)))
                    .FirstOrDefault();
                var lower = readings
                    .Where(r => areas.Contains(r.Number) && r.Number.Value == min)
                    .Select(r => (r.Number.Value, Y: ToScreenY(r.Number, r.Region)))
                    .FirstOrDefault();
                if (max > min && max - min <= 10 && lower.Y > upper.Y)
                {
                    double rowRatio = (max - target) / (double)(max - min);
                    double x = fullRegion.ScreenRect.Left + fullRegion.ScreenRect.Width / 2.0;
                    double y = upper.Y + rowRatio * (lower.Y - upper.Y);
                    _log($"整块 OCR 未直接读出 {target}，按边界 {max}–{min} 插值定位目标行。");
                    await _screen.ClickScreenAsync(window, new System.Windows.Point(x, y), cancellationToken);
                    await Task.Delay(ListSettleMs, cancellationToken);
                    return true;
                }

                _log($"目标已进入区间但无法可靠定位 {target}，停止，避免误点。");
                return false;
            }
            if (visible.Length >= 2)
                scrollDirection = target < min ? -1 : target > max ? 1 : 0;
            else
                _log("本次边界只有一个可信数字，保持上次滚动方向。");
            if (scrollDirection == 0)
            {
                _log($"目标 {target} 位于可见范围 {min}–{max}，但未定位到对应行，停止。");
                return false;
            }
            int gap = target < min ? min - target : target - max;
            ConfigPoint center = new(
                _config.DifficultyListTopLeft.X + _config.DifficultyListSize.Width / 2,
                _config.DifficultyListTopLeft.Y + _config.DifficultyListSize.Height / 2);
            // 实测一次满幅拖动约移动 4 行，而一个可视区域约 7 行。
            // 目标还在一个可视区域以外时连续拖动数次再识别；进入 10 行内才改为小步。
            bool isNear = gap <= 10;
            int dragRepeats = isNear ? 1 : Math.Clamp((gap - 7) / 4, 1, 5);
            double dragRatio = isNear ? 0.35 : 0.88;
            int settleMs = isNear ? ListSettleMs / 2 : 250;
            int dragDistance = Math.Max(100, (int)Math.Round(_config.DifficultyListSize.Height * dragRatio));
            ConfigPoint fromRef = new(center.X, center.Y + (scrollDirection < 0 ? dragDistance / 2 : -dragDistance / 2));
            ConfigPoint toRef = new(center.X, center.Y + (scrollDirection < 0 ? -dragDistance / 2 : dragDistance / 2));
            System.Windows.Point from = topRegion.Geometry.ToScreen(fromRef);
            System.Windows.Point to = topRegion.Geometry.ToScreen(toRef);
            string pace = isNear ? "小步" : $"大步连续 {dragRepeats} 次";
            _log($"区域列表可见 {min}–{max}，距目标约 {gap}，向{(scrollDirection < 0 ? "下" : "上")}{pace}拖动查找 {target}。");
            for (int drag = 0; drag < dragRepeats; drag++)
            {
                await _screen.Mouse.DragAsync(window.Handle, from, to, cancellationToken, isNear ? 6 : 4);
                if (drag + 1 < dragRepeats)
                    await Task.Delay(80, cancellationToken);
            }
            _log($"{dragRepeats} 次拖动均已完成并松开鼠标，等待 {settleMs}ms 后再截图识别。");
            await Task.Delay(settleMs, cancellationToken);
        }
        _log($"滚动查找区域 {target} 超过上限，停止。");
        return false;
    }

    private static double ToScreenY(
        DigitOcrService.LocatedNumber number, RegionCapture region) =>
        region.ScreenRect.Top + number.CenterY * region.ScreenRect.Height / region.Image.PixelHeight;

    /// <summary>
    /// 区域列表相邻行编号连续；取跨度不超过 10、成员最多的数字簇，
    /// 排除 OCR 混入的孤立数字（例如图标、等级或把 199 误读成 1）。
    /// </summary>
    private static IReadOnlyList<DigitOcrService.LocatedNumber> PickAreaNumberCluster(
        IReadOnlyList<DigitOcrService.LocatedNumber> numbers)
    {
        DigitOcrService.LocatedNumber[] candidates = numbers
            .Where(n => n.Value is >= 1 and <= 999)
            .GroupBy(n => n.Value)
            .Select(group => group.First())
            .OrderBy(n => n.Value)
            .ToArray();
        if (candidates.Length <= 1) return candidates;

        List<DigitOcrService.LocatedNumber> best = [];
        for (int i = 0; i < candidates.Length; i++)
        {
            List<DigitOcrService.LocatedNumber> cluster = candidates
                .Where(n => n.Value >= candidates[i].Value && n.Value <= candidates[i].Value + 10)
                .ToList();
            if (cluster.Count > best.Count ||
                (cluster.Count == best.Count && cluster.Max(n => n.Value) > best.Max(n => n.Value)))
                best = cluster;
        }
        return best;
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
