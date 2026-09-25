using System.Text.Json;

namespace BetterMuv.Core;

public sealed class AutomationConfig
{
    public string WindowTitleKeyword { get; set; } = "マブラヴ";
    /// <summary>game=按游戏标题自动查找；selected=恢复用户选择的浏览器/其他窗口。</summary>
    public string WindowSelectionMode { get; set; } = "game";
    public string SelectedWindowProcessName { get; set; } = "";
    public string SelectedWindowClassName { get; set; } = "";
    public string SelectedWindowTitle { get; set; } = "";
    /// <summary>截图器启动时是否一并启动游戏。</summary>
    public bool LaunchGameWithCapture { get; set; }
    public bool CaptureTaskEnabled { get; set; } = true;
    public bool DesktopCloneTaskEnabled { get; set; }
    public bool MazeTaskEnabled { get; set; } = true;
    public string GameExecutablePath { get; set; } = "";
    public string GameLaunchArguments { get; set; } = "";
    public int GameLaunchTimeoutSeconds { get; set; } = 90;
    public double MatchThreshold { get; set; } = 0.78;
    public bool SaveDiagnostics { get; set; } = false;
    public string DiagnosticDirectory { get; set; } = "diagnostics";
    /// <summary>配置坐标基准分辨率（1080p）。映射到窗口客户区，横纵方向独立缩放。</summary>
    public int ReferenceWidth { get; set; } = 1920;
    public int ReferenceHeight { get; set; } = 1080;

