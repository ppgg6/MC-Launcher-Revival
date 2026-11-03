# X Launcher v3.4 开发版

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
