# X Launcher v3.5 开发版

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
