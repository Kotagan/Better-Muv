# Better-Muv

Windows 上的 Muv-Luv 迷宫自动化原型。程序查找标题包含 `マブラヴ` 的游戏窗口，
并以该窗口所在的完整 16:9 显示器作为截图和坐标基准，
在指定区域匹配任务图标，识别成功后执行一轮配置好的点击流程。

## 运行

要求 .NET 10 SDK：

```powershell
dotnet run --project Better-Muv.csproj
```

点击“开始单轮”前，请确保游戏窗口可见，且游戏与本工具使用相同权限级别。
识别区域、阈值、坐标和延时均可在 `config.json` 中调整。

## 验证

```powershell
dotnet test tests\BetterMuv.Tests\BetterMuv.Tests.csproj -c Release
```
