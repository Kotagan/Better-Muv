using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>迷宫准备界面（第三步）难度选择。</summary>
public sealed class MazeDifficultyRunner
{
    private const int MaxListSearchAttempts = 80;
    private const int ListSettleMs = 1100;
    private const int AfterOpenListMs = 1000;
    private const int AfterConfirmMs = 1100;
    private const int ArrowClickIntervalMs = 90;
    private const int MaxListSelectRetries = 4;
    private const double ListConfirmThreshold = 0.78;

    private const double ArrowPresenceThreshold = 0.68;

    private enum Strategy
    {
        Unset,
        Arrows,
        List
    }

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _decreaseArrowMatcher;
    private readonly TemplateMatcher _increaseArrowMatcher;
    private readonly TemplateMatcher _listConfirmMatcher;
    /// <summary>本轮难度选定的唯一策略；箭头与环境选择互斥，选定后不切换。</summary>
    private Strategy _strategy = Strategy.Unset;

    /// <summary>本会话已成功对准过的区域；OCR 再误读时可跳过重复选择。</summary>
    private int? _sessionConfirmedDifficulty;

    /// <summary>区域选择弹窗粉钮「選択」搜索区（1080p）。</summary>
    private static readonly ConfigPoint ListConfirmTopLeft = new(900, 880);
    private static readonly ConfigSize ListConfirmSize = new(500, 180);

    public MazeDifficultyRunner(AutomationConfig config, ScreenAutomation screen, Action<string> log)
    {
        _config = config;
        _screen = screen;
        _log = log;
        _decreaseArrowMatcher = TemplateAssets.Load("difficulty-arrow-left.png");
        _increaseArrowMatcher = TemplateAssets.Load("difficulty-arrow-right.png");
        _listConfirmMatcher = TemplateAssets.Load("area-select-ok.png");
    }

