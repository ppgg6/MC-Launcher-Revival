using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace MC_Launcher_Revival;

public partial class MainWindow : Window
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly SemaphoreSlim DownloadSlots = new(8, 8);
    private int _assetDone;
    private List<VersionRow> _versions = new();
    private string _gameDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    private readonly HashSet<string> _downloadedAssets = new(StringComparer.OrdinalIgnoreCase);
    private string? _accessToken, _uuid, _username, _userType;
    private bool _offlineMode;
    private string? _javaPath;

    public MainWindow()
    {
        InitializeComponent();
        GameDirectoryText.Text = _gameDir;
        Log("X 启动器初始化：独立下载页、启动页、设置页与模组管理。");
        LoadSavedSettings();
        Log("启动器已初始化。请先选择账户，再安装并启动游戏。");
    }

    private void Log(string text)
    {
        Dispatcher.Invoke(() =>
        {
            LogText.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
            LogText.ScrollToEnd();
        });
    }

    private async void LoadVersions_Click(object sender, RoutedEventArgs e) => await LoadVersionsAsync();

    private async Task LoadVersionsAsync()
    {
        SetStatus("正在连接 Mojang 官方版本清单……");
        try
        {
            using var response = await Http.GetAsync(ManifestUrl);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            _versions = doc.RootElement.GetProperty("versions").EnumerateArray().Select(x => new VersionRow(
                x.GetProperty("id").GetString() ?? "",
                x.GetProperty("type").GetString() ?? "",
                (x.GetProperty("releaseTime").GetString() ?? "").Replace("T", " ").Replace("Z", ""),
                x.GetProperty("url").GetString() ?? "")).ToList();
            VersionsList.ItemsSource = _versions;
            VersionsList.SelectionChanged += (_, __) => UpdateLaunchAvailability();
            SetStatus($"成功获取 {_versions.Count} 个官方版本。");
            Log($"版本清单已加载：{_versions.Count} 个版本。");
        }
        catch (Exception ex) { SetStatus("获取版本列表失败：" + ex.Message); Log("清单失败：" + ex); }
    }

    private void SetStatus(string text) => Dispatcher.Invoke(() => StatusText.Text = "状态：" + text);

    private void DetectJava_Click(object sender, RoutedEventArgs e)
    {
        foreach (var path in new[] {
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JAVA_HOME")) ? "" :
                Path.Combine(Environment.GetEnvironmentVariable("JAVA_HOME")!, "bin", "java.exe"),
            "java.exe"
        }.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            try
            {
                var psi = new ProcessStartInfo(path, "-version") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                if (p == null) continue;
                var output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                SetStatus("Java 检测结果：" + output.Trim());
                Log("Java 检测：" + output.Trim());
                return;
            }
            catch { }
        }
        SetStatus("未找到 Java。请安装适配 Minecraft 版本的 Java，并配置 PATH 或 JAVA_HOME。");
    }


    private async void InstallJava_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show("将从 Eclipse Adoptium 官方 API 下载并安装 Temurin Java 21（Windows x64）。是否继续？", "自动安装 Java 21", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            string javaRoot = Path.Combine(_gameDir, "runtime");
            string installDir = Path.Combine(javaRoot, "temurin-21");
            Directory.CreateDirectory(javaRoot);
            SetStatus("正在查询 Adoptium 官方 Java 21 下载包……");
            Log("开始下载 Eclipse Temurin Java 21。");
            string api = "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse";
            string archive = Path.Combine(javaRoot, "temurin-21.zip");
            using (var response = await Http.GetAsync(api, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
                await input.CopyToAsync(output);
            }
            SetStatus("下载完成，正在解压 Java 运行环境……");
            if (Directory.Exists(installDir)) Directory.Delete(installDir, true);
            Directory.CreateDirectory(installDir);
            ZipFile.ExtractToDirectory(archive, installDir, true);
            File.Delete(archive);
            string? javaExe = Directory.GetFiles(installDir, "java.exe", SearchOption.AllDirectories).FirstOrDefault(x => x.Replace('\\','/').Contains("/bin/java.exe", StringComparison.OrdinalIgnoreCase));
            if (javaExe == null) throw new FileNotFoundException("压缩包中没有找到 bin/java.exe。");
            _javaPath = javaExe;
            SetStatus("Java 21 安装完成：" + javaExe);
            Log("Java 21 安装完成：" + javaExe);
        }
        catch (Exception ex) { SetStatus("Java 自动安装失败：" + ex.Message); Log("Java 安装错误：" + ex); MessageBox.Show(ex.Message, "Java 安装失败"); }
    }

    private void ChooseDirectory_Click(object sender, RoutedEventArgs e)
    {
        // FolderBrowserDialog would require WinForms; use a simple path prompt for dependency-free WPF.
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "选择游戏目录：在文件名处输入目录名并保存", FileName = "选择目录", Filter = "目录选择提示|*.folder" };
        if (dialog.ShowDialog() == true)
        {
            _gameDir = Path.GetDirectoryName(dialog.FileName) ?? _gameDir;
            GameDirectoryText.Text = _gameDir;
        }
    }

    private async void InstallSelected_Click(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not VersionRow selected)
        {
            SetStatus("请先选择一个版本。");
            return;
        }
        _gameDir = string.IsNullOrWhiteSpace(GameDirectoryText.Text) ? _gameDir : GameDirectoryText.Text.Trim();
        try
        {
            Directory.CreateDirectory(_gameDir);
            Progress.Value = 0;
            SetStatus($"正在准备安装 {selected.Id}……");
            Log($"开始安装 Vanilla {selected.Id}");
            var metaBytes = await Http.GetByteArrayAsync(selected.Url);
            using var metaDoc = JsonDocument.Parse(metaBytes);
            var meta = metaDoc.RootElement;
            var versionDir = Path.Combine(_gameDir, "versions", selected.Id);
            Directory.CreateDirectory(versionDir);
            await File.WriteAllBytesAsync(Path.Combine(versionDir, selected.Id + ".json"), metaBytes);
            Progress.Value = 5;

            await DownloadClientAsync(meta, versionDir, selected.Id);
            Progress.Value = 20;
            await DownloadLibrariesAsync(meta);
            await ExtractNativesAsync(meta);
            await DownloadLoggingAsync(meta);
            Progress.Value = 60;
            await DownloadAssetsAsync(meta);
            Progress.Value = 100;
            SetStatus($"版本 {selected.Id} 安装完成。选择正版登录或离线本地账户后即可尝试启动。");
            Log($"安装流程完成：{selected.Id}。已处理依赖、原生库和 logging 配置。");
            UpdateLaunchAvailability();
        }
        catch (Exception ex)
        {
            SetStatus("安装失败：" + ex.Message);
            Log("安装失败：" + ex);
        }
    }

    private async Task DownloadClientAsync(JsonElement meta, string versionDir, string id)
    {
        if (!meta.TryGetProperty("downloads", out var downloads) || !downloads.TryGetProperty("client", out var client))
            throw new InvalidDataException("版本元数据中没有 client 下载项。");
        var url = client.GetProperty("url").GetString() ?? throw new InvalidDataException("客户端 URL 缺失。");
        var path = Path.Combine(versionDir, id + ".jar");
        await DownloadAndVerifyAsync(url, path,
            client.TryGetProperty("sha1", out var sha) ? sha.GetString() : null,
            client.TryGetProperty("size", out var size) ? size.GetInt64() : null);
        Log("客户端 JAR 下载并校验完成。");
    }

    private async Task DownloadLibrariesAsync(JsonElement meta)
    {
        if (!meta.TryGetProperty("libraries", out var libs)) return;
        var list = libs.EnumerateArray().ToList();
        var jobs = new List<Task>();
        foreach (var lib in list)
        {
            if (!IsAllowed(lib) || !lib.TryGetProperty("downloads", out var downloads)) continue;
            if (downloads.TryGetProperty("artifact", out var artifact)) AddArtifactJob(artifact);
            if (downloads.TryGetProperty("classifiers", out var classifiers))
            {
                var nativeKey = FindNativeClassifier(lib);
                if (nativeKey != null && classifiers.TryGetProperty(nativeKey, out var native)) AddArtifactJob(native);
            }
        }
        await Task.WhenAll(jobs);
        Log($"依赖库并发处理完成：{jobs.Count} 个文件任务。");
        void AddArtifactJob(JsonElement artifact)
        {
            var path = ResolveLibraryPath(artifact);
            var url = artifact.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(url)) return;
            jobs.Add(DownloadAndVerifyAsync(url!, path!, artifact.TryGetProperty("sha1", out var sh) ? sh.GetString() : null,
                artifact.TryGetProperty("size", out var sz) ? sz.GetInt64() : null));
        }
    }

    private bool IsAllowed(JsonElement lib)
    {
        if (!lib.TryGetProperty("rules", out var rules)) return true;
        bool allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            bool match = true;
            if (rule.TryGetProperty("os", out var os))
            {
                if (os.TryGetProperty("name", out var name) && name.GetString() != "windows") match = false;
                if (os.TryGetProperty("arch", out var arch) && arch.GetString() is string a &&
                    !(Environment.Is64BitOperatingSystem ? (a == "x86_64" || a == "amd64" || a == "x64") : (a == "x86" || a == "i386"))) match = false;
                if (os.TryGetProperty("version", out var ver) && ver.GetString() is string pattern &&
                    !System.Text.RegularExpressions.Regex.IsMatch(Environment.OSVersion.Version.ToString(), pattern)) match = false;
            }
            if (rule.TryGetProperty("features", out var features) && features.EnumerateObject().Any()) match = false;
            if (match) allowed = rule.TryGetProperty("action", out var action) && action.GetString() == "allow";
        }
        return allowed;
    }

    private string? FindNativeClassifier(JsonElement lib)
    {
        if (!lib.TryGetProperty("natives", out var natives) || !natives.TryGetProperty("windows", out var win)) return null;
        var key = win.GetString()?.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
        return key;
    }

    private async Task DownloadAssetsAsync(JsonElement meta)
    {
        if (!meta.TryGetProperty("assetIndex", out var index)) return;
        var id = index.GetProperty("id").GetString() ?? "legacy";
        var url = index.GetProperty("url").GetString();
        if (string.IsNullOrWhiteSpace(url)) return;
        var indexPath = Path.Combine(_gameDir, "assets", "indexes", id + ".json");
        await DownloadAndVerifyAsync(url!, indexPath, index.TryGetProperty("sha1", out var sha) ? sha.GetString() : null,
            index.TryGetProperty("size", out var size) ? size.GetInt64() : null);
        using var indexDoc = JsonDocument.Parse(await File.ReadAllBytesAsync(indexPath));
        if (!indexDoc.RootElement.TryGetProperty("objects", out var objects)) return;
        var entries = objects.EnumerateObject().ToList();
        int done = 0;
        await Parallel.ForEachAsync(entries, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (obj, ct) =>
        {
            var hash = obj.Value.GetProperty("hash").GetString();
            if (!string.IsNullOrWhiteSpace(hash) && hash.Length >= 2)
            {
                var assetPath = Path.Combine(_gameDir, "assets", "objects", hash[..2], hash);
                var assetUrl = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
                await DownloadAndVerifyAsync(assetUrl, assetPath, hash,
                    obj.Value.TryGetProperty("size", out var sz) ? sz.GetInt64() : null);
            }
            int current = Interlocked.Increment(ref done);
            if (current % 50 == 0 || current == entries.Count)
            {
                Dispatcher.Invoke(() => Progress.Value = 60 + 39.0 * current / Math.Max(1, entries.Count));
                Log($"资源并发下载进度：{current}/{entries.Count}");
            }
        });
        Log($"资源文件并发处理完成：{done}/{entries.Count}。");
    }

    private async Task DownloadAndVerifyAsync(string url, string path, string? sha1, long? expectedSize)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            if ((!expectedSize.HasValue || fi.Length == expectedSize.Value) &&
                (string.IsNullOrWhiteSpace(sha1) || await Sha1Async(path) == sha1.ToLowerInvariant())) return;
            File.Delete(path);
        }
        await DownloadSlots.WaitAsync();
        try
        {
            string temp = path + ".part";
            Exception? last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    await using (var input = await response.Content.ReadAsStreamAsync())
                    await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                        await input.CopyToAsync(output);
                    var info = new FileInfo(temp);
                    if (expectedSize.HasValue && info.Length != expectedSize.Value) throw new InvalidDataException("文件大小校验失败：" + Path.GetFileName(path));
                    if (!string.IsNullOrWhiteSpace(sha1) && await Sha1Async(temp) != sha1.ToLowerInvariant()) throw new InvalidDataException("SHA-1 校验失败：" + Path.GetFileName(path));
                    File.Move(temp, path, true);
                    return;
                }
                catch (Exception ex) { last = ex; if (File.Exists(temp)) File.Delete(temp); if (attempt < 3) await Task.Delay(500 * attempt); }
            }
            throw new IOException($"下载失败（已重试 3 次）：{Path.GetFileName(path)}", last);
        }
        finally { DownloadSlots.Release(); }
    }

    private static async Task<string> Sha1Async(string path)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA1.HashDataAsync(fs)).ToLowerInvariant();
    }

    private string? ResolveLibraryPath(JsonElement artifact)
    {
        if (artifact.TryGetProperty("path", out var p))
            return Path.Combine(_gameDir, "libraries", p.GetString()!.Replace('/', Path.DirectorySeparatorChar));
        return null;
    }

    private async Task ExtractNativesAsync(JsonElement meta)
    {
        string nativesDir = Path.Combine(_gameDir, "versions", meta.TryGetProperty("id", out var idEl) ? idEl.GetString()! : "unknown", "natives");
        Directory.CreateDirectory(nativesDir);
        if (!meta.TryGetProperty("libraries", out var libs)) return;
        foreach (var lib in libs.EnumerateArray())
        {
            if (!IsAllowed(lib) || !lib.TryGetProperty("downloads", out var downloads) || !downloads.TryGetProperty("classifiers", out var classifiers)) continue;
            string? key = FindNativeClassifier(lib);
            if (key == null || !classifiers.TryGetProperty(key, out var native)) continue;
            string? jarPath = ResolveLibraryPath(native);
            if (jarPath == null || !File.Exists(jarPath)) continue;
            string[] excludes = Array.Empty<string>();
            if (lib.TryGetProperty("extract", out var extract) && extract.TryGetProperty("exclude", out var ex)) excludes = ex.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            using var archive = ZipFile.OpenRead(jarPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) || excludes.Any(x => entry.FullName.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
                string destination = Path.GetFullPath(Path.Combine(nativesDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(Path.GetFullPath(nativesDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
        }
        Log("Windows 原生库解压完成。");
    }

    private async Task DownloadLoggingAsync(JsonElement meta)
    {
        if (!meta.TryGetProperty("logging", out var logging) || !logging.TryGetProperty("client", out var client) || !client.TryGetProperty("file", out var file)) return;
        string id = meta.GetProperty("id").GetString()!;
        string? url = file.GetProperty("url").GetString();
        if (string.IsNullOrWhiteSpace(url)) return;
        string path = Path.Combine(_gameDir, "assets", "log_configs", file.GetProperty("id").GetString() ?? "client-logging.xml");
        await DownloadAndVerifyAsync(url, path, file.TryGetProperty("sha1", out var sha) ? sha.GetString() : null, file.TryGetProperty("size", out var size) ? size.GetInt64() : null);
        Log("Logging 配置下载完成。");
    }

    private async void MicrosoftLogin_Click(object sender, RoutedEventArgs e)
    {
        string clientId = OAuthClientIdText.Text.Trim();
        if (string.IsNullOrWhiteSpace(clientId)) { MessageBox.Show("请先填写你在 Microsoft Entra 应用注册中创建的 Public client application Client ID。", "需要 Client ID"); return; }
        try
        {
            SetStatus("正在请求 Microsoft 设备代码……");
            using var deviceResp = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", new FormUrlEncodedContent(new Dictionary<string,string> { ["client_id"] = clientId, ["scope"] = "XboxLive.signin offline_access" }));
            var device = JsonDocument.Parse(await deviceResp.Content.ReadAsStringAsync()).RootElement;
            if (!deviceResp.IsSuccessStatusCode) throw new Exception(device.ToString());
            string code = device.GetProperty("device_code").GetString()!;
            string userCode = device.GetProperty("user_code").GetString()!;
            string verifyUrl = device.TryGetProperty("verification_uri_complete", out var vu) ? vu.GetString()! : device.GetProperty("verification_uri").GetString()!;
            Process.Start(new ProcessStartInfo(verifyUrl) { UseShellExecute = true });
            MessageBox.Show($"请在浏览器登录 Microsoft 账号并输入代码：{userCode}\n完成后点击确定，启动器将继续验证。", "Microsoft 设备登录");
            string? msToken = null;
            int interval = device.TryGetProperty("interval", out var it) ? it.GetInt32() : 5;
            DateTime deadline = DateTime.UtcNow.AddSeconds(device.GetProperty("expires_in").GetInt32());
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(Math.Max(5, interval) * 1000);
                using var poll = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string,string> { ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["client_id"] = clientId, ["device_code"] = code }));
                var resultText = await poll.Content.ReadAsStringAsync(); using var result = JsonDocument.Parse(resultText);
                if (poll.IsSuccessStatusCode) { msToken = result.RootElement.GetProperty("access_token").GetString(); break; }
                string error = result.RootElement.TryGetProperty("error", out var er) ? er.GetString() ?? "" : "";
                if (error == "authorization_pending") continue;
                if (error == "slow_down") { interval += 5; continue; }
                throw new Exception(resultText);
            }
            if (msToken == null) throw new TimeoutException("Microsoft 登录超时。");
            var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new { Properties = new { AuthMethod="RPS", SiteName="user.auth.xboxlive.com", RpsTicket="d=" + msToken }, RelyingParty="http://auth.xboxlive.com", TokenType="JWT" });
            string xblToken = xbl.GetProperty("Token").GetString()!; string userHash = xbl.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;
            var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new { Properties = new { SandboxId="RETAIL", UserTokens = new[] { xblToken } }, RelyingParty="rp://api.minecraftservices.com/", TokenType="JWT" });
            string xstsToken = xsts.GetProperty("Token").GetString()!;
            using var mcResp = await Http.PostAsJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" });
            using var mcDoc = JsonDocument.Parse(await mcResp.Content.ReadAsStringAsync()); mcResp.EnsureSuccessStatusCode();
            _accessToken = mcDoc.RootElement.GetProperty("access_token").GetString();
            _offlineMode = false;
            // The profile endpoint verifies ownership of a Java profile before allowing launch.
            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            profileRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
            using var profileResp = await Http.SendAsync(profileRequest);
            using var profile = JsonDocument.Parse(await profileResp.Content.ReadAsStringAsync()); profileResp.EnsureSuccessStatusCode();
            _uuid = profile.RootElement.GetProperty("id").GetString(); _username = profile.RootElement.GetProperty("name").GetString(); _userType = "msa"; _offlineMode = false;
            PlayerNameText.Text = _username; AccountStatusText.Text = "账号：已登录正版 Java 版 · " + _username;
            UpdateLaunchAvailability();
            Log("Microsoft → Xbox Live → XSTS → Minecraft Services 认证成功；已获取 Java Profile。"); SetStatus("正版账号认证成功。");
        }
        catch (Exception ex) { _accessToken = null; _offlineMode = false; UpdateLaunchAvailability(); AccountStatusText.Text = "账号：登录失败"; SetStatus("认证失败：" + ex.Message); Log("认证失败：" + ex); MessageBox.Show(ex.Message, "登录失败"); }
    }

    private void OfflineLogin_Click(object sender, RoutedEventArgs e)
    {
        string name = PlayerNameText.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9_]{3,16}$"))
        {
            MessageBox.Show("离线昵称必须为 3–16 位，只能使用英文字母、数字和下划线。", "昵称格式不正确");
            return;
        }

        // Minecraft Java offline profile UUID: UUID.nameUUIDFromBytes("OfflinePlayer:" + name).
        _username = name;
        _uuid = OfflineUuid(name);
        _accessToken = "0"; // Offline sessions are not authenticated by Mojang/Microsoft.
        _userType = "legacy";
        _offlineMode = true;
        AccountStatusText.Text = "账号：离线本地账户 · " + name + "（未验证正版身份）";
        UpdateLaunchAvailability();
        Log($"已切换到离线本地账户：{name}；该身份不具备正版服务器认证。UUID={_uuid}");
        SetStatus("离线模式已启用。可启动本地已完整安装的版本；正版服务器、Realms 等服务不可用。");
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        _accessToken = null; _uuid = null; _username = null; _userType = null; _offlineMode = false;
        AccountStatusText.Text = "账号：未登录";
        UpdateLaunchAvailability();
        SetStatus("已退出当前账户。请选择微软正版登录或离线本地账户。");
    }

    private static string OfflineUuid(string playerName)
    {
        byte[] bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        // Match java.util.UUID.nameUUIDFromBytes(): set UUID version 3 and RFC 4122 variant bits.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        string hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private void UpdateLaunchAvailability()
    {
        bool hasIdentity = !string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_uuid) && !string.IsNullOrWhiteSpace(_accessToken);
        bool hasVersion = VersionsList.SelectedItem is VersionRow vr &&
            File.Exists(Path.Combine(_gameDir, "versions", vr.Id, vr.Id + ".jar")) &&
            File.Exists(Path.Combine(_gameDir, "versions", vr.Id, vr.Id + ".json"));
        LaunchButton.IsEnabled = hasIdentity && hasVersion;
    }

    private async Task<JsonElement> PostJsonAsync(string url, object body)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = null });
        using var response = await Http.PostAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json")); string content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content); if (!response.IsSuccessStatusCode) throw new Exception(content); return doc.RootElement.Clone();
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_accessToken == null || _uuid == null || _username == null) { MessageBox.Show("请先选择微软正版登录或离线本地账户。", "需要选择账户"); return; }
        if (VersionsList.SelectedItem is not VersionRow selected) { SetStatus("请先选择版本。"); return; }
        try
        {
            string versionDir = Path.Combine(_gameDir, "versions", selected.Id), jsonPath = Path.Combine(versionDir, selected.Id + ".json"), jarPath = Path.Combine(versionDir, selected.Id + ".jar");
            if (!File.Exists(jsonPath) || !File.Exists(jarPath)) throw new FileNotFoundException("所选版本尚未完整安装，请先安装。 ");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath)); var meta = doc.RootElement;
            int javaMajor = meta.TryGetProperty("javaVersion", out var jv) && jv.TryGetProperty("majorVersion", out var jm) ? jm.GetInt32() : (selected.Id.StartsWith("1.20.5") || selected.Id.StartsWith("1.21") ? 21 : selected.Id.StartsWith("1.18") || selected.Id.StartsWith("1.19") || selected.Id.StartsWith("1.20") ? 17 : 8);
            string java = FindJava(javaMajor); string natives = Path.Combine(versionDir, "natives");
            var cp = new List<string>(); if (meta.TryGetProperty("libraries", out var libs)) foreach (var lib in libs.EnumerateArray()) if (IsAllowed(lib) && lib.TryGetProperty("downloads", out var dl) && dl.TryGetProperty("artifact", out var art)) { var lp = ResolveLibraryPath(art); if (lp != null && File.Exists(lp)) cp.Add(lp); }
            cp.Add(jarPath);
            string assetIndex = meta.TryGetProperty("assetIndex", out var ai) ? ai.GetProperty("id").GetString()! : selected.Id;
            var vars = new Dictionary<string,string> { ["${natives_directory}"]=natives, ["${launcher_name}"]="MCLauncherRevival", ["${launcher_version}"]="1.2", ["${classpath}"]=string.Join(Path.PathSeparator,cp), ["${classpath_separator}"]=Path.PathSeparator.ToString(), ["${library_directory}"]=Path.Combine(_gameDir,"libraries"), ["${auth_player_name}"]=_username, ["${version_name}"]=selected.Id, ["${game_directory}"]=_gameDir, ["${assets_root}"]=Path.Combine(_gameDir,"assets"), ["${assets_index_name}"]=assetIndex, ["${auth_uuid}"]=_uuid, ["${auth_access_token}"]=_accessToken!, ["${user_type}"]=_userType ?? "legacy", ["${version_type}"]=meta.TryGetProperty("type",out var typ)?typ.GetString()!:"release", ["${user_properties}"]="{}", ["${clientid}"]="", ["${auth_xuid}"]="", ["${auth_session}"]=_accessToken!, ["${game_assets}"]=Path.Combine(_gameDir,"assets","virtual","legacy"), ["${resolution_width}"]="854", ["${resolution_height}"]="480" };
            var args = new List<string>();
            if (meta.TryGetProperty("arguments", out var arguments)) { args.Add("-Xmx"+GetMemory()+"M"); args.Add("-Djava.library.path="+natives); AddArguments(arguments,"jvm",args,vars); if(meta.TryGetProperty("mainClass",out var mc)) args.Add(mc.GetString()!); AddArguments(arguments,"game",args,vars); }
            else { args.Add("-Djava.library.path="+natives); args.Add("-cp"); args.Add(vars["${classpath}"]); args.Add("-Xmx"+GetMemory()+"M"); args.Add("net.minecraft.client.main.Main"); string legacy = meta.TryGetProperty("minecraftArguments",out var ma)?ma.GetString()??"":""; args.AddRange(legacy.Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(x=>ReplaceVars(x,vars))); args.Add("--version"); args.Add(selected.Id); args.Add("--gameDir"); args.Add(_gameDir); args.Add("--assetsDir"); args.Add(Path.Combine(_gameDir,"assets")); args.Add("--assetIndex"); args.Add(assetIndex); args.Add("--uuid"); args.Add(_uuid); args.Add("--accessToken"); args.Add(_accessToken!); args.Add("--userType"); args.Add(_userType ?? "legacy"); args.Add("--username"); args.Add(_username); }
            if (meta.TryGetProperty("logging",out var logging) && logging.TryGetProperty("client",out var lc) && lc.TryGetProperty("argument",out var la)) { string cfg=Path.Combine(_gameDir,"assets","log_configs",lc.GetProperty("file").GetProperty("id").GetString()??""); if(File.Exists(cfg)) args.Insert(0,ReplaceVars(la.GetString()??"",new Dictionary<string,string>{{"${path}",cfg}})); }
            var psi = new ProcessStartInfo(java) { WorkingDirectory=_gameDir, UseShellExecute=false };
            foreach(var a in args) psi.ArgumentList.Add(a);
            Log("启动命令："+java+" "+string.Join(" ",args.Select(a=>a.Contains(' ')?"[带空格参数]":a)));
            var proc=Process.Start(psi) ?? throw new Exception("Java 进程未能启动。"); SetStatus((_offlineMode ? "Minecraft 已通过离线模式启动，进程 PID=" : "Minecraft 已启动，进程 PID=")+proc.Id); Log("Minecraft 进程已启动 PID="+proc.Id+(_offlineMode?"（离线身份）":"（正版身份）"));
        }
        catch(Exception ex) { SetStatus("启动失败："+ex.Message); Log("启动失败："+ex); MessageBox.Show(ex.Message,"启动失败"); }
    }

    private static string ReplaceVars(string value, Dictionary<string,string> vars) { foreach(var kv in vars) value=value.Replace(kv.Key,kv.Value,StringComparison.Ordinal); return value; }
    private static void AddArguments(JsonElement root,string kind,List<string> output,Dictionary<string,string> vars)
    {
        if(!root.TryGetProperty(kind,out var list)) return;
        foreach(var item in list.EnumerateArray())
        {
            JsonElement value=item; if(item.ValueKind==JsonValueKind.Object) { if(!RuleMatches(item)) continue; if(!item.TryGetProperty("value",out value)) continue; }
            if(value.ValueKind==JsonValueKind.String) output.Add(ReplaceVars(value.GetString()!,vars));
            else if(value.ValueKind==JsonValueKind.Array) foreach(var v in value.EnumerateArray()) if(v.ValueKind==JsonValueKind.String) output.Add(ReplaceVars(v.GetString()!,vars));
        }
    }
    private static bool RuleMatches(JsonElement item)
    {
        if(!item.TryGetProperty("rules",out var rules)) return true; bool allow=false;
        foreach(var rule in rules.EnumerateArray()) { bool match=true; if(rule.TryGetProperty("os",out var os)&&os.TryGetProperty("name",out var n)&&n.GetString()!="windows") match=false; if(rule.TryGetProperty("features",out var f)&&f.EnumerateObject().Any()) match=false; if(match) allow=rule.TryGetProperty("action",out var a)&&a.GetString()=="allow"; } return allow;
    }
    private int GetMemory() => MemoryCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(),out int mb) ? mb : 4096;
    private string FindJava(int major)
    {
        var candidates=new List<string>(); if (!string.IsNullOrWhiteSpace(_javaPath) && File.Exists(_javaPath)) candidates.Add(_javaPath); string? home=Environment.GetEnvironmentVariable("JAVA_HOME"); if(!string.IsNullOrWhiteSpace(home)) candidates.Add(Path.Combine(home,"bin","java.exe")); candidates.Add("java.exe");
        foreach(var candidate in candidates) try { var p=Process.Start(new ProcessStartInfo(candidate,"-version"){RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true}); if(p==null) continue; string output=p.StandardError.ReadToEnd(); p.WaitForExit(); var m=System.Text.RegularExpressions.Regex.Match(output,@"version ""(?<v>\d+(?:\.\d+)*)"); int found=8; if(m.Success) { var parts=m.Groups["v"].Value.Split('.'); found=parts[0]=="1" && parts.Length>1 ? int.Parse(parts[1]) : int.Parse(parts[0]); } if(found>=major) return candidate; } catch{} throw new FileNotFoundException($"需要 Java {major} 或更高版本，请安装并配置 JAVA_HOME。");
    }

    private void Mods_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _gameDir = string.IsNullOrWhiteSpace(GameDirectoryText.Text) ? _gameDir : GameDirectoryText.Text.Trim();
            string modsDir = Path.Combine(_gameDir, "mods");
            Directory.CreateDirectory(modsDir);
            var files = Directory.GetFiles(modsDir).Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)).ToList();
            var window = new Window { Title = "模组管理 · MC Launcher Revival", Width = 650, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, Background = System.Windows.Media.Brushes.White };
            var root = new DockPanel { Margin = new Thickness(16) };
            var info = new TextBlock { Text = "模组目录：" + modsDir + "\n把 Forge / Fabric 模组 .jar 文件放入此目录。双击列表可启用或禁用模组。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
            DockPanel.SetDock(info, Dock.Top); root.Children.Add(info);
            var buttons = new WrapPanel { Margin = new Thickness(0,12,0,0) }; DockPanel.SetDock(buttons, Dock.Bottom);
            var list = new ListBox();
            void Refresh() { list.Items.Clear(); foreach (var f in Directory.GetFiles(modsDir).Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))) list.Items.Add(Path.GetFileName(f)); }
            list.MouseDoubleClick += (_,__) => { if (list.SelectedItem is string name) { string oldPath = Path.Combine(modsDir,name); string newPath = name.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase) ? oldPath[..^9] : oldPath + ".disabled"; File.Move(oldPath,newPath,true); Refresh(); } };
            var open = new Button { Content = "打开模组文件夹", Margin = new Thickness(0,0,8,0), Padding = new Thickness(12,7,12,7) };
            open.Click += (_,__) => Process.Start(new ProcessStartInfo(modsDir) { UseShellExecute = true });
            var forge = new Button { Content = "Forge 官方下载", Margin = new Thickness(0,0,8,0), Padding = new Thickness(12,7,12,7) };
            forge.Click += (_,__) => Process.Start(new ProcessStartInfo("https://files.minecraftforge.net/") { UseShellExecute = true });
            var import = new Button { Content = "导入模组", Margin = new Thickness(0,0,8,0), Padding = new Thickness(12,7,12,7) };
            import.Click += (_,__) => { var picker = new OpenFileDialog { Filter = "Minecraft 模组 (*.jar)|*.jar", Multiselect = true }; if (picker.ShowDialog() == true) { foreach (var f in picker.FileNames) { string dest = Path.Combine(modsDir, Path.GetFileName(f)); File.Copy(f, dest, true); } Refresh(); } };
            var remove = new Button { Content = "删除选中模组", Margin = new Thickness(0,0,8,0), Padding = new Thickness(12,7,12,7) };
            remove.Click += (_,__) => { if (list.SelectedItem is string name) { File.Delete(Path.Combine(modsDir, name)); Refresh(); } };
            var fabric = new Button { Content = "Fabric 官方下载", Margin = new Thickness(0,0,8,0), Padding = new Thickness(12,7,12,7) };
            fabric.Click += (_,__) => Process.Start(new ProcessStartInfo("https://fabricmc.net/use/installer/") { UseShellExecute = true });
            buttons.Children.Add(open); buttons.Children.Add(import); buttons.Children.Add(remove); buttons.Children.Add(forge); buttons.Children.Add(fabric);
            root.Children.Add(buttons); root.Children.Add(list); window.Content = root; Refresh(); window.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show("打开模组管理失败：" + ex.Message); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window { Title = "X 启动器设置", Width = 620, Height = 560, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "启动器设置", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,16) });
        TextBox AddField(string label, string value) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0,8,0,4), Foreground = System.Windows.Media.Brushes.DimGray }); var box = new TextBox { Text = value, Padding = new Thickness(8) }; panel.Children.Add(box); return box; }
        var dir = AddField("Minecraft 游戏目录", _gameDir);
        var java = AddField("Java 可执行文件路径（留空则自动查找）", _javaPath ?? "");
        var oauth = AddField("Microsoft OAuth Client ID", OAuthClientIdText.Text);
        panel.Children.Add(new TextBlock { Text = "默认启动内存", Margin = new Thickness(0,10,0,4), Foreground = System.Windows.Media.Brushes.DimGray });
        var memory = new ComboBox { Padding = new Thickness(6), ItemsSource = new[] { "1024", "2048", "4096", "6144", "8192", "12288", "16384" }, SelectedItem = GetMemory().ToString() }; panel.Children.Add(memory);
        var auto = new CheckBox { Content = "启动器启动时自动检查已安装版本", IsChecked = true, Margin = new Thickness(0,14,0,6) }; panel.Children.Add(auto);
        var note = new TextBlock { Text = "提示：修改游戏目录后，下载和启动页面都会使用新目录。Java 路径需指向 java.exe。", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0,8,0,12) }; panel.Children.Add(note);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; var save = new Button { Content = "保存设置", Padding = new Thickness(18,9,18,9), Background = System.Windows.Media.Brushes.SteelBlue, Foreground = System.Windows.Media.Brushes.White }; var cancel = new Button { Content = "取消", Padding = new Thickness(18,9,18,9) }; buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        save.Click += (_,__) => { try { _gameDir = dir.Text.Trim(); GameDirectoryText.Text = _gameDir; _javaPath = string.IsNullOrWhiteSpace(java.Text) ? null : java.Text.Trim(); OAuthClientIdText.Text = oauth.Text.Trim(); if (memory.SelectedItem is string m) { int idx = new[] {"1024","2048","4096","6144","8192"}.ToList().IndexOf(m); if (idx >= 0) MemoryCombo.SelectedIndex = Math.Min(idx, MemoryCombo.Items.Count - 1); else MemoryCombo.SelectedIndex = 2; } Directory.CreateDirectory(_gameDir); SaveSettings(); SetStatus("设置已保存"); win.Close(); } catch(Exception ex) { MessageBox.Show("保存失败：" + ex.Message); } };
        cancel.Click += (_,__) => win.Close(); win.Content = new ScrollViewer { Content = panel }; win.ShowDialog();
    }

    private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XLauncher", "settings.json");
    private void LoadSavedSettings() { try { if (!File.Exists(SettingsPath)) return; using var d = JsonDocument.Parse(File.ReadAllText(SettingsPath)); var r=d.RootElement; if(r.TryGetProperty("gameDir",out var gd)&&Directory.Exists(gd.GetString())) { _gameDir=gd.GetString()!; GameDirectoryText.Text=_gameDir; } if(r.TryGetProperty("javaPath",out var jp)) _javaPath=jp.GetString(); if(r.TryGetProperty("oauthClientId",out var oc)) OAuthClientIdText.Text=oc.GetString()??""; if(r.TryGetProperty("memory",out var mm)&&int.TryParse(mm.GetString(),out var mem)) MemoryCombo.SelectedIndex=Math.Clamp(new[]{1024,2048,4096,6144,8192,12288,16384}.ToList().IndexOf(mem),0,4); } catch(Exception ex) { Log("读取设置失败："+ex.Message); } }
    private void SaveSettings() { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { gameDir=_gameDir, javaPath=_javaPath, oauthClientId=OAuthClientIdText.Text, memory=GetMemory().ToString() }, new JsonSerializerOptions { WriteIndented=true })); }

    private void DownloadPage_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window { Title = "X · 下载游戏", Width = 900, Height = 650, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White };
        var root = new DockPanel { Margin = new Thickness(20) }; var head = new StackPanel(); head.Children.Add(new TextBlock { Text="下载游戏版本", FontSize=24, FontWeight=FontWeights.SemiBold }); head.Children.Add(new TextBlock { Text="在这里选择要下载的 Minecraft 版本；下载完成后会出现在‘启动游戏’页面。", Foreground=System.Windows.Media.Brushes.Gray, Margin=new Thickness(0,5,0,12) }); DockPanel.SetDock(head,Dock.Top); root.Children.Add(head);
        var list = new ListView(); list.ItemsSource = _versions; list.DisplayMemberPath = "Id"; root.Children.Add(list);
        var bottom = new StackPanel { Orientation=Orientation.Horizontal, Margin=new Thickness(0,12,0,0) }; DockPanel.SetDock(bottom,Dock.Bottom); var refresh = new Button { Content="刷新官方版本列表" }; var install = new Button { Content="下载 / 安装选中版本", Background=System.Windows.Media.Brushes.SteelBlue, Foreground=System.Windows.Media.Brushes.White }; var progress = new ProgressBar { Width=220, Height=14, Margin=new Thickness(12,8,0,0), IsIndeterminate=false }; bottom.Children.Add(refresh); bottom.Children.Add(install); bottom.Children.Add(progress); root.Children.Add(bottom);
        refresh.Click += async (_,__) => { await LoadVersionsAsync(); list.ItemsSource=_versions; }; install.Click += async (_,__) => { if(list.SelectedItem is not VersionRow row) { MessageBox.Show("请先选择版本。"); return; } VersionsList.SelectedItem = _versions.FirstOrDefault(x=>x.Id==row.Id); if(VersionsList.SelectedItem==null) { MessageBox.Show("版本清单尚未加载，请先刷新。"); return; } win.Activate(); InstallSelected_Click(this, new RoutedEventArgs()); progress.Value=Progress.Value; };
        win.Content=root; win.Loaded += async (_,__) => { if(_versions.Count==0) { await LoadVersionsAsync(); list.ItemsSource=_versions; } }; win.ShowDialog();
    }

    private void LaunchPage_Click(object sender, RoutedEventArgs e)
    {
        try { _gameDir=string.IsNullOrWhiteSpace(GameDirectoryText.Text)?_gameDir:GameDirectoryText.Text.Trim(); var versionsDir=Path.Combine(_gameDir,"versions"); Directory.CreateDirectory(versionsDir); var installed=Directory.GetDirectories(versionsDir).Select(Path.GetFileName).Where(id=>!string.IsNullOrWhiteSpace(id)).Where(id=>File.Exists(Path.Combine(versionsDir,id!,id+".json")) && File.Exists(Path.Combine(versionsDir,id!,id+".jar"))).ToList(); var win=new Window { Title="X · 启动游戏", Width=700, Height=500, Owner=this, WindowStartupLocation=WindowStartupLocation.CenterOwner, Background=System.Windows.Media.Brushes.White }; var panel=new StackPanel { Margin=new Thickness(22) }; panel.Children.Add(new TextBlock { Text="启动游戏", FontSize=25, FontWeight=FontWeights.SemiBold }); panel.Children.Add(new TextBlock { Text="这里只显示已经安装完成的版本，不会混入尚未下载的版本。", Foreground=System.Windows.Media.Brushes.Gray, Margin=new Thickness(0,5,0,14) }); var list=new ListBox { Height=260, ItemsSource=installed }; panel.Children.Add(list); var go=new Button { Content="启动选中版本", Padding=new Thickness(18,10,18,10), Background=System.Windows.Media.Brushes.SteelBlue, Foreground=System.Windows.Media.Brushes.White, HorizontalAlignment=HorizontalAlignment.Left }; panel.Children.Add(go); go.Click += (_,__) => { if(list.SelectedItem is not string id) { MessageBox.Show("请先选择已安装版本。"); return; } var row=_versions.FirstOrDefault(v=>v.Id==id) ?? new VersionRow(id,"local","本地安装",""); if(!_versions.Any(v=>v.Id==id)) _versions.Add(row); VersionsList.SelectedItem=row; Launch_Click(this,new RoutedEventArgs()); }; win.Content=panel; win.ShowDialog(); } catch(Exception ex) { MessageBox.Show("读取已安装版本失败："+ex.Message); }
    }



    private void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(GameDirectoryText.Text);
            Process.Start(new ProcessStartInfo(GameDirectoryText.Text) { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("无法打开目录：" + ex.Message); }
    }

    private sealed record VersionRow(string Id, string Type, string ReleaseTime, string Url);
}