    // 搜索区：1080p 写死矩形（左上 + 宽高）。覆盖「クエスト」文字并留少量缩放/校准余量。
    public ConfigPoint SearchTopLeft { get; set; } = new(1080, 950);
    public ConfigSize FirstSearchSize { get; set; } = new(150, 110);
    /// <summary>底栏「ホーム」选中态（粉色底）；用于确认真正在主页，避免任务页误判。</summary>
    public ConfigPoint HomeNavTopLeft { get; set; } = new(590, 910);
    public ConfigSize HomeNavSize { get; set; } = new(220, 140);
    /// <summary>底栏「クエスト」搜索区（与 FirstSearch 对齐的紧 ROI）。</summary>
    public ConfigPoint QuestNavTopLeft { get; set; } = new(1080, 950);
    public ConfigSize QuestNavSize { get; set; } = new(150, 110);
    /// <summary>任务页「模擬戦」入口搜索区（1080p）。</summary>
    public ConfigPoint QuestBattleSimulateTopLeft { get; set; } = new(1040, 590);
    public ConfigSize QuestBattleSimulateSize { get; set; } = new(360, 160);
    /// <summary>任务页「演習」入口搜索区（1080p）。</summary>
    public ConfigPoint QuestExercisesTopLeft { get; set; } = new(1380, 650);
    public ConfigSize QuestExercisesSize { get; set; } = new(340, 130);
    /// <summary>任务页「活動」入口搜索区（1080p）。</summary>
    public ConfigPoint QuestActivityTopLeft { get; set; } = new(1410, 820);
    public ConfigSize QuestActivitySize { get; set; } = new(340, 130);
    /// <summary>通用返回箭头搜索区（1080p）。</summary>
    public ConfigPoint NavBackTopLeft { get; set; } = new(0, 0);
    public ConfigSize NavBackSize { get; set; } = new(160, 130);
    /// <summary>任务页「メイズ探索」文字带；略大于旧 220×59，避免校准偏移后扫空。</summary>
    public ConfigPoint SecondSearchTopLeft { get; set; } = new(1470, 420);
    public ConfigSize SecondSearchSize { get; set; } = new(300, 100);
    public ConfigPoint ThirdSearchTopLeft { get; set; } = new(1568, 796);
    public ConfigSize ThirdSearchSize { get; set; } = new(208, 98);
    public ConfigPoint FourthSearchTopLeft { get; set; } = new(1540, 940);
    /// <summary>右下角粉钮「探索」完整区域（旧 1560,910 320×120 在 1080p 只擦到上沿，分数约 0.25）。</summary>
    public ConfigSize FourthSearchSize { get; set; } = new(380, 140);
    public ConfigPoint FifthSearchTopLeft { get; set; } = new(1684, 981);
    public ConfigSize FifthSearchSize { get; set; } = new(100, 50);
    /// <summary>「立ち去る」；旧 1633,919 183×75 偏右上，助っ人类型页分数约 0.09。</summary>
    public ConfigPoint PartnerSelectionTopLeft { get; set; } = new(1540, 930);
    public ConfigSize PartnerSelectionSize { get; set; } = new(360, 130);
    public ConfigPoint BattleSkipTopLeft { get; set; } = new(1720, 70);
    public ConfigSize BattleSkipSize { get; set; } = new(180, 70);
    /// <summary>活动/商店宣传弹窗右上角关闭「X」搜索区（1080p）。</summary>
    public ConfigPoint PopupCloseTopLeft { get; set; } = new(1720, 15);
    public ConfigSize PopupCloseSize { get; set; } = new(180, 100);
    public ConfigPoint PopupCloseClick { get; set; } = new(1810, 55);
    public ConfigPoint EventChoiceTopLeft { get; set; } = new(1018, 628);
    public ConfigSize EventChoiceSize { get; set; } = new(814, 367);
    public ConfigPoint EventChoiceFirstOption { get; set; } = new(1300, 700);
    public ConfigPoint EventChoiceSecondOption { get; set; } = new(1300, 825);
    /// <summary>迷宫「一時の休息」按钮搜索区（1080p；右栏单选项）。</summary>
    public ConfigPoint EventRestTopLeft { get; set; } = new(900, 640);
    public ConfigSize EventRestSize { get; set; } = new(980, 160);
    /// <summary>结算「完了」点击坐标（1080p）。</summary>
    public ConfigPoint SettlementTopLeft { get; set; } = new(1674, 948);
    public ConfigPoint SettlementSearchTopLeft { get; set; } = new(1500, 880);
    public ConfigSize SettlementSearchSize { get; set; } = new(360, 180);
    /// <summary>结算大类「日常」点击中心（1080p；左栏竖条）。</summary>
    public ConfigPoint SettlementCategoryDaily { get; set; } = new(101, 323);
    /// <summary>结算大类「类型装备」点击中心。</summary>
    public ConfigPoint SettlementCategoryEquipment { get; set; } = new(108, 470);
    /// <summary>结算大类「挖掘」点击中心。</summary>
    public ConfigPoint SettlementCategoryExcavation { get; set; } = new(100, 594);
    /// <summary>结算大类「Artifactor」点击中心。</summary>
    public ConfigPoint SettlementCategoryArtifactor { get; set; } = new(99, 731);
    /// <summary>大类选中蓝底检测：以点击点为中心的半宽×半高（逻辑像素）。</summary>
    public ConfigSize SettlementCategoryProbeHalfSize { get; set; } = new(24, 22);
    /// <summary>大类模板搜索区尺寸（须盖住收紧后的 shop-cat-*-on）。</summary>
    public ConfigSize SettlementCategorySearchSize { get; set; } = new(180, 80);
    public List<ConfigPoint> SettlementSubcategoryTabs { get; set; } =
    [
        new(370, 186),
        new(600, 186),
        new(830, 186),
        new(1060, 186)
    ];
    public List<ConfigPoint> SettlementBuyButtons { get; set; } =
    [
        new(891, 354),
        new(1673, 354),
        new(891, 552),
        new(1673, 552),
        new(891, 750),
        new(1673, 750)
    ];
    public ConfigPoint SettlementMultiplierToggle { get; set; } = new(1610, 200);
    public ConfigPoint SettlementMultiplierTopLeft { get; set; } = new(1640, 165);
    public ConfigSize SettlementMultiplierSize { get; set; } = new(140, 70);
    /// <summary>相对购买钮中心向左上扩展，使搜索区中心落在 LvMAX/購入钮上。</summary>
    public ConfigPoint SettlementBuyButtonSearchInset { get; set; } = new(170, 65);
    /// <summary>须盖住 LvMAX 文本紧裁模板（约 330×94@4K→165×47 逻辑）与暗色購入钮；过大匹配变慢。</summary>
    public ConfigSize SettlementBuyButtonSearchSize { get; set; } = new(340, 130);
    public ConfigPoint SettlementConfirmTopLeft { get; set; } = new(650, 500);
    public ConfigSize SettlementConfirmSize { get; set; } = new(680, 450);
    public ConfigPoint SettlementConfirmCancel { get; set; } = new(809, 820);
    public ConfigPoint SettlementConfirmOk { get; set; } = new(1130, 820);
    /// <summary>余矿弹窗模板中心 → OK 的 1080p 偏移（备用）。</summary>
    public ConfigPoint SettlementConfirmOkOffset { get; set; } = new(84, 140);
    /// <summary>余矿弹窗模板中心 → キャンセル 的 1080p 偏移（备用）。</summary>
    public ConfigPoint SettlementConfirmCancelOffset { get; set; } = new(-117, 140);
    public bool SettlementConfirmLeftover { get; set; } = true;
    public SettlementPurchases SettlementPurchases { get; set; } = new();
    public ConfigPoint TreasureStateTopLeft { get; set; } = new(700, 15);
    /// <summary>「レリック選択」标题区；旧 775,34 376×82 在 1080p 裁掉左侧，分数约 0.11。</summary>
    public ConfigSize TreasureStateSize { get; set; } = new(520, 120);
    public ConfigPoint TreasureOptionsTopLeft { get; set; } = new(478, 316);
    public ConfigSize TreasureOptionsSize { get; set; } = new(1081, 90);
    public double TreasureMatchThreshold { get; set; } = 0.58;
    public int TreasureMatchRetryCount { get; set; } = 3;
    public int TreasureMatchRetryDelayMs { get; set; } = 150;
    public ConfigPoint TreasureClickOffset { get; set; } = new(-40, 55);
    public ConfigPoint RouteSelectionTopLeft { get; set; } = new(1500, 880);
    public ConfigSize RouteSelectionSize { get; set; } = new(360, 180);
    public ConfigPoint RouteTreasureOptionsTopLeft { get; set; } = new(661, 243);
    public ConfigSize RouteTreasureOptionsSize { get; set; } = new(209, 554);
    public List<string> TreasurePriority { get; set; } = ["diamond", "shield", "sword", "heart", "skull", "shoe", "sparkle"];
    // 写死点击点（1080p）；实际点击 = Client 原点 + 点 × (Client宽高 / Reference宽高)。
    public ConfigPoint FirstClick { get; set; } = new(1143, 961);
    public ConfigPoint SecondClick { get; set; } = new(1611, 473);
    public ConfigPoint ThirdClick { get; set; } = new(1667, 836);
    public ConfigPoint FourthClick { get; set; } = new(1730, 1015);
    public ConfigPoint FifthClick { get; set; } = new(1672, 959);
    public ConfigPoint PartnerClick { get; set; } = new(1655, 990);
    public ConfigPoint BattleSkipClick { get; set; } = new(1805, 100);
    public ConfigPoint RouteClick { get; set; } = new(1680, 940);
    /// <summary>迷宫难度：keep=保持不变，custom=自选数字。</summary>
    public string MazeDifficultyMode { get; set; } = "keep";
    /// <summary>自选难度目标（仅 custom 生效）。</summary>
    public int MazeDifficultyTarget { get; set; } = 1;
    // 难度大数字在左右箭头之间；ROI 须对齐 DigitTemplateReader 的 840×300 归一化（1080p 为 420×150）。
    public ConfigPoint DifficultyDigitTopLeft { get; set; } = new(1320, 310);
    public ConfigSize DifficultyDigitSize { get; set; } = new(420, 150);
    // 4K 左上角 (1938,700)/(3724,700)；模板约 92×132 / 74×126。
    public ConfigPoint DifficultyDecreaseTopLeft { get; set; } = new(969, 350);
    public ConfigSize DifficultyDecreaseSize { get; set; } = new(140, 150);
    public ConfigPoint DifficultyIncreaseTopLeft { get; set; } = new(1862, 350);
    public ConfigSize DifficultyIncreaseSize { get; set; } = new(140, 150);
    public ConfigPoint DifficultyDecreaseClick { get; set; } = new(992, 383);
    public ConfigPoint DifficultyIncreaseClick { get; set; } = new(1880, 381);
    // 中间数字区域：点击后打开滚动选择 UI（与左右箭头是两种不同交互）。
    public ConfigPoint DifficultyOpenSliderClick { get; set; } = new(1633, 261);
    // 4K (1158,584)→(1522,1706)
    public ConfigPoint DifficultyListTopLeft { get; set; } = new(579, 292);
    public ConfigSize DifficultyListSize { get; set; } = new(182, 561);
    // 4K (2250,1860)
    public ConfigPoint DifficultyConfirmClick { get; set; } = new(1140, 975);
    public int DoubleClickIntervalMs { get; set; } = 180;
    public int DetectionPollIntervalMs { get; set; } = 50;
    public int DetectionTimeoutMs { get; set; } = 10000;
    /// <summary>迷宫轮次上限；0 表示无限。</summary>
    public int MazeRunLimit { get; set; }
    /// <summary>自动主线循环次数（至少 1）。</summary>
    public int MainQuestRunLimit { get; set; } = 10;
    public bool MainQuestTaskEnabled { get; set; } = true;
    public bool HardMainQuestTaskEnabled { get; set; }
    public bool DailyShopTaskEnabled { get; set; }
    public bool DailyFreeGiftTaskEnabled { get; set; }
    public bool DailyExercisesTaskEnabled { get; set; }
    /// <summary>一条龙任务顺序，项为 maze / mainQuest / hardMainQuest / dailyShop / dailyFreeGift / dailyExercises。</summary>
    public List<string> PipelineTaskOrder { get; set; } =
        ["maze", "mainQuest", "hardMainQuest", "dailyShop", "dailyFreeGift", "dailyExercises"];
    // 每日商店连点（1080p）；注释为 4K 客户区坐标（÷2）。
    // 4K (3560,1930)
    public ConfigPoint DailyShopEntryClick { get; set; } = new(1780, 965);
    /// <summary>商店内「交換所」入口点击中心（1080p）。</summary>
    public ConfigPoint DailyShopExchangeHallClick { get; set; } = new(1263, 790);
    public ConfigPoint DailyShopExchangeHallTopLeft { get; set; } = new(1100, 700);
    public ConfigSize DailyShopExchangeHallSize { get; set; } = new(350, 200);
    // 4K (2822,1416)
    public ConfigPoint DailyShopTabClick { get; set; } = new(1411, 708);
    // 4K (924,1362)
    public ConfigPoint DailyShopItemClick { get; set; } = new(462, 681);
    // 4K (2258,1870)
    public ConfigPoint DailyShopConfirmClick { get; set; } = new(1129, 935);
    // 4K (1944,1874)
    public ConfigPoint DailyShopDoneClick { get; set; } = new(972, 937);
    /// <summary>每日商店「100%OFF」搜索区（1080p）。</summary>
    public ConfigPoint DailyShopFreeOffTopLeft { get; set; } = new(348, 628);
    public ConfigSize DailyShopFreeOffSize { get; set; } = new(140, 36);
    public ConfigPoint DailyShopFreeItemClick { get; set; } = new(413, 698);
    /// <summary>零元购弹窗「交換」按钮搜索区（1080p）。</summary>
    public ConfigPoint DailyShopExchangeTopLeft { get; set; } = new(1050, 920);
    public ConfigSize DailyShopExchangeSize { get; set; } = new(240, 140);
    public ConfigPoint DailyShopOkTopLeft { get; set; } = new(800, 900);
    public ConfigSize DailyShopOkSize { get; set; } = new(400, 160);
    public ConfigPoint DailyShopTicketTopLeft { get; set; } = new(200, 750);
    public ConfigSize DailyShopTicketSize { get; set; } = new(500, 300);
    public ConfigPoint DailyShopScrollPoint { get; set; } = new(960, 700);
    public int DailyShopScrollWheelNotches { get; set; } = -8;
    /// <summary>商店左栏「お得パック」固定点击（1080p）。</summary>
    public ConfigPoint DailyFreeGiftOtokuClick { get; set; } = new(130, 380);
    public ConfigPoint DailyFreeGiftOtokuTopLeft { get; set; } = new(40, 250);
    public ConfigSize DailyFreeGiftOtokuSize { get; set; } = new(220, 450);
    /// <summary>「デイリー無料パック」标题搜索区（1080p）。</summary>
    public ConfigPoint DailyFreeGiftTitleTopLeft { get; set; } = new(1200, 700);
    public ConfigSize DailyFreeGiftTitleSize { get; set; } = new(700, 350);
    /// <summary>相对标题匹配中心的点击偏移（屏幕像素，偏下点価格/無料条）。</summary>
    public ConfigPoint DailyFreeGiftTitleClickOffset { get; set; } = new(0, 90);
    public ConfigPoint DailyFreeGiftPurchaseTopLeft { get; set; } = new(900, 880);
    public ConfigSize DailyFreeGiftPurchaseSize { get; set; } = new(500, 160);
    public ConfigPoint DailyFreeGiftOkTopLeft { get; set; } = new(700, 820);
    public ConfigSize DailyFreeGiftOkSize { get; set; } = new(520, 200);
    /// <summary>每日免费礼包上次领完的商店日（每天 5:00 刷新）。</summary>
    public string? LastDailyFreeGiftDay { get; set; }
    /// <summary>戦術演習大厅「出撃準備」搜索区（1080p）。</summary>
    public ConfigPoint DailyExercisesPrepareTopLeft { get; set; } = new(1500, 820);
    public ConfigSize DailyExercisesPrepareSize { get; set; } = new(400, 120);
    /// <summary>大厅「本日あとN回」数字区（1080p），用于识别 0 次。</summary>
    public ConfigPoint DailyExercisesRemainingTopLeft { get; set; } = new(1680, 790);
    public ConfigSize DailyExercisesRemainingSize { get; set; } = new(180, 70);
    /// <summary>每日演习上次打完的游戏日（每天 5:00 刷新）。</summary>
    public string? LastDailyExercisesDay { get; set; }
    // 主线 ROI（1080p）；模板匹配后点中心。
    public ConfigPoint MainQuestHomeTopLeft { get; set; } = new(1000, 880);
    public ConfigSize MainQuestHomeSize { get; set; } = new(340, 160);
    public ConfigPoint MainQuestBannerTopLeft { get; set; } = new(1050, 380);
    public ConfigSize MainQuestBannerSize { get; set; } = new(520, 240);
    public ConfigPoint MainQuestStartTopLeft { get; set; } = new(1400, 880);
    public ConfigSize MainQuestStartSize { get; set; } = new(480, 160);
    public ConfigPoint MainQuestSortieTopLeft { get; set; } = new(1550, 960);
    public ConfigSize MainQuestSortieSize { get; set; } = new(400, 120);
    /// <summary>剧情界面右上角展开菜单按钮（1080p）。</summary>
    public ConfigPoint MainQuestScenarioMenuTopLeft { get; set; } = new(1780, 0);
    public ConfigSize MainQuestScenarioMenuSize { get; set; } = new(140, 130);
    /// <summary>剧情菜单展开后最左侧加速按钮搜索区（1080p）。</summary>
    public ConfigPoint MainQuestScenarioSpeedTopLeft { get; set; } = new(1180, 0);
    public ConfigSize MainQuestScenarioSpeedSize { get; set; } = new(280, 160);
    /// <summary>剧情/奖励确认弹窗中下方 OK 按钮搜索区（1080p）。</summary>
    public ConfigPoint MainQuestScenarioOkTopLeft { get; set; } = new(700, 820);
    public ConfigSize MainQuestScenarioOkSize { get; set; } = new(520, 200);
    /// <summary>剧情分支粉色选项按钮搜索区（1080p，偏右中；含 CAUTION 特殊选项下移）。</summary>
    public ConfigPoint MainQuestScenarioChoiceTopLeft { get; set; } = new(850, 250);
    public ConfigSize MainQuestScenarioChoiceSize { get; set; } = new(1050, 550);
    /// <summary>粉色选项命中左端后，向右偏移到按钮中部（1080p）。</summary>
    public ConfigPoint MainQuestScenarioChoiceClickOffset { get; set; } = new(220, 0);
    /// <summary>剧情特殊选项：双立绘框搜索区（1080p，仅左框）。</summary>
    public ConfigPoint MainQuestScenarioPortraitTopLeft { get; set; } = new(200, 140);
    public ConfigSize MainQuestScenarioPortraitSize { get; set; } = new(700, 720);
    /// <summary>立绘选项默认点左侧头像中心（1080p）；命中后不再用角点偏移。</summary>
    public ConfigPoint MainQuestScenarioPortraitClick { get; set; } = new(700, 480);
    /// <summary>剧情选项默认点击点（1080p，第一项中心；模板未命中时兜底）。</summary>
    public ConfigPoint MainQuestScenarioChoiceClick { get; set; } = new(1395, 520);
    public ConfigPoint MainQuestSkipTopLeft { get; set; } = new(1600, 10);
    public ConfigSize MainQuestSkipSize { get; set; } = new(300, 120);
    public ConfigPoint MainQuestNextTopLeft { get; set; } = new(1500, 880);
    public ConfigSize MainQuestNextSize { get; set; } = new(360, 180);
    public ConfigPoint MainQuestRematchTopLeft { get; set; } = new(1480, 900);
    public ConfigSize MainQuestRematchSize { get; set; } = new(360, 120);
    public ConfigPoint MainQuestToHomeTopLeft { get; set; } = new(40, 900);
    public ConfigSize MainQuestToHomeSize { get; set; } = new(400, 120);
    public ConfigPoint HardQuestDifficultyTopLeft { get; set; } = new(1600, 780);
    public ConfigSize HardQuestDifficultySize { get; set; } = new(300, 100);
    /// <summary>关卡列表左下「MAIN QUEST BATTLE」红底标记（困难已选中）。</summary>
    public ConfigPoint HardQuestBattleTopLeft { get; set; } = new(40, 700);
    public ConfigSize HardQuestBattleSize { get; set; } = new(500, 120);
    /// <summary>左上角主界面房子按钮搜索区（1080p）。</summary>
    public ConfigPoint HudHomeTopLeft { get; set; } = new(0, 0);
    public ConfigSize HudHomeSize { get; set; } = new(220, 160);
    /// <summary>右上角汉堡菜单搜索区（1080p）。</summary>
    public ConfigPoint HudMenuTopLeft { get; set; } = new(1700, 0);
    public ConfigSize HudMenuSize { get; set; } = new(220, 140);
    /// <summary>菜单未命中时的菜单点击兜底（1080p）。</summary>
    public ConfigPoint RedeemMenuClick { get; set; } = new(1868, 55);
    /// <summary>菜单内「コード入力」搜索区（1080p，居中菜单）。</summary>
    public ConfigPoint RedeemCodeEntryTopLeft { get; set; } = new(550, 350);
    public ConfigSize RedeemCodeEntrySize { get; set; } = new(800, 350);
    /// <summary>「コード入力」未命中时的点击兜底（1080p）。</summary>
    public ConfigPoint RedeemCodeEntryClick { get; set; } = new(780, 460);
    /// <summary>兑换码输入框点击点（1080p）。</summary>
    public ConfigPoint RedeemInputClick { get; set; } = new(960, 510);
    /// <summary>「決定」确认钮搜索区（1080p）。</summary>
    public ConfigPoint RedeemConfirmTopLeft { get; set; } = new(900, 880);
    public ConfigSize RedeemConfirmSize { get; set; } = new(500, 180);
    /// <summary>「決定」未命中时的点击兜底（1080p）。</summary>
    public ConfigPoint RedeemConfirmClick { get; set; } = new(1160, 975);
    /// <summary>兑换成功「OK」搜索区（1080p）。</summary>
    public ConfigPoint RedeemResultOkTopLeft { get; set; } = new(700, 820);
    public ConfigSize RedeemResultOkSize { get; set; } = new(520, 200);
    /// <summary>已兑换粉条提示搜索区（1080p，コード入力弹窗中部）。</summary>
    public ConfigPoint RedeemAlreadyUsedTopLeft { get; set; } = new(550, 450);
    public ConfigSize RedeemAlreadyUsedSize { get; set; } = new(820, 180);
    /// <summary>コード入力「キャンセル」点击（1080p）。</summary>
    public ConfigPoint RedeemCancelClick { get; set; } = new(760, 975);
    /// <summary>底栏「ホーム」点击（1080p）；兑换结束后强制回主页。</summary>
    public ConfigPoint RedeemBottomHomeClick { get; set; } = new(150, 990);
    /// <summary>发现 GameKee 新兑换码时是否弹窗询问游戏内兑换。</summary>
    public bool RedeemPromptOnNewCodes { get; set; } = true;
    public string ToggleHotkey { get; set; } = "F10";
    /// <summary>全局暂停/继续快捷键。</summary>
    public string PauseHotkey { get; set; } = "F10";
    /// <summary>全局停止快捷键。</summary>
    public string StopHotkey { get; set; } = "F11";
    /// <summary>是否已确认过首次运行提示弹窗。</summary>
    public bool FirstRunNoticeAccepted { get; set; }
    /// <summary>首次迷宫时「商店未配置」提示是否已点过「继续」。</summary>
    public bool SettlementShopHintAccepted { get; set; }
    /// <summary>上次日常已买够配置数量的游戏日（yyyy-MM-dd，每天 4:00 起算新一日）。</summary>
    public string? LastDailyShopDay { get; set; }

