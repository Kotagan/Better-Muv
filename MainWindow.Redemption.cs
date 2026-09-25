using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterMuv.Core;
using BetterMuv.Services;

namespace BetterMuv;

public partial class MainWindow
{
    private RedemptionCodeCache _redemptionCache = new();
    private bool _redemptionSyncing;
    private bool _suppressRedeemPromptToggle;
    private GameKeeCdkClient? _cdkClient;
    private IReadOnlyList<RedemptionCodeEntry>? _pendingRedeemCodes;

    private void InitializeRedemptionPanel()
    {
        _redemptionCache = RedemptionCodeStore.Load();
        _suppressRedeemPromptToggle = true;
        try
        {
            AutomationConfig config = ConfigStore.Load();
            RedeemPromptCheckBox.IsChecked = config.RedeemPromptOnNewCodes;
        }
        catch
        {
            RedeemPromptCheckBox.IsChecked = true;
        }
        finally
        {
            _suppressRedeemPromptToggle = false;
        }

        RefreshRedemptionUi();
        _ = SyncRedemptionCodesAsync(quiet: true, promptIfNew: true);
    }

    private async void RunRedeemUnusedButton_Click(object sender, RoutedEventArgs e)
    {
        List<RedemptionCodeEntry> unused = _redemptionCache.Codes.Where(c => !c.MarkedUsed).ToList();
        if (unused.Count == 0)
        {
            SetPage(Page.Execute);
            OpenLogDrawer();
            AppendLog("兑换码：没有未用的码。");
            SettingsRedeemStatusText.Text = "没有未用的码";
            return;
        }

        await RunInGameRedeemAsync(unused);
    }

