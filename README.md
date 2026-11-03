# X 启动器（Minecraft Java）

这是 MC Launcher Revival 项目的改名与界面升级开发版。启动器名称为 **X**，界面布局参考常见 Minecraft 启动器的清晰导航方式；它不是 PCL2 官方产品，也不包含 PCL2 的专有资源。

## 本版更新
- 启动器名称改为 X。
- 左侧导航拆分为「启动游戏」「下载游戏」「已安装版本」「账户管理」「模组管理」「启动器设置」。
- 下载游戏页面：显示官方版本清单并触发安装。
- 启动游戏页面：只扫描游戏目录中 JSON 与 JAR 均存在的本地版本，避免把未安装版本和已安装版本混在一起。
- 设置窗口：游戏目录、Java 可执行文件路径、OAuth Client ID、默认内存，并将设置保存到 `%AppData%\XLauncher\settings.json`。
- 保留已有的微软登录/离线账户、基础 Java 检测、模组文件导入/启用/禁用、并发下载和 SHA-1 校验代码。

## 构建
使用 Windows 和 .NET 8 SDK：
```powershell
dotnet publish MC_Launcher_Revival.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```
也可以使用仓库中的 GitHub Actions 工作流。

## 当前限制
- 这是开发版源码，尚未在本环境完成 Windows 编译与真实 Minecraft 启动测试。
- 下载页当前以 Mojang 官方 Vanilla 版本清单为主；Forge/Fabric 自动安装器尚未集成。
- 已安装版本页以本地版本目录中同时存在 `.json` 和 `.jar` 为识别条件。
- 暂停/继续、单文件分段断点续传、模组市场仍待开发。
- 微软正版登录需要用户自行配置 Microsoft OAuth Client ID。
