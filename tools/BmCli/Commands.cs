using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using BetterMuv.Core;
using BetterMuv.Services;

namespace BetterMuv.BmCli;

internal static class Commands
{
    public static int Help()
    {
        Console.WriteLine("""
BmCli — Better-Muv 调试 CLI（不进安装包，给 AI/人工联调用）

用法:  bm <command> [args] [options]
  或:  dotnet run --project tools/BmCli -- <command> ...

全局选项:
  --json              机器可读 JSON
  --shot <png>        离线截图（不碰游戏窗）
  --templates <dir>   模板目录（默认仓库 Assets/Templates）
  --title <kw>        窗口标题关键字
  --selected          使用 config 里上次选定的窗口
  --no-focus          截图/探测时不抢前台

命令:
  help                本说明
  flows [--json]      流程地图
  presets             列出 probe preset
  status [--json]     窗口 / 几何 / 配置路径
  windows [--json]    枚举候选窗口
  capture [out.png]   截取客户区
  click <x> <y>       1080 逻辑坐标单击（--dbl 双击）
  click-ratio <rx> <ry>
  probe <preset>      批量模板探测（与生产 ROI/阈值对齐）
  match <tpl.png> --roi x,y,w,h [--thr 0.7]
  crop <key> --preset <p> [--out path]
  rois [all|maze|main|hard|entry] [--json]
  points              常用点击点（FirstClick 等）

示例见: tools/README.md
""");
        return 0;
    }

    public static int Flows(CliContext ctx)
    {
        if (ctx.Json)
            ctx.WriteJson(FlowCatalog.AsObject());
        else
            Console.WriteLine(FlowCatalog.AsMarkdown());
        return 0;
    }

    public static int Presets(CliContext ctx)
    {
        if (ctx.Json)
            ctx.WriteJson(new { presets = ProbeCatalog.PresetNames });
        else
            Console.WriteLine(string.Join(Environment.NewLine, ProbeCatalog.PresetNames));
        return 0;
    }

    public static async Task<int> StatusAsync(CliContext ctx, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["repoRoot"] = ctx.RepoRoot,
            ["templatesDir"] = ctx.TemplatesDir,
            ["userConfig"] = ConfigStore.UserConfigPath,
            ["userConfigExists"] = File.Exists(ConfigStore.UserConfigPath),
            ["titleKeyword"] = ctx.TitleKeyword,
            ["windowMode"] = ctx.Config.WindowSelectionMode,
            ["matchThreshold"] = ctx.Config.MatchThreshold,
            ["reference"] = $"{ctx.Config.ReferenceWidth}x{ctx.Config.ReferenceHeight}",
            ["shot"] = ctx.ShotPath
        };

        try
        {
            GameWindow w = await ctx.FindAndFocusAsync(ct, focus: false);
            var geo = ctx.Screen.Geometry(w);
            payload["window"] = new
            {
                title = w.Title,
                handle = w.Handle.ToInt64(),
                client = $"{w.ClientRect.Width}x{w.ClientRect.Height}",
                clientOrigin = $"{w.ClientRect.Left},{w.ClientRect.Top}",
                display = $"{w.DisplayRect.Width}x{w.DisplayRect.Height}",
                viewport = $"{geo.ViewportRect.Width}x{geo.ViewportRect.Height}",
                scale = $"{geo.ScaleX:F4}x{geo.ScaleY:F4}"
            };
        }
        catch (Exception ex)
        {
            payload["windowError"] = ex.Message;
        }

        if (ctx.Json)
            ctx.WriteJson(payload);
        else
        {
            foreach (var kv in payload)
                Console.WriteLine($"{kv.Key}: {FormatVal(kv.Value)}");
        }