    /// <summary>尝试读出可信层数；失败时不打「禁止点击」类日志。</summary>
    public async Task<int?> TryReadTrustedFloorAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        int? floor = await ReadDifficultyAsync(window, cancellationToken);
        if (floor is >= 1 and <= 999 && !DigitOcrService.IsUnreliableDifficultyReading(floor.Value))
            return floor;
        return null;
    }

    /// <summary>
    /// 在点「探索準備」前必须读到可信层数；读不到则禁止盲点。
    /// </summary>
    public async Task<bool> EnsureFloorRecognizedAsync(
        GameWindow window, CancellationToken cancellationToken, int attempts = 3)
    {
        for (int i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int? floor = await TryReadTrustedFloorAsync(window, cancellationToken);
            if (floor is int ok)
            {
                _log($"已识别迷宫层数/区域：{ok}。");
                return true;
            }

            if (i + 1 < attempts)
            {
                _log($"层数模板未读出，重试（{i + 1}/{attempts}）。");
                await Task.Delay(350, cancellationToken);
            }
        }

        _log("未识别到迷宫层数，禁止点击探索準備。");
        return false;
    }

    public async Task<bool> ApplyAsync(GameWindow window, CancellationToken cancellationToken)
    {
        string mode = NormalizeMode(_config.MazeDifficultyMode);
        if (mode == "keep")
        {
            _log("难度选择：保持不变，跳过。");
            return true;
        }

        if (mode == "tower")
        {
            // 进关前已确认无右箭头；此处只保持当前层。
            _log("难度选择：爬塔模式，保持当前层（层选无右箭头）。");
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
            _sessionConfirmedDifficulty = target;
            _log($"难度选择：当前区域已经是 {target}，无需调整。");
            return true;
        }

        // 100/111 等常为误读；再读一次，仍可疑则当作未识别，避免按错误差值乱点。
        if (current is int suspicious && DigitOcrService.IsUnreliableDifficultyReading(suspicious))
        {
            _log($"难度模板 {suspicious} 不可信（易把 {target} 读成 100），重新识别。");
            await Task.Delay(200, cancellationToken);
            current = await ReadDifficultyAsync(window, cancellationToken);
            if (current == target)
            {
                _sessionConfirmedDifficulty = target;
                _log($"难度选择：复核后当前区域已经是 {target}，无需调整。");
                return true;
            }
            if (current is int again && DigitOcrService.IsUnreliableDifficultyReading(again))
            {
                if (_sessionConfirmedDifficulty == target)
                {
                    _log($"复核仍为 {again}，但本会话已确认过 {target}，跳过调整。");
                    return true;
                }
                _log($"复核仍为 {again}，忽略该读数，改用环境列表对准 {target}。");
                current = null;
            }
        }
        else if (current is null && _sessionConfirmedDifficulty == target)
        {
            _log($"难度模板失败，但本会话已确认过 {target}，跳过调整。");
            return true;
        }

        // 自选难度统一走：エリア選択 → 拖动 → OCR → 点行 → 「選択」。
        // 左右箭头仅作模板兜底能力保留，不再作为切换策略（远距易点不动/漏档）。
        if (_strategy == Strategy.Unset)
        {
            _strategy = Strategy.List;
            _log($"难度选择：本轮策略=区域列表（当前 {(current?.ToString() ?? "?")} → {target}）。");
        }
        else
        {
            _log($"难度选择：继续本轮策略=区域列表（当前 {(current?.ToString() ?? "?")} → {target}）。");
        }

        return await SelectViaListWithRetryAsync(window, target, cancellationToken);
    }

    private async Task<bool> AdjustByArrowsOnlyAsync(
        GameWindow window, int target, CancellationToken cancellationToken)
    {
        int? start = await ReadVerifiedDifficultyAsync(window, cancellationToken);
        if (start == target)
        {
            _sessionConfirmedDifficulty = target;
            _log($"左右箭头调整完成，当前区域 {target}。");
            return true;
        }

        if (start is null or < 1 or > 999)
        {
            _log("箭头策略下无法识别当前区域，失败（不改用环境选择）。");
            return false;
        }

        // 只按初始读数点固定次数。中途 OCR 常把 120 读成 12/1，若按错误读数重算会一路点到 130+。
        int difference = target - start.Value;
        bool increase = difference > 0;
        string direction = increase ? "右" : "左";
        int clicks = Math.Abs(difference);
        _log($"难度选择：当前 {start}，目标 {target}，仅用{direction}箭头模板点击 {clicks} 次（不用环境选择，不中途重算）。");
        for (int click = 1; click <= clicks; click++)
        {
            if (!await ClickDifficultyArrowAsync(window, increase, $"{direction}箭头 {click}/{clicks}", cancellationToken))
                return false;
            if (click < clicks)
                await Task.Delay(ArrowClickIntervalMs, cancellationToken);
        }

        await Task.Delay(200, cancellationToken);
        int? final = await ReadVerifiedDifficultyAsync(window, cancellationToken);
        if (final == target)
        {
            _sessionConfirmedDifficulty = target;
            _log($"左右箭头调整完成，当前区域 {target}。");
            return true;
        }

        // OCR 常把 120 读成 100/111/12；固定点击已完成则视为成功，继续点探索準備。
        _sessionConfirmedDifficulty = target;
        _log($"箭头已按计划点击 {clicks} 次；复核识别为 {(final?.ToString() ?? "失败")}（可能 OCR 误读），" +
             $"目标 {target}。按点击次数视为已到达，继续后续步骤。");
        return true;
    }

    private async Task<bool> ClickDifficultyArrowAsync(
        GameWindow window, bool increase, string reason, CancellationToken cancellationToken)
    {
        if (increase && NormalizeMode(_config.MazeDifficultyMode) == "tower")
        {
            _log($"{reason}：爬塔模式禁止点右箭头。");
            return false;
        }

        TemplateMatcher matcher = increase ? _increaseArrowMatcher : _decreaseArrowMatcher;
        ConfigPoint topLeft = increase ? _config.DifficultyIncreaseTopLeft : _config.DifficultyDecreaseTopLeft;
        ConfigSize size = increase ? _config.DifficultyIncreaseSize : _config.DifficultyDecreaseSize;
        ConfigPoint fallback = increase ? _config.DifficultyIncreaseClick : _config.DifficultyDecreaseClick;

        TemplateProbeResult probe = await _screen.ProbeAsync(
            window, matcher, topLeft, size, cancellationToken, ArrowPresenceThreshold);
        if (probe.IsMatch)
        {
            await _screen.ClickProbeAsync(window, probe, reason, cancellationToken, settleDelayMs: 0);
            return true;
        }

        if (increase)
        {
            // 爬塔/无右箭头界面：禁止写死点盲点右箭头区域。
            _log($"{reason}模板未命中（{probe.Score:F3} < {ArrowPresenceThreshold:F2}），不写死盲点右箭头。");
            return false;
        }

        _log($"{reason}模板未命中（{probe.Score:F3} < {ArrowPresenceThreshold:F2}），改用写死点。");
        await _screen.ClickAsync(window, fallback, $"{reason}(坐标)", cancellationToken);
        return true;
    }

    private async Task<bool> SelectViaListWithRetryAsync(
        GameWindow window, int target, CancellationToken cancellationToken)
    {
        // 滑动后行位置会漂：若上次点偏一格，下轮按行距纠偏，不写死屏幕坐标。
        int rowBias = 0;
        for (int attempt = 1; attempt <= MaxListSelectRetries; attempt++)
        {
            bool listAlreadyOpen = await IsListVisibleAsync(window, cancellationToken);
            if (!listAlreadyOpen)
            {
                _log($"难度选择：打开区域选择，目标 {target}（第 {attempt}/{MaxListSelectRetries} 次）。");
                window = await _screen.ClickAsync(
                    window, _config.DifficultyOpenSliderClick, "打开区域选择", cancellationToken);
                await Task.Delay(AfterOpenListMs, cancellationToken);
            }
            else
            {
                _log($"难度选择：区域列表已打开，继续选 {target}（第 {attempt}/{MaxListSelectRetries} 次" +
                     (rowBias != 0 ? $"，行偏置 {rowBias}" : "") + "）。");
            }

            if (!await WaitForListVisibleAsync(window, cancellationToken))
            {
                _log("区域列表未出现，可能未点开或仍在动画中。");
                if (attempt < MaxListSelectRetries)
                    continue;
                return false;
            }

            if (!await SelectFromListAsync(window, target, cancellationToken, rowBias))
            {
                // 选失败时点キャンセル，避免弹窗卡住下一轮。
                await _screen.ClickAsync(
                    window, new ConfigPoint(726, 980), "区域选择取消", cancellationToken);
                await Task.Delay(500, cancellationToken);
                if (attempt < MaxListSelectRetries)
                {
                    _log("区域列表未选中目标，将再试一次。");
                    continue;
                }
                return false;
            }

            // 旧坐标 (1125,930) 落在粉钮上方空白，等于没点「選択」；优先模板点粉钮中心。
            await ClickListConfirmAsync(window, cancellationToken);
            await Task.Delay(AfterConfirmMs, cancellationToken);

            if (!await WaitForListGoneAsync(window, cancellationToken))
            {
                _log("点「選択」后列表仍在，再点一次粉钮确定。");
                await ClickListConfirmAsync(window, cancellationToken);
                await Task.Delay(AfterConfirmMs, cancellationToken);
                if (!await WaitForListGoneAsync(window, cancellationToken))
                {
                    _log("区域选择弹窗仍未关闭，本轮放弃（避免误点取消）。");
                    if (attempt < MaxListSelectRetries)
                        continue;
                    return false;
                }
            }

            int? selected = await ReadVerifiedDifficultyAsync(window, cancellationToken);
            if (selected == target)
            {
                _sessionConfirmedDifficulty = target;
                _log($"区域选择复核成功：当前区域 {target}。");
                return true;
            }

            if (attempt < MaxListSelectRetries)
            {
                // 选成相邻格：下轮按实测行距上移/下移一格，而不是重用旧屏幕 Y。
                if (selected == target - 1)
                    rowBias = -1;
                else if (selected == target + 1)
                    rowBias = 1;
                else
                    rowBias = 0;
                _log($"区域选择后复核为 {(selected?.ToString() ?? "失败")}（目标 {target}），将重试。");
                continue;
            }

            if (selected is null)
            {
                // 弹窗已关但大号数字偶发读不出：列表已点中目标并点过确定，视为成功。
                _sessionConfirmedDifficulty = target;
                _log($"区域选择后大号数字暂未读出（目标 {target}）；弹窗已关且已点「選択」，视为成功。");
                return true;
            }

            _log($"区域选择后复核为 {selected}（目标 {target}），未对准。");
            return false;
        }

        return false;
    }

    private async Task ClickListConfirmAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult confirm = await _screen.ProbeAsync(
            window, _listConfirmMatcher, ListConfirmTopLeft, ListConfirmSize,
            cancellationToken, ListConfirmThreshold);
        if (confirm.IsMatch)
        {
            _log($"区域选择・選択：模板命中 {confirm.Score:F3}，点击中心。");
            await _screen.ClickProbeAsync(window, confirm, "区域选择・選択", cancellationToken, settleDelayMs: 150);
            return;
        }

        _log($"区域选择・選択：模板未命中（{confirm.Score:F3}），改用写死点 " +
             $"({_config.DifficultyConfirmClick.X},{_config.DifficultyConfirmClick.Y})。");
        await _screen.ClickAsync(window, _config.DifficultyConfirmClick, "区域选择・選択(坐标)", cancellationToken);
    }

    private async Task<bool> IsListVisibleAsync(GameWindow window, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        RegionCapture list = _screen.CaptureRegion(
            window, _config.DifficultyListTopLeft, _config.DifficultyListSize);
        IReadOnlyList<DigitOcrService.LocatedNumber> numbers =
            await DigitOcrService.LocateNumbersAsync(list.Image, cancellationToken);
        return numbers.Any(n => n.Value is >= 1 and <= 999);
    }

    private async Task<bool> WaitForListVisibleAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        for (int i = 0; i < 5; i++)
        {
            if (await IsListVisibleAsync(window, cancellationToken))
                return true;
            await Task.Delay(250, cancellationToken);
        }
        return false;
    }

    private async Task<bool> WaitForListGoneAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        for (int i = 0; i < 8; i++)
        {
            if (!await IsListVisibleAsync(window, cancellationToken))
                return true;
            await Task.Delay(200, cancellationToken);
        }
        return !await IsListVisibleAsync(window, cancellationToken);
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
        GameWindow window, int target, CancellationToken cancellationToken, int rowBias = 0)
    {
        int scrollDirection = -1; // 列表初始偏高编号；默认向下寻找较小区域。
        CaptureGeometry geometry = _screen.Geometry(window);
        for (int attempt = 1; attempt <= MaxListSearchAttempts; attempt++)
        {
            // 滑动惯性停稳前坐标会漂：连续两次 OCR 编号集合一致才点。
            var stable = await CaptureStableListAsync(window, cancellationToken);
            if (stable is null)
            {
                _log($"区域列表第 {attempt} 次未能稳定识别，停止。");
                return false;
            }

            (RegionCapture fullRegion, IReadOnlyList<DigitOcrService.LocatedNumber> fullNumbers,
                IReadOnlyList<DigitOcrService.LocatedNumber> areas) = stable.Value;
            _log($"区域列表 OCR（第 {attempt} 次）：原始 [{string.Join(',', fullNumbers.Select(n => n.Value))}]，" +
                 $"有效 [{string.Join(',', areas.Select(n => n.Value))}]。");

            DigitOcrService.LocatedNumber? hit = areas.FirstOrDefault(n => n.Value == target)
                ?? fullNumbers.FirstOrDefault(n => n.Value == target);
            if (hit is not null)
            {
                await ClickListRowAsync(window, fullRegion, areas, hit, target, rowBias, cancellationToken);
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
                // 游戏字体中的 0 带斜杠，系统 OCR 常把 100 只读成 1。
                DigitOcrService.LocatedNumber? truncatedHundred = fullNumbers
                    .Where(n => n.Value == target / 100 && target % 100 == 0)
                    .FirstOrDefault(candidate =>
                        fullNumbers.Any(n => n.Value == target + 1 && n.CenterY < candidate.CenterY) &&
                        fullNumbers.Any(n => n.Value == target - 1 && n.CenterY > candidate.CenterY));
                if (truncatedHundred is not null)
                {
                    _log($"OCR 将 {target} 读成 {truncatedHundred.Value}，已通过上下相邻行确认并点击。");
                    await ClickListRowAsync(
                        window, fullRegion, areas, truncatedHundred, target, rowBias, cancellationToken);
                    return true;
                }

                DigitOcrService.LocatedNumber? nearestAbove = areas
                    .Where(n => n.Value > target).OrderBy(n => n.Value).FirstOrDefault();
                DigitOcrService.LocatedNumber? nearestBelow = areas
                    .Where(n => n.Value < target).OrderByDescending(n => n.Value).FirstOrDefault();
                if (nearestAbove is not null && nearestBelow is not null &&
                    nearestAbove.Value - nearestBelow.Value <= 10 &&
                    nearestBelow.CenterY > nearestAbove.CenterY)
                {
                    double ratio = (nearestAbove.Value - target) /
                        (double)(nearestAbove.Value - nearestBelow.Value);
                    double logicalY = nearestAbove.CenterY +
                        ratio * (nearestBelow.CenterY - nearestAbove.CenterY);
                    var synthetic = new DigitOcrService.LocatedNumber(target, nearestAbove.CenterX, logicalY);
                    _log($"整块 OCR 漏读 {target}，按相邻行 {nearestAbove.Value}/{nearestBelow.Value} 插值点击。");
                    await ClickListRowAsync(
                        window, fullRegion, areas, synthetic, target, rowBias, cancellationToken);
                    return true;
                }

                _log($"目标已进入区间 {min}–{max} 但无法可靠定位 {target}，停止。");
                return false;
            }

            if (visible.Length >= 2)
                scrollDirection = target < min ? -1 : target > max ? 1 : 0;
            else
                _log("本次只有一个可信数字，保持上次滚动方向。");

            if (scrollDirection == 0)
            {
                _log($"目标 {target} 位于可见范围 {min}–{max}，但未定位到对应行，停止。");
                return false;
            }

            int gap = target < min ? min - target : target - max;
            ConfigPoint center = new(
                _config.DifficultyListTopLeft.X + _config.DifficultyListSize.Width / 2,
                _config.DifficultyListTopLeft.Y + _config.DifficultyListSize.Height / 2);
            // 滑动距离不固定；近距只小拖一次，远距少次中幅拖，拖完必须等 OCR 稳定。
            bool isNear = gap <= 8;
            int dragRepeats = isNear ? 1 : Math.Clamp((gap + 3) / 5, 1, 4);
            double dragRatio = isNear ? 0.22 : 0.55;
            int dragDistance = Math.Max(70, (int)Math.Round(_config.DifficultyListSize.Height * dragRatio));
            ConfigPoint fromRef = new(center.X, center.Y + (scrollDirection < 0 ? dragDistance / 2 : -dragDistance / 2));
            ConfigPoint toRef = new(center.X, center.Y + (scrollDirection < 0 ? -dragDistance / 2 : dragDistance / 2));
            System.Windows.Point from = geometry.ToScreen(fromRef);
            System.Windows.Point to = geometry.ToScreen(toRef);
            string pace = isNear ? "小步" : $"中幅连续 {dragRepeats} 次";
            _log($"区域列表可见 {min}–{max}，距目标约 {gap}，向{(scrollDirection < 0 ? "下" : "上")}{pace}拖动查找 {target}。");
            for (int drag = 0; drag < dragRepeats; drag++)
            {
                await _screen.Mouse.DragAsync(window.Handle, from, to, cancellationToken, isNear ? 8 : 5);
                if (drag + 1 < dragRepeats)
                    await Task.Delay(120, cancellationToken);
            }
            await Task.Delay(isNear ? ListSettleMs : 700, cancellationToken);
        }
        _log($"滚动查找区域 {target} 超过上限，停止。");
        return false;
    }

    /// <summary>连续两次 OCR 可见编号一致，认为滑动惯性已停。</summary>
    private async Task<(
        RegionCapture Region,
        IReadOnlyList<DigitOcrService.LocatedNumber> FullNumbers,
        IReadOnlyList<DigitOcrService.LocatedNumber> Areas)?> CaptureStableListAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        int[]? previous = null;
        RegionCapture? lastRegion = null;
        IReadOnlyList<DigitOcrService.LocatedNumber>? lastFull = null;
        IReadOnlyList<DigitOcrService.LocatedNumber>? lastAreas = null;

        for (int i = 0; i < 7; i++)
        {
            window = _screen.Refresh(window);
            RegionCapture region = _screen.CaptureRegion(
                window, _config.DifficultyListTopLeft, _config.DifficultyListSize);
            IReadOnlyList<DigitOcrService.LocatedNumber> full =
                await DigitOcrService.LocateNumbersAsync(region.Image, cancellationToken);
            IReadOnlyList<DigitOcrService.LocatedNumber> areas = PickAreaNumberCluster(full);
            int[] signature = areas.Select(n => n.Value).OrderBy(v => v).ToArray();

            lastRegion = region;
            lastFull = full;
            lastAreas = areas;

            if (previous is not null &&
                previous.Length > 0 &&
                previous.SequenceEqual(signature))
            {
                return (region, full, areas);
            }

            previous = signature;
            await Task.Delay(280, cancellationToken);
        }

        if (lastRegion is null || lastFull is null || lastAreas is null)
            return null;

        return (lastRegion.Value, lastFull, lastAreas);
    }

    private async Task ClickListRowAsync(
        GameWindow window,
        RegionCapture fullRegion,
        IReadOnlyList<DigitOcrService.LocatedNumber> areas,
        DigitOcrService.LocatedNumber hit,
        int target,
        int rowBias,
        CancellationToken cancellationToken)
    {
        double logicalY = ResolveListRowY(target, hit, areas, rowBias);
        double x = fullRegion.ScreenRect.Left + fullRegion.ScreenRect.Width / 2.0;
        double y = fullRegion.ScreenRect.Top +
            logicalY * fullRegion.ScreenRect.Height / fullRegion.Image.PixelHeight;
        _log($"区域列表找到 {target}，按相邻行推算点击" +
             (rowBias != 0 ? $"（行偏置 {rowBias}）" : "") + "。");
        await _screen.ClickScreenAsync(window, new System.Windows.Point(x, y), cancellationToken, parkCursor: false);
        await Task.Delay(ListSettleMs, cancellationToken);
        _screen.ParkCursorAway(window);
    }

    /// <summary>
    /// 用相邻行中点定行心，避免 OCR 单字框偏下点到下一格；
    /// rowBias 为复核偏一格后的相对纠正（单位：行）。
    /// </summary>
    private static double ResolveListRowY(
        int target,
        DigitOcrService.LocatedNumber hit,
        IReadOnlyList<DigitOcrService.LocatedNumber> areas,
        int rowBias)
    {
        Dictionary<int, DigitOcrService.LocatedNumber> byValue = areas
            .GroupBy(n => n.Value)
            .ToDictionary(g => g.Key, g => g.First());

        double? gap = EstimateRowGap(areas);
        double y;
        bool hasAbove = byValue.TryGetValue(target + 1, out DigitOcrService.LocatedNumber? above);
        bool hasBelow = byValue.TryGetValue(target - 1, out DigitOcrService.LocatedNumber? below);
        if (hasAbove && hasBelow && above is not null && below is not null)
            y = (above.CenterY + below.CenterY) / 2.0;
        else if (hasAbove && above is not null)
            y = above.CenterY + (gap ?? 80);
        else if (hasBelow && below is not null)
            y = below.CenterY - (gap ?? 80);
        else
            y = hit.CenterY;

        // 数字框常略偏行底；默认略上移，减少点到下一行。
        double step = gap ?? 80;
        y -= step * 0.1;
        y += rowBias * step;
        return y;
    }

    private static double? EstimateRowGap(IReadOnlyList<DigitOcrService.LocatedNumber> areas)
    {
        DigitOcrService.LocatedNumber[] ordered = areas.OrderBy(n => n.CenterY).ToArray();
        if (ordered.Length < 2)
            return null;

        List<double> gaps = [];
        for (int i = 1; i < ordered.Length; i++)
        {
            if (Math.Abs(ordered[i - 1].Value - ordered[i].Value) == 1)
                gaps.Add(ordered[i].CenterY - ordered[i - 1].CenterY);
        }

        return gaps.Count > 0 ? gaps.Average() : null;
    }

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
            .Select(group => group.OrderBy(n => n.CenterX).First())
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

        int? value = await DigitOcrService.TryReadIntAsync(region.Image, cancellationToken);
        _log($"难度数字模板：{(value?.ToString() ?? "失败")}（ROI 1080p {_config.DifficultyDigitTopLeft.X},{_config.DifficultyDigitTopLeft.Y} " +
             $"{_config.DifficultyDigitSize.Width}×{_config.DifficultyDigitSize.Height}）。");
        return value;
    }

    public static string NormalizeMode(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            "custom" or "自选" => "custom",
            "tower" or "climb" or "爬塔" => "tower",
            _ => "keep"
        };

    public static bool RequiresRecognizedFloor(string? mode) =>
        NormalizeMode(mode) is "custom" or "tower";

    /// <summary>爬塔层选：可读层数，且右箭头不存在。</summary>
    public async Task<bool> EnsureTowerFloorSelectAsync(
        GameWindow window, CancellationToken cancellationToken, int attempts = 3)
    {
        for (int i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int? floor = await TryReadTrustedFloorAsync(window, cancellationToken);
            bool hasRight = await HasRightArrowAsync(window, cancellationToken);
            if (floor is int ok && !hasRight)
            {
                _log($"爬塔层选确认：层数 {ok}，右箭头未出现。");
                return true;
            }

            if (floor is null)
                _log($"爬塔层选：层数未读出（{i + 1}/{attempts}）。");
            else if (hasRight)
                _log($"爬塔层选：读到层数 {floor}，但仍检测到右箭头（非爬塔层选，{i + 1}/{attempts}）。");

            if (i + 1 < attempts)
                await Task.Delay(350, cancellationToken);
        }

        _log("爬塔层选未确认（需可读层数且无右箭头），禁止点击探索準備。");
        return false;
    }

    public async Task<bool> HasRightArrowAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult probe = await _screen.ProbeAsync(
            window,
            _increaseArrowMatcher,
            _config.DifficultyIncreaseTopLeft,
            _config.DifficultyIncreaseSize,
            cancellationToken,
            ArrowPresenceThreshold);
        if (probe.IsMatch)
            _log($"右箭头模板命中 {probe.Score:F2}（阈值 {ArrowPresenceThreshold:F2}）。");
        else
            _log($"右箭头未命中（最高 {probe.Score:F2} < {ArrowPresenceThreshold:F2}）。");
        return probe.IsMatch;
    }
}
