using System.Windows;
using System.Windows.Input;
using BetterMuv.Core;

namespace BetterMuv;

/// <summary>有未用兑换码时询问：启动兑换，或全部标为已兑换并不再提示。</summary>
public partial class RedeemPromptWindow : Window
{
    public enum Choice
    {
        Cancel,
        StartRedeem,
        MarkAllUsed
    }

    public Choice ResultChoice { get; private set; } = Choice.Cancel;

    private sealed record CodeRow(string Code, string Content);

    public RedeemPromptWindow(IReadOnlyList<RedemptionCodeEntry> unusedCodes)
    {
        InitializeComponent();
        TitleText.Text = unusedCodes.Count == 1
            ? "有 1 个未用兑换码"
            : $"有 {unusedCodes.Count} 个未用兑换码";

        var rows = unusedCodes
            .Take(8)
            .Select(c => new CodeRow(
                c.Code,
                string.IsNullOrWhiteSpace(c.Content) ? "（无说明）" : c.Content))
            .ToList();
        if (unusedCodes.Count > 8)
            rows.Add(new CodeRow($"…另有 {unusedCodes.Count - 8} 个", ""));
        CodesList.ItemsSource = rows;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        ResultChoice = Choice.Cancel;
        DialogResult = false;
        Close();
    }

    private void MarkUsedButton_Click(object sender, RoutedEventArgs e)
    {
        ResultChoice = Choice.MarkAllUsed;
        DialogResult = true;
        Close();
    }

    private void StartRedeemButton_Click(object sender, RoutedEventArgs e)
    {
        ResultChoice = Choice.StartRedeem;
        DialogResult = true;
        Close();
    }
}
