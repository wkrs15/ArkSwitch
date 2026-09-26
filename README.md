# ArkSwitch · 方舟切服 / 账号切换

明日方舟 PC 端**官服 / B服 双服切换 + 多账号切换备份**小工具。基于 [lTinchl/ArknightsLauncher](https://github.com/lTinchl/ArknightsLauncher)（MIT）重写，使用 **WPF + [HandyControls](https://github.com/ghost1372/HandyControls)**，仅保留核心功能，界面紧凑。

> 说明：软件内不使用任何明日方舟 / 鹰角 / B 站官方素材（图标为自行生成）。仅更新切服文件时访问鹰角官方启动器 API 与官方 CDN（含启动时的自动检查，无更新时零下载），且所有下载均经 MD5 校验。

## 功能

- **双服切换启动**：点击「启动官服 / 启动B服」，自动把 `load/ArkOfficial` 或 `load/ArkBilibili` 中的 SDK 文件覆盖到游戏根目录并启动游戏；软件与游戏同磁盘时优先使用**硬链接**，失败自动回退为复制
- **切服文件在线更新**：软件**启动时自动检查**更新（无更新时完全静默，网络异常自动跳过），也可点「更新切服文件」手动更新——从鹰角官方启动器 API（`launcher.hypergryph.com`）获取最新版本，从**官方 CDN**（`ak.hycdn.cn`）下载当前最新的渠道差异文件，逐文件 MD5 校验、原子替换，已有相同文件自动跳过；启动游戏时若本地缺少对应服的切服文件也会自动拉取
- **游戏目录选择**：主窗口切服按钮上方可直接浏览选择游戏根目录（含 `Arknights.exe`，未设置时启动游戏会自动引导选择）
- **启动时弹窗选账号**：点击「启动官服 / 启动B服」会弹出选择窗口，列出该服的账号与「保留当前登录（不切换账号）」选项（默认预选设为默认的账号），取消则不启动
- **账号所属服务器**：添加 / 重命名账号时可选官服 / B服，账号列表显示 `[官服]` / `[B服]` 标签
- **账号管理（弹窗）**：主窗口点「账号管理」弹出独立窗口，含账号列表、新增（可选服务器）、删除、重命名（可改服务器）、设为默认（⭐ 标记）、「备份当前账号」与「切换到选中账号」——切换会恢复该账号登录数据**并自动切到其所属服务器**（切服文件缺失时自动从官方 CDN 下载）

## 原理

- 切服：游戏根目录下的平台 SDK 文件（`U8SDK.dll`、`PlatformProcess`、`hgsdk.dll` / `PCGameSDK.dll`、`U8Data/` 等）决定连接哪个服务器，用对应服务器的文件覆盖即可
- 账号：登录数据位于 `%USERPROFILE%\AppData\LocalLow\Hypergryph\Arknights\sdk_data_*`，备份保存在 `%LOCALAPPDATA%\ArkSwitch\AccountBackups\{账号ID}`，复制失败自动重试

## 使用

1. 安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0/runtime)（x64，选 "Desktop App"；已装更高版本如 .NET 10 的可直接运行）。未安装时启动软件 Windows 会弹窗引导下载，装一次即可
2. 以管理员身份运行 `ArkSwitch.exe`（写游戏目录、硬链接、结束游戏进程需要）
3. **首次运行会自动从官方 CDN 下载切服文件**（约 210MB，保存到 exe 旁的 `load` 目录；也可以点「更新切服文件」手动下载）
4. 在主窗口「游戏目录」行浏览选择游戏根目录（需包含 `Arknights.exe`）
5. 点击「启动官服 / 启动B服」→ 在弹窗中选择本次使用的账号 → 自动切服并启动游戏；软件启动时会自动检查并更新切服文件
6. 账号：主窗口点「账号管理」→ 新增账号时选择所属服务器 → 先登录该账号并关闭游戏 → 列表选中槽位 → 「备份当前账号」；之后「切换到选中账号」会同时切换登录数据和服务器
7. 若装有旧版 ArknightsLauncher，首次运行会自动迁移其配置与账号备份

- 配置文件：`%LOCALAPPDATA%\ArkSwitch\config.json`
- 账号备份：`%LOCALAPPDATA%\ArkSwitch\AccountBackups`
- 删除上述目录即可重置软件

## 构建

需要 .NET 8 SDK（依赖 HandyControls NuGet 包）：

```bash
dotnet build -c Debug    # 调试版（asInvoker，不弹 UAC）
dotnet publish -c Release -r win-x64 --self-contained false
```

**仓库与构建产物不包含任何游戏资源**（`load/` 切服文件），用户首次运行时由软件从官方 CDN 自动下载。

发布采用**依赖框架**模式：产物为单个 `ArkSwitch.exe`（约 2MB），要求用户已安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0/runtime)（缺失时 Windows 会弹窗引导下载；`RollForward=LatestMajor` 允许直接使用更高版本的运行时）。若想改回免安装的自包含模式，把 csproj 中 `SelfContained` 改为 `true` 重新发布即可（exe 约 160MB）。

核心逻辑有自动化测试（`tests/ArkSwitch.Tests`，使用临时目录沙箱，CI 自动执行）；本地可跑 `dotnet run --project tests/ArkSwitch.Tests -- --real` 做真机数据的**非破坏性**验证（备份 → MD5 校验 → 恒等恢复）。

### GitHub Actions

仓库内置 `.github/workflows/build.yml`：

- push 到 `master` / `main`、提交 PR 或手动触发时：自动构建并上传 artifact
- 推送 `v*` 标签（如 `v1.4.0`）时：自动创建 GitHub Release 并附上单文件 `ArkSwitch-<版本>-win-x64.exe`（依赖框架模式产物仅约 2MB，不再打包 zip）

开发辅助参数：`--devshot <目录>` 输出窗口截图；`--dump-api <文件>` 导出 HandyControls API 与主题资源键；`--update-payload <日志>` 实际执行一次切服文件更新（用于测试）。

## 项目结构

```
├── App.xaml(.cs)          # 应用入口、全局异常、开发辅助模式
├── MainWindow.xaml(.cs)   # 官服账号选择 + 双服启动
├── Core/
│   ├── Models.cs          # AppConfig/ConfigStore（JSON 配置与旧版迁移）
│   ├── ServerSwitcher.cs  # 切服（硬链接优先）+ 游戏启动/进程管理
│   └── Accounts.cs        # sdk_data 备份/恢复
├── Windows/               # 账号管理弹窗、启动进度窗、输入对话框
├── Assets/app.ico         # 自绘中性图标（tools/make_icon.ps1 生成）
└── load/                  # 运行时由软件从官方 CDN 自动下载生成（仓库与构建产物不含）
```

## 致谢

- 思路与切服文件：[lTinchl/ArknightsLauncher](https://github.com/lTinchl/ArknightsLauncher)（MIT）、[SkyHao723/arknightsSwitcher](https://github.com/SkyHao723/arknightsSwitcher)
- 切服文件在线更新机制：移植自 [lTinchl/Xel-Launcher](https://github.com/lTinchl/Xel-Launcher)（Apache-2.0）的 ServerPayloadUpdater，思路来源 [Hi3Helper.Plugin.Arknights](https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph)（game_files 清单 AES 解密实现同源）
- UI 框架：[HandyControls](https://github.com/ghost1372/HandyControls)

## 免责声明

本工具不会将您的账号文件上传至互联网，仅用于本地文件替换；使用本工具造成的任何后果由使用者自行承担。
