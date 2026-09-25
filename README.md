English | **中文** | 繁體中文 | 日本語

# Better-Muv · 更好的 MuvLuvGirlsGarden

一个基于计算机视觉技术，意图让 **MuvLuvGirlsGarden（マブラヴ・ガールズガーデン）** 日常养成更省心的 Windows 自动化工具。

程序查找标题包含 `マブラヴ` 的游戏窗口，以窗口所在完整 **16:9** 显示器为截图与坐标基准，通过模板匹配识别界面并模拟键鼠操作。

> 本项目**不会**修改游戏文件、读写游戏内存，仅依赖截图识别与模拟点击。

---

## 功能

### 任务（可单独运行 / 一条龙串行）

- **迷宫探索**：可配置次数、结算商店购买、肉鸽宝物优先级；事件默认选第二项
- **自动主线 / 困难主线**
- **每日商店**：日常兑换（如 100% OFF 等）
- **每日免费礼包**：商店 → お得パック → デイリー無料パック
- **一条龙**：按任务页开关与顺序依次执行

### 兑换码

- 启动时自动从 GameKee 同步兑换码
- 有未用码时可弹窗询问：**启动兑换** / **已兑换，不再提示**
- 设置页可开关询问、手动兑换未用码、打开网页
- 游戏内路径：右上菜单 → コード入力 → 粘贴确认（已用码会识别并标记后继续下一条）

### 其它

- **模板定位工具**：导入裁图，生成 1080p 基准搜索 ROI
- **游戏启动**：可随工具自动拉起游戏 exe
- **执行控制**：默认 F10 暂停/继续，F11 停止（可在设置中改为其它 F1–F12）
- **运行日志**：侧栏查看；支持导出日志与截图包
- **桌面分身**：独立会话入口（需管理员等条件）

### Todolist

1. 迷宫目前主要针对高难度区域调参，低等级场景可能需再适配
2. 社团等周边自动化仍待完善

---

## 下载

> [!NOTE]
> 下载地址：[→ GitHub Releases](https://github.com/Kotagan/Better-Muv/releases)  
> 仓库主页：[Kotagan/Better-Muv](https://github.com/Kotagan/Better-Muv)

也可自行从源码编译（见下方「开发者」）。

---

## 使用方法

图像识别较吃 CPU。建议在游戏能流畅运行的机器上使用，否则匹配延迟会明显上升。

### 系统要求

- Windows 10 / 11 **64 位**
- [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)（SDK 用于编译；运行自包含安装包时通常无需额外安装）
- 游戏窗口标题包含 `マブラヴ`
- **16:9** 分辨率显示器（`config.json` 坐标基准为 **1920×1080**）

### 坐标与缩放

- 搜索 ROI、点击点均按 **1080p** 配置
- 运行时按显示器尺寸缩放到实际分辨率（如 4K 下倍率约为 ×2）
- 模板图可为高分辨率素材，匹配前会缩到逻辑 1080p

### 注意事项

- 窗口大小变化、切换游戏分辨率、切换显示器后，请**重启本软件**
- 不支持画面滤镜（HDR、显卡滤镜等）；请保持默认亮度
- 当前只针对 **16:9** 布局调参；非 16:9 会拒绝运行
- 模拟点击可能被安全软件拦截，请将程序加入白名单
- 游戏与本工具建议使用**相同权限级别**（若游戏以管理员运行，本工具也需管理员）

### 快速开始

1. （可选）在「设置」配置游戏路径，并开启「同时启动游戏」
2. 在首页点「启动一条龙」，或进入「任务」页单独运行某项
3. 兑换码：设置 → 兑换码；有未用码时启动会询问是否兑换
4. 运行中可按快捷键暂停/停止；可打开「运行日志」查看进度

### 生成新模板的搜索范围

在「设置」页的「模板定位工具」中选择一张从**当前游戏画面**裁出的图像，保持游戏显示需要识别的界面，再点击「定位 ROI」。工具会输出 1080p 基准下的 `TopLeft` / `Size` 与匹配分数。

配置与日志默认路径：

```text
%LocalAppData%\Better-Muv\config.json
%LocalAppData%\Better-Muv\redemption-codes.json
%LocalAppData%\Better-Muv\logs\
```

---

## FAQ

### 为什么可能需要管理员权限？

如果游戏以管理员权限启动，本工具若不以管理员运行，将无法向游戏窗口注入模拟点击。

### 会不会封号 / 违规？

理论上本工具不做内存读写与文件篡改，但第三方辅助与模拟操作是否被允许取决于游戏运营方条款。请自行承担风险，低调使用。

### 识别不到界面怎么办？

1. 确认游戏所在显示器为 16:9，且窗口标题含 `マブラヴ`
2. 确认本工具与游戏权限级别一致，客户区关键按钮不被其它窗口遮挡
3. 开启诊断模式，对照诊断目录与日志置信度，微调 `config.json` 的 ROI / 点击点，或更新 `Assets/Templates\` 模板

### 配置改了为什么重启还在？

运行时读写的是 `%LocalAppData%\Better-Muv\config.json`，不是程序目录下的默认 `config.json`。

### 兑换码弹窗不想再出现？

在「设置 → 兑换码」关闭「有未用码时询问是否兑换」。点弹窗「已兑换，不再提示」只会把当前未用码标为已用，**不会**关掉该开关。

---

## 开发者

### 如何编译？

要求 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
dotnet build Better-Muv.csproj -c Release
dotnet run --project Better-Muv.csproj
```

运行测试：

```powershell
dotnet test tests\BetterMuv.Tests\BetterMuv.Tests.csproj -c Release
```

发布示例（自包含）：

```powershell
dotnet publish Better-Muv.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
```

打包可安装程序（需先安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\pack-installer.ps1 -Version 1.2.1
```

生成物：`dist\Better-Muv-Setup-1.2.1.exe`。

---

## 许可证

请以仓库内许可证文件为准；若暂无许可证文件，使用前请与作者确认。

---

## 问题反馈

请提交 [GitHub Issue](https://github.com/Kotagan/Better-Muv/issues)，或加群 **294674780**。

---

## 致谢

灵感与 README 结构参考了 [BetterGI · 更好的原神](https://github.com/babalae/better-genshin-impact) 等开源自动化项目。
