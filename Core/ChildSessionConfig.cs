namespace BetterMuv.Core;

public sealed class ChildSessionConfig
{
    public WindowPositionConfig? NormalWindowPosition { get; set; }
    public WindowPositionConfig? SmallWindowPosition { get; set; }
    public bool TopmostEnabled { get; set; }
    public bool SmartSizingEnabled { get; set; } = true;
    public bool KeepAspectRatio { get; set; } = true;
    public bool SendSystemShortcutsToRemote { get; set; } = true;
    public bool AudioMuted { get; set; }
}

public sealed class WindowPositionConfig
{
    public int Left { get; set; }
    public int Top { get; set; }

    public WindowPositionConfig()
    {
    }

    public WindowPositionConfig(int left, int top)
    {
        Left = left;
        Top = top;
    }
}
