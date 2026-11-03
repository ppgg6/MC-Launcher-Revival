# MC Launcher Revival 1.2 (开发测试版)

Windows WPF / .NET 8 启动器。支持 Mojang 官方版本清单、Vanilla 文件安装、SHA-1/大小校验、Windows libraries 规则筛选、natives 解压、logging 配置下载、Java 主版本检测、现代/旧版启动参数、Minecraft 正版账号设备代码登录、离线本地账户启动。

## v1.3 界面调整

- 调整为 PCL2 风格的浅色布局：左侧导航、顶部标题栏、首页启动横幅、版本选择、账户卡片、Java 检测卡片、游戏目录与日志区域。
- 保留原有微软正版登录、离线账户、版本清单、安装与启动事件绑定。
- 本次主要是界面重构，不代表 Java 自动下载、多线程下载、Forge/Fabric 安装等功能已经实现。

## 编译

需要 Windows 10/11、.NET 8 SDK：

```powershell
dotnet publish .\MC_Launcher_Revival.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

输出 `publish/MC_Launcher_Revival.exe`。仓库已包含 Windows GitHub Actions 工作流，可在 GitHub Actions 中构建并下载 artifact。

## Microsoft 正版登录配置（必须）

本项目不内置任何人的 OAuth 密钥。需要开发者自己在 Microsoft Entra 管理中心注册应用：

1. 选择受支持的帐户类型（个人 Microsoft 帐户）。
2. 在 Authentication 中启用 Public client/native client 流程与允许公共客户端流。
3. 复制 Application (client) ID，填入启动器的 Microsoft OAuth Client ID。
4. 该应用需要允许设备代码流，并使用 `XboxLive.signin offline_access` scope。

用户会在微软官方页面输入一次性设备代码；程序随后执行 Microsoft OAuth → Xbox Live → XSTS → Minecraft Services → Java Profile 验证。程序不接收或保存微软密码。令牌目前仅保存在当前进程内存，关闭程序后需要重新登录。

## 离线 / 本地账户模式

首页提供“离线 / 本地账户”按钮。玩家可不登录微软账号，输入 3–16 位英文字母、数字或下划线组成的昵称；启动器按 Minecraft Java 离线身份规则生成稳定的 OfflinePlayer UUID，并使用 `legacy` 用户类型和离线令牌占位值启动本机已安装的版本。

- 离线模式不验证正版所有权，也不会伪装成微软正版身份。
- 离线模式适合本地单人游戏；正版验证服务器、Realms 等要求在线身份的服务不可用，部分第三方服务器也会拒绝离线身份。
- **离线不等于免安装**：游戏版本的 JSON、客户端 JAR、libraries、assets、natives 和 Java 运行环境必须已经在本机。首次获取版本清单或下载游戏文件仍需要网络；如果电脑完全没有网络，需要事先从可信来源准备完整游戏文件。
- “退出账户”可清除当前会话；重新选择微软正版登录即可切回正版模式。

## 当前实现边界（请务必阅读）

- 当前目标为 Windows x64 Vanilla；离线启动路径已接入，但仍需 Windows 实机集成测试。部分旧版、特殊快照、实验性版本 JSON、依赖规则中的 feature 条件、带继承关系的第三方版本（Forge/Fabric/OptiFine）尚未完整覆盖。
- 规则筛选支持 Windows OS 与常见架构；当前未实现完整 feature 语义（例如自定义分辨率/quick play feature），含 feature 条件的规则会跳过，需继续扩展。
- 安装阶段对依赖和资源下载失败采取失败退出，避免错误地标记为完整安装。
- 启动参数来自版本 JSON，现代 arguments 与旧版 minecraftArguments 均有处理；仍需在真实 Windows 环境用多代版本进行集成测试。
- 本开发容器没有 .NET SDK/Windows 桌面运行时，无法在此处编译或运行 WPF，也无法声称已通过 Windows 实机测试。请使用 Windows GitHub Actions 工作流或 Windows + .NET 8 SDK 编译。

## 安全

请仅从 Mojang/Microsoft 官方服务获取游戏文件并使用你拥有的正版账号。不要把 OAuth Client ID 以外的密钥、密码或令牌发给任何人。

## 测试状态

本次只完成源代码层面的实现和静态检查（XAML XML 可解析、代码大括号配对）；尚未在 Windows 上编译运行，未完成多版本端到端验收。不得将此包视为已通过测试的正式发行版。建议至少验证 1.8.9、1.12.2、1.16.5、1.18.2、1.20.4、1.20.6、1.21.x 与一个最新快照。
