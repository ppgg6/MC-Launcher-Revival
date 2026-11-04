# X Launcher v4.0.0-preview2 开发版

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
- 本项目需要在 Windows x64 上使用 .NET 8 SDK 编译。源码包尚未由当前环境进行 Windows 编译或实际 Minecraft 启动测试。


## v3.5 Java 下载优化
- Java 运行时下载改为独立 HTTP 连接池，使用 1 MiB 缓冲区，减少磁盘与网络小块 I/O。
- 下载服务器支持 HTTP Range 时，自动启用 4 路分段并发下载；不支持时自动回退。
- 普通下载支持保留 `.part` 临时文件并尝试断点续传，最多 5 次重试。
- 每隔约 0.7 秒显示已下载大小、百分比和 MB/s 速度。
- 下载后先验证 ZIP 可读，再解压；解压后继续验证 Java 主版本号。
- 说明：实际速度仍受网络、下载服务器限速和电脑带宽影响；并非所有服务器都支持分段下载。


## v3.6 Lite（小体积版）

- 改为 .NET 8 framework-dependent 单文件发布，启动器本体不再捆绑 .NET/WPF 运行时，以显著减小体积。
- GitHub Actions 会检查整个发布目录是否不超过 10 MB；超过则构建失败，不会把超限包冒充为合格版本。
- **运行前提：** Windows x64 电脑需要安装 .NET 8 Desktop Runtime（桌面运行时）。这部分运行环境不包含在启动器内，因此安装后启动器体积更小。
- Java 仍由启动器按需下载，不包含在启动器安装包中。
- 注意：本源码包已生成，但需要 GitHub Actions 实际发布后才能确认最终发布目录体积及启动情况。



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
