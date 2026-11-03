# MC Launcher Revival 3.0（开发版）

Windows WPF / .NET 8 Minecraft Java 启动器。界面采用 PCL2 的浅色布局与导航习惯进行独立设计；本项目不是 PCL 官方产品，不包含 PCL 专有资源。

## 3.0 本次更新
- 启动器版本标识升级为 3.0，保留浅色导航、首页启动区、版本列表、账户卡片、日志与下载状态区。
- Minecraft 官方版本清单、Vanilla 客户端/依赖/资源下载流程保留。
- 多文件并发下载（最多 8 个）、失败自动重试 3 次、已有文件大小与 SHA-1 校验。
- 新增 Temurin Java 21（Windows x64 JRE）自动下载与解压，安装到所选游戏目录下的 `runtime/temurin-21`；本次会话可直接使用安装后的 Java。
- 模组管理新增批量导入 `.jar`、删除、双击启用/禁用，以及打开模组目录。
- 保留微软 OAuth 登录入口、离线昵称模式、启动内存设置和日志。

## 尚未完成 / 重要限制
- 下载目前是多个文件并发，不是单文件 HTTP 分段下载；暂停、继续、断点续传、独立下载任务队列尚未实现。
- Java 自动安装当前提供 Java 21 JRE；尚未根据所选 Minecraft 版本自动选择 Java 8/17/21，也未提供多版本 Java 管理界面。
- Forge/Fabric 目前提供官方安装器入口；尚未实现安装器自动化执行、版本配置合并或 OptiFine 自动安装。
- 尚无内置模组市场、模组兼容性检查或 Modrinth/CurseForge API 搜索。
- 游戏目录选择仍使用简化的保存对话框方式，建议后续换成标准文件夹选择器。
- 微软正版登录依赖用户自行配置 OAuth Client ID；需要在 Windows 真机上完成 Xbox/XSTS/Minecraft 登录链路测试。
- 本包尚未在 Windows 上编译或实际启动游戏验证。不能据此保证 EXE 已可运行。

## 构建
需要 Windows 和 .NET 8 SDK。仓库 GitHub Actions 使用 Windows runner 构建：

```powershell
dotnet publish MC_Launcher_Revival.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## 使用提醒
首次自动安装 Java 需要网络连接和足够磁盘空间。请仅从可信来源获取模组；模组需与 Minecraft 版本及 Forge/Fabric 加载器匹配。离线模式不是微软正版身份认证。
