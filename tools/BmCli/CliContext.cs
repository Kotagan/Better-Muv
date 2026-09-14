using System.Text.Json;
using System.Windows.Media.Imaging;
using BetterMuv.Core;
using BetterMuv.Services;

namespace BetterMuv.BmCli;

internal sealed class CliContext
{
    public string RepoRoot { get; }
    public string TemplatesDir { get; }
    public AutomationConfig Config { get; }
    public ScreenAutomation Screen { get; }
    public bool Json { get; }
    public string? ShotPath { get; }
    public bool UseSelectedWindow { get; }
    public string TitleKeyword { get; }
    public bool NoFocus { get; }
    public bool DoubleClick { get; }
    public string[] RawArgs { get; }

    private CliContext(
        string repoRoot,
        string templatesDir,
        AutomationConfig config,
        bool json,
        string? shotPath,
        bool useSelectedWindow,
        string titleKeyword,
        bool noFocus,
        bool doubleClick,
        string[] rawArgs)
    {
        RepoRoot = repoRoot;
        TemplatesDir = templatesDir;
        Config = config;
        Screen = new ScreenAutomation(config, msg =>
        {
            if (!json)
                Console.Error.WriteLine(msg);
        });
        Json = json;
        ShotPath = shotPath;
        UseSelectedWindow = useSelectedWindow;
        TitleKeyword = titleKeyword;
        NoFocus = noFocus;
        DoubleClick = doubleClick;
        RawArgs = rawArgs;
    }

    public static CliContext Create(string[] args)
    {
        string repoRoot = FindRepoRoot();
        bool json = HasFlag(args, "--json");
        bool useSelected = HasFlag(args, "--selected");
        bool noFocus = HasFlag(args, "--no-focus");
        bool dbl = HasFlag(args, "--dbl");
        string? shot = GetOpt(args, "--shot");
        string? templates = GetOpt(args, "--templates");
        string templatesDir = templates ?? Path.Combine(repoRoot, "Assets", "Templates");
        if (!Directory.Exists(templatesDir))
            throw new DirectoryNotFoundException($"模板目录不存在: {templatesDir}");

        AutomationConfig config = LoadConfig(repoRoot);
        // CLI 默认按标题找窗，避免卡在「上次选择的浏览器窗」。
        if (!useSelected)
            config.WindowSelectionMode = "title";

        string keyword = GetOpt(args, "--title") ?? config.WindowTitleKeyword;
        return new CliContext(
            repoRoot, templatesDir, config, json, shot, useSelected, keyword, noFocus, dbl, args);
    }

    public TemplateMatcher LoadTpl(string fileName) =>
        TemplateAssets.Load(TemplatesDir, fileName);

    public GameWindow FindWindow()
    {
        if (!string.IsNullOrWhiteSpace(ShotPath))
            throw new InvalidOperationException("当前命令使用了 --shot，不需要游戏窗口。");
        return Screen.FindWindow(TitleKeyword);
    }

    public async Task<GameWindow> FindAndFocusAsync(CancellationToken ct, bool focus = true)
    {
        GameWindow window = FindWindow();
        if (focus)
        {
            await Screen.FocusAsync(window.Handle, ct);
            await Task.Delay(200, ct);
            window = Screen.Refresh(window);
        }

        Screen.EnsureUsableViewport(window);
        return window;
    }

    public BitmapSource LoadShot()
    {
        if (string.IsNullOrWhiteSpace(ShotPath) || !File.Exists(ShotPath))
            throw new FileNotFoundException("找不到截图。", ShotPath);
        using var stream = File.OpenRead(ShotPath);
        BitmapSource full = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        full.Freeze();
        return full;
    }

    /// <summary>
    /// 客户区截图 → 按生产 cover 视口垫成 16:9 画布，使离线 ROI 与线上一致（顶栏可超出客户区）。
    /// </summary>
    public (BitmapSource Image, GameWindow Window) PrepareShot(BitmapSource clientShot)
    {
        var clientWindow = new GameWindow(
            0, Path.GetFileName(ShotPath) ?? "shot",
            new ScreenRect(0, 0, clientShot.PixelWidth, clientShot.PixelHeight),
            new ScreenRect(0, 0, clientShot.PixelWidth, clientShot.PixelHeight));
        CaptureGeometry geo = Screen.Geometry(clientWindow);
        ScreenRect vp = geo.ViewportRect;
        if (vp.Width == clientShot.PixelWidth && vp.Height == clientShot.PixelHeight &&
            vp.Left == 0 && vp.Top == 0)
        {
            return (clientShot, clientWindow);
        }

        int destX = -vp.Left; // client origin relative to viewport
        int destY = -vp.Top;
        var padded = new System.Windows.Media.Imaging.WriteableBitmap(
            vp.Width, vp.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null);
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            clientShot, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        padded.WritePixels(
            new System.Windows.Int32Rect(0, 0, converted.PixelWidth, converted.PixelHeight),
            pixels, stride, destX, destY);
        padded.Freeze();

        var window = new GameWindow(
            0, Path.GetFileName(ShotPath) ?? "shot",
            new ScreenRect(0, 0, vp.Width, vp.Height),
            new ScreenRect(0, 0, vp.Width, vp.Height));
        return (padded, window);
    }

    public void WriteJson(object payload)
    {
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static AutomationConfig LoadConfig(string repoRoot)
    {
        if (File.Exists(ConfigStore.UserConfigPath))
            return ConfigStore.Load();
        string repoConfig = Path.Combine(repoRoot, "config.json");
        if (File.Exists(repoConfig))
            return AutomationConfig.Load(repoConfig);
        throw new FileNotFoundException(
            $"找不到配置：{ConfigStore.UserConfigPath} 或 {repoConfig}");
    }

    private static string FindRepoRoot()
    {
        string? env = Environment.GetEnvironmentVariable("BETTER_MUV_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, "Better-Muv.csproj")))
            return Path.GetFullPath(env);

        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(dir, "Better-Muv.csproj")))
                return dir;
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) break;
            dir = parent;
        }

        // 从源码目录启动：tools/BmCli -> ../..
        string fromSrc = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(fromSrc, "Better-Muv.csproj")))
            return fromSrc;

        throw new DirectoryNotFoundException("找不到仓库根目录（Better-Muv.csproj）。可设 BETTER_MUV_ROOT。");
    }

    public static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    public static string? GetOpt(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    public static string[] Positional(string[] args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (a is "--json" or "--selected" or "--focus" or "--no-focus" or "--dbl" or "--help" or "-h")
                    continue;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    i++;
                continue;
            }

            list.Add(a);
        }

        return list.ToArray();
    }
}