    private void OpenRedemptionWebButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(GameKeeCdkClient.SourceUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("打开兑换码网页失败：" + ex.Message);
        }
    }

    private void RedeemPromptCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressRedeemPromptToggle)
            return;
        try
        {
            AutomationConfig config = ConfigStore.Load();
            config.RedeemPromptOnNewCodes = RedeemPromptCheckBox.IsChecked == true;
            ConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog("保存兑换码设置失败：" + ex.Message);
        }
    }

    private async Task SyncRedemptionCodesAsync(bool quiet, bool promptIfNew)
    {
        if (_redemptionSyncing)
            return;
        _redemptionSyncing = true;
        try
        {
            if (!quiet)
                SettingsRedeemStatusText.Text = "正在从 GameKee 同步…";

            _cdkClient ??= new GameKeeCdkClient();
            IReadOnlyList<GameKeeCdkItem> remote =
                await _cdkClient.FetchActiveAsync().ConfigureAwait(true);
            RedemptionSyncResult result =
                RedemptionCodeStore.MergeFromRemote(_redemptionCache, remote);
            _redemptionCache = RedemptionCodeStore.Load();
            RefreshRedemptionUi();

            List<RedemptionCodeEntry> unused = _redemptionCache.Codes
                .Where(c => !c.MarkedUsed)
                .ToList();

            if (result.Added > 0)
            {
                string msg = $"兑换码已更新：新增 {result.Added} 个（共 {result.Total}）。";
                AppendLog(msg);
                SettingsRedeemStatusText.Text = msg;
            }
            else if (result.Updated > 0)
            {
                string msg = $"兑换码已刷新：内容变更 {result.Updated} 个（共 {result.Total}）。";
                AppendLog(msg);
                SettingsRedeemStatusText.Text = msg;
            }
            else
            {
                SettingsRedeemStatusText.Text =
                    $"已自动同步（共 {result.Total} 个）· {FormatSyncedAt(_redemptionCache.LastSyncedAt)}";
                if (!quiet)
                    AppendLog($"兑换码同步完成：无新增（共 {result.Total}）。");
            }

            // 有未用码且开启询问时弹窗（不限于「本轮新增」）。
            if (promptIfNew &&
                RedeemPromptCheckBox.IsChecked == true &&
                unused.Count > 0)
            {
                await PromptAndMaybeRedeemAsync(unused);
            }
        }
        catch (Exception ex)
        {
            SettingsRedeemStatusText.Text = "自动同步失败：" + ex.Message;
            AppendLog("兑换码同步失败：" + ex.Message);
            RefreshRedemptionUi();
        }
        finally
        {
            _redemptionSyncing = false;
        }
    }

    private async Task PromptAndMaybeRedeemAsync(IReadOnlyList<RedemptionCodeEntry> unusedCodes)
    {
        var dialog = new RedeemPromptWindow(unusedCodes) { Owner = this };
        bool? result = dialog.ShowDialog();
        if (result != true || dialog.ResultChoice == RedeemPromptWindow.Choice.Cancel)
        {
            AppendLog("兑换码：用户关闭了询问弹窗。");
            return;
        }

        if (dialog.ResultChoice == RedeemPromptWindow.Choice.MarkAllUsed)
        {
            foreach (RedemptionCodeEntry entry in _redemptionCache.Codes)
            {
                if (!entry.MarkedUsed)
                    entry.MarkedUsed = true;
            }

            RedemptionCodeStore.Save(_redemptionCache);
            RefreshRedemptionUi();
            // 只标记已用；设置里的询问开关保持原样，以后有新未用码仍会提示。
            AppendLog($"兑换码：用户选择已兑换，已全部标记（{unusedCodes.Count} 个）。");
            SettingsRedeemStatusText.Text = "已全部标记为已兑换";
            return;
        }

        await RunInGameRedeemAsync(unusedCodes);
    }

    /// <summary>与迷宫/商店等任务同一套：任务页 + 运行日志 + 可停止。</summary>
    private async Task RunInGameRedeemAsync(IReadOnlyList<RedemptionCodeEntry> codes)
    {
        if (_runCancellation is not null || _isPaused)
        {
            SetPage(Page.Execute);
            OpenLogDrawer();
            AppendLog("兑换码：当前有任务在跑，请先停止后再兑换。");
            return;
        }

        SetPage(Page.Execute);
        OpenLogDrawer();
        if (!await EnsureCaptureForRunAsync("redeem"))
            return;

        _pendingRedeemCodes = codes;
        await StartRedeemAsync();
    }

    private async Task StartRedeemAsync()
    {
        if (_runCancellation is not null) return;
        IReadOnlyList<RedemptionCodeEntry> codes = _pendingRedeemCodes ?? [];
        if (codes.Count == 0)
        {
            AppendLog("兑换码：没有待兑换的码。");
            return;
        }

        BeginRunLogSession(resume: false);
        _resumeDiagnosticTask = false;
        BeginDiagnosticRun(resume: false);
        _isPaused = false;
        _pauseRequested = false;
        _activeTask = ActiveTask.Redeem;
        _runCancellation = new CancellationTokenSource();
        UpdateRunUi();
        try
        {
            WindowState = WindowState.Minimized;
            await Task.Delay(250, _runCancellation.Token);
            AutomationConfig config = PrepareDiagnosticTask("redeem");
            var automation = new RedemptionCodeAutomation(config, AppendLog);
            int ok = await automation.RunAsync(_redemptionCache, codes, _runCancellation.Token);
            _redemptionCache = RedemptionCodeStore.Load();
            RefreshRedemptionUi();
            SettingsRedeemStatusText.Text = $"游戏内兑换完成 {ok}/{codes.Count}";
            AppendLog($"兑换码：本轮结束（完成 {ok}/{codes.Count}）。");
        }
        catch (OperationCanceledException)
        {
            AppendLog(_pauseRequested ? "兑换码已暂停。" : "兑换码已停止。");
        }
        catch (Exception ex)
        {
            AppendLog("兑换码错误：" + ex.Message);
            SettingsRedeemStatusText.Text = "兑换失败：" + ex.Message;
            CaptureOnAutoStop("redeem");
        }
        finally
        {
            _pendingRedeemCodes = null;
            // 兑换不支持暂停续跑：停止后一律清空。
            _pauseRequested = false;
            FinishRunSession();
        }
    }

    private void RefreshRedemptionUi()
    {
        int total = _redemptionCache.Codes.Count;
        int unused = _redemptionCache.Codes.Count(c => !c.MarkedUsed);
        string synced = FormatSyncedAt(_redemptionCache.LastSyncedAt);
        if (!_redemptionSyncing)
        {
            SettingsRedeemStatusText.Text = total == 0
                ? "尚未同步"
                : $"{unused} 个未用 / 共 {total} · {synced}";
        }

        SettingsRedeemListPanel.Children.Clear();
        if (total == 0)
        {
            SettingsRedeemEmptyText.Visibility = Visibility.Visible;
            return;
        }

        SettingsRedeemEmptyText.Visibility = Visibility.Collapsed;
        foreach (RedemptionCodeEntry entry in _redemptionCache.Codes)
            SettingsRedeemListPanel.Children.Add(BuildSettingsRedeemCard(entry));
    }

    private Border BuildSettingsRedeemCard(RedemptionCodeEntry entry)
    {
        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Opacity = entry.MarkedUsed ? 0.55 : 1
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = entry.Code,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Content) ? "（无奖励说明）" : entry.Content,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAE, 0xB8, 0xC5)),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        info.Children.Add(new TextBlock
        {
            Text = RedemptionCodeStore.FormatExpiry(entry.EndAtUnix) +
                     (entry.MarkedUsed ? " · 已用" : ""),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA3)),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var copy = new Button
        {
            Style = (Style)FindResource("PrimaryButton"),
            Content = "复制",
            Tag = entry.Code,
            Margin = new Thickness(0, 0, 8, 0)
        };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(entry.Code);
                AppendLog($"已复制兑换码：{entry.Code}");
            }
            catch (Exception ex)
            {
                AppendLog("复制失败：" + ex.Message);
            }
        };
        var used = new Button
        {
            Style = (Style)FindResource("PrimaryButton"),
            Content = entry.MarkedUsed ? "恢复" : "已用",
            Tag = entry.Id
        };
        used.Click += (_, _) =>
        {
            entry.MarkedUsed = !entry.MarkedUsed;
            RedemptionCodeStore.Save(_redemptionCache);
            RefreshRedemptionUi();
        };
        actions.Children.Add(copy);
        actions.Children.Add(used);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        card.Child = grid;
        return card;
    }

    private static string FormatSyncedAt(DateTimeOffset? at) =>
        at is null ? "未同步" : $"同步于 {at.Value.LocalDateTime:MM-dd HH:mm}";
}
