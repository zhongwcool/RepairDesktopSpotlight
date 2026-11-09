# 桌面聚焦修复

Windows 11 桌面「Windows 聚焦」如果只在几张很旧的系统底图之间循环，多半不是锁屏聚焦坏了。桌面新图由系统包 `MicrosoftWindows.Client.CBS` 里的后台任务，向 `fd.api.iris.microsoft.com`（`placement=88000820`）拉取。

这个工具做只读体检，并在你明确要求时补齐缺失的系统任务、安排一次单次拉取，或把一组备用图写进「下一个背景」清单。它不安装、不写启动项、不常驻。窗口关掉或命令跑完就退出。系统自己的 `BackgroundTask` 可以继续跑，那不是本工具的进程。

## 环境

- 面向 **Windows 11**。依赖本机已经有 `MicrosoftWindows.Client.CBS`，以及 Windows PowerShell 5.1 里的 `Invoke-CommandInDesktopPackage`（通常随系统 Appx 模块提供）。
- 其他 Windows 版本没有验证。
- 包身份和「管理员」是两回事。主路径不需要提权。
- 只提供 **win-x64** 自包含单文件。没有在 ARM64 上构建或实测，不能当成一个 exe 覆盖所有架构。
- WPF 不能用 NativeAOT。发布没有设置 `PublishAot`。

自包含单文件第一次运行时，.NET 可能会在本机解出原生库缓存。那不是常驻程序，本工具也不会在运行时删除这块共享缓存。

## 命令

无参数会打开窗口，并自动做一次只读体检。下面这些参数走命令行，不打开主窗口：

```text
DesktopSpotlightFix.exe --diagnose
DesktopSpotlightFix.exe --repair
DesktopSpotlightFix.exe --force
DesktopSpotlightFix.exe --force --open-gate
DesktopSpotlightFix.exe --force --open-gate --wait
DesktopSpotlightFix.exe --inject-fallback
DesktopSpotlightFix.exe --rollback
DesktopSpotlightFix.exe --clear-oneshot
DesktopSpotlightFix.exe --log C:\path\run.log --redact
```

| 参数 | 作用 |
| --- | --- |
| `--diagnose` | 只读体检。不改注册表、文件或任务 |
| `--repair` | 补齐缺失的系统后台任务。已有官方拉图证据时不安排单次拉取 |
| `--force` | 即使任务都在，也安排一次单次拉取 |
| `--open-gate` | 把 `WallpaperRefresh` 回拨至少 2 小时。若 `Rotation` 仍把 60 分钟冷却关着，会一并回拨，并写进日志 |
| `--inject-fallback` | 下载一组图到「图片\SpotlightDesktop」，写入聚焦清单。不能代替官方每日拉图 |
| `--wait` | 安排之后最多观察约 35 分钟。只在 `--repair` 或 `--force` 时生效 |
| `--rollback` | 只回滚本工具最近一次备份里已经写下的项。发现后来又被改过就中止，不覆盖 |
| `--clear-oneshot` | 只注销名字完全等于 `DesktopSpotlight.BackgroundTask.UpdateTimer.oneshot` 的单次任务 |
| `--redact` | 日志里隐藏机器名和用户名。asid 默认只保留前 8 位 |

安排成功只表示系统任务已登记。Windows 的 `TimeTrigger` 最短大约 15 分钟，还要对齐时间片，实际常常是 **15–30 分钟** 后才可能拉取，不是整点准时。单次任务的名字带 `.oneshot`，它不是正式的每日 `UpdateTimer`。

## 退出码

| 码 | 含义 |
| --- | --- |
| 0 | 体检没有发现需要处理的问题，或修复、安排、注入、回滚、清理按设计完成 |
| 1 | 只读体检发现需要处理的问题 |
| 2 | 运行错误，例如进不了包身份、安排失败、回滚冲突 |
| 3 | 用户取消 |
| 4 | `--wait` 大约 35 分钟内没有观察到变化 |

冷却还没结束所以没有安排拉图，也是按设计完成，退出码是 0。退出码 0 同样不等于壁纸已经换好。

## 发布

在仓库根目录：

```bat
dotnet publish DesktopSpotlightFix\DesktopSpotlightFix.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o dist\win-x64
```

