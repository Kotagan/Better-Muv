using System.Windows;
using BetterMuv.Services;

namespace BetterMuv.Core;

public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

/// <summary>
/// 配置坐标（Reference，默认 1080p）↔ 屏幕坐标的唯一缩放入口。
/// 映射区域可以是任意宽高比；横纵方向分别缩放。
/// </summary>
public sealed class CaptureGeometry
{
    public const int LogicalWidth = 1920;
    public const int LogicalHeight = 1080;
    private const double AspectTolerance = 0.005;

    public CaptureGeometry(ScreenRect viewportRect, int referenceWidth, int referenceHeight)
    {
        if (viewportRect.Width <= 0 || viewportRect.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportRect), "映射区域尺寸必须大于零。");
        if (referenceWidth <= 0 || referenceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(referenceWidth), "参考分辨率必须大于零。");

        ViewportRect = viewportRect;
        ReferenceWidth = referenceWidth;
        ReferenceHeight = referenceHeight;
        ScaleX = viewportRect.Width / (double)referenceWidth;
        ScaleY = viewportRect.Height / (double)referenceHeight;
    }

    public ScreenRect ViewportRect { get; }
    // 保留旧名称，避免外部调用方一次性迁移；其含义现在是映射视口。
    public ScreenRect ClientRect => ViewportRect;
    public int ReferenceWidth { get; }
    public int ReferenceHeight { get; }
    public double ScaleX { get; }
    public double ScaleY { get; }

    public bool IsSixteenByNine =>
        Math.Abs(ViewportRect.Width / (double)ViewportRect.Height - 16.0 / 9.0) <= AspectTolerance;

    /// <summary>配置点 → 屏幕点：screen = Viewport原点 + point × (ScaleX, ScaleY)。</summary>
    public Point ToScreen(ConfigPoint point) => new(
        ClientRect.Left + point.X * ScaleX,
        ClientRect.Top + point.Y * ScaleY);

    /// <summary>配置位移 → 屏幕位移（不含原点）。</summary>
    public Point ScaleDelta(ConfigPoint delta) => new(delta.X * ScaleX, delta.Y * ScaleY);

    /// <summary>配置尺寸 → 屏幕像素尺寸。</summary>
    public ConfigSize ToScreenSize(ConfigSize size) => new(
        Math.Max(1, (int)Math.Round(size.Width * ScaleX)),
        Math.Max(1, (int)Math.Round(size.Height * ScaleY)));

    /// <summary>配置尺寸 → 模板匹配用的逻辑尺寸（通常与 Reference 同为 1080p 时不变）。</summary>
    public int ToLogical(int referenceLength, bool horizontal) =>
        Math.Max(1, (int)Math.Round(referenceLength *
            (horizontal ? LogicalWidth / (double)ReferenceWidth : LogicalHeight / (double)ReferenceHeight)));

    public (int Width, int Height) ToLogicalSize(ConfigSize size) =>
        (ToLogical(size.Width, horizontal: true), ToLogical(size.Height, horizontal: false));

    /// <summary>以中心点标定的配置矩形 → 屏幕矩形。</summary>
    public ScreenRect RegionFromCenterToScreen(ConfigPoint center, ConfigSize size)
    {
        ConfigSize screenSize = ToScreenSize(size);
        Point centerOnScreen = ToScreen(center);
        int left = Math.Clamp(
            (int)Math.Round(centerOnScreen.X) - screenSize.Width / 2,
            ClientRect.Left, ClientRect.Right - screenSize.Width);
        int top = Math.Clamp(
            (int)Math.Round(centerOnScreen.Y) - screenSize.Height / 2,
            ClientRect.Top, ClientRect.Bottom - screenSize.Height);
        return new ScreenRect(left, top, screenSize.Width, screenSize.Height);
    }

    /// <summary>以左上角标定的配置矩形 → 屏幕矩形（截图 ROI）。</summary>
    public ScreenRect RegionFromTopLeftToScreen(ConfigPoint topLeft, ConfigSize size)
    {
        ConfigSize screenSize = ToScreenSize(size);
        int left = ClientRect.Left + (int)Math.Round(topLeft.X * ScaleX);
        int top = ClientRect.Top + (int)Math.Round(topLeft.Y * ScaleY);
        left = Math.Clamp(left, ClientRect.Left, ClientRect.Right - screenSize.Width);
        top = Math.Clamp(top, ClientRect.Top, ClientRect.Bottom - screenSize.Height);
        return new ScreenRect(left, top, screenSize.Width, screenSize.Height);
    }

    /// <summary>
    /// 逻辑空间匹配结果中心 → 屏幕点。
    /// 搜索 ROI 已由 <see cref="RegionFromTopLeftToScreen"/> 乘过缩放系数，此处按 ROI 内比例换算。
    /// </summary>
    public Point MatchCenterToScreen(
        ScreenRect searchRect, TemplateMatchResult match, int logicalWidth, int logicalHeight)
    {
        double centerX = searchRect.Left +
            (match.X + match.Width / 2.0) * searchRect.Width / Math.Max(1, logicalWidth);
        double centerY = searchRect.Top +
            (match.Y + match.Height / 2.0) * searchRect.Height / Math.Max(1, logicalHeight);
        return new Point(centerX, centerY);
    }

    public static bool CheckSixteenByNine(int width, int height) =>
        width > 0 && height > 0 &&
        Math.Abs(width / (double)height - 16.0 / 9.0) <= AspectTolerance;
}
