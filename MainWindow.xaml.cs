using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace MC_Launcher_Revival;

public partial class MainWindow : Window
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    // Java 下载使用独立连接池，避免与游戏资源下载互相争抢连接。
    private static readonly HttpClient JavaHttp = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(30)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly SemaphoreSlim DownloadSlots = new(8, 8);
    private int _assetDone;
    private List<VersionRow> _versions = new();
    private string _gameDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    private readonly HashSet<string> _downloadedAssets = new(StringComparer.OrdinalIgnoreCase);
    private string? _accessToken, _uuid, _username, _userType;
    private bool _offlineMode;
    private string? _javaPath;
    private Dictionary<string, string> _versionJavaPaths = new(StringComparer.OrdinalIgnoreCase);

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
        int major = JavaVersionCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out int v) ? v : 21;
        if (MessageBox.Show($"将从 Eclipse Adoptium 官方 API 下载 Temurin Java {major}（Windows x64）。是否继续？", $"自动安装 Java {major}", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await InstallJavaRuntimeAsync(major); }
        catch (Exception ex) { SetStatus("Java 自动安装失败：" + ex.Message); Log("Java 安装错误：" + ex); MessageBox.Show(ex.Message, "Java 安装失败"); }
    }

    private async Task<string> InstallJavaRuntimeAsync(int major)
    {
        if (major < 8 || major > 99) throw new ArgumentOutOfRangeException(nameof(major));
        string javaRoot = Path.Combine(_gameDir, "runtime");
        string installDir = Path.Combine(javaRoot, $"temurin-{major}");
        Directory.CreateDirectory(javaRoot);
        string? existing = Directory.Exists(installDir) ? Directory.GetFiles(installDir, "java.exe", SearchOption.AllDirectories).FirstOrDefault(x => x.Replace('\\','/').Contains("/bin/java.exe", StringComparison.OrdinalIgnoreCase)) : null;
        if (existing != null && IsJavaCompatible(existing, major)) { _javaPath = existing; return existing; }
        SetStatus($"正在查询 Adoptium 官方 Java {major} 下载包……");
        Log($"开始下载 Eclipse Temurin Java {major}。安装目录：{installDir}");
        string api = $"https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jre/hotspot/normal/eclipse";
        string archive = Path.Combine(javaRoot, $"temurin-{major}.zip");
        await DownloadJavaArchiveAsync(api, archive, major);
        if (!File.Exists(archive) || new FileInfo(archive).Length < 1024 * 1024) throw new InvalidDataException("Java 下载包大小异常，已拒绝解压。");
        SetStatus($"Java {major} 下载完成，正在解压……");
        if (Directory.Exists(installDir)) Directory.Delete(installDir, true);
        Directory.CreateDirectory(installDir);
        ZipFile.ExtractToDirectory(archive, installDir, true);
        File.Delete(archive);
        string? javaExe = Directory.GetFiles(installDir, "java.exe", SearchOption.AllDirectories).FirstOrDefault(x => x.Replace('\\','/').Contains("/bin/java.exe", StringComparison.OrdinalIgnoreCase));
        if (javaExe == null) throw new FileNotFoundException("压缩包中没有找到 bin/java.exe。");
        if (!TryGetJavaMajor(javaExe, out int installedMajor) || installedMajor != major)
        {
            try { Directory.Delete(installDir, true); } catch { }
            throw new InvalidDataException($"Java 安装校验失败：期望 Java {major}，实际检测到 {installedMajor}。");
        }
        _javaPath = javaExe;
        SetStatus($"Java {major} 安装完成并通过版本校验：" + javaExe);
        Log($"Java {major} 安装完成：" + javaExe);
        return javaExe;
    }

    private async Task DownloadJavaArchiveAsync(string url, string archive, int major)
    {
        string partial = archive + ".part";
        Exception? lastError = null;
        const int maxAttempts = 5;
        const int bufferSize = 1024 * 1024; // 1 MiB 缓冲，减少小块 I/O 开销
        // 下载服务器支持 HTTP Range 时，尝试 4 路分段并发；不支持则自动回退到续传单流。
        if (await TryDownloadJavaInParallelAsync(url, partial, major))
        {
            File.Move(partial, archive, true);
            Log($"Java {major} 多线程下载完成，文件大小：{new FileInfo(archive).Length / 1048576d:F1} MB。");
            return;
        }
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                using var response = await JavaHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                // 只有服务器明确返回 206 才能追加；若忽略 Range 返回 200，就安全地从头覆盖。
                bool append = offset > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (offset > 0 && !append) offset = 0;
                response.EnsureSuccessStatusCode();
                long? totalBytes = response.Content.Headers.ContentRange?.Length;
                if (!totalBytes.HasValue && response.Content.Headers.ContentLength.HasValue)
                    totalBytes = response.Content.Headers.ContentLength.Value + offset;

                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var output = new FileStream(partial,
                    append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] buffer = new byte[bufferSize];
                long received = offset;
                var clock = Stopwatch.StartNew();
                long lastReport = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    received += read;
                    if (clock.ElapsedMilliseconds - lastReport >= 700)
                    {
                        double mbps = received / 1024d / 1024d / Math.Max(0.001, clock.Elapsed.TotalSeconds);
                        string sizeText = totalBytes.HasValue
                            ? $"{received / 1048576d:F1}/{totalBytes.Value / 1048576d:F1} MB"
                            : $"{received / 1048576d:F1} MB";
                        string percent = totalBytes.HasValue && totalBytes.Value > 0
                            ? $"（{Math.Min(100, received * 100d / totalBytes.Value):F0}%）" : "";
                        SetStatus($"正在下载 Java {major}：{sizeText}{percent}，速度 {mbps:F2} MB/s，第 {attempt}/{maxAttempts} 次");
                        lastReport = clock.ElapsedMilliseconds;
                    }
                }
                await output.FlushAsync(timeout.Token);
                if (totalBytes.HasValue && new FileInfo(partial).Length != totalBytes.Value)
                    throw new IOException($"下载长度不完整：已收到 {new FileInfo(partial).Length} 字节，预期 {totalBytes.Value} 字节。将尝试续传。");

                // 先完整打开 ZIP，避免损坏包进入安装目录。
                using (var zip = ZipFile.OpenRead(partial))
                {
                    if (zip.Entries.Count == 0) throw new InvalidDataException("Java 压缩包为空。");
                }
                File.Move(partial, archive, true);
                Log($"Java {major} 下载完成，文件大小：{new FileInfo(archive).Length / 1048576d:F1} MB。");
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log($"Java {major} 下载第 {attempt} 次失败：{ex.Message}");
                if (attempt < maxAttempts)
                {
                    SetStatus($"Java {major} 下载中断，保留已下载数据，{attempt}/{maxAttempts} 次失败，准备续传……");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, attempt * 2)));
                }
            }
        }
        throw new IOException($"Java {major} 下载连续失败 {maxAttempts} 次。已保留临时下载文件（如果服务器支持 Range，下次可续传）。最后错误：{lastError?.Message}", lastError);
    }

    private async Task<bool> TryDownloadJavaInParallelAsync(string url, string partial, int major)
    {
        const int workerCount = 4;
        const int bufferSize = 1024 * 1024;
        long totalLength;
        try
        {
            using var probeRequest = new HttpRequestMessage(HttpMethod.Get, url);
            probeRequest.Headers.Range = new RangeHeaderValue(0, 0);
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var probe = await JavaHttp.SendAsync(probeRequest, HttpCompletionOption.ResponseHeadersRead, probeTimeout.Token);
            if (probe.StatusCode != System.Net.HttpStatusCode.PartialContent ||
                probe.Content.Headers.ContentRange?.Length is not long detectedLength || detectedLength < 2 * 1024 * 1024)
                return false;
            totalLength = detectedLength;
            // 丢弃探测响应的单字节内容，后续按明确的区间重新下载。
            await using var probeStream = await probe.Content.ReadAsStreamAsync(probeTimeout.Token);
            await probeStream.CopyToAsync(Stream.Null, 8192, probeTimeout.Token);
        }
        catch (Exception ex)
        {
            Log($"Java {major} 服务器不支持分段探测，改用普通下载：{ex.Message}");
            return false;
        }

        long completed = 0;
        var clock = Stopwatch.StartNew();
        long segmentSize = (totalLength + workerCount - 1) / workerCount;
        string[] segmentPaths = Enumerable.Range(0, workerCount).Select(i => partial + $".seg{i}").ToArray();
        try
        {
            var tasks = Enumerable.Range(0, workerCount).Select(async index =>
            {
                long from = index * segmentSize;
                long to = Math.Min(totalLength - 1, from + segmentSize - 1);
                if (from > to) return;
                string segmentPath = segmentPaths[index];
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.Range = new RangeHeaderValue(from, to);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                        using var response = await JavaHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent ||
                            response.Content.Headers.ContentRange?.From != from ||
                            response.Content.Headers.ContentRange?.To != to)
                            throw new IOException("下载服务器未正确返回分段数据。");
                        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                        await using var output = new FileStream(segmentPath, FileMode.Create, FileAccess.Write, FileShare.None,
                            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        byte[] buffer = new byte[bufferSize];
                        long segmentReceived = 0;
                        while (true)
                        {
                            int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token);
                            if (read == 0) break;
                            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                            segmentReceived += read;
                            long nowDone = Interlocked.Add(ref completed, read);
                            double speed = nowDone / 1048576d / Math.Max(0.001, clock.Elapsed.TotalSeconds);
                            SetStatus($"Java {major} 加速下载：{nowDone / 1048576d:F1}/{totalLength / 1048576d:F1} MB（{nowDone * 100d / totalLength:F0}%），{speed:F2} MB/s，4 路并发");
                        }
                        if (segmentReceived != to - from + 1)
                            throw new IOException($"分段 {index + 1} 长度不完整。");
                        return;
                    }
                    catch
                    {
                        if (File.Exists(segmentPath)) File.Delete(segmentPath);
                        if (attempt == 4) throw;
                        await Task.Delay(500 * attempt);
                    }
                }
            });
            await Task.WhenAll(tasks);
            await using var combined = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            foreach (string segment in segmentPaths)
            {
                if (!File.Exists(segment)) continue;
                await using var input = File.OpenRead(segment);
                await input.CopyToAsync(combined, bufferSize);
            }
            await combined.FlushAsync();
            if (new FileInfo(partial).Length != totalLength)
                throw new IOException("分段合并后的文件大小不正确。");
            using (var zip = ZipFile.OpenRead(partial))
                if (zip.Entries.Count == 0) throw new InvalidDataException("Java 压缩包为空。");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Java {major} 分段加速下载不可用，切换为断点续传模式：{ex.Message}");
            if (File.Exists(partial)) File.Delete(partial);
            return false;
        }
        finally
        {
            foreach (string segment in segmentPaths)
                try { if (File.Exists(segment)) File.Delete(segment); } catch { }
        }
    }

    private static bool IsJavaCompatible(string javaExe, int requiredMajor)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(javaExe, "-version") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return false;
            string output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
            var m = System.Text.RegularExpressions.Regex.Match(output, @"version ""(?<v>\d+(?:\.\d+)*)");
            if (!m.Success) return false;
            var parts = m.Groups["v"].Value.Split('.'); int found = parts[0] == "1" && parts.Length > 1 ? int.Parse(parts[1]) : int.Parse(parts[0]);
            return found == requiredMajor;
        }
        catch { return false; }
    }

    private async void JavaManager_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window { Title = "X · Java 管理中心", Width = 1120, Height = 650, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White };
        var root = new DockPanel { Margin = new Thickness(20) };
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "Java 管理中心", FontSize = 25, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = "统一查看、添加、安装和管理 Java 运行环境。不同 Minecraft 版本可以使用不同主版本。",
            Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 5, 0, 14) });
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var list = new ListView { Margin = new Thickness(0, 0, 0, 12) };
        list.View = new System.Windows.Controls.GridView();
        var view = (System.Windows.Controls.GridView)list.View;
        view.Columns.Add(new System.Windows.Controls.GridViewColumn { Header = "Java 主版本", DisplayMemberBinding = new System.Windows.Data.Binding("Major"), Width = 105 });
        view.Columns.Add(new System.Windows.Controls.GridViewColumn { Header = "来源", DisplayMemberBinding = new System.Windows.Data.Binding("Source"), Width = 125 });
        view.Columns.Add(new System.Windows.Controls.GridViewColumn { Header = "状态", DisplayMemberBinding = new System.Windows.Data.Binding("Status"), Width = 100 });
        view.Columns.Add(new System.Windows.Controls.GridViewColumn { Header = "java.exe 路径", DisplayMemberBinding = new System.Windows.Data.Binding("Path"), Width = 440 });
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom);
        var refresh = new Button { Content = "重新扫描" };
        var add = new Button { Content = "手动添加 Java" };
        var install = new Button { Content = "安装 Java 8 / 17 / 21 / 25" };
        var use = new Button { Content = "设为启动器优先 Java" };
        var remove = new Button { Content = "删除启动器托管 Java" };
        var repair = new Button { Content = "检查 / 修复 Java" };
        var knownVersions = _versions.Select(v => v.Id).Concat(Directory.Exists(Path.Combine(_gameDir, "versions")) ? Directory.GetDirectories(Path.Combine(_gameDir, "versions")).Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x))! : Enumerable.Empty<string>()).Distinct().ToList();
        var versionPick = new ComboBox { Width = 125, Margin = new Thickness(5), ItemsSource = knownVersions, IsEditable = true };
        var assign = new Button { Content = "指定给所选游戏版本" };
        foreach (var b in new[] { refresh, add, install, use, remove, repair }) buttons.Children.Add(b);
        buttons.Children.Add(new TextBlock { Text = "游戏版本：", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,0,0,0) });
        buttons.Children.Add(versionPick); buttons.Children.Add(assign);
        root.Children.Add(buttons); root.Children.Add(list);
        List<JavaEntry> entries = new();
        void Refresh()
        {
            entries = ScanJavaEntries(); list.ItemsSource = null; list.ItemsSource = entries;
            SetStatus($"Java 管理中心：发现 {entries.Count} 个 Java 环境。");
        }
        refresh.Click += (_, __) => Refresh();
        add.Click += (_, __) =>
        {
            var picker = new OpenFileDialog { Title = "选择 Java 可执行文件", Filter = "Java 可执行文件 (java.exe)|java.exe|所有文件 (*.*)|*.*" };
            if (picker.ShowDialog(win) == true)
            {
                if (!File.Exists(picker.FileName) || !TryGetJavaMajor(picker.FileName, out int major)) { MessageBox.Show(win, "所选文件不是可识别的 Java。", "添加失败"); return; }
                _javaPath = picker.FileName; SaveSettings(); Refresh(); Log($"已将 Java {major} 设置为启动器优先环境：{_javaPath}");
            }
        };
        install.Click += async (_, __) =>
        {
            var choose = new Window { Title = "安装 Java", Width = 330, Height = 180, Owner = win, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White };
            var panel = new StackPanel { Margin = new Thickness(18) }; panel.Children.Add(new TextBlock { Text = "选择要安装的 Temurin Java 主版本：", Margin = new Thickness(0,0,0,10) });
            var combo = new ComboBox { ItemsSource = new[] { "8", "17", "21", "25" }, SelectedItem = "21", Padding = new Thickness(6) }; panel.Children.Add(combo);
            var go = new Button { Content = "开始安装", Margin = new Thickness(0,12,0,0), HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(go); choose.Content = panel;
            go.Click += async (_, ____) => { int major = int.Parse(combo.SelectedItem?.ToString() ?? "21"); go.IsEnabled = false; try { _javaPath = await InstallJavaRuntimeAsync(major); SaveSettings(); Refresh(); MessageBox.Show(choose, $"Java {major} 安装完成。", "安装成功"); choose.Close(); } catch (Exception ex) { MessageBox.Show(choose, ex.Message, "安装失败"); } finally { go.IsEnabled = true; } };
            choose.ShowDialog();
        };
        use.Click += (_, __) => { if (list.SelectedItem is not JavaEntry entry) { MessageBox.Show(win, "请先选择一个 Java 环境。"); return; } _javaPath = entry.Path; SaveSettings(); SetStatus($"已设为优先 Java：{entry.Major}"); Log("启动器优先 Java 已设置为：" + entry.Path); };
        remove.Click += (_, __) =>
        {
            if (list.SelectedItem is not JavaEntry entry) { MessageBox.Show(win, "请先选择一个 Java 环境。"); return; }
            string managedRoot = Path.GetFullPath(Path.Combine(_gameDir, "runtime")) + Path.DirectorySeparatorChar;
            string selectedPath = Path.GetFullPath(entry.Path);
            if (!selectedPath.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(win, "为避免误删电脑已有 Java，只能删除 X 启动器 runtime 目录内托管的环境。"); return; }
            if (MessageBox.Show(win, $"确定删除这个启动器托管的 Java 环境吗？\n{entry.Path}", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            string javaHome = Directory.GetParent(Path.GetDirectoryName(entry.Path)!)?.FullName ?? "";
            // Adoptium archives may contain one nested top-level directory; remove only the matching managed major folder.
            string majorFolder = Path.Combine(_gameDir, "runtime", "temurin-" + entry.Major);
            if (Directory.Exists(majorFolder)) Directory.Delete(majorFolder, true);
            if (string.Equals(_javaPath, entry.Path, StringComparison.OrdinalIgnoreCase)) _javaPath = null;
            SaveSettings(); Refresh(); Log($"已删除启动器托管 Java {entry.Major}。");
        };
        repair.Click += async (_, __) =>
        {
            repair.IsEnabled = false;
            try
            {
                foreach (int major in new[] { 8, 17, 21, 25 })
                {
                    string folder = Path.Combine(_gameDir, "runtime", $"temurin-{major}");
                    string? exe = Directory.Exists(folder) ? Directory.GetFiles(folder, "java.exe", SearchOption.AllDirectories).FirstOrDefault(x => x.Replace('\\','/').EndsWith("/bin/java.exe", StringComparison.OrdinalIgnoreCase)) : null;
                    if (exe == null || !TryGetJavaMajor(exe, out int detectedMajor) || detectedMajor != major)
                    {
                        SetStatus($"Java {major} 缺失或校验失败，正在修复……");
                        await InstallJavaRuntimeAsync(major);
                    }
                }
                SaveSettings(); Refresh(); MessageBox.Show(win, "Java 8、17、21、25 检查完成；缺失或损坏的托管环境已尝试修复。", "检查完成");
            }
            catch (Exception ex) { MessageBox.Show(win, "Java 修复失败：" + ex.Message); }
            finally { repair.IsEnabled = true; }
        };
        assign.Click += (_, __) =>
        {
            string version = versionPick.Text.Trim(); if (string.IsNullOrWhiteSpace(version)) { MessageBox.Show(win, "请先选择或输入游戏版本号。"); return; }
            if (list.SelectedItem is not JavaEntry entry || !File.Exists(entry.Path)) { MessageBox.Show(win, "请先在上方列表选择有效 Java。"); return; }
            int required = version.StartsWith("1.20.5", StringComparison.OrdinalIgnoreCase) || version.StartsWith("1.21", StringComparison.OrdinalIgnoreCase) ? 21 : version.StartsWith("1.18", StringComparison.OrdinalIgnoreCase) || version.StartsWith("1.19", StringComparison.OrdinalIgnoreCase) || version.StartsWith("1.20", StringComparison.OrdinalIgnoreCase) ? 17 : 8;
            using (var versionDoc = TryReadVersionMetadata(version))
                if (versionDoc != null && versionDoc.RootElement.TryGetProperty("javaVersion", out var jv) && jv.TryGetProperty("majorVersion", out var jm)) required = jm.GetInt32();
            if (!IsJavaCompatible(entry.Path, required)) { MessageBox.Show(win, $"该版本元数据要求 Java {required}，当前所选 Java 主版本不匹配。"); return; }
            _versionJavaPaths[version] = entry.Path; SaveSettings(); Log($"已为 Minecraft {version} 指定 Java：{entry.Path}");
            MessageBox.Show(win, $"已为 {version} 指定 Java {entry.Major}。", "设置成功");
        };
        Refresh(); win.Content = root; win.ShowDialog();
    }

    private List<JavaEntry> ScanJavaEntries()
    {
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path, string source) { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) candidates[path] = source; }
        Add(_javaPath, "启动器优先");
        string? home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home)) Add(Path.Combine(home, "bin", "java.exe"), "JAVA_HOME");
        foreach (var part in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) Add(Path.Combine(part.Trim('"'), "java.exe"), "系统 PATH");
        string runtime = Path.Combine(_gameDir, "runtime");
        if (Directory.Exists(runtime)) foreach (var file in Directory.GetFiles(runtime, "java.exe", SearchOption.AllDirectories)) Add(file, "X 托管运行时");
        foreach (var baseDir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            string adoptium = Path.Combine(baseDir, "Eclipse Adoptium");
            if (Directory.Exists(adoptium)) foreach (var file in Directory.GetFiles(adoptium, "java.exe", SearchOption.AllDirectories)) Add(file, "系统已安装");
        }
        var result = new List<JavaEntry>();
        foreach (var item in candidates)
        {
            if (TryGetJavaMajor(item.Key, out int major)) result.Add(new JavaEntry(major.ToString(), item.Value, "可用", item.Key));
            else result.Add(new JavaEntry("未知", item.Value, "无法识别", item.Key));
        }
        return result.OrderBy(x => int.TryParse(x.Major, out int n) ? n : 999).ThenBy(x => x.Source).ToList();
    }

    private static bool TryGetJavaMajor(string javaExe, out int major)
    {
        major = 0;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(javaExe, "-version") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return false;
            string output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { p.Kill(true); return false; }
            var m = System.Text.RegularExpressions.Regex.Match(output, @"version ""(?<v>\d+(?:\.\d+)*)");
            if (!m.Success) return false;
            var parts = m.Groups["v"].Value.Split('.'); major = parts[0] == "1" && parts.Length > 1 ? int.Parse(parts[1]) : int.Parse(parts[0]);
            return major > 0;
        }
        catch { return false; }
    }

    private sealed record JavaEntry(string Major, string Source, string Status, string Path);

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
            string java;
            try
            {
                if (_versionJavaPaths.TryGetValue(selected.Id, out var assigned) && !string.IsNullOrWhiteSpace(assigned))
                {
                    if (!File.Exists(assigned) || !IsJavaCompatible(assigned, javaMajor))
                        throw new FileNotFoundException($"版本 {selected.Id} 指定的 Java 不存在或版本不兼容（需要 Java {javaMajor}+）：{assigned}");
                    java = assigned;
                }
                else java = FindJava(javaMajor);
            }
            catch (FileNotFoundException)
            {
                var choice = MessageBox.Show($"Minecraft {selected.Id} 需要 Java {javaMajor}。当前没有找到对应主版本的运行环境。现在自动下载并安装 Java {javaMajor} 吗？", "缺少匹配的 Java", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice != MessageBoxResult.Yes) return;
                java = await InstallJavaRuntimeAsync(javaMajor);
                SaveSettings();
            }
            Log($"版本 {selected.Id} 使用 Java {javaMajor}：{java}");
            string natives = Path.Combine(versionDir, "natives");
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
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_javaPath) && File.Exists(_javaPath)) candidates.Add(_javaPath);
        string? home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home)) candidates.Add(Path.Combine(home, "bin", "java.exe"));
        candidates.Add("java.exe");
        // Also search launcher-managed runtimes, newest compatible Java first.
        string runtimeRoot = Path.Combine(_gameDir, "runtime");
        if (Directory.Exists(runtimeRoot)) candidates.AddRange(Directory.GetFiles(runtimeRoot, "java.exe", SearchOption.AllDirectories));
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if ((File.Exists(candidate) || candidate == "java.exe") && IsJavaCompatible(candidate, major)) { _javaPath = candidate; return candidate; }
        throw new FileNotFoundException($"此 Minecraft 版本需要 Java {major}。没有找到匹配的 Java 主版本。");
    }

    private JsonDocument? TryReadVersionMetadata(string version)
    {
        try { string path = Path.Combine(_gameDir, "versions", version, version + ".json"); return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null; }
        catch { return null; }
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
            var browse = new Button { Content = "在线搜索 / 下载模组（Modrinth）", Margin = new Thickness(0,0,8,0), Padding = new Thickness(12,7,12,7), Background = System.Windows.Media.Brushes.SteelBlue, Foreground = System.Windows.Media.Brushes.White };
            browse.Click += async (_,__) => await OpenModrinthBrowserAsync(modsDir, Refresh);
            buttons.Children.Add(open); buttons.Children.Add(import); buttons.Children.Add(remove); buttons.Children.Add(forge); buttons.Children.Add(fabric); buttons.Children.Add(browse);
            root.Children.Add(buttons); root.Children.Add(list); window.Content = root; Refresh(); window.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show("打开模组管理失败：" + ex.Message); }
    }

    private sealed record ModrinthHit(string ProjectId, string Title, string Description, string Slug, string Downloads, string IconUrl);

    private async Task OpenModrinthBrowserAsync(string modsDir, Action refreshMods)
    {
        var win = new Window { Title = "X · Modrinth 模组下载中心", Width = 980, Height = 690, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White };
        var root = new DockPanel { Margin = new Thickness(20) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "在线模组下载中心", FontSize = 24, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "通过 Modrinth 官方 API 搜索模组。请确保所选 Minecraft 版本和加载器与游戏一致。", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0,5,0,10) });
        var controls = new WrapPanel(); var query = new TextBox { Width = 300, Padding = new Thickness(8), Text = "" }; var mc = new TextBox { Width = 100, Padding = new Thickness(8), Text = "1.20.1" };
        var loader = new ComboBox { Width = 120, Padding = new Thickness(6), ItemsSource = new[] { "fabric", "forge", "neoforge", "quilt" }, SelectedIndex = 0 };
        var search = new Button { Content = "搜索模组", Background = System.Windows.Media.Brushes.SteelBlue, Foreground = System.Windows.Media.Brushes.White };
        controls.Children.Add(new TextBlock { Text = "关键词", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) }); controls.Children.Add(query);
        controls.Children.Add(new TextBlock { Text = "Minecraft", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,4,4,4) }); controls.Children.Add(mc);
        controls.Children.Add(new TextBlock { Text = "加载器", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,4,4,4) }); controls.Children.Add(loader); controls.Children.Add(search);
        top.Children.Add(controls); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var results = new ListView { Margin = new Thickness(0,12,0,12) };
        var grid = new GridView(); grid.Columns.Add(new GridViewColumn { Header = "模组名称", DisplayMemberBinding = new System.Windows.Data.Binding("Title"), Width = 190 });
        grid.Columns.Add(new GridViewColumn { Header = "简介", DisplayMemberBinding = new System.Windows.Data.Binding("Description"), Width = 490 });
        grid.Columns.Add(new GridViewColumn { Header = "下载量", DisplayMemberBinding = new System.Windows.Data.Binding("Downloads"), Width = 100 }); results.View = grid; root.Children.Add(results);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(bottom, Dock.Bottom);
        var install = new Button { Content = "下载并安装选中模组", Background = System.Windows.Media.Brushes.SeaGreen, Foreground = System.Windows.Media.Brushes.White };
        var openProject = new Button { Content = "打开项目网页" }; var status = new TextBlock { Text = "搜索结果将显示在这里。", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10,0,0,0) };
        var progress = new ProgressBar { Width = 170, Height = 14, Margin = new Thickness(8,8,0,0) }; bottom.Children.Add(install); bottom.Children.Add(openProject); bottom.Children.Add(progress); bottom.Children.Add(status); root.Children.Add(bottom);
        List<ModrinthHit> hits = new();
        search.Click += async (_,__) =>
        {
            if (string.IsNullOrWhiteSpace(query.Text)) { MessageBox.Show(win, "请输入模组名称或关键词。"); return; }
            search.IsEnabled = false; status.Text = "正在搜索 Modrinth……";
            try
            {
                string url = "https://api.modrinth.com/v2/search?query=" + Uri.EscapeDataString(query.Text.Trim()) + "&facets=" + Uri.EscapeDataString("[[\"project_type:mod\"]]") + "&limit=30";
                using var req = new HttpRequestMessage(HttpMethod.Get, url); req.Headers.UserAgent.ParseAdd("XLauncher/3.4 (https://github.com/ppgg6/MC-Launcher-Revival)");
                using var resp = await Http.SendAsync(req); resp.EnsureSuccessStatusCode(); using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                hits = doc.RootElement.GetProperty("hits").EnumerateArray().Select(x => new ModrinthHit(x.GetProperty("project_id").GetString() ?? "", x.GetProperty("title").GetString() ?? "", x.GetProperty("description").GetString() ?? "", x.GetProperty("slug").GetString() ?? "", x.GetProperty("downloads").ToString(), x.TryGetProperty("icon_url", out var icon) ? icon.GetString() ?? "" : "")).ToList();
                results.ItemsSource = hits; status.Text = $"找到 {hits.Count} 个项目。安装时会再次核对版本和加载器。";
            }
            catch (Exception ex) { status.Text = "搜索失败：" + ex.Message; }
            finally { search.IsEnabled = true; }
        };
        openProject.Click += (_,__) => { if (results.SelectedItem is ModrinthHit hit) Process.Start(new ProcessStartInfo("https://modrinth.com/mod/" + Uri.EscapeDataString(hit.Slug)) { UseShellExecute = true }); };
        install.Click += async (_,__) =>
        {
            if (results.SelectedItem is not ModrinthHit hit) { MessageBox.Show(win, "请先选择一个模组。"); return; }
            if (string.IsNullOrWhiteSpace(mc.Text) || loader.SelectedItem is not string loaderName) { MessageBox.Show(win, "请填写 Minecraft 版本并选择加载器。"); return; }
            install.IsEnabled = false; progress.IsIndeterminate = true; status.Text = "正在查找兼容文件……";
            try
            {
                string versionsUrl = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(hit.ProjectId)}/version?game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { mc.Text.Trim() }))}&loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loaderName }))}";
                using var req = new HttpRequestMessage(HttpMethod.Get, versionsUrl); req.Headers.UserAgent.ParseAdd("XLauncher/3.4 (https://github.com/ppgg6/MC-Launcher-Revival)");
                using var resp = await Http.SendAsync(req); resp.EnsureSuccessStatusCode(); using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var version = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (version.ValueKind != JsonValueKind.Object) throw new Exception($"没有找到适用于 Minecraft {mc.Text} + {loaderName} 的发布文件。请确认版本和加载器。 ");
                var file = version.GetProperty("files").EnumerateArray().FirstOrDefault(f => f.TryGetProperty("primary", out var primary) && primary.GetBoolean());
                if (file.ValueKind != JsonValueKind.Object) file = version.GetProperty("files")[0];
                string fileName = Path.GetFileName(file.GetProperty("filename").GetString() ?? "mod.jar"); string downloadUrl = file.GetProperty("url").GetString() ?? throw new Exception("模组文件缺少下载地址。");
                if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new Exception("下载文件不是 JAR 模组，已取消安装。");
                Directory.CreateDirectory(modsDir); string target = Path.Combine(modsDir, fileName);
                if (File.Exists(target) && MessageBox.Show(win, $"{fileName} 已存在。是否覆盖？", "文件已存在", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                await DownloadAndVerifyAsync(downloadUrl, target, file.TryGetProperty("hashes", out var hashes) && hashes.TryGetProperty("sha1", out var sha) ? sha.GetString() : null, file.TryGetProperty("size", out var size) ? size.GetInt64() : null);
                refreshMods(); status.Text = "安装完成：" + fileName;
                Log($"从 Modrinth 安装模组：{hit.Title} / {fileName}（MC {mc.Text}, {loaderName}）");
                MessageBox.Show(win, $"模组已下载到 mods 文件夹：\n{fileName}\n\n如果它依赖其他模组，请按项目页面说明安装依赖。", "安装完成");
            }
            catch (Exception ex) { status.Text = "安装失败：" + ex.Message; MessageBox.Show(win, ex.Message, "模组下载失败"); }
            finally { progress.IsIndeterminate = false; install.IsEnabled = true; }
        };
        win.Content = root; win.ShowDialog();
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
    private void LoadSavedSettings() { try { if (!File.Exists(SettingsPath)) return; using var d = JsonDocument.Parse(File.ReadAllText(SettingsPath)); var r=d.RootElement; if(r.TryGetProperty("gameDir",out var gd)&&Directory.Exists(gd.GetString())) { _gameDir=gd.GetString()!; GameDirectoryText.Text=_gameDir; } if(r.TryGetProperty("javaPath",out var jp)) _javaPath=jp.GetString(); if(r.TryGetProperty("oauthClientId",out var oc)) OAuthClientIdText.Text=oc.GetString()??""; if(r.TryGetProperty("memory",out var mm)&&int.TryParse(mm.GetString(),out var mem)) MemoryCombo.SelectedIndex=Math.Clamp(new[]{1024,2048,4096,6144,8192,12288,16384}.ToList().IndexOf(mem),0,4); if(r.TryGetProperty("versionJavaPaths",out var mappings)&&mappings.ValueKind==JsonValueKind.Object) _versionJavaPaths=mappings.EnumerateObject().Where(x=>x.Value.ValueKind==JsonValueKind.String).ToDictionary(x=>x.Name,x=>x.Value.GetString()!,StringComparer.OrdinalIgnoreCase); } catch(Exception ex) { Log("读取设置失败："+ex.Message); } }
    private void SaveSettings() { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { gameDir=_gameDir, javaPath=_javaPath, oauthClientId=OAuthClientIdText.Text, memory=GetMemory().ToString(), versionJavaPaths=_versionJavaPaths }, new JsonSerializerOptions { WriteIndented=true })); }

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
