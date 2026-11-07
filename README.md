# X Launcher v5.0.0-preview9 开发版

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



## v4.0.0-preview7：修复 Java 下载「文件被占用」

- 复现：安装 Java 时出现 `The process cannot access the file '...temurin-25.zip.part' because it is being used by another process`，整个启动流程被判失败。
- 原因：`.part` 临时文件可能被另一条下载线程或杀毒软件占用，而删除动作没有容错，异常一路抛到界面上。
- 修复：新增 `TryDeleteFile`，所有临时文件（`.part`、`.segN`、安装包）删除失败只记日志、不中断流程。
- 修复：Java 安装加互斥锁。同一时间只跑一个安装流程；重复点击时给出「Java 正在安装中，请稍候」的明确提示，而不是抢同一个文件。



## v4.0.0-preview8：版本隔离 / 多源下载 / Forge 安装

参考 PCL 社区版（Apache-2.0，C#）的工程思路重写，未复制其源码。

- **版本隔离**：版本卡片新增「版本隔离」勾选。勾上后该版本用自己的 `versions/<id>/` 作为游戏目录，存档、模组、配置互不干扰；不勾则沿用全局目录。选择状态保存在 `settings.json` 的 `isolatedVersions`。
- **下载多源回退**：官方源（Mojang / Forge maven）下载失败时自动切换 BMCLAPI 镜像（`resources` / `libraries` / `piston-meta` 三类地址已覆盖），国内网络更快。
- **Forge 安装**：版本卡片新增「安装 Forge」。流程为查 `promotions_slim.json` 拿推荐版本 → 下载官方 installer → 用匹配主版本的 Java 执行 `--installClient` → 扫描 `versions/` 找到安装好的版本并加入列表；安装器输出会记进日志，失败时把最后 500 字符显示出来。
- 资源包与安装器统一放进 `installers/` 目录，避免污染游戏目录。

> 说明：Forge 安装器需要联网且耗时较长，未在本机实机验证；建议先用一个原版版本试装。



## v4.0.0-preview9：模组列表免搜索

- 打开「在线搜索 / 下载模组」窗口时**自动加载 50 个模组**，不再必须先输关键词。
- 关键词留空＝浏览模式，按当前「Minecraft 版本 + 加载器」过滤，默认按下载量排序。
- 新增排序下拉：按下载量 / 按最近更新 / 按相关度；切换即重新加载。
- 加载结果为空时会提示「换加载器或清空条件」，而不是静默空白。



## v4.0.0-preview10

- **模组下载自动带版本**：打开模组窗口时，Minecraft 版本和加载器按当前选中的版本自动填好（加载器版本会沿 `inheritsFrom` 找到原版版本号）。
- **下载游戏页只列官方版本**：Fabric / Forge 这类加载器版本不会再混进「下载游戏」列表，它们只属于「已安装版本」。



## v4.0.0-preview11

- 模组列表新增「图标」列，显示每个模组在 Modrinth 的头像，便于一眼区分（图标由 Modrinth CDN 提供，需要联网加载）。



## v4.0.0-preview12

- 版本卡片新增「删除版本」：删除选中版本的整个目录（含该版本的存档、模组、配置）。
- 删除前会额外提醒两件事：该版本开了「版本隔离」时存档会一起没；如果有加载器版本继承它，删掉后那些版本起不来。
- 删除只动 `versions/<id>/`，不碰共享的 `libraries/`、`assets/` 和全局目录。



## v4.5.0-preview1（4.5 第一批）

- **NeoForge 安装**：从 `maven-metadata.xml` 取版本（编号规则 `21.1.x` ↔ Minecraft `1.21.1`），下载官方 installer 后跑 `--installClient`。
- **崩溃分析**：读取最近一次 `crash-reports/*.txt` 或 `logs/latest.log`，匹配常见特征（内存不足、Java 版本、模组缺失/冲突、Mixin 失败、GLFW 等）后给出中文结论，并附上日志结尾，不用玩家自己看堆栈。

> 4.5 后续批次：整合包导入（.mrpack）、界面重做、皮肤编辑器、单文件断点续传。



## v4.5.0-preview2（4.5 第二批）

- **整合包导入**：选一个 `.mrpack`，读 `modrinth.index.json`，把 `files` 里的资源逐个下载到当前版本的游戏目录，再解压 `overrides/` 覆盖配置；带路径穿越防护，包版本与所选版本不一致时会先警告。
- **单文件断点续传**：下载中断后保留 `.part`，下次用 HTTP Range 续传，不再整包重下；只有校验失败才丢弃临时文件。



## v4.5.0-preview3（4.5 第三批）

- **皮肤编辑器**：侧栏新增入口，打开一个 64×64 画布，左键涂色、右键擦除，16 色调色板，可导入已有 PNG（自动补齐 64×32 旧皮肤）并导出 PNG。
- 说明：目前是平面像素编辑，没有 3D 人物预览；皮肤保存后需要自己上传到 Minecraft 官网或对应服务器。



## v4.5.0-preview4（4.5 第四批）

