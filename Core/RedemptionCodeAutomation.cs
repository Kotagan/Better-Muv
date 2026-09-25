using System.Windows;
using BetterMuv.Services;

namespace BetterMuv.Core;

/// <summary>
/// 游戏内兑换：回主页 → 右上菜单 → コード入力 → 粘贴码 → 決定 → 结果（成功 OK / 已用横幅）。
/// </summary>
public sealed class RedemptionCodeAutomation
{
    private enum RedeemOutcome
    {
        Success,
        AlreadyUsed,
        Failed
    }

    private const double MenuThreshold = 0.72;
    private const double EntryThreshold = 0.70;
    private const double ConfirmThreshold = 0.65;
    private const double OkThreshold = 0.70;
    private const double AlreadyUsedThreshold = 0.70;
    private const int AfterHomeMs = 700;
    private const int AfterMenuMs = 900;
    private const int AfterEntryMs = 800;
    private const int AfterPasteMs = 500;
    private const int AfterOkMs = 700;
    private const int AfterCancelMs = 600;
    private const int OutcomeTimeoutMs = 8000;
    /// <summary>已兑换粉条约 1s 后才出现且很短，前几秒只扫它。</summary>
    private const int AlreadyUsedPriorityMs = 2800;

    private readonly AutomationConfig _config;
    private readonly ScreenAutomation _screen;
    private readonly Action<string> _log;
    private readonly TemplateMatcher _menuMatcher;
    private readonly TemplateMatcher? _entryMatcher;
    private readonly TemplateMatcher _confirmMatcher;
    private readonly TemplateMatcher _okMatcher;
    private readonly TemplateMatcher _okMatcherAlt;
    private readonly TemplateMatcher _alreadyUsedMatcher;

    public RedemptionCodeAutomation(AutomationConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _screen = new ScreenAutomation(config, log);
        _menuMatcher = TemplateAssets.Load("hud-menu.png");
        string entryPath = Path.Combine(TemplateAssets.DirectoryPath, "serial-code-entry.png");
        _entryMatcher = File.Exists(entryPath) ? TemplateAssets.Load("serial-code-entry.png") : null;
        string confirmPath = Path.Combine(TemplateAssets.DirectoryPath, "serial-code-confirm.png");
        _confirmMatcher = File.Exists(confirmPath)
            ? TemplateAssets.Load("serial-code-confirm.png")
            : TemplateAssets.Load("settlement-confirm.png");
        _okMatcher = TemplateAssets.Load("settlement-confirm.png");
        _okMatcherAlt = TemplateAssets.Load("daily-shop-ok.png");
        string alreadyPath = Path.Combine(TemplateAssets.DirectoryPath, "serial-code-already-used.png");
        if (!File.Exists(alreadyPath))
            throw new FileNotFoundException("缺少已兑换模板 serial-code-already-used.png", alreadyPath);
        _alreadyUsedMatcher = TemplateAssets.Load("serial-code-already-used.png");
    }