        return 0;
    }

    public static int Windows(CliContext ctx)
    {
        var capture = new WindowCaptureService();
        var list = capture.EnumerateWindows()
            .Select(w => new { w.Title, w.ProcessName, w.ClassName, handle = w.Handle.ToInt64() })
            .ToList();
        if (ctx.Json)
            ctx.WriteJson(new { windows = list });
        else
        {
            foreach (var w in list)
                Console.WriteLine($"{w.ProcessName,-24} {w.handle,-12} {w.Title}");
        }

        return 0;
    }

    public static async Task<int> CaptureAsync(CliContext ctx, string[] pos, CancellationToken ct)
    {
        string outPath = pos.Length > 0
            ? pos[0]
            : Path.Combine(ctx.RepoRoot, "_logtmp", "probe", $"live-{DateTime.Now:HHmmss}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        GameWindow window = await ctx.FindAndFocusAsync(ct, focus: !ctx.NoFocus);
        BitmapSource shot = ctx.Screen.CaptureClient(window);
        SavePng(shot, outPath);

        var result = new
        {
            path = Path.GetFullPath(outPath),
            width = shot.PixelWidth,
            height = shot.PixelHeight,
            title = window.Title
        };
        if (ctx.Json)
            ctx.WriteJson(result);
        else
            Console.WriteLine($"saved {result.path} ({result.width}x{result.height})");
        return 0;
    }

    public static async Task<int> ClickAsync(CliContext ctx, string[] pos, bool ratio, CancellationToken ct)
    {
        if (pos.Length < 2 ||
            !double.TryParse(pos[0], out double a) ||
            !double.TryParse(pos[1], out double b))
        {
            Console.Error.WriteLine(ratio
                ? "用法: click-ratio <rx> <ry> [--dbl]"
                : "用法: click <x> <y> [--dbl]  （1080 逻辑坐标）");
            return 1;
        }

        GameWindow window = await ctx.FindAndFocusAsync(ct, focus: true);
        ConfigPoint point = ratio
            ? new ConfigPoint(
                (int)Math.Round(a * ctx.Config.ReferenceWidth),
                (int)Math.Round(b * ctx.Config.ReferenceHeight))
            : new ConfigPoint((int)Math.Round(a), (int)Math.Round(b));

        window = await ctx.Screen.ClickAsync(window, point, "cli", ct);
        if (ctx.DoubleClick)
        {
            await Task.Delay(Math.Max(ctx.Config.DoubleClickIntervalMs, 50), ct);
            window = await ctx.Screen.ClickAsync(window, point, "cli-2", ct);
        }

        var geo = ctx.Screen.Geometry(window);
        var screen = geo.ToScreen(point);
        var result = new
        {
            logical = new { x = point.X, y = point.Y },
            screen = new { x = screen.X, y = screen.Y },
            doubleClick = ctx.DoubleClick
        };
        if (ctx.Json)
            ctx.WriteJson(result);
        else
            Console.WriteLine(
                $"clicked logical=({point.X},{point.Y}) screen=({screen.X:F0},{screen.Y:F0}) dbl={ctx.DoubleClick}");
        return 0;
    }

    public static async Task<int> ProbeAsync(CliContext ctx, string[] pos, CancellationToken ct)
    {
        if (pos.Length < 1)
        {
            Console.Error.WriteLine($"用法: probe <preset>  preset={string.Join("|", ProbeCatalog.PresetNames)}");
            return 1;
        }

        string preset = pos[0];
        IReadOnlyList<TemplateProbe> probes = ProbeCatalog.Build(preset, ctx);
        var rows = new List<ProbeRow>();

        if (!string.IsNullOrWhiteSpace(ctx.ShotPath))
        {
            BitmapSource client = ctx.LoadShot();
            (BitmapSource shot, GameWindow fake) = ctx.PrepareShot(client);
            foreach (TemplateProbe p in probes)
            {
                RegionCapture region = ctx.Screen.CropRegion(fake, shot, p.TopLeft, p.Size, p.Matcher);
                TemplateMatchResult match = ctx.Screen.Match(
                    p.Matcher, region.Image, region.LogicalWidth, region.LogicalHeight);
                rows.Add(new ProbeRow(
                    p.Key, match.Score >= p.Threshold, match.Score, p.Threshold,
                    match.X, match.Y, false, p.TopLeft.X, p.TopLeft.Y, p.Size.Width, p.Size.Height));
            }
        }
        else
        {
            GameWindow window = await ctx.FindAndFocusAsync(ct, !ctx.NoFocus);
            IReadOnlyDictionary<string, TemplateProbeResult> results =
                await ctx.Screen.ProbeManyAsync(window, probes, ct);
            foreach (TemplateProbe p in probes)
            {
                TemplateProbeResult r = results.TryGetValue(p.Key, out TemplateProbeResult? found)
                    ? found
                    : new TemplateProbeResult(false, 0, new Point());
                rows.Add(new ProbeRow(
                    p.Key, r.IsMatch, r.Score, p.Threshold,
                    (int)r.Center.X, (int)r.Center.Y, true,
                    p.TopLeft.X, p.TopLeft.Y, p.Size.Width, p.Size.Height));
            }
        }

        int hits = rows.Count(r => r.Hit);
        if (ctx.Json)
        {
            ctx.WriteJson(new
            {
                preset,
                shot = ctx.ShotPath,
                hitCount = hits,
                total = rows.Count,
                results = rows.Select(r => new
                {
                    key = r.Key,
                    hit = r.Hit,
                    score = r.Score,
                    threshold = r.Threshold,
                    at = new { x = r.AtX, y = r.AtY, screenCenter = r.ScreenCenter },
                    roi = new { x = r.RoiX, y = r.RoiY, w = r.RoiW, h = r.RoiH }
                })
            });
        }
        else
        {
            Console.WriteLine($"preset={preset} hits={hits}/{rows.Count}");
            foreach (ProbeRow r in rows)
            {
                Console.WriteLine(
                    $"{(r.Hit ? "HIT " : "miss"),-4} {r.Key,-18} {r.Score:F3}/{r.Threshold:F2} " +
                    $"roi=({r.RoiX},{r.RoiY},{r.RoiW},{r.RoiH})");
            }
        }

        return 0;
    }

    private sealed record ProbeRow(
        string Key, bool Hit, double Score, double Threshold,
        int AtX, int AtY, bool ScreenCenter,
        int RoiX, int RoiY, int RoiW, int RoiH);

    public static async Task<int> MatchAsync(CliContext ctx, string[] allArgs, string[] pos, CancellationToken ct)
    {
        if (pos.Length < 1)
        {
            Console.Error.WriteLine("用法: match <tpl.png> --roi x,y,w,h [--thr 0.7]");
            return 1;
        }

        string? roiStr = CliContext.GetOpt(allArgs, "--roi");
        if (roiStr is null || !TryParseRoi(roiStr, out int rx, out int ry, out int rw, out int rh))
        {
            Console.Error.WriteLine("--roi 需要 x,y,w,h");
            return 1;
        }

        double thr = 0.7;
        string? thrStr = CliContext.GetOpt(allArgs, "--thr");
        if (thrStr is not null)
            double.TryParse(thrStr, out thr);

        string tplName = pos[0];
        string tplPath = Path.IsPathRooted(tplName)
            ? tplName
            : Path.Combine(ctx.TemplatesDir, tplName);
        if (!File.Exists(tplPath))
        {
            Console.Error.WriteLine($"模板不存在: {tplPath}");
            return 1;
        }

        var matcher = new TemplateMatcher(tplPath);
        var topLeft = new ConfigPoint(rx, ry);
        var size = new ConfigSize(rw, rh);
        double score;
        bool hit;
        int mx, my;

        if (!string.IsNullOrWhiteSpace(ctx.ShotPath))
        {
            BitmapSource client = ctx.LoadShot();
            (BitmapSource shot, GameWindow fake) = ctx.PrepareShot(client);
            RegionCapture region = ctx.Screen.CropRegion(fake, shot, topLeft, size, matcher);
            TemplateMatchResult match = ctx.Screen.Match(
                matcher, region.Image, region.LogicalWidth, region.LogicalHeight);
            score = match.Score;
            hit = score >= thr;
            mx = match.X;
            my = match.Y;
        }
        else
        {
            GameWindow window = await ctx.FindAndFocusAsync(ct, !ctx.NoFocus);
            TemplateProbeResult r = await ctx.Screen.ProbeAsync(
                window, matcher, topLeft, size, ct, thr);
            score = r.Score;
            hit = r.IsMatch;
            mx = (int)r.Center.X;
            my = (int)r.Center.Y;
        }

        var payload = new
        {
            hit,
            score,
            threshold = thr,
            at = new { x = mx, y = my },
            roi = new { x = rx, y = ry, w = rw, h = rh },
            template = tplPath
        };
        if (ctx.Json)
            ctx.WriteJson(payload);
        else
            Console.WriteLine($"{(hit ? "HIT" : "miss")} {score:F3}/{thr:F2} at({mx},{my}) tpl={Path.GetFileName(tplPath)}");
        return hit ? 0 : 2;
    }

    public static async Task<int> CropAsync(CliContext ctx, string[] allArgs, string[] pos, CancellationToken ct)
    {
        if (pos.Length < 1)
        {
            Console.Error.WriteLine("用法: crop <key> --preset <name> [--out path]");
            return 1;
        }

        string key = pos[0];
        string preset = CliContext.GetOpt(allArgs, "--preset") ?? "mainquest";
        if (!ProbeCatalog.TryGet(preset, key, ctx, out TemplateProbe p))
        {
            Console.Error.WriteLine($"preset={preset} 没有 key={key}");
            return 1;
        }

        string outPath = CliContext.GetOpt(allArgs, "--out")
            ?? Path.Combine(ctx.RepoRoot, "_logtmp", "probe", $"crop-{preset}-{key}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        BitmapSource image;
        if (!string.IsNullOrWhiteSpace(ctx.ShotPath))
        {
            BitmapSource client = ctx.LoadShot();
            (BitmapSource shot, GameWindow fake) = ctx.PrepareShot(client);
            image = ctx.Screen.CropRegion(fake, shot, p.TopLeft, p.Size, p.Matcher).Image;
        }
        else
        {
            GameWindow window = await ctx.FindAndFocusAsync(ct, !ctx.NoFocus);
            image = ctx.Screen.CaptureRegion(window, p.TopLeft, p.Size, p.Matcher).Image;
        }

        SavePng(image, outPath);
        if (ctx.Json)
            ctx.WriteJson(new { path = Path.GetFullPath(outPath), key, preset, roi = RoiObj(p) });
        else
            Console.WriteLine($"saved {Path.GetFullPath(outPath)}");
        return 0;
    }

    public static int Rois(CliContext ctx, string[] pos)
    {
        string scope = pos.Length > 0 ? pos[0].ToLowerInvariant() : "all";
        AutomationConfig c = ctx.Config;
        var items = new List<object>();

        void Add(string group, string name, ConfigPoint tl, ConfigSize sz) =>
            items.Add(new { group, name, x = tl.X, y = tl.Y, w = sz.Width, h = sz.Height });

        if (scope is "all" or "entry")
        {
            Add("entry", "HudHome", c.HudHomeTopLeft, c.HudHomeSize);
            Add("entry", "FirstSearch/quest", c.SearchTopLeft, c.FirstSearchSize);
            Add("entry", "SecondSearch/maze", c.SecondSearchTopLeft, c.SecondSearchSize);
            Add("entry", "MainQuestBanner", c.MainQuestBannerTopLeft, c.MainQuestBannerSize);
        }

        if (scope is "all" or "maze")
        {
            Add("maze", "Third", c.ThirdSearchTopLeft, c.ThirdSearchSize);
            Add("maze", "Fourth", c.FourthSearchTopLeft, c.FourthSearchSize);
            Add("maze", "Fifth/next", c.FifthSearchTopLeft, c.FifthSearchSize);
            Add("maze", "BattleSkip", c.BattleSkipTopLeft, c.BattleSkipSize);
            Add("maze", "Event", c.EventChoiceTopLeft, c.EventChoiceSize);
            Add("maze", "Partner", c.PartnerSelectionTopLeft, c.PartnerSelectionSize);
            Add("maze", "Settlement", c.SettlementSearchTopLeft, c.SettlementSearchSize);
            Add("maze", "Treasure", c.TreasureStateTopLeft, c.TreasureStateSize);
            Add("maze", "Route", c.RouteSelectionTopLeft, c.RouteSelectionSize);
        }

        if (scope is "all" or "main" or "mainquest")
        {
            Add("main", "Start", c.MainQuestStartTopLeft, c.MainQuestStartSize);
            Add("main", "ScenarioMenu", c.MainQuestScenarioMenuTopLeft, c.MainQuestScenarioMenuSize);
            Add("main", "ScenarioSpeed", c.MainQuestScenarioSpeedTopLeft, c.MainQuestScenarioSpeedSize);
            Add("main", "ScenarioOk", c.MainQuestScenarioOkTopLeft, c.MainQuestScenarioOkSize);
            Add("main", "ScenarioChoice", c.MainQuestScenarioChoiceTopLeft, c.MainQuestScenarioChoiceSize);
            Add("main", "Skip", c.MainQuestSkipTopLeft, c.MainQuestSkipSize);
            Add("main", "Next", c.MainQuestNextTopLeft, c.MainQuestNextSize);
            Add("main", "Rematch", c.MainQuestRematchTopLeft, c.MainQuestRematchSize);
            Add("main", "ToHome", c.MainQuestToHomeTopLeft, c.MainQuestToHomeSize);
        }

        if (scope is "all" or "hard")
        {
            Add("hard", "Sortie", c.MainQuestSortieTopLeft, c.MainQuestSortieSize);
            Add("hard", "Difficulty", c.HardQuestDifficultyTopLeft, c.HardQuestDifficultySize);
            Add("hard", "BattleMark", c.HardQuestBattleTopLeft, c.HardQuestBattleSize);
        }

        if (ctx.Json)
            ctx.WriteJson(new { scope, rois = items });
        else
        {
            foreach (dynamic i in items)
                Console.WriteLine($"{i.group,-6} {i.name,-18} ({i.x},{i.y},{i.w},{i.h})");
        }

        return 0;
    }

    public static int Points(CliContext ctx)
    {
        AutomationConfig c = ctx.Config;
        var pts = new Dictionary<string, object>
        {
            ["FirstClick"] = Pt(c.FirstClick),
            ["SecondClick"] = Pt(c.SecondClick),
            ["ThirdClick"] = Pt(c.ThirdClick),
            ["FourthClick"] = Pt(c.FourthClick),
            ["FifthClick"] = Pt(c.FifthClick),
            ["PartnerClick"] = Pt(c.PartnerClick),
            ["BattleSkipClick"] = Pt(c.BattleSkipClick),
            ["RouteClick"] = Pt(c.RouteClick),
            ["PopupCloseClick"] = Pt(c.PopupCloseClick),
            ["DailyShopEntryClick"] = Pt(c.DailyShopEntryClick),
            ["DailyShopTabClick"] = Pt(c.DailyShopTabClick),
            ["DailyShopItemClick"] = Pt(c.DailyShopItemClick),
            ["DailyShopConfirmClick"] = Pt(c.DailyShopConfirmClick),
            ["DailyShopDoneClick"] = Pt(c.DailyShopDoneClick),
            ["MainQuestBannerCenter"] = Pt(QuestFromHomeEntry.MainQuestBannerClick(c)),
            ["BeginStageClick"] = Pt(new ConfigPoint(
                c.MainQuestStartTopLeft.X + c.MainQuestStartSize.Width / 2,
                c.MainQuestStartTopLeft.Y + c.MainQuestStartSize.Height / 2))
        };
        if (ctx.Json)
            ctx.WriteJson(pts);
        else
        {
            foreach (var kv in pts)
                Console.WriteLine($"{kv.Key,-24} {FormatVal(kv.Value)}");
        }

        return 0;
    }

    private static object Pt(ConfigPoint p) => new { x = p.X, y = p.Y };

    private static object RoiObj(TemplateProbe p) =>
        new { x = p.TopLeft.X, y = p.TopLeft.Y, w = p.Size.Width, h = p.Size.Height };

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static bool TryParseRoi(string s, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        string[] parts = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return false;
        return int.TryParse(parts[0], out x) &&
               int.TryParse(parts[1], out y) &&
               int.TryParse(parts[2], out w) &&
               int.TryParse(parts[3], out h);
    }

    private static string FormatVal(object? v) =>
        v is null ? "" :
        v is string or int or double or bool or long ? v.ToString()! :
        JsonSerializer.Serialize(v, CliContext.JsonOptions);
}
