English | **中文** | 繁體中文 | 日本語

# Better-Muv · 更好的 MuvLuvGirlsGarden

一个基于计算机视觉技术，意图让 **MuvLuvGirlsGarden（マブラヴ・ガールズガーデン）** 迷宫探索更省心的 Windows 自动化工具。

程序查找标题包含 `マブラヴ` 的游戏窗口，以窗口所在完整 **16:9** 显示器为截图与坐标基准，通过模板匹配识别界面并模拟键鼠操作，完成从入口到结算的迷宫循环。

> 本项目**不会**修改游戏文件、读写游戏内存，仅依赖截图识别与模拟点击。

---

## 功能

### 迷宫探索

- 可配置刷取次数
- 可配置商店兑换
- 可设置肉鸽宝物优先度选取

- **事件选择**：默认选择第二选项
- **模板定位工具**：导入从游戏画面裁出的图片，在整屏中定位并生成 1080p 基准的搜索 ROI
- **首页截图器**：先校验游戏窗口与 16:9 显示器，再开放迷宫探索执行页
- **游戏启动**：可在截图器启动时按配置的 exe 路径与参数启动游戏
- **执行控制**：F10 暂停/继续，F11 停止（可在设置中改为其他 F1–F12）

### Todolist

1. 目前只适配 **200 级以下**迷宫
2. 计划添加**自动主线**
3. 计划添加**自动日常**
4. 计划添加**桌面分身**功能

---

## 下载

> [!NOTE]
> 下载地址：[⚡ GitHub Releases](https://github.com/Kotagan/Better-Muv/releases)  
> 仓库主页：[Kotagan/Better-Muv](https://github.com/Kotagan/Better-Muv)

也可自行从源码编译（见下方「开发者」）。

---

## 使用方法

图像识别较吃 CPU。建议在游戏能流畅运行的机器上使用，否则匹配延迟会明显上升。

### 系统要求

- Windows 10 / 11 **64 位**
- [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)（SDK 用于编译；运行自包含发布包时通常无需额外安装）
- 游戏窗口标题包含 `マブラヴ`
- **16:9** 分辨率显示器（`config.json` 坐标基准为 **1920×1080**）

### 坐标与缩放

- 搜索 ROI、写死点击点均按 **1080p** 配置（`TopLeft` + 固定 `Size`，不再用模板尺寸 + padding 推算）
- 映射公式：`screen = Display原点 + 点 × (Display尺寸 / Reference尺寸)`（例如 4K 显示器约为 ×2）
- 模板图可为高分辨率素材，匹配前会缩到逻辑 1080p；模板大于搜索区时会报错或自动放大搜索区

### 注意事项

- 窗口大小变化、切换游戏分辨率、切换显示器后，请**重启本软件**
- 不支持画面滤镜（HDR、显卡滤镜等）；请保持默认亮度
- 当前只针对 **16:9** 布局调参；非 16:9 会拒绝运行
- 模拟点击可能被安全软件拦截，请将程序加入白名单
- 游戏与本工具建议使用**相同权限级别**（若游戏以管理员运行，本工具也需管理员）
- 客户区高度因任务栏略小于显示器属正常情况，以显示器 16:9 为准

### 快速开始

1. （可选）在「设置」页配置游戏 exe 与启动参数，并开启「同时启动游戏」
2. 在首页启动「Better-Muv 截图器」；它会确认游戏窗口和 16:9 显示器
3. 进入「执行」页，点击 **▶ 执行迷宫探索**
4. 运行中可按 F10 暂停/继续，按 F11 停止

### 生成新模板的搜索范围

在「设置」页的「模板定位工具」中选择一张从**当前游戏画面**裁出的图像，保持游戏显示需要识别的界面，再点击「定位并生成 ROI」。工具会输出：

- 模板在 1080p 基准下的左上角与尺寸
- 带 12px 边距的推荐 `TopLeft` 与 `Size`，可直接用于 `AutomationConfig` / `config.json`
- 匹配分数（分数低时建议换一张包含更多文字或图标细节的模板）

模板最好来自同一显示器、同一游戏分辨率的截图；不要使用纯色或过于小的图片。

配置与日志默认路径：

```text
%LocalAppData%\Better-Muv\config.json
%LocalAppData%\Better-Muv\logs\Better-Muv-yyyyMMdd.log
```

---

## FAQ

### 为什么可能需要管理员权限？

如果游戏以管理员权限启动，本工具若不以管理员运行，将无法向游戏窗口注入模拟点击。

### 会不会封号 / 违规？

理论上本工具不做内存读写与文件篡改，但第三方辅助与模拟操作是否被允许取决于游戏运营方条款。请自行承担风险，低调使用。

### 识别不到界面怎么办？

1. 确认游戏所在显示器为 16:9，且窗口标题含 `マブラヴ`
2. 确认本工具与游戏权限级别一致，游戏客户区关键按钮不被其它窗口长期遮挡
3. 开启诊断模式，对照 `diagnostics\` 与日志里的置信度，微调 `config.json` 中 ROI / 点击点，或更新 `Assets/Templates\` 模板
4. 开局约 30 秒、迷宫内约 10 秒持续未命中会主动停止——这是保护机制，不是崩溃

### 配置改了为什么重启还在？

运行时读写的是 `%LocalAppData%\Better-Muv\config.json`，不是程序目录下的默认 `config.json`。

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

打包可安装程序（需先安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)；写入「程序和功能」卸载项与 HKLM 产品注册表）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\pack-installer.ps1 -Version 1.0.0
```

生成物：`dist\Better-Muv-Setup-1.0.0.exe`。

---

## 许可证

请以仓库内许可证文件为准；若暂无许可证文件，使用前请与作者确认。

---

## 问题反馈

请提交 [GitHub Issue](https://github.com/Kotagan/Better-Muv/issues)，或加群 **294674780**。

---

## 致谢

灵感与 README 结构参考了 [BetterGI · 更好的原神](https://github.com/babalae/bettergi) 等开源自动化项目。
