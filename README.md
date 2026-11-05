# X Launcher v4.0.0-preview6 开发版

Windows WPF / .NET 8 Minecraft Java Edition launcher.

## 本版新增
- Modrinth 在线模组搜索与安装：搜索项目，按 Minecraft 版本与 Fabric / Forge / NeoForge / Quilt 加载器筛选兼容发布版本，下载 JAR 到当前游戏目录的 `mods` 文件夹，并执行 SHA-1（若 API 提供）与文件大小校验。
- Java 按游戏版本独立指定：Java 管理中心可选择 Java 环境并绑定到指定 Minecraft 版本；绑定关系保存在 `%AppData%/XLauncher/settings.json`。
- Java 精确主版本校验：按版本元数据 `javaVersion.majorVersion` 匹配 Java 主版本，不把 Java 25 错当作 Java 21 的等价替代。
- Java 环境检查 / 修复：检查启动器托管的 Java 8、17、21、25，缺失或校验失败时重新下载安装；下载失败最多重试 3 次，解压后执行 `java -version` 校验。

## 使用说明
1. 在启动器中安装 Minecraft 原版版本，并确保对应 Forge / Fabric / NeoForge / Quilt 加载器已安装。
2. 打开「模组管理」→「在线搜索 / 下载模组（Modrinth）」。
3. 输入关键词，填写 Minecraft 版本，选择加载器后搜索；选中模组并下载。
4. 模组将放入当前游戏目录的 `mods` 文件夹。若模组声明了依赖，请按项目页面说明另外安装依赖。请勿把 Fabric 模组放入 Forge 环境，反之亦然。
5. 打开「Java 管理中心」，选中一个 Java 环境，输入 / 选择 Minecraft 版本，点击「指定给所选游戏版本」。启动时会再次检查指定 Java 是否与版本元数据要求一致。
6. 「检查 / 修复 Java」会检查启动器托管的 Java 8、17、21、25。不会删除系统自行安装的 Java。

## 注意
- Modrinth 搜索和下载需要联网；本功能只安装 Modrinth 的 `mod` 项目，不把资源包、光影包当作模组。
- 模组下载目前为单个发布文件安装；模组依赖需要用户按项目说明补齐，下载目标是当前 Minecraft 游戏目录的共享 `mods` 文件夹。
- 本项目需要在 Windows x64 上使用 .NET 8 SDK 编译。GitHub Actions 已完成 Windows 编译并产出 artifact，但尚未在 Windows 实机上进行 Minecraft 启动测试。


## v3.5 Java 下载优化
- Java 运行时下载改为独立 HTTP 连接池，使用 1 MiB 缓冲区，减少磁盘与网络小块 I/O。
- 下载服务器支持 HTTP Range 时，自动启用 4 路分段并发下载；不支持时自动回退。
- 普通下载支持保留 `.part` 临时文件并尝试断点续传，最多 5 次重试。
- 每隔约 0.7 秒显示已下载大小、百分比和 MB/s 速度。
- 下载后先验证 ZIP 可读，再解压；解压后继续验证 Java 主版本号。
- 说明：实际速度仍受网络、下载服务器限速和电脑带宽影响；并非所有服务器都支持分段下载。


## 体积与运行环境

- 当前发布方式为 **self-contained 单文件**（`--self-contained true`）：启动器自带 .NET / WPF 运行时，用户下载后**双击即可运行，不需要预先安装 .NET 8 Desktop Runtime**。
- 代价是体积较大：GitHub Actions 产出的 win-x64 artifact 压缩后约 64 MB。
- Java 仍由启动器按需下载，不包含在启动器安装包中。
- 构建状态：GitHub Actions 已能在 `windows-latest` 上成功编译并产出 artifact（2026-11-04 实测通过），但尚未在 Windows 实机上进行 Minecraft 启动测试。

### 如果以后想改成小体积分发

- 把 `.github/workflows/build-windows.yml` 里的 `--self-contained true` 改成 `false`（framework-dependent 单文件，体积可降到几 MB）。
- **必须同时**把本节改成「Windows x64 需要安装 .NET 8 Desktop Runtime」，否则文档会与实际产物不符，用户会按错误的说明操作。
- 也可以给工作流加一步「发布目录不超过 N MB 否则构建失败」的检查，避免超限包被当成合格版本发布。



## v4.0.0-preview2 修复