    /// <summary>LastDailyShopDay 是否由购买前后次数复核产生；旧版本的未验证缓存不用于跳过。</summary>
    public bool LastDailyShopDayVerified { get; set; }
    /// <summary>日常四小类临时完成状态所属游戏日（每天 4:00 切日）。</summary>
    public string? DailyShopSubcategoryStateDay { get; set; }
    /// <summary>当日已完成的小类；仅临时跳过，不改用户配置数量。</summary>
    public List<string> DailyShopCompletedSubcategories { get; set; } = [];
    /// <summary>商店「强化素材不足」绿提示搜索区（中心对齐绿条约 (962,411)；模板约 556×102@4K）。</summary>
    public ConfigPoint SettlementShopTipTopLeft { get; set; } = new(782, 351);
    public ConfigSize SettlementShopTipSize { get; set; } = new(360, 120);
    /// <summary>结算左下角「本日の購入回数：已购/上限」整行 ROI（1080p；四个日常小类各自独立，切入后再读）。</summary>
    public ConfigPoint SettlementPurchaseCountTopLeft { get; set; } = new(30, 875);
    public ConfigSize SettlementPurchaseCountSize { get; set; } = new(580, 70);
    /// <summary>结算右上紫晶货币 ROI（1080p；略含图标，OCR 更稳）。</summary>
    public ConfigPoint SettlementCurrencyTopLeft { get; set; } = new(1650, 30);
    public ConfigSize SettlementCurrencySize { get; set; } = new(250, 70);