    /// <summary>兑换指定列表，并把已用状态写回 cache。</summary>
    public async Task<int> RunAsync(
        RedemptionCodeCache cache,
        IReadOnlyList<RedemptionCodeEntry> codes,
        CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            _log("兑换码：没有待兑换的码。");
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        GameWindow window = _screen.FindWindow(_config.WindowTitleKeyword);
        _log($"兑换码：已找到窗口 {window.Title}");
        if (!await _screen.FocusAsync(window.Handle, cancellationToken))
        {
            _log("未能将游戏置于前台，请先手动点一下游戏窗口。");
            return 0;
        }

        await Task.Delay(200, cancellationToken);
        window = await _screen.EnsurePreferredClientAsync(window, cancellationToken);

        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        await Task.Delay(AfterHomeMs, cancellationToken);
        window = _screen.Refresh(window);
        await TryDismissResultOkAsync(window, cancellationToken, timeoutMs: 1500);
        window = _screen.Refresh(window);
        await TryCloseCodeDialogAsync(window, cancellationToken);
        window = _screen.Refresh(window);

        if (!await EnsureCodeDialogAsync(window, cancellationToken))
        {
            _log("兑换码：未能打开「コード入力」，本轮取消。");
            return 0;
        }

        int ok = 0;
        for (int i = 0; i < codes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RedemptionCodeEntry entry = codes[i];
            _log($"兑换码：开始兑换 {entry.Code}（{entry.Content}）[{i + 1}/{codes.Count}]。");

            window = _screen.Refresh(window);
            if (!await IsCodeDialogOpenAsync(window, cancellationToken))
            {
                _log("兑换码：入力弹窗已关闭，重新打开。");
                if (!await EnsureCodeDialogAsync(window, cancellationToken))
                {
                    _log($"兑换码：{entry.Code} 无法再次打开入力弹窗，保留未用。");
                    continue;
                }
            }

            RedeemOutcome outcome = await SubmitCodeInOpenDialogAsync(window, entry.Code, cancellationToken);
            window = _screen.Refresh(window);
            if (outcome == RedeemOutcome.Failed)
            {
                _log($"兑换码：{entry.Code} 未能提交（未点到決定），保留未用，继续下一条。");
                continue;
            }

            ok++;
            RedemptionCodeEntry? local = cache.Codes.FirstOrDefault(c => c.Id == entry.Id)
                ?? cache.Codes.FirstOrDefault(c =>
                    string.Equals(c.Code, entry.Code, StringComparison.OrdinalIgnoreCase));
            if (local is not null)
                local.MarkedUsed = true;
            entry.MarkedUsed = true;
            RedemptionCodeStore.Save(cache);
            _log(outcome == RedeemOutcome.AlreadyUsed
                ? $"兑换码：{entry.Code} 已兑换过（已标记已用），继续下一条。"
                : $"兑换码：{entry.Code} 已提交（已标记已用），继续下一条。");

            // 成功可能关弹窗；已用则留着，下一条直接全选删除再粘贴。
            window = _screen.Refresh(window);
            await TryDismissResultOkAsync(window, cancellationToken, timeoutMs: 1200);
        }

        RedemptionCodeStore.Save(cache);
        window = _screen.Refresh(window);
        await ReturnHomeAsync(window, cancellationToken);
        _log($"兑换码：本轮完成 {ok}/{codes.Count}。");
        return ok;
    }

    /// <summary>关掉结果 OK / コード入力后，点右上房子回主页（不再点底栏ホーム，避免多余左下角点击）。</summary>
    private async Task ReturnHomeAsync(GameWindow window, CancellationToken cancellationToken)
    {
        _log("兑换码：返回主页。");
        await TryDismissResultOkAsync(window, cancellationToken, timeoutMs: 1500);
        window = _screen.Refresh(window);
        await TryCloseCodeDialogAsync(window, cancellationToken);
        window = _screen.Refresh(window);
        await new HudHomeReturn(_config, _screen, _log).TryAsync(window, cancellationToken);
        await Task.Delay(AfterHomeMs, cancellationToken);
        _log("兑换码：已回主页。");
    }

    private async Task<bool> EnsureCodeDialogAsync(GameWindow window, CancellationToken cancellationToken)
    {
        window = _screen.Refresh(window);
        if (await IsCodeDialogOpenAsync(window, cancellationToken))
            return true;

        if (!await OpenMenuAsync(window, cancellationToken))
            return false;
        await Task.Delay(AfterMenuMs, cancellationToken);
        window = _screen.Refresh(window);

        if (!await OpenCodeEntryAsync(window, cancellationToken))
            return false;
        await Task.Delay(AfterEntryMs, cancellationToken);
        window = _screen.Refresh(window);
        return await WaitForCodeDialogAsync(window, cancellationToken);
    }

    private async Task<bool> IsCodeDialogOpenAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult confirm = await _screen.ProbeAsync(
            window,
            _confirmMatcher,
            _config.RedeemConfirmTopLeft,
            _config.RedeemConfirmSize,
            cancellationToken,
            ConfirmThreshold);
        return confirm.IsMatch;
    }

    /// <summary>在已打开的入力弹窗里：点输入框 → 全选 → 删除 → 粘贴 → 決定。</summary>
    private async Task<RedeemOutcome> SubmitCodeInOpenDialogAsync(
        GameWindow window, string code, CancellationToken cancellationToken)
    {
        window = await _screen.ClickAsync(
            window, _config.RedeemInputClick, "兑换输入框", cancellationToken, parkCursor: false);
        await Task.Delay(250, cancellationToken);

        try
        {
            SetClipboardText(code);
        }
        catch (Exception ex)
        {
            _log("写入剪贴板失败：" + ex.Message);
            return RedeemOutcome.Failed;
        }

        await _screen.ClearFieldAndPasteAsync(window, $"写入 {code}", cancellationToken);
        await Task.Delay(AfterPasteMs, cancellationToken);
        window = _screen.Refresh(window);

        if (!await ClickConfirmAsync(window, cancellationToken))
            return RedeemOutcome.Failed;

        // 已点決定：无论成功/已用/超时，都视为已提交。
        RedeemOutcome outcome = await WaitForOutcomeAsync(window, cancellationToken);
        return outcome == RedeemOutcome.Failed ? RedeemOutcome.AlreadyUsed : outcome;
    }

    private async Task<RedeemOutcome> WaitForOutcomeAsync(
        GameWindow window, CancellationToken cancellationToken)
    {
        _log("兑换码：等待结果（成功 OK / 已兑换提示）；超时也按已兑换处理。");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < OutcomeTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = _screen.Refresh(window);

            // 粉条常在点決定约 1s 后闪一下；前几秒只扫它，避免被 OK 探测拖慢漏检。
            bool priorityAlreadyUsed = sw.ElapsedMilliseconds < AlreadyUsedPriorityMs;
            TemplateProbeResult used = await _screen.ProbeAsync(
                window,
                _alreadyUsedMatcher,
                _config.RedeemAlreadyUsedTopLeft,
                _config.RedeemAlreadyUsedSize,
                cancellationToken,
                AlreadyUsedThreshold);
            if (used.IsMatch)
            {
                _log($"兑换码：识别到已兑换提示（{used.Score:F4}）。");
                return RedeemOutcome.AlreadyUsed;
            }

            if (!priorityAlreadyUsed)
            {
                foreach (TemplateMatcher matcher in new[] { _okMatcherAlt, _okMatcher })
                {
                    TemplateProbeResult ok = await _screen.ProbeAsync(
                        window,
                        matcher,
                        _config.RedeemResultOkTopLeft,
                        _config.RedeemResultOkSize,
                        cancellationToken,
                        OkThreshold);
                    if (!ok.IsMatch)
                        continue;

                    _log($"兑换码：结果确认（{ok.Score:F4}），点击。");
                    await _screen.ClickProbeAsync(
                        window, ok, "结果OK", cancellationToken, settleDelayMs: AfterOkMs);
                    return RedeemOutcome.Success;
                }
            }
        }

        _log("兑换码：结果未明确，按已兑换处理。");
        return RedeemOutcome.AlreadyUsed;
    }

    private async Task<bool> WaitForCodeDialogAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult confirm = await _screen.WaitForProbeAsync(
            window,
            _confirmMatcher,
            _config.RedeemConfirmTopLeft,
            _config.RedeemConfirmSize,
            cancellationToken,
            timeoutMs: 5000,
            matchThreshold: ConfirmThreshold);
        if (confirm.IsMatch)
        {
            _log($"兑换码：已确认进入输入弹窗（決定 {confirm.Score:F4}）。");
            return true;
        }

        _log($"兑换码：输入弹窗未出现（決定最高 {confirm.Score:F3}）。");
        return false;
    }

    private async Task<bool> OpenMenuAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult menu = await _screen.ProbeAsync(
            window, _menuMatcher, _config.HudMenuTopLeft, _config.HudMenuSize,
            cancellationToken, MenuThreshold);
        if (menu.IsMatch)
        {
            await _screen.ClickProbeAsync(window, menu, "右上菜单", cancellationToken, settleDelayMs: 200);
            return true;
        }

        _log($"右上菜单未命中（{menu.Score:F3}），改点固定坐标。");
        await _screen.ClickAsync(window, _config.RedeemMenuClick, "右上菜单(兜底)", cancellationToken);
        return true;
    }

    private async Task<bool> OpenCodeEntryAsync(GameWindow window, CancellationToken cancellationToken)
    {
        if (_entryMatcher is not null)
        {
            TemplateProbeResult entry = await _screen.WaitForProbeAsync(
                window, _entryMatcher,
                _config.RedeemCodeEntryTopLeft, _config.RedeemCodeEntrySize,
                cancellationToken,
                timeoutMs: 4000,
                matchThreshold: EntryThreshold);
            if (entry.IsMatch)
            {
                await _screen.ClickProbeAsync(
                    window, entry, "コード入力", cancellationToken, settleDelayMs: 200);
                return true;
            }

            _log($"コード入力模板未命中（{entry.Score:F3}），改点固定坐标。");
        }
        else
        {
            _log("尚无 serial-code-entry.png 模板，使用固定坐标点「コード入力」。");
        }

        await _screen.ClickAsync(
            window, _config.RedeemCodeEntryClick, "コード入力(兜底)", cancellationToken);
        return true;
    }

    private async Task<bool> ClickConfirmAsync(GameWindow window, CancellationToken cancellationToken)
    {
        TemplateProbeResult hit = await _screen.WaitForProbeAsync(
            window,
            _confirmMatcher,
            _config.RedeemConfirmTopLeft,
            _config.RedeemConfirmSize,
            cancellationToken,
            timeoutMs: 4000,
            matchThreshold: ConfirmThreshold);
        if (!hit.IsMatch)
        {
            _log($"決定未命中（最高 {hit.Score:F3}），放弃本次以免误点。");
            return false;
        }

        _log($"兑换确认「決定」已识别（{hit.Score:F4}）。");
        await _screen.ClickProbeAsync(window, hit, "決定", cancellationToken, settleDelayMs: 50);
        return true;
    }

    private async Task<bool> TryDismissResultOkAsync(
        GameWindow window, CancellationToken cancellationToken, int timeoutMs)
    {
        foreach (TemplateMatcher matcher in new[] { _okMatcherAlt, _okMatcher })
        {
            TemplateProbeResult ok = await _screen.WaitForProbeAsync(
                window,
                matcher,
                _config.RedeemResultOkTopLeft,
                _config.RedeemResultOkSize,
                cancellationToken,
                timeoutMs: timeoutMs,
                matchThreshold: OkThreshold);
            if (!ok.IsMatch)
                continue;

            _log($"兑换码：关闭结果 OK（{ok.Score:F4}）。");
            await _screen.ClickProbeAsync(window, ok, "结果OK", cancellationToken, settleDelayMs: AfterOkMs);
            return true;
        }

        return false;
    }

    /// <summary>若仍停在コード入力弹窗，点キャンセル关闭。</summary>
    private async Task TryCloseCodeDialogAsync(GameWindow window, CancellationToken cancellationToken)
    {
        // 用更高阈值，避免在非弹窗界面误点キャンセル。
        TemplateProbeResult confirm = await _screen.ProbeAsync(
            window,
            _confirmMatcher,
            _config.RedeemConfirmTopLeft,
            _config.RedeemConfirmSize,
            cancellationToken,
            0.85);
        if (!confirm.IsMatch)
            return;

        _log($"兑换码：关闭コード入力弹窗（キャンセル，決定 {confirm.Score:F3}）。");
        await _screen.ClickAsync(
            window, _config.RedeemCancelClick, "キャンセル", cancellationToken);
        await Task.Delay(AfterCancelMs, cancellationToken);
    }

    private static void SetClipboardText(string text)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            Clipboard.SetText(text);
            return;
        }

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw error;
    }
}
