using BetterMuv.Core;
using BetterMuv.Services;

namespace BetterMuv.BmCli;

internal static class ProbeCatalog
{
    public static IReadOnlyList<string> PresetNames { get; } =
    [
        "entry", "mainquest", "maze", "hard", "promo", "hud"
    ];

    public static IReadOnlyList<TemplateProbe> Build(string preset, CliContext ctx)
    {
        AutomationConfig c = ctx.Config;
        return preset.ToLowerInvariant() switch
        {
            "entry" =>
            [
                P(ctx, "hudHome", "hud-home.png", c.HudHomeTopLeft, c.HudHomeSize, 0.70),
                P(ctx, "questIcon", "quest.png", c.SearchTopLeft, c.FirstSearchSize, 0.58),
                P(ctx, "maze", "maze-search.png", c.SecondSearchTopLeft, c.SecondSearchSize, 0.78),
                P(ctx, "mainBanner", "main-quest-banner.png",
                    c.MainQuestBannerTopLeft, c.MainQuestBannerSize, 0.70),
                P(ctx, "battleSimulate", "quest-battle-simulate.png",
                    c.QuestBattleSimulateTopLeft, c.QuestBattleSimulateSize, 0.78),
                P(ctx, "exercises", "quest-exercises.png",
                    c.QuestExercisesTopLeft, c.QuestExercisesSize, 0.78),
                P(ctx, "activity", "quest-activity.png",
                    c.QuestActivityTopLeft, c.QuestActivitySize, 0.78),
                P(ctx, "navBack", "nav-back.png",
                    c.NavBackTopLeft, c.NavBackSize, 0.78),
            ],
            "mainquest" or "mq" =>
            [
                P(ctx, "start", "main-quest-start.png", c.MainQuestStartTopLeft, c.MainQuestStartSize, 0.62),
                P(ctx, "scenarioMenu", "main-quest-scenario-menu.png",
                    c.MainQuestScenarioMenuTopLeft, c.MainQuestScenarioMenuSize, 0.45),
                P(ctx, "scenarioSpeed", "main-quest-scenario-speed.png",
                    c.MainQuestScenarioSpeedTopLeft, c.MainQuestScenarioSpeedSize, 0.45),
                P(ctx, "scenarioOk", "main-quest-scenario-ok.png",
                    c.MainQuestScenarioOkTopLeft, c.MainQuestScenarioOkSize, 0.55),
                P(ctx, "scenarioChoice", "main-quest-scenario-choice.png",
                    c.MainQuestScenarioChoiceTopLeft, c.MainQuestScenarioChoiceSize, 0.55),
                P(ctx, "scenarioChoiceAlt", "main-quest-scenario-choice-alt.png",
                    c.MainQuestScenarioChoiceTopLeft, c.MainQuestScenarioChoiceSize, 0.55),
                P(ctx, "scenarioPortrait", "main-quest-scenario-choice-portrait.png",
                    c.MainQuestScenarioPortraitTopLeft, c.MainQuestScenarioPortraitSize, 0.88),
                P(ctx, "skip", "main-quest-skip.png", c.MainQuestSkipTopLeft, c.MainQuestSkipSize, 0.52),
                P(ctx, "skipAlt", "battle-skip.png", c.MainQuestSkipTopLeft, c.MainQuestSkipSize, 0.52),
                P(ctx, "next", "main-quest-next.png", c.MainQuestNextTopLeft, c.MainQuestNextSize, 0.52),
                P(ctx, "rematch", "main-quest-rematch.png",
                    c.MainQuestRematchTopLeft, c.MainQuestRematchSize, 0.72),
                P(ctx, "toHome", "main-quest-to-home.png",
                    c.MainQuestToHomeTopLeft, c.MainQuestToHomeSize, 0.62),
            ],
            "maze" =>
            [
                P(ctx, "third", "exploration-ready.png", c.ThirdSearchTopLeft, c.ThirdSearchSize, 0.62),
                P(ctx, "fourth", "exploration-action.png", c.FourthSearchTopLeft, c.FourthSearchSize, 0.68),
                P(ctx, "battle", "battle-skip.png", c.BattleSkipTopLeft, c.BattleSkipSize, 0.68),
                P(ctx, "event", "event-choice.png", c.EventChoiceTopLeft, c.EventChoiceSize, 0.68),
                P(ctx, "partner", "partner-leave.png",
                    c.PartnerSelectionTopLeft, c.PartnerSelectionSize, 0.68),
                P(ctx, "settlement", "settlement-complete.png",
                    new ConfigPoint(
                        Math.Max(0, c.SettlementSearchTopLeft.X - 40),
                        Math.Max(0, c.SettlementSearchTopLeft.Y - 40)),
                    new ConfigSize(
                        Math.Max(c.SettlementSearchSize.Width + 80, 360),
                        Math.Max(c.SettlementSearchSize.Height + 80, 180)),
                    0.72),
                P(ctx, "treasure", "treasure-state.png", // may fall back — see ResolveTreasure
                    c.TreasureStateTopLeft, c.TreasureStateSize, 0.68),
                P(ctx, "route", "route-selection.png",
                    c.RouteSelectionTopLeft, c.RouteSelectionSize, 0.72),
                P(ctx, "next", "maze-next.png",
                    new ConfigPoint(
                        Math.Max(0, c.FifthSearchTopLeft.X - 40),
                        Math.Max(0, c.FifthSearchTopLeft.Y - 40)),
                    new ConfigSize(
                        Math.Max(c.FifthSearchSize.Width + 80, 360),
                        Math.Max(c.FifthSearchSize.Height + 80, 180)),
                    0.70),
                P(ctx, "mazeCard", "maze-search.png", c.SecondSearchTopLeft, c.SecondSearchSize, 0.78),
            ],
            "hard" =>
            [
                P(ctx, "start", "main-quest-start.png", c.MainQuestStartTopLeft, c.MainQuestStartSize, 0.62),
                P(ctx, "sortie", "main-quest-sortie.png",
                    c.MainQuestSortieTopLeft, c.MainQuestSortieSize, 0.62),
                P(ctx, "difficulty", "hard-quest-difficulty.png",
                    c.HardQuestDifficultyTopLeft, c.HardQuestDifficultySize, 0.62),
                P(ctx, "hardMark", "hard-quest-battle.png",
                    c.HardQuestBattleTopLeft, c.HardQuestBattleSize, 0.62),
                P(ctx, "skip", "main-quest-skip.png", c.MainQuestSkipTopLeft, c.MainQuestSkipSize, 0.52),
                P(ctx, "skipAlt", "battle-skip.png", c.MainQuestSkipTopLeft, c.MainQuestSkipSize, 0.52),
                P(ctx, "next", "main-quest-next.png", c.MainQuestNextTopLeft, c.MainQuestNextSize, 0.52),
                P(ctx, "rematch", "main-quest-rematch.png",
                    c.MainQuestRematchTopLeft, c.MainQuestRematchSize, 0.72),
                P(ctx, "toHome", "main-quest-to-home.png",
                    c.MainQuestToHomeTopLeft, c.MainQuestToHomeSize, 0.62),
                P(ctx, "scenarioOk", "main-quest-scenario-ok.png",
                    c.MainQuestScenarioOkTopLeft, c.MainQuestScenarioOkSize, 0.55),
            ],
            "promo" =>
            [
                P(ctx, "popupClose", "popup-close.png",
                    c.PopupCloseTopLeft, c.PopupCloseSize, 0.70),
            ],
            "hud" =>
            [
                P(ctx, "hudHome", "hud-home.png", c.HudHomeTopLeft, c.HudHomeSize, 0.70),
            ],
            _ => throw new ArgumentException(
                $"未知 preset「{preset}」。可用: {string.Join(", ", PresetNames)}")
        };
    }

    public static bool TryGet(string preset, string key, CliContext ctx, out TemplateProbe probe)
    {
        foreach (TemplateProbe p in Build(preset, ctx))
        {
            if (p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                probe = p;
                return true;
            }
        }

        probe = default;
        return false;
    }

    private static TemplateProbe P(
        CliContext ctx, string key, string file, ConfigPoint tl, ConfigSize size, double thr)
    {
        string path = ResolveTemplateFile(ctx.TemplatesDir, file);
        return new TemplateProbe(key, new TemplateMatcher(path), tl, size, thr);
    }

    private static string ResolveTemplateFile(string dir, string file)
    {
        string direct = Path.Combine(dir, file);
        if (File.Exists(direct))
            return direct;

        string[] aliases = file switch
        {
            "treasure-state.png" => ["treasure-shoe.png"],
            "route-selection.png" => ["route-shoe.png"],
            _ => []
        };
        foreach (string a in aliases)
        {
            string p = Path.Combine(dir, a);
            if (File.Exists(p))
                return p;
        }

        throw new FileNotFoundException($"模板不存在: {direct}");
    }
}
