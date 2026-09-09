# tools — 调试 / AI 联调（**不进安装包**）

主工程 `Better-Muv.csproj` 已排除 `tools\**`。日常请用统一入口：

```powershell
.\tools\bm.ps1 help
.\tools\bm.ps1 flows
.\tools\bm.ps1 status --json
```

二次调用可跳过编译：`$env:BM_NO_BUILD='1'; .\tools\bm.ps1 probe maze`

## BmCli 命令速查

| 命令 | 作用 |
|------|------|
| `flows` | 主线/迷宫/困难/一条龙流程地图 |
| `status` | 窗口、视口缩放、配置路径 |
| `windows` | 枚举可见窗口候选 |
| `capture [path]` | 截客户区（默认 `_logtmp/probe/live-*.png`） |
| `probe <preset>` | 批量模板探测（对齐生产 ROI/阈值） |
| `match tpl --roi x,y,w,h` | 单模板试匹配 |
| `crop <key> --preset mq` | 导出 ROI 图给人眼核对 |
| `click x y [--dbl]` | 1080 逻辑坐标点击 |
| `rois [all\|maze\|main\|hard]` | 打印配置 ROI |
| `points` | 打印 FirstClick / BeginStage 等 |

全局：`--json`、`--shot png`、`--templates dir`、`--title kw`、`--no-focus`、`--selected`

### Preset

- `entry` — HUD 主页 / 任务 / 迷宫卡 / 主线横幅  
- `mainquest` — 主线全套（menu/speed/skip/next/…）  
- `maze` — 迷宫内界面 + 入口卡  
- `hard` — 困难主线  
- `promo` / `hud`

### 推荐卡住排查

```powershell
.\tools\bm.ps1 capture
.\tools\bm.ps1 probe mainquest --json
.\tools\bm.ps1 crop scenarioMenu --preset mainquest
.\tools\bm.ps1 probe mainquest --shot _logtmp\probe\live-HHMMSS.png --json
```

## 旧单用途工具

仍可用，逻辑较散：`CaptureGame`、`ClickGame`、`ProbeTpl`、`ProbeNav`、`ProbeSettle`、`DebugMaze`、`ocr-probe`。新联调优先 `BmCli`。