    public static AutomationConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到配置文件。", path);
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        AutomationConfig config = JsonSerializer.Deserialize<AutomationConfig>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("配置文件内容为空。");
        config.Validate();
        return config;
    }

    public void Save(string path)
    {
        Validate();
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(WindowTitleKeyword))
            throw new InvalidDataException("windowTitleKeyword 不能为空。");
        WindowSelectionMode = WindowSelectionMode.Equals("selected", StringComparison.OrdinalIgnoreCase)
            ? "selected" : "game";
        if (MatchThreshold is <= 0 or > 1)
            throw new InvalidDataException("matchThreshold 必须在 (0, 1] 内。");
        if (TreasureMatchThreshold is <= 0 or > 1)
            throw new InvalidDataException("treasureMatchThreshold 必须在 (0, 1] 内。");
        if (TreasureMatchRetryCount < 1)
            throw new InvalidDataException("treasureMatchRetryCount 必须大于等于 1。");
        if (TreasureMatchRetryDelayMs < 0)
            throw new InvalidDataException("treasureMatchRetryDelayMs 不能为负。");
        if (ReferenceWidth <= 0 || ReferenceHeight <= 0)
            throw new InvalidDataException("参考分辨率必须大于零。");
        if (SettlementSubcategoryTabs.Count < 4 || SettlementBuyButtons.Count < 6)
            throw new InvalidDataException("结算小类页签至少 4 个，购买格至少 6 个。");
        ConfigSize[] requiredSizes =
        [
            FirstSearchSize, SecondSearchSize, ThirdSearchSize, FourthSearchSize, FifthSearchSize,
            QuestBattleSimulateSize, QuestExercisesSize, QuestActivitySize, NavBackSize,
            HomeNavSize, QuestNavSize,
            PartnerSelectionSize, BattleSkipSize, PopupCloseSize, EventChoiceSize, EventRestSize,
            SettlementSearchSize,
            SettlementMultiplierSize, SettlementBuyButtonSearchSize, SettlementConfirmSize,
            SettlementShopTipSize, SettlementPurchaseCountSize, SettlementCurrencySize,
            SettlementCategoryProbeHalfSize, SettlementCategorySearchSize,
            TreasureStateSize, TreasureOptionsSize, RouteSelectionSize, RouteTreasureOptionsSize,
            DifficultyDigitSize, DifficultyListSize, DifficultyDecreaseSize, DifficultyIncreaseSize,
            MainQuestHomeSize, MainQuestBannerSize, MainQuestStartSize, MainQuestSortieSize,
            MainQuestScenarioMenuSize, MainQuestScenarioSpeedSize, MainQuestScenarioOkSize,
            MainQuestScenarioChoiceSize, MainQuestScenarioPortraitSize,
            MainQuestSkipSize, MainQuestNextSize, MainQuestRematchSize, MainQuestToHomeSize,
            HardQuestDifficultySize, HardQuestBattleSize, HudHomeSize, HudMenuSize,
            RedeemCodeEntrySize, RedeemConfirmSize, RedeemResultOkSize, RedeemAlreadyUsedSize,
            DailyShopFreeOffSize, DailyShopExchangeSize, DailyShopExchangeHallSize,
            DailyShopOkSize, DailyShopTicketSize,
            DailyFreeGiftOtokuSize, DailyFreeGiftTitleSize,
            DailyFreeGiftPurchaseSize, DailyFreeGiftOkSize,
            DailyExercisesPrepareSize, DailyExercisesRemainingSize
        ];
        if (requiredSizes.Any(s => s.Width <= 0 || s.Height <= 0))
            throw new InvalidDataException("所有搜索区域尺寸必须大于零。");
        if (SettlementBuyButtonSearchInset.X < 0 || SettlementBuyButtonSearchInset.Y < 0)
            throw new InvalidDataException("settlementBuyButtonSearchInset 不能为负。");
        if (TreasurePriority.Count == 0)
            throw new InvalidDataException("宝物优先级不能为空。");
        TreasurePriority = NormalizeTreasurePriority(TreasurePriority);
        if (DoubleClickIntervalMs < 0 || DetectionPollIntervalMs <= 0 || DetectionTimeoutMs <= 0)
            throw new InvalidDataException("点击间隔不能为负，检测间隔和超时必须大于零。");
        if (MainQuestRunLimit < 1)
            throw new InvalidDataException("mainQuestRunLimit 必须大于等于 1。");
        PipelineTaskOrder = NormalizePipelineTaskOrder(PipelineTaskOrder).ToList();
        if (MazeRunLimit < 0)
            throw new InvalidDataException("mazeRunLimit 不能为负数（0 表示无限）。");
        MazeDifficultyMode = MazeDifficultyRunner.NormalizeMode(MazeDifficultyMode);
        if (MazeDifficultyTarget < 1 || MazeDifficultyTarget > 999)
            throw new InvalidDataException("mazeDifficultyTarget 必须在 1–999。");
        if (ToggleHotkey is not ("F1" or "F2" or "F3" or "F4" or "F5" or "F6" or "F7" or
                                 "F8" or "F9" or "F10" or "F11" or "F12"))
            throw new InvalidDataException("toggleHotkey 仅支持 F1 至 F12。");
        if (!IsSupportedFunctionKey(PauseHotkey) || !IsSupportedFunctionKey(StopHotkey))
            throw new InvalidDataException("暂停和停止快捷键仅支持 F1 至 F12。");
        if (PauseHotkey.Equals(StopHotkey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("暂停和停止快捷键不能相同。");
        if (GameLaunchTimeoutSeconds is < 5 or > 600)
            throw new InvalidDataException("gameLaunchTimeoutSeconds 必须在 5–600 秒。");
        NormalizeSettlementPurchases();
        if (SettlementShopTipSize.Width <= 0 || SettlementShopTipSize.Height <= 0)
            throw new InvalidDataException("settlementShopTipSize 宽高必须大于零。");
    }

    public static IReadOnlyList<string> NormalizePipelineTaskOrder(IEnumerable<string>? order)
    {
        string[] known = ["maze", "mainQuest", "hardMainQuest", "dailyShop", "dailyFreeGift", "dailyExercises"];
        var result = new List<string>();
        if (order is not null)
        {
            foreach (string raw in order)
            {
                string id = raw.Trim();
                if (known.Contains(id, StringComparer.OrdinalIgnoreCase) &&
                    !result.Contains(id, StringComparer.OrdinalIgnoreCase))
                    result.Add(known.First(item => item.Equals(id, StringComparison.OrdinalIgnoreCase)));
            }
        }

        foreach (string id in known)
        {
            if (!result.Contains(id, StringComparer.OrdinalIgnoreCase))
                result.Add(id);
        }

        return result;
    }

    public static bool IsSupportedFunctionKey(string? key) =>
        key is "F1" or "F2" or "F3" or "F4" or "F5" or "F6" or "F7" or
               "F8" or "F9" or "F10" or "F11" or "F12";

    private static List<string> NormalizeTreasurePriority(IEnumerable<string> priority)
    {
        var result = new List<string>();
        foreach (string raw in priority)
        {
            string key = raw.Equals("greatsword", StringComparison.OrdinalIgnoreCase) ? "sword" : raw;
            if (result.Any(existing => existing.Equals(key, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(key);
        }
        return result;
    }

    private void NormalizeSettlementPurchases()
    {
        SettlementPurchases.Daily.SkillBook1 = NormalizeSlots(SettlementPurchases.Daily.SkillBook1, 6);
        SettlementPurchases.Daily.SkillBook2 = NormalizeSlots(SettlementPurchases.Daily.SkillBook2, 6);
        SettlementPurchases.Daily.Disk = NormalizeSlots(SettlementPurchases.Daily.Disk, 6);
        SettlementPurchases.Daily.Unit = NormalizeSlots(SettlementPurchases.Daily.Unit, 6);
        SettlementPurchases.Equipment.Physics = NormalizeSlots(SettlementPurchases.Equipment.Physics, 4);
        SettlementPurchases.Equipment.En = NormalizeSlots(SettlementPurchases.Equipment.En, 4);
        SettlementPurchases.Equipment.Agility = NormalizeSlots(SettlementPurchases.Equipment.Agility, 4);
        SettlementPurchases.Artifactor.Physics = NormalizeSlots(SettlementPurchases.Artifactor.Physics, 5);
        SettlementPurchases.Artifactor.En = NormalizeSlots(SettlementPurchases.Artifactor.En, 5);
        SettlementPurchases.Artifactor.Agility = NormalizeSlots(SettlementPurchases.Artifactor.Agility, 5);
    }

    private static int[] NormalizeSlots(int[]? values, int length)
    {
        var result = new int[length];
        if (values is null)
            return result;
        Array.Copy(values, result, Math.Min(values.Length, length));
        return result;
    }
}

public sealed record ConfigPoint(int X, int Y);
public sealed record ConfigSize(int Width, int Height);
