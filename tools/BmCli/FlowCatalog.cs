namespace BetterMuv.BmCli;

internal static class FlowCatalog
{
    public static object AsObject() => new Dictionary<string, object?>
    {
        ["coordinateSystem"] = "1080p logical (1920x1080); CaptureGeometry scales to client cover viewport",
        ["sharedEntry"] = new Dictionary<string, object?>
        {
            ["id"] = "QuestFromHomeEntry",
            ["file"] = "Core/QuestFromHomeEntry.cs",
            ["steps"] = new[]
            {
                "HudHomeReturn: match hud-home once → click once → wait 500ms",
                "DoubleClick FirstClick (任务) → wait ~1500ms",
                "DoubleClick target (主线横幅中心 / SecondClick メイズ) → wait ~800ms",
                "Does NOT recognize quest-select page"
            }
        },
        ["flows"] = new object[]
        {
            Flow("maze", "迷宫探索", "Core/MazeAutomation.cs", "maze",
            [
                "开局校准视口一次（可失败继续）",
                "扫迷宫内界面（third/fourth/treasure/route/...）",
                "未命中 → QuestFromHomeEntry → SecondClick",
                "难度 OCR（失败仍点探索準備）→ 探索",
                "循环：next > battle > event > partner > settlement > treasure > route",
                "结算商店 SettlementShopRunner；宣传弹窗 PromoPopupDismisser"
            ]),
            Flow("mainQuest", "自动主线", "Core/MainQuestAutomation.cs", "mainquest",
            [
                "HudHome + Bootstrap：看 next/menu/skip/start",
                "未在关内 → QuestFromHomeEntry → 主线横幅",
                "Start: 双击 BeginStageClick（开始/情景同点）",
                "AwaitBranch: scenarioMenu→Scenario；skip→Next（战斗）",
                "Scenario: 菜单→加速；加速后 next 优先；箭头卡住>15s 再点选项",
                "Battle/Next: SKIP / 下一步；偶发宣传 X",
                "再戦 rematch / ホームへ toHome 结束一关"
            ]),
            Flow("hardMainQuest", "困难主线", "Core/HardMainQuestAutomation.cs", "hard",
            [
                "与主线类似，开局切困难（红底 MAIN QUEST BATTLE）",
                "Sortie → Battle → Next；再戦点 ホームへ 结束"
            ]),
            Flow("pipeline", "一条龙", "MainWindow.xaml.cs", null,
            [
                "按 PipelineTaskOrder 串行 maze / mainQuest / hardMainQuest / dailyShop",
                "共享 DiagnosticTaskSession 诊断目录"
            ]),
            Flow("dailyShop", "每日商店", "Core/DailyShopAutomation.cs", null,
            [
                "HudHomeReturn → 商店 → 交換所",
                "100%OFF（10s 未识别则跳过）→ 交換 → OK → 滚轮下拉",
                "左下角 2500 → 交換 → OK（×2；首次 10s 未识别则回主页结束）"
            ])
        },
        ["helperModules"] = new[]
        {
            "Core/HudHomeReturn.cs",
            "Core/PromoPopupDismisser.cs",
            "Core/ScreenAutomation.cs",
            "Core/CaptureGeometry.cs",
            "Services/WindowCaptureService.cs",
            "Services/TemplateMatcher.cs"
        },
        ["aiTips"] = new[]
        {
            "先 bm status 确认窗与几何",
            "卡住时 bm capture + bm probe <preset> --json",
            "调 ROI/阈值后用同一截图 bm probe <preset> --shot path.png",
            "bm crop <key> --preset mainquest 导出 ROI 给人眼/视觉核对",
            "点击用 1080 逻辑坐标：bm click 1143 961 --dbl"
        }
    };

    private static Dictionary<string, object?> Flow(
        string id, string name, string file, string? probePreset, string[] phases) =>
        new()
        {
            ["id"] = id,
            ["name"] = name,
            ["file"] = file,
            ["phases"] = phases,
            ["probePreset"] = probePreset
        };

    public static string AsMarkdown() => """
# Better-Muv 流程速查（给 AI / 调试）

坐标：配置一律 **1080p 逻辑**；运行时经 `CaptureGeometry` 按客户区 cover 视口缩放（4K≈×2）。

## 共享入口 `QuestFromHomeEntry`
1. 有 HUD 主页钮 → 点一次 → 等 500ms  
2. 双击「任务」`FirstClick` → 等 ~1.5s  
3. 双击目标（主线横幅中心 / 迷宫 `SecondClick`）  
不识别任务选择页。

## 迷宫 `maze` → preset `maze`
校准一次 → 扫迷宫内界面 → 否则入口连点 → 难度/探索 → 循环  
优先级：next > battle > event > partner > settlement > treasure > route

## 主线 `mainQuest` → preset `mainquest`
Bootstrap → 入口连点 → Start 双击开始/情景同点 →  
AwaitBranch：右上箭头=`Scenario`，否则 SKIP→战斗/`Next` →  
Scenario 加速后 next 优先，箭头卡住 >15s 再选项

## 困难主线 `hardMainQuest` → preset `hard`
开局切困难 → Sortie/Battle/Next → 再戦回主页结束

## 一条龙 `pipeline`
按配置顺序串行上述任务。

## 推荐调试顺序
```
.\tools\bm.ps1 status
.\tools\bm.ps1 capture _logtmp\probe\live.png
.\tools\bm.ps1 probe mainquest --json
.\tools\bm.ps1 probe mainquest --shot _logtmp\probe\live.png --json
.\tools\bm.ps1 crop scenarioMenu --preset mainquest --out _logtmp\probe\menu.png
.\tools\bm.ps1 click 1700 960 --dbl
```
""";
}