- Java 续传：HTTP 416 且本地 `.part` 长度已等于服务器总长度时，直接复用而不重下。
- Java 续传：`Content-Range` 缺失时不再误判为「起点不一致」并删除有效的续传文件。
- Java 续传：返回的续传起点与本地长度不一致时才重置；416 分支不再重复 Dispose 响应。
- 启动前清理上一批残留的 `.segN` 分段文件，避免把不同批次的片段拼在一起。
- 内存设置：12288 / 16384 保存后不会在重启时被截断成 8192；未知值回落到 4096 而不是 1024。
- 版本列表：`SelectionChanged` 改为在构造函数里只订阅一次，刷新清单不再叠加回调。
- 模组管理：删除模组前增加二次确认。
- 启动器设置：「启动时自动检查已安装版本」现在真的会生效，并写入 `settings.json`。
- 选择游戏目录改用 .NET 8 自带的 `OpenFolderDialog`，不再借用保存文件对话框。
- 新增全局异常处理：未处理异常写入 `%AppData%/XLauncher/logs/launcher-yyyyMMdd.log` 并提示用户，不再静默闪退。
- 老版本启动参数：`minecraftArguments` 已有的 flag 不再重复追加。
- 库文件架构判断改用进程位数（`Is64BitProcess`）。
- GitHub Actions 与 README 对齐：framework-dependent 单文件发布 + 10 MB 体积闸门。

> 本版本仍为源码开发预览，未在 Windows 实机上完成编译与 Minecraft 启动验证。



## v4.0.0-preview3 新增：安装游戏时自动准备 Java

- 安装 Minecraft 版本时读取该版本元数据里的 `javaVersion.majorVersion`，自动下载对应主版本的 Eclipse Temurin，并把结果绑定到这个版本（写入 `settings.json` 的 `versionJavaPaths`）。
- 主页面「版本选择」新增开关「安装时同时下载对应 Java」，默认开启，状态会保存；关掉后仍可在启动时按弹窗提示安装。
- Adoptium 若没有该主版本的 JRE 包（例如部分新版本），自动改用 JDK 包，不再直接失败。
- 元数据缺少 `javaVersion` 时按版本号推断：1.20.5+/1.21 → 21，1.18~1.20 → 17，更早 → 8。
- Java 安装失败不会中断游戏安装：游戏文件保留，界面提示失败原因，日志里记录完整异常。

> Java 运行时约 40 MB，安装阶段状态栏会显示「正在准备 Minecraft x.x 需要的 Java N……」，请耐心等待。



## v4.0.0-preview4：主界面精简

- 首页只保留「启动 / 版本 / 账户」三块，一屏放得下；Java 手选、游戏目录、日志收进「更多设置」折叠区。
- 状态文字与进度条移到底部常驻状态栏，下载和安装过程不会再「看不到进度」。
- 统一按钮、输入框、卡片的圆角与留白，侧栏去掉花哨图标，改成纯文字导航。
- 账户状态文案统一（正版 / 离线 / 未登录）。



## v4.0.0-preview5：Fabric 加载器安装

- 版本卡片新增「安装 Fabric」：选中一个原版版本后点它，启动器会向 Fabric 官方 meta API 查询该版本可用的加载器，写入 `versions/fabric-loader-<loader>-<mc>/`，并把 Fabric 新增的库下载到 `libraries/`。
- 新增版本继承（`inheritsFrom`）合并：启动加载器版本时，会把父版本（原版）的 `assets`、`assetIndex`、`javaVersion`、`logging` 与库列表合并进来，`libraries` 和 `arguments` 按「父在前、子在后」拼接。
- 库解析支持只有 Maven 坐标（`name` + `url`）的库，这是 Fabric 提供的库的格式。
- 启动判断放宽：加载器版本自己没有 jar，只要继承链根版本（原版）的 jar 存在就认为可启动。
- natives 目录改为使用根版本（原版）的 `natives`，不再要求加载器版本自己也有一份。

> 说明：本版只做 Fabric。Forge / NeoForge 需要运行官方安装器，留给下个版本。
> 仍然没有在 Windows 实机上跑过，请以 GitHub Actions 编译 + 实机测试结果为准。



## v4.0.0-preview6：性能与老机器适配

- **下载并发自适应**：按 CPU 核数取 `核数 ÷ 2`（2~8 路）。老机器 4 核只跑 2 路，新机器最多 8 路，避免下载把机器压死。
- **日志与状态批量刷新**：原来每条日志、每次状态更新都同步阻塞 UI 线程；改成入队后每 150 ms 合并刷新一次，下载线程不再排队等界面。
- **日志框限长**：超过 1200 行只保留最后 600 行，长时间下载不会越用越卡。
- **已存在文件快速通道**：大小一致就直接复用，不再对每个文件重算 SHA-1 —— 机械盘上这是安装慢的主因。
- **版本列表虚拟化**：列表开启 `VirtualizingStackPanel` 回收模式，几千个版本也不会一次性创建控件。
- **首次运行按物理内存给默认值**：4 GB 及以下默认 2048 MB，其余 4096 MB。
- **构建配置**：工作站 GC + 并发 GC + TieredPGO，冷启动更稳。

> 说明：并发与内存的自动值只在第一次运行（没有 `settings.json`）时生效，之后以你自己保存的设置为准。