产物是 `dist\win-x64\DesktopSpotlightFix.exe`。目标框架是 `net10.0-windows`。

## 日志、备份、临时文件

- 日志：`%LOCALAPPDATA%\DesktopSpotlightFix\logs\`
- 备份：`%LOCALAPPDATA%\DesktopSpotlightFix\backups\<时间>\`
- 包身份临时目录：`%TEMP%\dsfix-<guid>\`。确认本次 helper 结束后删除。删不掉会留下带归属标记的目录，下次启动再清。没有标记的 `dsfix-*` 不会因为前缀被删掉。符号链接和目录联接会跳过。

工具不会上传日志，也不会调用设置壁纸的系统接口。那样会把背景从「Windows 聚焦」改成「图片」。

## 和参考脚本的差异

参考脚本是 `Repair-DesktopSpotlight.ps1`，本工具默认仍用 Windows PowerShell 5.1 在包身份里注册任务。

- `--wait` 对比安排前的快照，看壁纸、上次尝试时间、图片数量或 `88000820` 缓存有没有变化。参考脚本在已经有成功记录或缓存时可能立刻结束。
- `--repair` 跳过单次拉取时，要求 `UpdateTimer` 加上一组证据：`success=true`、时间戳、以及带图片字段的 `88000820` 缓存；或者当前壁纸不是系统底图、也不是本工具注入目录，并且已有 `88000820` 缓存。单独的历史 success、单独的缓存、单独的注入图都不够。
- 诊断会分别进入 `Global.DesktopSpotlight` 和 `Global.IrisService` 再合并任务列表。
- `--open-gate` 在 `Rotation` 仍关闭冷却时会同时回拨 `Rotation`。参考脚本只写 `WallpaperRefresh`。
- 临时脚本使用进程级 `-ExecutionPolicy Bypass`，不修改系统执行策略。策略仍被组织锁定时会直接报告失败。
- `Invoke-CommandInDesktopPackage` 经常在包内脚本写完结果之前就返回，所以这里会轮询输出文件里的 `END=1`，而不是只等外层进程退出。

## 本机测试记录

记录时间：2026-11-09。没有把未跑过的项目写成已验证。

| 项 | 结果 |
| --- | --- |
| 系统 | 注册表产品名 Windows 10 Pro，DisplayVersion 26H2，内部版本 26300.9818。这台机器的桌面聚焦组件是 Windows 11 的 CBS 包 |
| CBS | `MicrosoftWindows.Client.CBS_cw5n1h2txyewy`，清单版本 1000.26100.385.0 |
| SDK | .NET SDK 10.0.403 |
| 单元测试 | `dotnet test` Release，22 项通过 |
| 发布包 | `dist\win-x64\DesktopSpotlightFix.exe`（自包含单文件，约 62 MB）同样跑过 `--diagnose`，退出码 0，helper 临时目录已删除 |
| `--diagnose` | 退出码 0。进入了 `Global.DesktopSpotlight`，8 个核心任务都在，后台权限 `AllowedSubjectToSystemPolicy`，`88000820` 状态码 200 且含图片字段，接口能返回壁纸，`BackgroundType` 为 3。`SpotlightDisabledReason=100` 只作为信息 |
| 只读证明 | 体检前后导出并比对了 `HKCU\...\DesktopSpotlight`、`IrisService`、`Explorer\Wallpapers`、`Control Panel\Desktop`，哈希一致 |
| 临时目录 | 带本工具归属标记、且 helper 已退出的 `dsfix-*` 会在下次启动删除。没有标记的同前缀目录会留下。本次体检自己的 helper 目录在结束后已删除 |
| `--clear-oneshot` | 退出码 0，结果是当前没有这个单次任务（`REMOVED=0`）。没有注销正式 `UpdateTimer`。`DesktopSpotlight` 注册表导出哈希与运行前一致 |
| 窗口 | 启动了主窗口，关闭窗口后进程退出码 0。没有做界面像素级验收 |
| 未跑 | `--repair`、`--force`、`--open-gate`、`--inject-fallback`、`--wait`、`--rollback`。这台机器体检结论是无需处理，这些命令会改注册表、登记单次任务或下载图片。也没有做任务缺失机器、ARM64，以及其他 Windows 版本 |
