using System.Windows;

namespace BetterMuv.Core;

public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

public sealed class CaptureGeometry
{
    public const int LogicalWidth = 1920;
    public const int LogicalHeight = 1080;
    private const double AspectTolerance = 0.005;

    public CaptureGeometry(ScreenRect clientRect)
    {
        if (clientRect.Width <= 0 || clientRect.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(clientRect), "客户区尺寸必须大于零。");
        ClientRect = clientRect;
    }

    public ScreenRect ClientRect { get; }

    public bool IsSixteenByNine =>
        Math.Abs(ClientRect.Width / (double)ClientRect.Height - 16.0 / 9.0) <= AspectTolerance;

    public Point ReferenceToScreen(ConfigPoint point, int referenceWidth, int referenceHeight)
    {
        ValidateReference(referenceWidth, referenceHeight);
        return new Point(
            ClientRect.Left + point.X * ClientRect.Width / (double)referenceWidth,
            ClientRect.Top + point.Y * ClientRect.Height / (double)referenceHeight);
    }

    public Point ReferenceToLogical(ConfigPoint point, int referenceWidth, int referenceHeight)
    {
        ValidateReference(referenceWidth, referenceHeight);
        return new Point(
            point.X * LogicalWidth / (double)referenceWidth,
            point.Y * LogicalHeight / (double)referenceHeight);
    }

    public ScreenRect ReferenceRegionToScreen(
        ConfigPoint center,
        ConfigSize size,
        int referenceWidth,
        int referenceHeight)
    {
        ValidateReference(referenceWidth, referenceHeight);
        int width = Math.Max(1, (int)Math.Round(size.Width * ClientRect.Width / (double)referenceWidth));
        int height = Math.Max(1, (int)Math.Round(size.Height * ClientRect.Height / (double)referenceHeight));
        Point centerOnScreen = ReferenceToScreen(center, referenceWidth, referenceHeight);
        int left = Math.Clamp((int)Math.Round(centerOnScreen.X) - width / 2, ClientRect.Left, ClientRect.Right - width);
        int top = Math.Clamp((int)Math.Round(centerOnScreen.Y) - height / 2, ClientRect.Top, ClientRect.Bottom - height);
        return new ScreenRect(left, top, width, height);
    }

    public ScreenRect ReferenceRegionFromTopLeftToScreen(
        ConfigPoint topLeft,
        ConfigSize size,
        int referenceWidth,
        int referenceHeight)
    {
        ValidateReference(referenceWidth, referenceHeight);
        int left = ClientRect.Left +
            (int)Math.Round(topLeft.X * ClientRect.Width / (double)referenceWidth);
        int top = ClientRect.Top +
            (int)Math.Round(topLeft.Y * ClientRect.Height / (double)referenceHeight);
        int width = Math.Max(1,
            (int)Math.Round(size.Width * ClientRect.Width / (double)referenceWidth));
        int height = Math.Max(1,
            (int)Math.Round(size.Height * ClientRect.Height / (double)referenceHeight));
        left = Math.Clamp(left, ClientRect.Left, ClientRect.Right - width);
        top = Math.Clamp(top, ClientRect.Top, ClientRect.Bottom - height);
        return new ScreenRect(left, top, width, height);
    }

    public static bool CheckSixteenByNine(int width, int height) =>
        width > 0 && height > 0 &&
        Math.Abs(width / (double)height - 16.0 / 9.0) <= AspectTolerance;

    private static void ValidateReference(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "参考分辨率必须大于零。");
    }
}