- **资源包与光影管理**：侧栏入口，两个标签页分别管理 `resourcepacks/` 与 `shaderpacks/`；双击/按钮切换启用（改 `.disabled` 后缀）、导入 zip、删除、打开文件夹。
- **服务器收藏**：侧栏入口，地址存在 `xlauncher-servers.json`，支持添加/删除/复制地址（进游戏后粘贴到「多人游戏」）。
- **检查更新**：读 GitHub 最新 release 的 tag 与当前版本对比，可直接打开下载页。
- **界面**：卡片加柔和投影，侧栏补齐新入口。



## v4.5.0-preview5：兼容性与优化（借鉴 PCL 思路）

- **Java 探测更全**：除 PATH / JAVA_HOME / 启动器 runtime 外，再读注册表（HKLM JavaSoft，64/32 位都查）与常见厂商目录（Oracle Java、Adoptium、Zulu、Microsoft、BellSoft、Corretto、Semeru）。
- **JVM 参数自动调优**：Java 9+ 自动加 G1GC 一组参数（G1NewSizePercent=20、G1ReservePercent=20、MaxGCPauseMillis=50、G1HeapRegionSize=32M），Java 8 加 `-XX:+UseG1GC`，统一加 `-Dfile.encoding=UTF-8`（中文日志不乱码）。
- **启动前自检**：内存分配超过物理内存 75%、Java 是 32 位、natives 目录为空，这三种情况先弹窗警告再让你决定是否继续。



## v5.0.0-preview1：账号持久化与安全模式

- **账号持久化**：微软登录后把 refresh_token 用 DPAPI（当前用户）加密存到 `%AppData%/XLauncher/account.bin`，下次启动自动静默刷新，不用每次重新登录；退出登录会同时删除凭据。
- **安全模式**：启动前若发现「上次启动没成功」（进程 60 秒内退出），会提示是否用安全模式启动 —— 临时把 `mods` 改名为 `mods.safe-<时间>`，游戏退出后自动还原，用来判断是不是模组把游戏搞崩的。
- 启动结果记录在 `versions/<id>/xlauncher-last-launch.json`。



## v5.0.0-preview2：手机遥控面板

- 侧栏新增「手机遥控」：在电脑上起一个本地小网页（端口 51234），手机连同一个 WiFi，浏览器打开弹出的地址即可。
- 手机页面能：查看已安装版本、选择版本并**启动游戏**、实时查看启动器日志（每 3 秒刷新）。
- 实现是内嵌的 TCP + 极简 HTTP，不依赖管理员权限或 URL 保留，不装任何运行时。
- 注意：这是「手机遥控电脑上的启动器」，不是手机本地跑 Minecraft（那需要 Android 上的 JRE 与图形转译层，属于另一个工程）。



## v5.0.0-preview3：实例化 / 自动更新 / servers.dat

- **实例设置**：每个版本可以单独指定内存、专用 Java、备注（存进 settings.json 的 `instances`），启动时实例设置优先于全局。
- **自动更新**：检查更新时如果 release 里带 `.exe` 资产，会下载后用批处理替换当前程序并重启（没有资产则回落到打开下载页）。
- **写入服务器列表**：服务器收藏窗口新增「同步到游戏」，把收藏写成 `servers.dat`（NBT + gzip，写入前自动备份原文件），进游戏就能在多人列表里看到。



## v5.0.0-preview4：修复编译错误

- 皮肤编辑器里 `BitmapScalingMode` 的命名空间写错了（`System.Windows.Media.Imaging` → 应为 `System.Windows.Media`），导致 CI 编译失败。
- 三处 `Dispatcher.BeginInvoke` 未等待产生 CS4014 警告，改成显式丢弃（`_ =`）。



## v5.0.0-preview5：Java 改用国内镜像

- Adoptium 官方接口会 302 到 GitHub，国内经常超时（报 `github.com:443` 连不上）。
- 现在优先从**清华 TUNA 的 Adoptium 镜像**取 Windows x64 的 JRE/JDK zip（`mirrors.tuna.tsinghua.edu.cn/Adoptium/<主版本>/<jre|jdk>/x64/windows/`），失败才回退 Adoptium 官方接口。
- 日志里会写明这次用的是镜像还是官方源。



## v5.0.0-preview6：下载进度窗口与并发优化

- **下载进度窗口**：开始装游戏时自动弹出，显示百分比、已完成/总数、还剩多少个文件、已下载大小、用时与平均速度，以及当前正在下载的文件名；完成后 3 秒自动关闭。
- **小文件高并发**：资源/模组这类小于 2 MB 的文件走独立通道（并发 = CPU 核数，6~16 路），大文件仍按核数÷2，避免几百个小文件被几个大文件堵住。



## v5.0.0-preview7：皮肤编辑器先给基础人物

- 打开皮肤编辑器时不再是空白画布，而是先铺好一个基础人物（按 64×64 标准 UV：肤色头部与四肢、蓝发、深蓝上衣 + 白围裙、裤子与鞋），再在上面涂改。
- 新增「重置为默认人物」按钮，可以一键回到这个基础稿；「清空」仍是纯空白画布。



## v5.0.0-preview9

- 撤掉「史蒂夫 / 爱丽克斯 / 猪」模板，皮肤编辑器只保留一个基础人物底稿。
- 想要原版人物，用「导入皮肤 PNG」加载自己的皮肤文件即可。
