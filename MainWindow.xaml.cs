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
using System.Windows.Media;

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
    // 按 CPU 核数自适应：老机器少开一点，避免下载 + SHA-1 把机器压死；新机器最多 8 路。
    private static readonly int MaxParallel = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    private static readonly SemaphoreSlim DownloadSlots = new(MaxParallel, MaxParallel);
    private static readonly long TotalRamBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    private List<VersionRow> _versions = new();
    private static readonly int[] MemoryOptions = { 1024, 2048, 4096, 6144, 8192, 12288, 16384 };
    private bool _autoCheckInstalled = true;
    private string _gameDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    private string? _accessToken, _uuid, _username, _userType;
    private string? _refreshToken;
    private bool _offlineMode;
    private string? _javaPath;
    private Dictionary<string, string> _versionJavaPaths = new(StringComparer.OrdinalIgnoreCase);
    // 版本隔离：勾上的版本用自己的 versions/<id>/ 当游戏目录（存档、模组、配置各自独立）。
    private readonly HashSet<string> _isolatedVersions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每个实例自己的内存、Java、备注（没设就用全局的）。</summary>
    private sealed class InstanceProfile
    {
        public int? Memory { get; set; }
        public string? JavaPath { get; set; }
        public string? Note { get; set; }
    }
    private Dictionary<string, InstanceProfile> _instances = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        StartUiPump();
        GameDirectoryText.Text = _gameDir;
        // 只在这里订阅一次；以前写在 LoadVersionsAsync 里，每刷新一次清单就多挂一个回调。
        VersionsList.SelectionChanged += (_, __) => { UpdateLaunchAvailability(); SyncIsolationCheck(); };
        IsolatedCheck.Checked += (_, __) => SetIsolation(true);
        IsolatedCheck.Unchecked += (_, __) => SetIsolation(false);
        SyncIsolationCheck();
        Log("X 启动器初始化：独立下载页、启动页、设置页与模组管理。");
        LoadSavedSettings();
        if (!File.Exists(SettingsPath)) ApplyRecommendedMemory();
        // Java 自动安装开关改动后立即存盘，下次打开还是上次的选择。
        AutoJavaCheck.Checked += (_, __) => { try { SaveSettings(); } catch { } };
        AutoJavaCheck.Unchecked += (_, __) => { try { SaveSettings(); } catch { } };
        CheckInstalledVersions();
        _ = RestoreAccountAsync();
        Log($"性能适配：{Environment.ProcessorCount} 核 CPU，物理内存约 {Math.Max(1, TotalRamBytes / 1024 / 1024 / 1024)} GB，下载并发 {MaxParallel}，本次分配 {GetMemory()} MB。");
        Log("启动器已初始化。请先选择账户，再安装并启动游戏。");
    }

    // 日志与状态先入队，由定时器合并刷新：老机器上频繁同步 Invoke 会让下载线程排队等 UI。
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLog = new();
    private string? _pendingStatus;
    private int _logLines;

    private void Log(string text)
    {
        App.WriteLogLine(text);
        _pendingLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] {text}");
        _recentLogs.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
        if (_recentLogs.Count > 200) _recentLogs.RemoveAt(0);
    }

    private void StartUiPump()
    {
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        timer.Tick += (_, __) => FlushUi();
        timer.Start();
    }

    private void FlushUi()
    {
        var sb = new System.Text.StringBuilder();
        while (sb.Length < 6000 && _pendingLog.TryDequeue(out var line)) sb.Append(line).Append(Environment.NewLine);
        if (sb.Length > 0)
        {
            string chunk = sb.ToString();
            LogText.AppendText(chunk);
            _logLines += chunk.Count(c => c == '\n');
            // 日志框无限增长会让老机器越来越卡：超过 1200 行就只留最后 600 行。
            if (_logLines > 1200)
            {
                var lines = LogText.Text.Split('\n');
                var tail = lines.Skip(Math.Max(0, lines.Length - 600)).ToArray();
                LogText.Text = string.Join("\n", tail);
                _logLines = tail.Length;
            }
            LogText.ScrollToEnd();
        }
        if (_pendingStatus is string status && !string.Equals(StatusText.Text, status, StringComparison.Ordinal))
            StatusText.Text = status;
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
            SetStatus($"成功获取 {_versions.Count} 个官方版本。");
            Log($"版本清单已加载：{_versions.Count} 个版本。");
        }
        catch (Exception ex) { SetStatus("获取版本列表失败：" + ex.Message); Log("清单失败：" + ex); }
    }

    private void SetStatus(string text) => _pendingStatus = "状态：" + text;

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

    /// <summary>探测 Adoptium 是否提供该包；网络异常时返回 true，交给正式下载流程去重试和报错。</summary>
    private async Task<bool> AdoptiumHasPackageAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        }
        catch { return true; }
    }

    // 同一时间只允许一个 Java 安装流程，避免两次点击抢同一个 .zip.part。
    private static readonly SemaphoreSlim JavaInstallGate = new(1, 1);

    /// <summary>删文件；被占用时只记日志，不往上抛（典型场景：杀毒软件或另一条下载线程还开着句柄）。</summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            try { System.Diagnostics.Debug.WriteLine($"删除失败 {path}: {ex.Message}"); } catch { }
        }
    }

    private async Task<string> InstallJavaRuntimeAsync(int major)
    {
        // 不排队也不硬闯：已有安装在进行时直接给一句明确提示，比让用户干等或报文件占用好。
        if (!await JavaInstallGate.WaitAsync(0))
            throw new InvalidOperationException("Java 正在安装中（可能是上一次点击触发的），请等这一次完成后再试。");
        try { return await InstallJavaRuntimeCoreAsync(major); }
        finally { JavaInstallGate.Release(); }
    }

    private async Task<string> InstallJavaRuntimeCoreAsync(int major)
    {
        if (major < 8 || major > 99) throw new ArgumentOutOfRangeException(nameof(major));
        string javaRoot = Path.Combine(_gameDir, "runtime");
        string installDir = Path.Combine(javaRoot, $"temurin-{major}");
        Directory.CreateDirectory(javaRoot);
        string? existing = Directory.Exists(installDir) ? Directory.GetFiles(installDir, "java.exe", SearchOption.AllDirectories).FirstOrDefault(x => x.Replace('\\','/').Contains("/bin/java.exe", StringComparison.OrdinalIgnoreCase)) : null;
        if (existing != null && IsJavaCompatible(existing, major)) { _javaPath = existing; return existing; }
        SetStatus($"正在查询 Adoptium 官方 Java {major} 下载包……");
        Log($"开始下载 Eclipse Temurin Java {major}。安装目录：{installDir}");
        // 部分主版本（例如 25）Adoptium 可能只提供 JDK 而没有 JRE，先探测再决定用哪个包。
        string api = $"https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jre/hotspot/normal/eclipse";
        if (!await AdoptiumHasPackageAsync(api))
        {
            api = $"https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jdk/hotspot/normal/eclipse";
            Log($"Adoptium 没有 Java {major} 的 JRE 包，改用 JDK 包。");
        }
        string archive = Path.Combine(javaRoot, $"temurin-{major}.zip");
        await DownloadJavaArchiveAsync(api, archive, major);
        if (!File.Exists(archive) || new FileInfo(archive).Length < 1024 * 1024) throw new InvalidDataException("Java 下载包大小异常，已拒绝解压。");
        SetStatus($"Java {major} 下载完成，正在解压……");
        if (Directory.Exists(installDir)) Directory.Delete(installDir, true);
        Directory.CreateDirectory(installDir);
        ZipFile.ExtractToDirectory(archive, installDir, true);
        TryDeleteFile(archive);
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
                // HTTP 416 表示请求的 Range 超出服务器文件长度：旧 .part 不能继续追加。
                if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    long? serverTotal = TryReadRangeTotal(response);
                    // 本地 .part 已经等于服务器文件总长度，说明上次其实已经下完（例如写完文件但没来得及改名），直接复用。
                    if (serverTotal.HasValue && File.Exists(partial) && new FileInfo(partial).Length == serverTotal.Value && TryFinalizeArchive(partial, archive, major))
                        return;
                    TryDeleteFile(partial);
                    throw new IOException("服务器返回 HTTP 416，已删除无效续传文件；下一次将从头下载。");
                }
                bool append = offset > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                // 注意：ContentRange 缺失时不能直接比较（null != offset 恒为 true，会误删一个完好的续传文件）。
                if (append && response.Content.Headers.ContentRange is { From: long rangeFrom } && rangeFrom != offset)
                {
                    TryDeleteFile(partial);
                    throw new IOException("服务器返回的续传起始位置与本地文件长度不一致，已重置下载。");
                }
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
                if (!TryFinalizeArchive(partial, archive, major)) throw new InvalidDataException("Java 下载包校验失败（压缩包为空或无法改名）。");
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

    /// <summary>读取 416 响应里的文件总长度（形如 Content-Range: bytes */1234）。</summary>
    private static long? TryReadRangeTotal(HttpResponseMessage response)
    {
        var range = response.Content.Headers.ContentRange;
        if (range?.HasLength == true) return range.Length;
        if (!response.Content.Headers.TryGetValues("Content-Range", out var values)) return null;
        string? raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        int slash = raw.LastIndexOf('/');
        return slash > 0 && long.TryParse(raw[(slash + 1)..].Trim(), out long total) ? total : null;
    }

    /// <summary>校验下载完成的 ZIP 并将其改名为最终文件；失败返回 false，不抛异常。</summary>
    private bool TryFinalizeArchive(string partial, string archive, int major)
    {
        try
        {
            using (var zip = ZipFile.OpenRead(partial))
            {
                if (zip.Entries.Count == 0) return false;
            }
            File.Move(partial, archive, true);
            Log($"Java {major} 下载完成，文件大小：{new FileInfo(archive).Length / 1048576d:F1} MB。");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Java {major} 下载包校验失败：{ex.Message}");
            return false;
        }
    }

    private async Task<bool> TryDownloadJavaInParallelAsync(string url, string partial, int major)
    {
        const int workerCount = 4;
        const int bufferSize = 1024 * 1024;
        long totalLength;
        // 不复用上一次崩溃留下的分段文件，避免把不同下载批次的数据拼在一起。
        for (int i = 0; i < workerCount; i++)
            try { string stale = partial + $".seg{i}"; if (File.Exists(stale)) File.Delete(stale); } catch { }
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
                        TryDeleteFile(segmentPath);
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
            // 这里的 partial 是多段合并到一半的产物，不能当作可续传的前缀，必须丢掉后从头下。
            TryDeleteFile(partial);
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
            int required = GuessJavaMajor(version);
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
        // 注册表里登记的 JDK/JRE（64/32 位都要看）
        foreach (var candidate in RegistryJavaPaths()) Add(candidate, "注册表");
        foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Eclipse Foundation", "Zulu", "Microsoft", "BellSoft", "Amazon Corretto", "Semeru" })
            foreach (var baseDir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                string root = Path.Combine(baseDir, vendor);
                if (!Directory.Exists(root)) continue;
                try { foreach (var file in Directory.GetFiles(root, "java.exe", SearchOption.AllDirectories)) Add(file, "系统已安装"); } catch { }
            }

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

    /// <summary>读注册表里登记的 Java 路径（HKEY_LOCAL_MACHINE\SOFTWARE\JavaSoft\...）。</summary>
    private static IEnumerable<string> RegistryJavaPaths()
    {
        var list = new List<string>();
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            foreach (var sub in new[] { @"SOFTWARE\JavaSoft\Java Runtime Environment", @"SOFTWARE\JavaSoft\Java Development Kit" })
            {
                try
                {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey(sub);
                    if (key == null) continue;
                    foreach (var version in key.GetSubKeyNames())
                    {
                        using var vk = key.OpenSubKey(version);
                        if (vk?.GetValue("JavaHome") is string home && !string.IsNullOrWhiteSpace(home))
                            list.Add(Path.Combine(home, "bin", "java.exe"));
                    }
                }
                catch { }
            }
        }
        return list;
    }

    private sealed record JavaEntry(string Major, string Source, string Status, string Path);

    private void ChooseDirectory_Click(object sender, RoutedEventArgs e)
    {
        // .NET 8 起 WPF 自带 OpenFolderDialog，不再需要用 SaveFileDialog 变通。
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择 Minecraft 游戏目录", Multiselect = false };
        if (Directory.Exists(_gameDir)) dialog.InitialDirectory = _gameDir;
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            _gameDir = dialog.FolderName;
            GameDirectoryText.Text = _gameDir;
            SaveSettings();
            Log("游戏目录已切换：" + _gameDir);
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
            Progress.Value = 92;
            if (AutoJavaCheck.IsChecked == true) await EnsureJavaForVersionAsync(meta, selected.Id);
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

    /// <summary>安装游戏后，顺带准备该版本元数据要求的 Java，并把结果绑定到版本上。</summary>
    private async Task EnsureJavaForVersionAsync(JsonElement meta, string version)
    {
        int javaMajor = meta.TryGetProperty("javaVersion", out var jv) && jv.TryGetProperty("majorVersion", out var jm)
            ? jm.GetInt32()
            : GuessJavaMajor(version);
        if (_versionJavaPaths.TryGetValue(version, out var bound) && File.Exists(bound) && IsJavaCompatible(bound, javaMajor))
        {
            Log($"版本 {version} 已有可用的 Java {javaMajor}：{bound}");
            return;
        }
        try
        {
            SetStatus($"正在准备 Minecraft {version} 需要的 Java {javaMajor}……");
            string javaPath = await InstallJavaRuntimeAsync(javaMajor);
            _versionJavaPaths[version] = javaPath;
            SaveSettings();
            SetStatus($"Java {javaMajor} 已就绪，并绑定到版本 {version}。");
            Log($"已为版本 {version} 绑定 Java {javaMajor}：{javaPath}");
        }
        catch (Exception ex)
        {
            // Java 装不上不影响已经下载好的游戏文件，提示用户并继续。
            SetStatus($"游戏已安装，但 Java {javaMajor} 自动安装失败：{ex.Message}");
            Log("Java 自动安装失败（游戏文件已保留）：" + ex);
        }
    }

    /// <summary>判断版本能否启动：自己的 jar，或继承链根版本（原版）的 jar 存在。</summary>
    private void InstanceSettings_Click(object sender, RoutedEventArgs e)
    {
        string id = SelectedVersionId();
        if (string.IsNullOrWhiteSpace(id)) { MessageBox.Show("先选一个版本。", "需要选择版本"); return; }
        _instances.TryGetValue(id, out var current);
        current ??= new InstanceProfile();

        var win = new Window
        {
            Title = "实例设置 · " + id, Width = 480, Height = 380, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "实例设置", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = id, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 12) });

        panel.Children.Add(new TextBlock { Text = "这个实例的内存（MB）", Foreground = Brushes.Gray, FontSize = 12 });
        var memBox = new ComboBox { ItemsSource = new[] { "1024", "2048", "4096", "6144", "8192", "12288", "16384" }, Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(6) };
        memBox.SelectedItem = (current.Memory ?? 0) > 0 ? current.Memory.ToString() : "4096";
        panel.Children.Add(memBox);

        panel.Children.Add(new TextBlock { Text = "这个实例专用的 Java（留空用默认）", Foreground = Brushes.Gray, FontSize = 12 });
        var javaBox = new TextBox { Text = current.JavaPath ?? "", Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(javaBox);
        var pick = new Button { Content = "选择 java.exe", HorizontalAlignment = HorizontalAlignment.Left };
        pick.Click += (_, __) =>
        {
            var dlg = new OpenFileDialog { Filter = "java.exe|java.exe|所有文件|*.*" };
            if (dlg.ShowDialog() == true) javaBox.Text = dlg.FileName;
        };
        panel.Children.Add(pick);

        panel.Children.Add(new TextBlock { Text = "备注", Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 12, 0, 0) });
        var noteBox = new TextBox { Text = current.Note ?? "", Margin = new Thickness(0, 4, 0, 12) };
        panel.Children.Add(noteBox);

        var save = new Button { Content = "保存", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(20, 8, 20, 8) };
        save.Click += (_, __) =>
        {
            var profile = new InstanceProfile
            {
                Memory = int.TryParse(memBox.SelectedItem?.ToString(), out int mb) ? mb : null,
                JavaPath = string.IsNullOrWhiteSpace(javaBox.Text) ? null : javaBox.Text.Trim(),
                Note = string.IsNullOrWhiteSpace(noteBox.Text) ? null : noteBox.Text.Trim()
            };
            _instances[id] = profile;
            try { SaveSettings(); } catch { }
            Log($"实例设置已保存：{id}（内存 {profile.Memory?.ToString() ?? "默认"}）");
            win.Close();
        };
        panel.Children.Add(save);
        win.Content = panel;
        win.ShowDialog();
    }

    private bool VersionLooksInstalled(string id)
    {
        try
        {
            string dir = Path.Combine(_gameDir, "versions", id);
            if (!File.Exists(Path.Combine(dir, id + ".json"))) return false;
            string root = ResolveRootVersion(id);
            return File.Exists(Path.Combine(_gameDir, "versions", root, root + ".jar"));
        }
        catch { return false; }
    }

    private async void InstallFabric_Click(object sender, RoutedEventArgs e)
    {
        string mc = (VersionsList.SelectedItem as VersionRow)?.Id ?? "";
        if (string.IsNullOrWhiteSpace(mc))
        {
            MessageBox.Show("请先在版本列表里选中一个 Minecraft 版本，再安装 Fabric。", "需要选择版本");
            return;
        }
        if (MessageBox.Show($"将为 Minecraft {mc} 安装 Fabric 加载器（会自动下载 Fabric 的库文件）。继续吗？",
                "安装 Fabric", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await InstallFabricAsync(mc); }
        catch (Exception ex)
        {
            SetStatus("Fabric 安装失败：" + ex.Message);
            Log("Fabric 安装失败：" + ex);
            MessageBox.Show(ex.Message, "安装失败");
        }
    }

    /// <summary>安装 Fabric：拉取加载器版本与 profile，落盘版本 JSON 并下载它新增的库。</summary>
    private async Task InstallFabricAsync(string mcVersion)
    {
        SetStatus($"正在查询 Minecraft {mcVersion} 可用的 Fabric 加载器……");
        using var listResp = await Http.GetAsync($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}");
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        if (listDoc.RootElement.ValueKind != JsonValueKind.Array || listDoc.RootElement.GetArrayLength() == 0)
            throw new InvalidDataException($"Minecraft {mcVersion} 没有可用的 Fabric 加载器（版本号可能写错，或还没有支持）。");
        string loader = listDoc.RootElement[0].GetProperty("loader").GetProperty("version").GetString()
            ?? throw new InvalidDataException("Fabric 返回的加载器版本为空。");

        SetStatus($"正在获取 Fabric {loader} 的版本配置……");
        using var profResp = await Http.GetAsync(
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loader)}/profile/json");
        profResp.EnsureSuccessStatusCode();
        string profileJson = await profResp.Content.ReadAsStringAsync();
        using var profDoc = JsonDocument.Parse(profileJson);
        var profile = profDoc.RootElement;

        string id = profile.TryGetProperty("id", out var idEl) && idEl.GetString() is string pid && !string.IsNullOrWhiteSpace(pid)
            ? pid : $"fabric-loader-{loader}-{mcVersion}";
        string versionDir = Path.Combine(_gameDir, "versions", id);
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, id + ".json"), profileJson);
        Log($"已写入 Fabric 版本配置：{id}");

        SetStatus($"正在下载 Fabric {loader} 的库文件……");
        int count = await DownloadMavenLibrariesAsync(profile);
        Log($"Fabric 库文件处理完成：{count} 个。");

        if (AutoJavaCheck.IsChecked == true) await EnsureJavaForVersionAsync(MergeVersionChain(id), id);

        Progress.Value = 100;
        SetStatus($"Fabric {loader} 已安装到 Minecraft {mcVersion}，版本名 {id}。");
        if (!_versions.Any(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            _versions.Insert(0, new VersionRow(id, "fabric", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ""));
            VersionsList.ItemsSource = null;
            VersionsList.ItemsSource = _versions;
        }
        VersionsList.SelectedItem = _versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
        UpdateLaunchAvailability();
        MessageBox.Show($"Fabric 安装完成。\n\n版本：{id}\n\n现在可以把 Fabric 模组放进 {Path.Combine(_gameDir, "mods")} 再启动。", "安装完成");
    }

    /// <summary>删除选中版本：连它自己的目录（存档/模组/配置）一起删，加载器版本要确认不误删原版。</summary>
    private void DeleteVersion_Click(object sender, RoutedEventArgs e)
    {
        string id = SelectedVersionId();
        if (string.IsNullOrWhiteSpace(id)) { MessageBox.Show("请先在版本列表里选中一个版本。", "需要选择版本"); return; }
        string dir = Path.Combine(_gameDir, "versions", id);
        if (!Directory.Exists(dir)) { MessageBox.Show("这个版本的目录不存在，可能已经被删掉了。", "找不到版本"); return; }

        // 有加载器版本依赖它时，删了会让那些版本起不来。
        var dependents = Directory.Exists(Path.Combine(_gameDir, "versions"))
            ? Directory.GetDirectories(Path.Combine(_gameDir, "versions"))
                .Select(d => Path.GetFileName(d))
                .Where(other => !string.Equals(other, id, StringComparison.OrdinalIgnoreCase))
                .Where(other => { try { return string.Equals(ResolveRootVersion(other), id, StringComparison.OrdinalIgnoreCase) && !string.Equals(other, id, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
                .ToList()
            : new List<string>();

        string extra = "";
        if (_isolatedVersions.Contains(id)) extra += "\n\n⚠ 这个版本开了隔离：它的存档、模组、配置都在这个目录里，会一起消失。";
        if (dependents.Count > 0) extra += $"\n\n⚠ 这些加载器版本依赖它，删掉后将无法启动：\n{string.Join("\n", dependents.Take(5))}";

        if (MessageBox.Show($"确定删除版本 {id} 吗？\n\n目录：{dir}{extra}", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            Directory.Delete(dir, true);
            _isolatedVersions.Remove(id);
            _versionJavaPaths.Remove(id);
            try { SaveSettings(); } catch { }
            _versions.RemoveAll(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
            VersionsList.ItemsSource = null;
            VersionsList.ItemsSource = _versions;
            SyncIsolationCheck();
            UpdateLaunchAvailability();
            Log($"已删除版本：{id}");
            SetStatus($"已删除版本 {id}。");
        }
        catch (Exception ex)
        {
            MessageBox.Show("删除失败：" + ex.Message + "\n\n如果游戏正在运行，请先退出游戏。", "删除失败");
            Log("删除版本失败：" + ex);
        }
    }

    private async void InstallNeoForge_Click(object sender, RoutedEventArgs e)
    {
        string mc = (VersionsList.SelectedItem as VersionRow)?.Id ?? "";
        if (string.IsNullOrWhiteSpace(mc)) { MessageBox.Show("请先在版本列表里选中一个 Minecraft 版本。", "需要选择版本"); return; }
        if (MessageBox.Show($"将为 Minecraft {mc} 安装 NeoForge（会下载官方安装器并运行，可能需要几分钟）。继续吗？",
                "安装 NeoForge", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await InstallNeoForgeAsync(mc); }
        catch (Exception ex)
        {
            SetStatus("NeoForge 安装失败：" + ex.Message);
            Log("NeoForge 安装失败：" + ex);
            MessageBox.Show(ex.Message, "安装失败");
        }
    }

    /// <summary>NeoForge 版本号形如 21.1.77，对应 Minecraft 1.21.1；安装同样走官方 installer。</summary>
    private async Task InstallNeoForgeAsync(string mcVersion)
    {
        SetStatus("正在查询 NeoForge 版本……");
        string xml;
        using (var resp = await Http.GetAsync("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml"))
        {
            resp.EnsureSuccessStatusCode();
            xml = await resp.Content.ReadAsStringAsync();
        }
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var all = doc.Descendants("version").Select(v => v.Value.Trim()).Where(v => v.Length > 0).ToList();
        string prefix = mcVersion.StartsWith("1.", StringComparison.Ordinal) ? mcVersion.Substring(2) + "." : "";
        string? picked = all.Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                            .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();
        if (picked is null)
            throw new InvalidDataException($"NeoForge 没有匹配 Minecraft {mcVersion} 的版本（它按 21.1.x 对应 1.21.1 这种规则编号）。");

        string fileName = $"neoforge-{picked}-installer.jar";
        string url = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{picked}/{fileName}";
        string installer = Path.Combine(_gameDir, "installers", fileName);
        SetStatus($"正在下载 NeoForge 安装器 {picked}……");
        await DownloadAndVerifyAsync(url, installer, null, null);

        int javaMajor = GuessJavaMajor(mcVersion);
        string java;
        try { java = FindJava(javaMajor); }
        catch (FileNotFoundException)
        {
            var choice = MessageBox.Show($"运行 NeoForge 安装器需要 Java {javaMajor}，现在下载吗？", "缺少 Java", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes) return;
            java = await InstallJavaRuntimeAsync(javaMajor);
        }

        SetStatus("正在运行 NeoForge 安装器（可能要好几分钟）……");
        var psi = new ProcessStartInfo(java) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = _gameDir };
        psi.ArgumentList.Add("-jar"); psi.ArgumentList.Add(installer); psi.ArgumentList.Add("--installClient"); psi.ArgumentList.Add(_gameDir);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("NeoForge 安装器没有启动。");
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = (await outTask) + "\n" + (await errTask);
        if (process.ExitCode != 0) throw new InvalidOperationException($"NeoForge 安装器返回 {process.ExitCode}。最后输出：\n{TailText(output, 500)}");
        Log("NeoForge 安装器完成：" + TailText(output, 160));

        string versionsDir = Path.Combine(_gameDir, "versions");
        string? installed = Directory.GetDirectories(versionsDir)
            .Where(d => Path.GetFileName(d).Contains("neoforge", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Directory.GetLastWriteTime).FirstOrDefault();
        if (installed == null) throw new InvalidDataException("安装器跑完了，但 versions 里没有 NeoForge 版本。");
        string id = Path.GetFileName(installed);
        if (!_versions.Any(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            _versions.Insert(0, new VersionRow(id, "neoforge", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ""));
            VersionsList.ItemsSource = null; VersionsList.ItemsSource = _versions;
        }
        VersionsList.SelectedItem = _versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
        UpdateLaunchAvailability();
        SetStatus($"NeoForge {picked} 已安装（版本 {id}）。");
        MessageBox.Show($"NeoForge 安装完成。\n\n版本：{id}", "安装完成");
    }

    private async void ImportModpack_Click(object sender, RoutedEventArgs e)
    {
        string id = SelectedVersionId();
        if (string.IsNullOrWhiteSpace(id)) { MessageBox.Show("先选一个版本：整合包会装进这个版本的游戏目录。", "需要选择版本"); return; }
        var picker = new OpenFileDialog { Title = "选择 Modrinth 整合包", Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack|所有文件 (*.*)|*.*" };
        if (picker.ShowDialog() != true) return;
        string gameDir = GameDirForVersion(id);
        if (MessageBox.Show($"将把整合包导入到：\n{gameDir}\n\n已有的同名文件会被覆盖。继续吗？",
                "导入整合包", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await ImportModpackAsync(picker.FileName, gameDir, id); }
        catch (Exception ex)
        {
            SetStatus("整合包导入失败：" + ex.Message);
            Log("整合包导入失败：" + ex);
            MessageBox.Show(ex.Message, "导入失败");
        }
    }

    /// <summary>导入 .mrpack：读 modrinth.index.json，下载 files、解压 overrides 到游戏目录。</summary>
    private async Task ImportModpackAsync(string mrpack, string gameDir, string versionId)
    {
        string temp = Path.Combine(Path.GetTempPath(), "xlauncher-pack-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            SetStatus("正在解压整合包……");
            System.IO.Compression.ZipFile.ExtractToDirectory(mrpack, temp, true);
            string indexPath = Path.Combine(temp, "modrinth.index.json");
            if (!File.Exists(indexPath)) throw new InvalidDataException("这个包里没有 modrinth.index.json，可能不是 Modrinth 整合包。");

            using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
            string packName = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : Path.GetFileName(mrpack);
            if (doc.RootElement.TryGetProperty("dependencies", out var deps))
            {
                string? mc = deps.TryGetProperty("minecraft", out var m) ? m.GetString() : null;
                string loader = deps.EnumerateObject().FirstOrDefault(p => p.Name.Contains("loader", StringComparison.OrdinalIgnoreCase) || p.Name is "forge" or "neoforge").Name;
                string root = ResolveRootVersion(versionId);
                if (!string.IsNullOrWhiteSpace(mc) && !string.Equals(mc, root, StringComparison.OrdinalIgnoreCase)
                    && MessageBox.Show($"这个整合包是给 Minecraft {mc} 的，当前选的版本继承自 {root}。仍要导入吗？", "版本不一致",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }

            var files = doc.RootElement.GetProperty("files").EnumerateArray().ToList();
            int done = 0;
            foreach (var f in files)
            {
                string rel = f.GetProperty("path").GetString() ?? "";
                if (string.IsNullOrWhiteSpace(rel)) continue;
                string dest = Path.GetFullPath(Path.Combine(gameDir, rel));
                // 防止 ../ 跳出游戏目录。
                if (!dest.StartsWith(Path.GetFullPath(gameDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var urls = f.TryGetProperty("downloads", out var dl) ? dl.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
                if (urls.Count == 0) continue;
                string? sha1 = f.TryGetProperty("hashes", out var hs) && hs.TryGetProperty("sha1", out var s1) ? s1.GetString() : null;
                long? size = f.TryGetProperty("fileSize", out var fs) ? fs.GetInt64() : null;
                await DownloadAndVerifyAsync(urls[0], dest, sha1, size);
                done++;
                SetStatus($"整合包 {packName}：{done}/{files.Count} 个文件");
                Dispatcher.BeginInvoke(() => Progress.Value = files.Count == 0 ? 90 : 90.0 * done / files.Count);
            }

            string overrides = Path.Combine(temp, "overrides");
            if (Directory.Exists(overrides)) CopyDirectory(overrides, gameDir);

            Progress.Value = 100;
            SetStatus($"整合包「{packName}」已导入：{done} 个文件。");
            Log($"整合包导入完成：{packName} → {gameDir}（{done} 个文件）");
            MessageBox.Show($"整合包「{packName}」导入完成。\n\n{files.Count} 个文件 + overrides 已放进：\n{gameDir}\n\n提示：记得确认加载器版本与整合包要求一致。", "导入完成");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    /// <summary>读取最近的崩溃报告/日志，给出中文结论，而不是让玩家自己看堆栈。</summary>
    /// <summary>资源包 / 光影包管理：列出目录，双击切换启用，支持导入与删除。</summary>
    private void ResourcePacks_Click(object sender, RoutedEventArgs e)
    {
        string baseDir = GameDirForVersion(SelectedVersionId());
        var win = new Window
        {
            Title = "X · 资源包与光影", Width = 760, Height = 560, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White
        };
        var tabs = new TabControl { Margin = new Thickness(16) };
        tabs.Items.Add(BuildPackTab(win, Path.Combine(baseDir, "resourcepacks"), "资源包", new[] { ".zip" }));
        tabs.Items.Add(BuildPackTab(win, Path.Combine(baseDir, "shaderpacks"), "光影包", new[] { ".zip" }));
        win.Content = tabs;
        win.ShowDialog();
    }

    private TabItem BuildPackTab(Window owner, string dir, string title, string[] extensions)
    {
        var list = new ListBox { Height = 380, Margin = new Thickness(0, 10, 0, 10) };
        var status = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(2, 0, 0, 0) };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };

        void Refresh()
        {
            Directory.CreateDirectory(dir);
            list.Items.Clear();
            foreach (var f in Directory.GetFiles(dir).Where(f => extensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase) || f.EndsWith(ext + ".disabled", StringComparison.OrdinalIgnoreCase))).OrderBy(f => Path.GetFileName(f)))
                list.Items.Add(Path.GetFileName(f));
            status.Text = $"{list.Items.Count} 个 · {dir}";
        }

        var toggle = new Button { Content = "启用 / 停用", Margin = new Thickness(0, 0, 8, 0) };
        toggle.Click += (_, __) =>
        {
            if (list.SelectedItem is not string name) return;
            string path = Path.Combine(dir, name);
            try
            {
                if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    File.Move(path, Path.Combine(dir, name[..^9]), true);
                else
                    File.Move(path, path + ".disabled", true);
                Refresh();
            }
            catch (Exception ex) { MessageBox.Show(owner, "操作失败：" + ex.Message); }
        };

        var import = new Button { Content = "导入", Margin = new Thickness(0, 0, 8, 0) };
        import.Click += (_, __) =>
        {
            var dlg = new OpenFileDialog { Filter = "压缩包 (*.zip)|*.zip", Multiselect = true };
            if (dlg.ShowDialog() != true) return;
            Directory.CreateDirectory(dir);
            foreach (var f in dlg.FileNames) { try { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true); } catch { } }
            Refresh();
        };

        var remove = new Button { Content = "删除", Margin = new Thickness(0, 0, 8, 0) };
        remove.Click += (_, __) =>
        {
            if (list.SelectedItem is not string name) return;
            if (MessageBox.Show(owner, $"确定删除 {name} 吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { File.Delete(Path.Combine(dir, name)); Refresh(); } catch (Exception ex) { MessageBox.Show(owner, "删除失败：" + ex.Message); }
        };

        var open = new Button { Content = "打开文件夹" };
        open.Click += (_, __) => { try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); } catch { } };

        foreach (var b in new[] { toggle, import, remove, open }) buttons.Children.Add(b);
        DockPanel.SetDock(buttons, Dock.Top); panel.Children.Add(buttons);
        DockPanel.SetDock(status, Dock.Bottom); panel.Children.Add(status);
        panel.Children.Add(list);
        Refresh();
        return new TabItem { Header = title, Content = panel };
    }

    /// <summary>服务器收藏：存 servers.json，可复制地址、启动游戏后手动粘贴。</summary>
    private void Servers_Click(object sender, RoutedEventArgs e)
    {
        string file = Path.Combine(_gameDir, "xlauncher-servers.json");
        List<ServerEntry> entries = new();
        try
        {
            if (File.Exists(file)) entries = JsonSerializer.Deserialize<List<ServerEntry>>(File.ReadAllText(file)) ?? new();
        }
        catch { }

        var win = new Window
        {
            Title = "X · 服务器收藏", Width = 620, Height = 480, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White
        };
        var list = new ListBox { Height = 300, DisplayMemberPath = "Display", Margin = new Thickness(0, 10, 0, 10) };
        var nameBox = new TextBox { Width = 150, Margin = new Thickness(0, 0, 8, 0) };
        var addrBox = new TextBox { Width = 260, Margin = new Thickness(0, 0, 8, 0) };
        var status = new TextBlock { Foreground = Brushes.Gray };

        void Save()
        {
            try { File.WriteAllText(file, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        }
        void Refresh()
        {
            list.ItemsSource = null;
            list.ItemsSource = entries.ToList();
            status.Text = $"{entries.Count} 个服务器 · {file}";
        }

        var add = new Button { Content = "添加" };
        add.Click += (_, __) =>
        {
            string addr = addrBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(addr)) { MessageBox.Show(win, "地址不能为空。"); return; }
            entries.Add(new ServerEntry { Name = string.IsNullOrWhiteSpace(nameBox.Text) ? addr : nameBox.Text.Trim(), Address = addr });
            Save(); Refresh(); nameBox.Clear(); addrBox.Clear();
        };
        var del = new Button { Content = "删除", Margin = new Thickness(8, 0, 0, 0) };
        del.Click += (_, __) =>
        {
            if (list.SelectedItem is not ServerEntry se) return;
            entries.RemoveAll(x => x.Address == se.Address && x.Name == se.Name);
            Save(); Refresh();
        };
        var copy = new Button { Content = "复制地址", Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += (_, __) =>
        {
            if (list.SelectedItem is not ServerEntry se) return;
            try { System.Windows.Clipboard.SetText(se.Address); status.Text = "已复制：" + se.Address; } catch { }
        };

        var sync = new Button { Content = "同步到游戏（覆盖 servers.dat）", Margin = new Thickness(8, 0, 0, 0) };
        sync.Click += (_, __) =>
        {
            if (entries.Count == 0) { MessageBox.Show(win, "先添加几个服务器。"); return; }
            try
            {
                string target = Path.Combine(_gameDir, "servers.dat");
                Directory.CreateDirectory(_gameDir);
                if (File.Exists(target)) File.Copy(target, target + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                File.WriteAllBytes(target, BuildServersDat(entries));
                status.Text = "已写入 " + target + "（原文件已备份）";
                Log("服务器列表已写入 servers.dat：" + entries.Count + " 个");
            }
            catch (Exception ex) { MessageBox.Show(win, "写入失败：" + ex.Message); }
        };

        var form = new WrapPanel();
        form.Children.Add(nameBox); form.Children.Add(addrBox); form.Children.Add(add); form.Children.Add(del); form.Children.Add(copy); form.Children.Add(sync);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "服务器收藏", FontSize = 22, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "这里只做收藏与复制；进游戏后在「多人游戏」里粘贴地址。", Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) });
        root.Children.Add(form); root.Children.Add(list); root.Children.Add(status);
        win.Content = root;
        Refresh();
        win.ShowDialog();
    }

    /// <summary>写 Minecraft 的 servers.dat（NBT + gzip）。字符串长度是 NBT 的 ushort，不是 .NET 的 7-bit。</summary>
    private static byte[] BuildServersDat(List<ServerEntry> servers)
    {
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
        using (var w = new BinaryWriter(gz, System.Text.Encoding.UTF8))
        {
            void Str(string v)
            {
                byte[] b = System.Text.Encoding.UTF8.GetBytes(v);
                w.Write((short)b.Length);
                w.Write(b);
            }

            w.Write((byte)10); Str("");        // root compound
            w.Write((byte)9); Str("servers");  // list
            w.Write((byte)10);                 // element: compound
            w.Write(servers.Count);
            foreach (var s in servers)
            {
                w.Write((byte)8); Str("name"); Str(s.Name);
                w.Write((byte)8); Str("ip"); Str(s.Address);
                w.Write((byte)1); Str("acceptTextures"); w.Write((byte)1);
                w.Write((byte)0);
            }
            w.Write((byte)0);                  // end root
        }
        return ms.ToArray();
    }

    private sealed class ServerEntry
    {
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore]
        public string Display => $"{Name}  —  {Address}";
    }

    /// <summary>检查 GitHub 上有没有比当前更新的 release。</summary>
    private void RemotePanel_Click(object sender, RoutedEventArgs e) => StartRemotePanel();

    private System.Net.Sockets.TcpListener? _remoteListener;
    private readonly List<string> _recentLogs = new();

    /// <summary>手机遥控面板：电脑上开一个小网页，手机浏览器能看状态、启动游戏、看日志。</summary>
    private void StartRemotePanel()
    {
        if (_remoteListener != null) { MessageBox.Show("面板已经在运行：http://" + LocalIPv4() + ":51234"); return; }
        try
        {
            _remoteListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 51234);
            _remoteListener.Start();
            _ = Task.Run(ServeRemoteAsync);
            string ip = LocalIPv4();
            Log("手机遥控面板已启动：http://" + ip + ":51234");
            MessageBox.Show("手机和电脑连同一个 WiFi，用手机浏览器打开：\n\nhttp://" + ip + ":51234", "手机遥控面板");
        }
        catch (Exception ex)
        {
            Log("遥控面板启动失败：" + ex.Message);
            MessageBox.Show("启动失败：" + ex.Message);
        }
    }

    private static string LocalIPv4()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return addr.Address.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    private async Task ServeRemoteAsync()
    {
        var listener = _remoteListener;
        if (listener == null) return;
        while (true)
        {
            System.Net.Sockets.TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, false, 1024, true);
                    string requestLine = await reader.ReadLineAsync() ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    var parts = requestLine.Split(' ');
                    string path = parts.Length > 1 ? parts[1] : "/";

                    string body; string contentType = "text/html; charset=utf-8";
                    if (path.StartsWith("/api/status")) { contentType = "application/json; charset=utf-8"; body = BuildStatusJson(); }
                    else if (path.StartsWith("/api/launch"))
                    {
                        contentType = "application/json; charset=utf-8";
                        string id = path.Contains("id=") ? Uri.UnescapeDataString(path[(path.IndexOf("id=") + 3)..]) : "";
                        Dispatcher.BeginInvoke(() => LaunchFromRemote(id));
                        body = "{\"ok\":true}";
                    }
                    else body = RemotePanelHtml();

                    byte[] payload = System.Text.Encoding.UTF8.GetBytes(body);
                    byte[] head = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: " + contentType + "\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(payload);
                }
                catch { }
                finally { try { client.Dispose(); } catch { } }
            });
        }
    }

    private void LaunchFromRemote(string id)
    {
        try
        {
            var row = _versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
            if (row == null) { Log("遥控：没有这个版本 " + id); return; }
            VersionsList.SelectedItem = row;
            Launch_Click(this, new RoutedEventArgs());
        }
        catch (Exception ex) { Log("遥控启动失败：" + ex.Message); }
    }

    private string BuildStatusJson() => JsonSerializer.Serialize(new
    {
        version = SelectedVersionId(),
        versions = _versions.Select(v => v.Id).Take(50).ToArray(),
        logs = _recentLogs.TakeLast(40).ToArray()
    });

    private string RemotePanelHtml() => "<!doctype html><html lang=\"zh\"><head><meta charset=\"utf-8\">"
        + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>X 启动器 · 手机遥控</title>"
        + "<style>body{font-family:system-ui,sans-serif;background:#f5f6f8;margin:0;padding:16px;color:#1f2430}"
        + "h1{font-size:18px;margin:0 0 12px}.card{background:#fff;border:1px solid #e9ecf2;border-radius:12px;padding:14px;margin-bottom:12px}"
        + "select,button{font-size:15px;padding:10px;border-radius:8px;border:1px solid #dde3ec;width:100%}"
        + "button{background:#3a6fe0;color:#fff;border:0;margin-top:10px}"
        + "pre{font-size:12px;background:#fbfcfe;padding:10px;border-radius:8px;overflow:auto;max-height:45vh;white-space:pre-wrap}</style></head><body>"
        + "<h1>X 启动器 · 手机遥控</h1><div class=\"card\"><select id=\"v\"></select><button onclick=\"launch()\">启动游戏</button></div>"
        + "<div class=\"card\"><pre id=\"log\">加载中…</pre></div><script>"
        + "async function refresh(){const r=await fetch('/api/status');const d=await r.json();const sel=document.getElementById('v');"
        + "if(!sel.dataset.done){sel.innerHTML=d.versions.map(x=>'<option'+(x===d.version?' selected':'')+'>'+x+'</option>').join('');sel.dataset.done=1;}"
        + "document.getElementById('log').textContent=(d.logs||[]).join('\n')||'（暂无日志）';}"
        + "async function launch(){const id=document.getElementById('v').value;await fetch('/api/launch?id='+encodeURIComponent(id));setTimeout(refresh,1500);}"
        + "refresh();setInterval(refresh,3000);</script></body></html>";

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("正在检查更新……");
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ppgg6/MC-Launcher-Revival/releases/latest");
            req.Headers.UserAgent.ParseAdd("XLauncher/4.5");
            using var resp = await Http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            string tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string url = doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            string current = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0";
            SetStatus($"最新版本：{tag}（当前 {current}）");
            string? assetUrl = null;
            if (doc.RootElement.TryGetProperty("assets", out var assets))
                assetUrl = assets.EnumerateArray()
                    .Where(a => (a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null)
                    .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(assetUrl))
            {
                if (MessageBox.Show($"发现新版本 {tag}（当前 {current}）。\n\n要现在下载并自动替换当前程序吗？\n替换后启动器会自己重启。", "自动更新",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                SetStatus("正在下载新版本……");
                string temp = Path.Combine(Path.GetTempPath(), "XLauncher-update.exe");
                await DownloadAndVerifyAsync(assetUrl!, temp, null, null);
                string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrWhiteSpace(currentExe)) throw new InvalidOperationException("拿不到当前程序路径，无法自动替换。");
                string bat = Path.Combine(Path.GetTempPath(), "xlauncher-update.bat");
                File.WriteAllText(bat,
                    "@echo off\r\ntimeout /t 2 >nul\r\nmove /y \"" + temp + "\" \"" + currentExe + "\" >nul\r\nstart \"\" \"" + currentExe + "\"\r\ndel \"%~f0\"\r\n");
                Process.Start(new ProcessStartInfo(bat) { UseShellExecute = true, CreateNoWindow = true });
                Application.Current.Shutdown();
                return;
            }

            var result = MessageBox.Show($"最新发布：{tag}\n当前版本：{current}\n\n这个 release 没有可直接替换的 exe，要打开下载页面吗？", "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes && !string.IsNullOrWhiteSpace(url))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("检查更新失败：" + ex.Message);
            Log("检查更新失败：" + ex);
        }
    }

    private void SkinEditor_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window
        {
            Title = "X · 皮肤编辑器（64×64）", Width = 620, Height = 720, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White
        };

        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(64, 64, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null);
        var pixels = new byte[64 * 64 * 4];
        void Flush() => bmp.WritePixels(new Int32Rect(0, 0, 64, 64), pixels, 64 * 4, 0);
        Flush();

        var image = new Image
        {
            Width = 512, Height = 512, Stretch = System.Windows.Media.Stretch.Fill,
            Source = bmp, Cursor = System.Windows.Input.Cursors.Pen
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(image, System.Windows.Media.Imaging.BitmapScalingMode.NearestNeighbor);

        Color current = Colors.White;

        void Paint(Point pos, bool erase)
        {
            int x = (int)(pos.X / image.ActualWidth * 64), y = (int)(pos.Y / image.ActualHeight * 64);
            if (x < 0 || y < 0 || x >= 64 || y >= 64) return;
            int i = (y * 64 + x) * 4;
            if (erase) { pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 0; pixels[i + 3] = 0; }
            else { pixels[i] = current.B; pixels[i + 1] = current.G; pixels[i + 2] = current.R; pixels[i + 3] = 255; }
            Flush();
        }

        image.MouseDown += (_, ev) => { if (ev.LeftButton == System.Windows.Input.MouseButtonState.Pressed) Paint(ev.GetPosition(image), false); };
        image.MouseMove += (_, ev) => { if (ev.LeftButton == System.Windows.Input.MouseButtonState.Pressed) Paint(ev.GetPosition(image), false); };
        image.MouseRightButtonDown += (_, ev) => Paint(ev.GetPosition(image), true);
        image.MouseMove += (_, ev) => { if (ev.RightButton == System.Windows.Input.MouseButtonState.Pressed) Paint(ev.GetPosition(image), true); };

        var palette = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Color[] colors = {
            Colors.White, Colors.Black, Color.FromRgb(0xE8,0xB4,0x9A), Color.FromRgb(0xC6,0x8B,0x6B),
            Color.FromRgb(0xFF,0x3B,0x3B), Color.FromRgb(0xFF,0x9A,0x3D), Color.FromRgb(0xFF,0xE0,0x5A), Color.FromRgb(0x6B,0xD3,0x4E),
            Color.FromRgb(0x2F,0xA8,0x8C), Color.FromRgb(0x4A,0x9B,0xFF), Color.FromRgb(0x33,0x45,0x99), Color.FromRgb(0x8F,0x5A,0xD6),
            Color.FromRgb(0xE0,0x4F,0xB8), Color.FromRgb(0x7A,0x4E,0x32), Color.FromRgb(0x9C,0x9C,0x9C), Color.FromRgb(0x3A,0x3A,0x3A)
        };
        foreach (var c in colors)
        {
            var sw = new Border { Width = 30, Height = 30, Margin = new Thickness(4), CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(c), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand };
            var capture = c;
            sw.MouseLeftButtonDown += (_, __) => { current = capture; };
            palette.Children.Add(sw);
        }

        var import = new Button { Content = "导入皮肤 PNG" };
        import.Click += (_, __) =>
        {
            var dlg = new OpenFileDialog { Filter = "皮肤图片 (*.png)|*.png" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var src = new System.Windows.Media.Imaging.BitmapImage(new Uri(dlg.FileName));
                var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int w = converted.PixelWidth, h = converted.PixelHeight;
                var tmp = new byte[w * h * 4];
                converted.CopyPixels(tmp, w * 4, 0);
                Array.Clear(pixels, 0, pixels.Length);
                for (int y = 0; y < Math.Min(64, h); y++)
                    for (int x = 0; x < Math.Min(64, w); x++)
                    {
                        int si = (y * w + x) * 4, di = (y * 64 + x) * 4;
                        pixels[di] = tmp[si]; pixels[di + 1] = tmp[si + 1]; pixels[di + 2] = tmp[si + 2]; pixels[di + 3] = tmp[si + 3];
                    }
                Flush();
            }
            catch (Exception ex) { MessageBox.Show(win, "导入失败：" + ex.Message); }
        };

        var save = new Button { Content = "保存为 PNG" };
        save.Click += (_, __) =>
        {
            var dlg = new SaveFileDialog { Filter = "PNG 图片 (*.png)|*.png", FileName = "skin.png" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = File.Create(dlg.FileName);
                encoder.Save(fs);
                Log("皮肤已保存：" + dlg.FileName);
                MessageBox.Show(win, "已保存：" + dlg.FileName);
            }
            catch (Exception ex) { MessageBox.Show(win, "保存失败：" + ex.Message); }
        };

        var clear = new Button { Content = "清空" };
        clear.Click += (_, __) => { Array.Clear(pixels, 0, pixels.Length); Flush(); };

        var tools = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        tools.Children.Add(import); tools.Children.Add(save); tools.Children.Add(clear);

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "皮肤编辑器", FontSize = 22, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "左键涂色、右键擦除。原版皮肤是 64×64（旧版 64×32 会自动补齐）。",
            Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 6, 0, 12)
        });
        root.Children.Add(image);
        root.Children.Add(palette);
        root.Children.Add(tools);
        win.Content = new ScrollViewer { Content = root };
        win.ShowDialog();
    }

    private void CrashAnalyze_Click(object sender, RoutedEventArgs e)
    {
        string id = SelectedVersionId();
        string gameDir = string.IsNullOrWhiteSpace(id) ? _gameDir : GameDirForVersion(id);
        var candidates = new List<string>();
        try
        {
            string crashDir = Path.Combine(gameDir, "crash-reports");
            if (Directory.Exists(crashDir)) candidates.AddRange(Directory.GetFiles(crashDir, "*.txt").OrderByDescending(File.GetLastWriteTime).Take(1));
            string latest = Path.Combine(gameDir, "logs", "latest.log");
            if (File.Exists(latest)) candidates.Add(latest);
        }
        catch { }
        if (candidates.Count == 0)
        {
            MessageBox.Show($"没有找到崩溃报告或日志。\n\n查找位置：{gameDir}\n\n先启动一次游戏，崩了之后再点这里。", "没有可分析的日志");
            return;
        }

        string file = candidates[0];
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception ex) { MessageBox.Show("读取日志失败：" + ex.Message); return; }

        var findings = new List<string>();
        void Hit(string pattern, string verdict) { if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase)) findings.Add(verdict); }

        Hit("OutOfMemoryError", "内存不够：把「内存」调大一些（比如从 4096 提到 6144），或减少模组数量。");
        Hit("UnsupportedClassVersionError", "Java 版本不匹配：这个版本要求的 Java 和你当前用的不一致，换对应主版本的 Java。");
        Hit("java.lang.NoSuchMethodError", "模组或加载器版本不兼容：检查模组是否对应当前 Minecraft 与加载器版本。");
        Hit("ModResolutionException", "模组依赖缺失或冲突：按崩溃报告里点名的模组补齐依赖或删掉冲突项。");
        Hit("Duplicate mods", "装了重复的模组（同一个模组两个文件），删掉多余的那个。");
        Hit("Mixin apply failed", "模组注入失败：通常是模组版本与该加载器不匹配，更新或移除最近装的模组。");
        Hit("GLFW error", "显卡/驱动问题：更新显卡驱动，或试着关掉光影。");
        Hit("The game crashed whilst", "游戏崩溃：详见报告里的 “Description” 一行。");

        string tail = TailText(text, 1200);
        string body = findings.Count > 0
            ? "可能原因：\n" + string.Join("\n", findings.Distinct().Select((f, i) => $"{i + 1}. {f}"))
            : "没有匹配到常见错误特征，先看下面报告的结尾部分。";
        MessageBox.Show($"{body}\n\n—— 日志：{Path.GetFileName(file)} ——\n{tail}", "崩溃分析", MessageBoxButton.OK, MessageBoxImage.Information);
        Log($"崩溃分析：{Path.GetFileName(file)}，命中 {findings.Count} 条特征。");
    }

    private async void InstallForge_Click(object sender, RoutedEventArgs e)
    {
        string mc = (VersionsList.SelectedItem as VersionRow)?.Id ?? "";
        if (string.IsNullOrWhiteSpace(mc))
        {
            MessageBox.Show("请先在版本列表里选中一个 Minecraft 版本，再安装 Forge。", "需要选择版本");
            return;
        }
        if (MessageBox.Show($"将为 Minecraft {mc} 安装 Forge（会下载官方安装器并运行它，可能需要几分钟）。继续吗？",
                "安装 Forge", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await InstallForgeAsync(mc); }
        catch (Exception ex)
        {
            SetStatus("Forge 安装失败：" + ex.Message);
            Log("Forge 安装失败：" + ex);
            MessageBox.Show(ex.Message, "安装失败");
        }
    }

    /// <summary>用官方安装器装 Forge：查推荐版本 → 下 installer → 用匹配的 Java 跑 --installClient。</summary>
    private async Task InstallForgeAsync(string mcVersion)
    {
        SetStatus($"正在查询 Forge {mcVersion} 的可用版本……");
        string promotions;
        using (var resp = await Http.GetAsync("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json"))
        {
            resp.EnsureSuccessStatusCode();
            promotions = await resp.Content.ReadAsStringAsync();
        }
        using var promosDoc = JsonDocument.Parse(promotions);
        string? forgeVer = null;
        if (promosDoc.RootElement.TryGetProperty("promos", out var promos))
        {
            if (promos.TryGetProperty(mcVersion + "-recommended", out var rec)) forgeVer = rec.GetString();
            else if (promos.TryGetProperty(mcVersion + "-latest", out var lat)) forgeVer = lat.GetString();
        }
        if (string.IsNullOrWhiteSpace(forgeVer))
            throw new InvalidDataException($"Forge 没有支持 Minecraft {mcVersion} 的版本（到官网确认该版本是否已被支持）。");

        string fileName = $"forge-{mcVersion}-{forgeVer}-installer.jar";
        string url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{forgeVer}/{fileName}";
        string installer = Path.Combine(_gameDir, "installers", fileName);
        SetStatus($"正在下载 Forge 安装器 {forgeVer}……");
        await DownloadAndVerifyAsync(url, installer, null, null);

        int javaMajor = GuessJavaMajor(mcVersion);
        string java;
        try { java = FindJava(javaMajor); }
        catch (FileNotFoundException)
        {
            var choice = MessageBox.Show($"运行 Forge 安装器需要 Java {javaMajor}，当前没有找到。现在下载吗？",
                "缺少 Java", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes) return;
            java = await InstallJavaRuntimeAsync(javaMajor);
        }

        SetStatus($"正在运行 Forge 安装器（这一步可能要好几分钟，请不要关掉启动器）……");
        var psi = new ProcessStartInfo(java)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _gameDir
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installer);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(_gameDir);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Forge 安装器没有启动。");
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = (await outTask) + "\n" + (await errTask);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Forge 安装器返回 {process.ExitCode}。最后输出：\n{TailText(output, 500)}");
        Log("Forge 安装器完成，输出片段：" + TailText(output, 160));

        string versionsDir = Path.Combine(_gameDir, "versions");
        string? installed = Directory.Exists(versionsDir)
            ? Directory.GetDirectories(versionsDir)
                .Where(d => Path.GetFileName(d).Contains("forge", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Directory.GetLastWriteTime)
                .FirstOrDefault()
            : null;
        if (installed == null) throw new InvalidDataException("Forge 安装器跑完了，但 versions 目录里没有出现新版本。");
        string id = Path.GetFileName(installed);
        Log($"Forge 版本已安装：{id}");

        if (!_versions.Any(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            _versions.Insert(0, new VersionRow(id, "forge", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ""));
            VersionsList.ItemsSource = null;
            VersionsList.ItemsSource = _versions;
        }
        Progress.Value = 100;
        SetStatus($"Forge {forgeVer} 已安装（版本 {id}）。");
        VersionsList.SelectedItem = _versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
        UpdateLaunchAvailability();
        MessageBox.Show($"Forge 安装完成。\n\n版本：{id}\n\n模组请放进对应版本的 mods 目录。", "安装完成");
    }

    private static string TailText(string text, int max)
        => string.IsNullOrEmpty(text) ? "" : (text.Length <= max ? text : text[^max..]);

    /// <summary>下载只有 Maven 坐标（name + url）的库，例如 Fabric 提供的那些。</summary>
    private async Task<int> DownloadMavenLibrariesAsync(JsonElement profile)
    {
        if (!profile.TryGetProperty("libraries", out var libs)) return 0;
        var jobs = new List<Task>();
        foreach (var lib in libs.EnumerateArray())
        {
            string? name = lib.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? baseUrl = lib.TryGetProperty("url", out var u) ? u.GetString() : null;
            string? rel = string.IsNullOrWhiteSpace(name) ? null : MavenRelativePath(name!);
            if (string.IsNullOrWhiteSpace(baseUrl) || rel is null) continue;
            string dest = Path.Combine(_gameDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
            jobs.Add(DownloadAndVerifyAsync(baseUrl!.TrimEnd('/') + "/" + rel, dest, null, null));
        }
        await Task.WhenAll(jobs);
        return jobs.Count;
    }

    /// <summary>元数据没有 javaVersion 时，按版本号猜一个主版本。</summary>
    private static int GuessJavaMajor(string version)
    {
        if (version.StartsWith("1.20.5", StringComparison.OrdinalIgnoreCase) || version.StartsWith("1.21", StringComparison.OrdinalIgnoreCase)) return 21;
        if (version.StartsWith("1.18", StringComparison.OrdinalIgnoreCase) || version.StartsWith("1.19", StringComparison.OrdinalIgnoreCase) || version.StartsWith("1.20", StringComparison.OrdinalIgnoreCase)) return 17;
        return 8;
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
                    !(Environment.Is64BitProcess ? (a == "x86_64" || a == "amd64" || a == "x64") : (a == "x86" || a == "i386"))) match = false;
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
        var key = win.GetString()?.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32");
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
        await Parallel.ForEachAsync(entries, new ParallelOptions { MaxDegreeOfParallelism = MaxParallel }, async (obj, ct) =>
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
                Dispatcher.BeginInvoke(() => Progress.Value = 60 + 39.0 * current / Math.Max(1, entries.Count));
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
            // 快速通道：大小对得上就认为已存在，不再对每个文件重算 SHA-1 —— 机械盘上这一步是安装慢的主因。
            // 真损坏了启动时会报错，届时删掉重下即可。
            if (!expectedSize.HasValue || fi.Length == expectedSize.Value) return;
            File.Delete(path);
        }
        await DownloadSlots.WaitAsync();
        try
        {
            string temp = path + ".part";
            Exception? last = null;
            // 多源回退：官方源失败后自动换 BMCLAPI 镜像，适合国内网络。
            foreach (string candidate in UrlCandidates(url))
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        // 断点续传：上次留下的 .part 能续就续，省掉重下几百 MB。
                        long already = File.Exists(temp) ? new FileInfo(temp).Length : 0;
                        using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
                        if (already > 0 && expectedSize.HasValue && already >= expectedSize.Value) already = 0;
                        if (already > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(already, null);
                        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();
                        bool append = already > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                        if (already > 0 && !append) already = 0;
                        await using (var input = await response.Content.ReadAsStreamAsync())
                        await using (var output = new FileStream(temp, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                            await input.CopyToAsync(output);
                        var info = new FileInfo(temp);
                        if (expectedSize.HasValue && info.Length != expectedSize.Value) throw new InvalidDataException("文件大小校验失败：" + Path.GetFileName(path));
                        if (!string.IsNullOrWhiteSpace(sha1) && await Sha1Async(temp) != sha1.ToLowerInvariant()) throw new InvalidDataException("SHA-1 校验失败：" + Path.GetFileName(path));
                        File.Move(temp, path, true);
                        return;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        // 网络中断保留 .part 以便续传；校验类失败才丢弃，避免拼出坏文件。
                        if (ex is InvalidDataException) TryDeleteFile(temp);
                        if (attempt < 3) await Task.Delay(400 * attempt);
                    }
                }
                if (!string.Equals(candidate, url, StringComparison.Ordinal))
                    Log("官方源失败，已切换镜像：" + Path.GetFileName(path));
            }
            throw new IOException($"下载失败（官方源与镜像都试过了）：{Path.GetFileName(path)}", last);
        }
        finally { DownloadSlots.Release(); }
    }

    /// <summary>给出同一个文件的候选地址：官方源在前，BMCLAPI 镜像在后。</summary>
    private static IEnumerable<string> UrlCandidates(string url)
    {
        yield return url;
        const string assets = "https://resources.download.minecraft.net/";
        const string libs = "https://libraries.minecraft.net/";
        const string meta = "https://piston-meta.mojang.com/";
        if (url.StartsWith(assets, StringComparison.OrdinalIgnoreCase))
            yield return "https://bmclapi2.bangbang93.com/assets/" + url[assets.Length..];
        else if (url.StartsWith(libs, StringComparison.OrdinalIgnoreCase))
            yield return "https://bmclapi2.bangbang93.com/maven/" + url[libs.Length..];
        else if (url.StartsWith(meta, StringComparison.OrdinalIgnoreCase))
            yield return "https://bmclapi2.bangbang93.com/" + url[meta.Length..];
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

    /// <summary>把 Maven 坐标（group:artifact:version[:classifier]）转成库目录里的相对路径。</summary>
    private static string? MavenRelativePath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3 || parts.Any(string.IsNullOrWhiteSpace)) return null;
        string group = parts[0].Replace('.', '/'), artifact = parts[1], version = parts[2];
        string classifier = parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]) ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    /// <summary>读取指定版本的 JSON（不存在则抛）。</summary>
    private JsonElement LoadVersionJson(string id)
    {
        string path = Path.Combine(_gameDir, "versions", id, id + ".json");
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到版本 JSON：{path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    /// <summary>沿 inheritsFrom 找到最底层的原版版本 id（无继承则返回自身）。</summary>
    private string ResolveRootVersion(string id, int depth = 0)
    {
        if (depth > 6) return id;
        var self = LoadVersionJson(id);
        if (self.TryGetProperty("inheritsFrom", out var parent) && parent.GetString() is string parentId && !string.IsNullOrWhiteSpace(parentId))
            return ResolveRootVersion(parentId, depth + 1);
        return id;
    }

    /// <summary>把加载器版本的增量 JSON 合并成完整版本（父在前、子覆盖；库和参数按序拼接）。</summary>
    private JsonElement MergeVersionChain(string id, int depth = 0)
    {
        if (depth > 6) throw new InvalidDataException("版本继承层数过深，疑似 inheritsFrom 循环。");
        var self = LoadVersionJson(id);
        if (!self.TryGetProperty("inheritsFrom", out var parentEl) || parentEl.GetString() is not string parentId || string.IsNullOrWhiteSpace(parentId))
            return self;
        var parent = MergeVersionChain(parentId, depth + 1);

        var merged = new System.Text.Json.Nodes.JsonObject();
        foreach (var prop in parent.EnumerateObject())
            merged[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());

        foreach (var prop in self.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "inheritsFrom":
                    break;
                case "libraries":
                    merged["libraries"] = ConcatLibraries(parent, self);
                    break;
                case "arguments":
                    merged["arguments"] = MergeArguments(parent, self);
                    break;
                case "minecraftArguments":
                    {
                        string a = parent.TryGetProperty("minecraftArguments", out var pa) ? pa.GetString() ?? "" : "";
                        string b = prop.Value.GetString() ?? "";
                        merged["minecraftArguments"] = (a + " " + b).Trim();
                        break;
                    }
                default:
                    merged[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
                    break;
            }
        }
        merged["id"] = id;
        return JsonDocument.Parse(merged.ToJsonString()).RootElement;
    }

    private static System.Text.Json.Nodes.JsonArray ConcatLibraries(JsonElement parent, JsonElement child)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var array = new System.Text.Json.Nodes.JsonArray();
        foreach (var source in new[] { parent, child })
        {
            if (!source.TryGetProperty("libraries", out var libs)) continue;
            foreach (var lib in libs.EnumerateArray())
            {
                string key = lib.TryGetProperty("name", out var n) ? n.GetString() ?? "" : Guid.NewGuid().ToString();
                if (!seen.Add(key)) continue;
                array.Add(System.Text.Json.Nodes.JsonNode.Parse(lib.GetRawText()));
            }
        }
        return array;
    }

    private static System.Text.Json.Nodes.JsonObject MergeArguments(JsonElement parent, JsonElement child)
    {
        var result = new System.Text.Json.Nodes.JsonObject();
        foreach (string kind in new[] { "game", "jvm" })
        {
            var list = new System.Text.Json.Nodes.JsonArray();
            foreach (var source in new[] { parent, child })
            {
                if (!source.TryGetProperty("arguments", out var args) || !args.TryGetProperty(kind, out var arr)) continue;
                foreach (var item in arr.EnumerateArray())
                    list.Add(System.Text.Json.Nodes.JsonNode.Parse(item.GetRawText()));
            }
            result[kind] = list;
        }
        return result;
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
                if (poll.IsSuccessStatusCode) { msToken = result.RootElement.GetProperty("access_token").GetString(); if (result.RootElement.TryGetProperty("refresh_token", out var rt)) _refreshToken = rt.GetString(); break; }
                string error = result.RootElement.TryGetProperty("error", out var er) ? er.GetString() ?? "" : "";
                if (error == "authorization_pending") continue;
                if (error == "slow_down") { interval += 5; continue; }
                throw new Exception(resultText);
            }
            if (msToken == null) throw new TimeoutException("Microsoft 登录超时。");
            await CompleteSignInAsync(msToken!);
            SaveAccount();
        }
        catch (Exception ex) { _accessToken = null; _offlineMode = false; UpdateLaunchAvailability(); AccountStatusText.Text = "登录失败"; SetStatus("认证失败：" + ex.Message); Log("认证失败：" + ex); MessageBox.Show(ex.Message, "登录失败"); }
    }

    /// <summary>从 MSA token 走完 Xbox Live → XSTS → Minecraft Services，拿到 Java Profile。</summary>
    private async Task CompleteSignInAsync(string msToken)
    {
        var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new { Properties = new { AuthMethod="RPS", SiteName="user.auth.xboxlive.com", RpsTicket="d=" + msToken }, RelyingParty="http://auth.xboxlive.com", TokenType="JWT" });
        string xblToken = xbl.GetProperty("Token").GetString()!;
        string userHash = xbl.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;
        var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new { Properties = new { SandboxId="RETAIL", UserTokens = new[] { xblToken } }, RelyingParty="rp://api.minecraftservices.com/", TokenType="JWT" });
        string xstsToken = xsts.GetProperty("Token").GetString()!;
        using var mcResp = await Http.PostAsJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" });
        using var mcDoc = JsonDocument.Parse(await mcResp.Content.ReadAsStringAsync()); mcResp.EnsureSuccessStatusCode();
        _accessToken = mcDoc.RootElement.GetProperty("access_token").GetString();
        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profileRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
        using var profileResp = await Http.SendAsync(profileRequest);
        using var profile = JsonDocument.Parse(await profileResp.Content.ReadAsStringAsync()); profileResp.EnsureSuccessStatusCode();
        _uuid = profile.RootElement.GetProperty("id").GetString();
        _username = profile.RootElement.GetProperty("name").GetString();
        _userType = "msa"; _offlineMode = false;
        PlayerNameText.Text = _username;
        AccountStatusText.Text = "已登录正版 · " + _username;
        UpdateLaunchAvailability();
        Log("Microsoft → Xbox Live → XSTS → Minecraft Services 认证成功；已获取 Java Profile。");
        SetStatus("正版账号认证成功。");
    }

    private string AccountPath => Path.Combine(Path.GetDirectoryName(SettingsPath)!, "account.bin");

    /// <summary>刷新令牌用 DPAPI 加密存盘，下次启动免登录。</summary>
    private void SaveAccount()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_refreshToken) || string.IsNullOrWhiteSpace(_uuid)) return;
            string payload = JsonSerializer.Serialize(new { refreshToken = _refreshToken, uuid = _uuid, name = _username, userType = _userType });
            byte[] bytes = System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(payload), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(AccountPath)!);
            File.WriteAllBytes(AccountPath, bytes);
            Log("账号凭据已加密保存。");
        }
        catch (Exception ex) { Log("保存账号失败：" + ex.Message); }
    }

    private sealed record SavedAccount(string RefreshToken, string Uuid, string? Name);

    private SavedAccount? LoadAccount()
    {
        try
        {
            if (!File.Exists(AccountPath)) return null;
            byte[] bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                File.ReadAllBytes(AccountPath), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            using var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            string? rt = doc.RootElement.TryGetProperty("refreshToken", out var r) ? r.GetString() : null;
            string? uuid = doc.RootElement.TryGetProperty("uuid", out var u) ? u.GetString() : null;
            string? name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(rt) || string.IsNullOrWhiteSpace(uuid)) return null;
            return new SavedAccount(rt!, uuid!, name);
        }
        catch { return null; }
    }

    private void ClearAccount()
    {
        try { if (File.Exists(AccountPath)) File.Delete(AccountPath); } catch { }
    }

    /// <summary>启动时用刷新令牌静默恢复正版账号。</summary>
    private async Task RestoreAccountAsync()
    {
        var saved = LoadAccount();
        if (saved == null) return;
        string clientId = OAuthClientIdText.Text.Trim();
        if (string.IsNullOrWhiteSpace(clientId)) { Log("已保存账号，但设置里没有 OAuth Client ID，无法自动恢复。"); return; }
        try
        {
            SetStatus("正在恢复上次的账号……");
            using var resp = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = clientId,
                    ["refresh_token"] = saved.RefreshToken,
                    ["scope"] = "XboxLive.signin offline_access"
                }));
            string text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) { Log("刷新令牌失效，需要重新登录。"); return; }
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("refresh_token", out var rt)) _refreshToken = rt.GetString();
            else _refreshToken = saved.RefreshToken;
            await CompleteSignInAsync(doc.RootElement.GetProperty("access_token").GetString()!);
            SaveAccount();
            Log("已自动恢复微软账号：" + _username);
        }
        catch (Exception ex)
        {
            Log("自动恢复账号失败：" + ex.Message);
            SetStatus("账号自动恢复失败，可以手动重新登录。");
        }
    }

    /// <summary>记录一次启动结果，用于下次判断要不要进安全模式。</summary>
    private string LaunchStampPath(string versionId) => Path.Combine(_gameDir, "versions", versionId, "xlauncher-last-launch.json");

    private void MarkLaunch(string versionId, bool ok)
    {
        try
        {
            string dir = Path.GetDirectoryName(LaunchStampPath(versionId))!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(LaunchStampPath(versionId), JsonSerializer.Serialize(new { ok, at = DateTime.Now }));
        }
        catch { }
    }

    private bool LastLaunchFailed(string versionId)
    {
        try
        {
            string path = LaunchStampPath(versionId);
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("ok", out var ok) && !ok.GetBoolean();
        }
        catch { return false; }
    }

    /// <summary>安全模式：把 mods 改名，游戏就不加载模组；进程退出后自动改回来。</summary>
    private string? DisableModsForSafeMode(string versionId)
    {
        try
        {
            string mods = Path.Combine(GameDirForVersion(versionId), "mods");
            if (!Directory.Exists(mods)) return null;
            string moved = mods + ".safe-" + DateTime.Now.ToString("HHmmss");
            Directory.Move(mods, moved);
            Directory.CreateDirectory(mods);
            Log("安全模式：mods 已临时改名 → " + Path.GetFileName(moved));
            return moved;
        }
        catch (Exception ex) { Log("安全模式改名失败：" + ex.Message); return null; }
    }

    private void RestoreModsAfterSafeMode(string versionId, string? movedDir)
    {
        if (string.IsNullOrWhiteSpace(movedDir)) return;
        try
        {
            string mods = Path.Combine(GameDirForVersion(versionId), "mods");
            if (Directory.Exists(movedDir))
            {
                if (Directory.Exists(mods) && !Directory.EnumerateFileSystemEntries(mods).Any()) Directory.Delete(mods, true);
                Directory.Move(movedDir, mods);
                Log("安全模式结束，mods 已还原。");
            }
        }
        catch (Exception ex) { Log("还原 mods 失败：" + ex.Message + "，手动改名回来即可：" + movedDir); }
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
        AccountStatusText.Text = "离线账户 · " + name + "（未验证）";
        UpdateLaunchAvailability();
        Log($"已切换到离线本地账户：{name}；该身份不具备正版服务器认证。UUID={_uuid}");
        SetStatus("离线模式已启用。可启动本地已完整安装的版本；正版服务器、Realms 等服务不可用。");
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        _accessToken = null; _uuid = null; _username = null; _userType = null; _offlineMode = false; _refreshToken = null; ClearAccount();
        AccountStatusText.Text = "未登录";
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
        bool hasVersion = VersionsList.SelectedItem is VersionRow vr && VersionLooksInstalled(vr.Id);
        LaunchButton.IsEnabled = hasIdentity && hasVersion;
    }

    /// <summary>启动时按设置检查已安装版本，只记日志，不改动版本列表。</summary>
    private void CheckInstalledVersions()
    {
        if (!_autoCheckInstalled) { Log("已跳过启动时的已安装版本检查（可在设置中重新开启）。"); return; }
        try
        {
            var versionsDir = Path.Combine(_gameDir, "versions");
            if (!Directory.Exists(versionsDir)) { Log("启动检查：游戏目录里还没有 versions 子目录。"); return; }
            int count = Directory.GetDirectories(versionsDir).Count(dir =>
            {
                string id = Path.GetFileName(dir);
                return File.Exists(Path.Combine(dir, id + ".jar")) && File.Exists(Path.Combine(dir, id + ".json"));
            });
            Log($"启动检查：游戏目录中已完整安装 {count} 个版本。");
        }
        catch (Exception ex) { Log("启动检查失败：" + ex.Message); }
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
            string versionDir = Path.Combine(_gameDir, "versions", selected.Id), jsonPath = Path.Combine(versionDir, selected.Id + ".json");
            if (!File.Exists(jsonPath)) throw new FileNotFoundException("所选版本尚未安装，请先安装。");
            // 加载器版本（Fabric/Forge）只有增量 JSON，需要沿 inheritsFrom 合并回原版。
            var meta = MergeVersionChain(selected.Id);
            string rootId = ResolveRootVersion(selected.Id);
            string jarPath = Path.Combine(_gameDir, "versions", rootId, rootId + ".jar");
            if (!File.Exists(jarPath)) throw new FileNotFoundException($"缺少原版客户端 {rootId}.jar，请先安装这个原版版本。");
            int javaMajor = meta.TryGetProperty("javaVersion", out var jv) && jv.TryGetProperty("majorVersion", out var jm) ? jm.GetInt32() : GuessJavaMajor(selected.Id);
            _instances.TryGetValue(selected.Id, out var inst);
            string java;
            try
            {
                if (inst?.JavaPath is string instJava && File.Exists(instJava))
                {
                    if (!IsJavaCompatible(instJava, javaMajor))
                        throw new FileNotFoundException($"实例设置的 Java 与要求的主版本不符（需要 Java {javaMajor}）：{instJava}");
                    java = instJava;
                }
                else if (_versionJavaPaths.TryGetValue(selected.Id, out var assigned) && !string.IsNullOrWhiteSpace(assigned))
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

            // 上次启动失败过 → 提议安全模式（临时禁用模组）
            string? safeModsMoved = null;
            if (LastLaunchFailed(selected.Id))
            {
                var choice = MessageBox.Show(
                    $"上次启动 {selected.Id} 似乎没有成功。\n\n要用安全模式启动吗？（临时把 mods 文件夹改名，游戏不加载模组，退出后自动还原）",
                    "安全模式", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Yes) safeModsMoved = DisableModsForSafeMode(selected.Id);
            }
            string natives = Path.Combine(_gameDir, "versions", rootId, "natives");

            // 启动前自检：PCL 也是先把明显会出问题的配置挡住，再谈启动。
            var warnings = new List<string>();
            int memMb = GetMemory();
            if (TotalRamBytes > 0 && memMb * 1024L * 1024L > TotalRamBytes * 0.75)
                warnings.Add($"分配内存 {memMb} MB 超过物理内存的 75%（本机约 {Math.Max(1, TotalRamBytes / 1024 / 1024 / 1024)} GB），系统可能卡死。");
            if (java.Contains("Program Files (x86)", StringComparison.OrdinalIgnoreCase))
                warnings.Add("当前 Java 是 32 位的，内存最多约 1 GB，大型模组包建议换 64 位 Java。");
            try
            {
                if (!Directory.Exists(natives) || Directory.GetFiles(natives).Length == 0)
                    warnings.Add("natives 目录为空，缺少原生库，游戏可能直接崩溃；重新安装一次这个版本可以修复。");
            }
            catch { }
            if (warnings.Count > 0)
            {
                string text = "启动前检查发现这些问题：\n\n" + string.Join("\n", warnings.Select((w, i) => $"{i + 1}. {w}")) + "\n\n仍要继续启动吗？";
                if (MessageBox.Show(text, "启动前检查", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            var cp = new List<string>(); if (meta.TryGetProperty("libraries", out var libs)) foreach (var lib in libs.EnumerateArray()) if (IsAllowed(lib) && lib.TryGetProperty("downloads", out var dl) && dl.TryGetProperty("artifact", out var art)) { var lp = ResolveLibraryPath(art); if (lp != null && File.Exists(lp)) cp.Add(lp); }
            cp.Add(jarPath);
            string assetIndex = meta.TryGetProperty("assetIndex", out var ai) ? ai.GetProperty("id").GetString()! : selected.Id;
            var vars = new Dictionary<string,string> { ["${natives_directory}"]=natives, ["${launcher_name}"]="MCLauncherRevival", ["${launcher_version}"]="1.2", ["${classpath}"]=string.Join(Path.PathSeparator,cp), ["${classpath_separator}"]=Path.PathSeparator.ToString(), ["${library_directory}"]=Path.Combine(_gameDir,"libraries"), ["${auth_player_name}"]=_username, ["${version_name}"]=selected.Id, ["${game_directory}"]=GameDirForVersion(selected.Id), ["${assets_root}"]=Path.Combine(_gameDir,"assets"), ["${assets_index_name}"]=assetIndex, ["${auth_uuid}"]=_uuid, ["${auth_access_token}"]=_accessToken!, ["${user_type}"]=_userType ?? "legacy", ["${version_type}"]=meta.TryGetProperty("type",out var typ)?typ.GetString()!:"release", ["${user_properties}"]="{}", ["${clientid}"]="", ["${auth_xuid}"]="", ["${auth_session}"]=_accessToken!, ["${game_assets}"]=Path.Combine(_gameDir,"assets","virtual","legacy"), ["${resolution_width}"]="854", ["${resolution_height}"]="480" };
            var args = new List<string>();
            if (meta.TryGetProperty("arguments", out var arguments)) { args.Add("-Xmx"+GetMemoryFor(selected.Id)+"M"); args.AddRange(JvmTuningArgs(javaMajor)); args.Add("-Djava.library.path="+natives); AddArguments(arguments,"jvm",args,vars); if(meta.TryGetProperty("mainClass",out var mc)) args.Add(mc.GetString()!); AddArguments(arguments,"game",args,vars); }
            else
            {
                args.Add("-Djava.library.path=" + natives); args.Add("-cp"); args.Add(vars["${classpath}"]); args.Add("-Xmx" + GetMemory() + "M");
                args.Add("net.minecraft.client.main.Main");
                string legacy = meta.TryGetProperty("minecraftArguments", out var ma) ? ma.GetString() ?? "" : "";
                args.AddRange(legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => ReplaceVars(x, vars)));
                // minecraftArguments 通常已经包含这些参数，只在缺失时补齐，避免同一 flag 出现两次。
                void AddIfMissing(string flag, string value)
                {
                    if (args.Contains(flag)) return;
                    args.Add(flag); args.Add(value);
                }
                AddIfMissing("--version", selected.Id);
                AddIfMissing("--gameDir", GameDirForVersion(selected.Id));
                AddIfMissing("--assetsDir", Path.Combine(_gameDir, "assets"));
                AddIfMissing("--assetIndex", assetIndex);
                AddIfMissing("--uuid", _uuid);
                AddIfMissing("--accessToken", _accessToken!);
                AddIfMissing("--userType", _userType ?? "legacy");
                AddIfMissing("--username", _username);
            }
            if (meta.TryGetProperty("logging",out var logging) && logging.TryGetProperty("client",out var lc) && lc.TryGetProperty("argument",out var la)) { string cfg=Path.Combine(_gameDir,"assets","log_configs",lc.GetProperty("file").GetProperty("id").GetString()??""); if(File.Exists(cfg)) args.Insert(0,ReplaceVars(la.GetString()??"",new Dictionary<string,string>{{"${path}",cfg}})); }
            var psi = new ProcessStartInfo(java) { WorkingDirectory=GameDirForVersion(selected.Id), UseShellExecute=false };
            foreach(var a in args) psi.ArgumentList.Add(a);
            Log("启动命令："+java+" "+string.Join(" ",args.Select(a=>a.Contains(' ')?"[带空格参数]":a)));
            MarkLaunch(selected.Id, false);
            var proc=Process.Start(psi) ?? throw new Exception("Java 进程未能启动。");
            if (safeModsMoved != null)
            {
                try
                {
                    string moved = safeModsMoved;
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, __) => RestoreModsAfterSafeMode(selected.Id, moved);
                }
                catch { }
            }
            // 进程活过一分钟就算这次启动成功，下次不再提示安全模式。
            {
                string launchId = selected.Id;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(60)); if (!proc.HasExited) MarkLaunch(launchId, true); } catch { }
                });
            } SetStatus((_offlineMode ? "Minecraft 已通过离线模式启动，进程 PID=" : "Minecraft 已启动，进程 PID=")+proc.Id); Log("Minecraft 进程已启动 PID="+proc.Id+(_offlineMode?"（离线身份）":"（正版身份）"));
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
    private int GetMemory() => MemoryCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(),out int mb) ? mb : MemoryOptions[2];

    /// <summary>取某个版本实际使用的内存：实例设置优先，其次全局。</summary>
    private int GetMemoryFor(string versionId)
        => _instances.TryGetValue(versionId, out var p) && p.Memory.HasValue ? p.Memory.Value : GetMemory();

    /// <summary>第一次运行时按物理内存给个安全默认值：4 GB 及以下给 2048，其它给 4096。</summary>
    private void ApplyRecommendedMemory()
    {
        int mb = TotalRamBytes > 0 && TotalRamBytes <= 4L * 1024 * 1024 * 1024 ? 2048 : 4096;
        MemoryCombo.SelectedIndex = MemoryIndex(mb.ToString());
    }

    /// <summary>把内存值映射成下拉框下标；未知值回落到默认 4096，不再被 Math.Clamp 截成 1024/8192。</summary>
    /// <summary>按 Java 主版本给一套稳妥的 JVM 参数；PCL 也是这么处理「装完就卡」的问题。</summary>
    private static IEnumerable<string> JvmTuningArgs(int javaMajor)
    {
        if (javaMajor >= 9)
        {
            yield return "-XX:+UseG1GC";
            yield return "-XX:G1NewSizePercent=20";
            yield return "-XX:G1ReservePercent=20";
            yield return "-XX:MaxGCPauseMillis=50";
            yield return "-XX:G1HeapRegionSize=32M";
        }
        else if (javaMajor >= 8)
        {
            yield return "-XX:+UseG1GC";
        }
        yield return "-Dfile.encoding=UTF-8";
    }

    private static int MemoryIndex(string? text)
    {
        int mb = int.TryParse(text, out int parsed) ? parsed : MemoryOptions[2];
        int index = Array.IndexOf(MemoryOptions, mb);
        return index >= 0 ? index : 2;
    }
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
            string modsDir = Path.Combine(GameDirForVersion(SelectedVersionId()), "mods");
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
            remove.Click += (_,__) =>
            {
                if (list.SelectedItem is not string name) return;
                if (MessageBox.Show(window, $"确定删除这个模组文件吗？\n{name}", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                try { File.Delete(Path.Combine(modsDir, name)); Refresh(); Log("已删除模组：" + name); }
                catch (Exception ex) { MessageBox.Show(window, "删除失败：" + ex.Message); }
            };
            var fabric = new Button { Content = "Fabric 官方下载", Margin = new Thickness(0,0,8,0), Padding = new Thickness(12,7,12,7) };
            fabric.Click += (_,__) => Process.Start(new ProcessStartInfo("https://fabricmc.net/use/installer/") { UseShellExecute = true });
            var browse = new Button { Content = "在线搜索 / 下载模组（Modrinth）", Margin = new Thickness(0,0,8,0), Padding = new Thickness(12,7,12,7), Background = System.Windows.Media.Brushes.SteelBlue, Foreground = System.Windows.Media.Brushes.White };
            browse.Click += async (_,__) =>
            {
                var (预设版本, 预设加载器) = GuessTargetFromSelection();
                await OpenModrinthBrowserAsync(modsDir, Refresh, 预设版本, 预设加载器);
            };
            buttons.Children.Add(open); buttons.Children.Add(import); buttons.Children.Add(remove); buttons.Children.Add(forge); buttons.Children.Add(fabric); buttons.Children.Add(browse);
            root.Children.Add(buttons); root.Children.Add(list); window.Content = root; Refresh(); window.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show("打开模组管理失败：" + ex.Message); }
    }

    private sealed record ModrinthHit(string ProjectId, string Title, string Description, string Slug, string Downloads, string IconUrl);

    /// <summary>从当前选中的版本推断「Minecraft 版本 + 加载器」，给模组下载窗口预填。</summary>
    private (string Mc, string Loader) GuessTargetFromSelection()
    {
        string id = SelectedVersionId();
        if (string.IsNullOrWhiteSpace(id)) return ("", "fabric");
        string loader = "fabric";
        string lower = id.ToLowerInvariant();
        if (lower.Contains("neoforge")) loader = "neoforge";
        else if (lower.Contains("forge")) loader = "forge";
        else if (lower.Contains("quilt")) loader = "quilt";
        else if (lower.Contains("fabric")) loader = "fabric";
        try
        {
            // 加载器版本继承自原版，用根版本号当 Minecraft 版本。
            string root = ResolveRootVersion(id);
            if (!string.IsNullOrWhiteSpace(root)) return (root, loader);
        }
        catch { }
        return (id, loader);
    }

    private async Task OpenModrinthBrowserAsync(string modsDir, Action refreshMods, string defaultMc = "1.20.1", string defaultLoader = "fabric")
    {
        var win = new Window { Title = "X · Modrinth 模组下载中心", Width = 980, Height = 690, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White };
        var root = new DockPanel { Margin = new Thickness(20) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "在线模组下载中心", FontSize = 24, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "打开即列出热门模组，也可以按关键词搜索。请确保所选 Minecraft 版本和加载器与游戏一致。", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0,5,0,10) });
        var controls = new WrapPanel(); var query = new TextBox { Width = 300, Padding = new Thickness(8), Text = "" }; var mc = new TextBox { Width = 100, Padding = new Thickness(8), Text = string.IsNullOrWhiteSpace(defaultMc) ? "1.20.1" : defaultMc };
        var loader = new ComboBox { Width = 120, Padding = new Thickness(6), ItemsSource = new[] { "fabric", "forge", "neoforge", "quilt" } };
        loader.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "fabric", "forge", "neoforge", "quilt" }, defaultLoader));
        var search = new Button { Content = "搜索模组", Background = System.Windows.Media.Brushes.SteelBlue, Foreground = System.Windows.Media.Brushes.White };
        controls.Children.Add(new TextBlock { Text = "关键词", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) }); controls.Children.Add(query);
        controls.Children.Add(new TextBlock { Text = "Minecraft", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,4,4,4) }); controls.Children.Add(mc);
        controls.Children.Add(new TextBlock { Text = "加载器", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,4,4,4) }); controls.Children.Add(loader); controls.Children.Add(search);
        top.Children.Add(controls); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var results = new ListView { Margin = new Thickness(0,12,0,12) };
        var grid = new GridView();
        // 每个模组显示自己的图标，方便一眼认出是哪个。
        var iconFactory = new FrameworkElementFactory(typeof(Image));
        iconFactory.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("IconUrl"));
        iconFactory.SetValue(Image.WidthProperty, 30.0);
        iconFactory.SetValue(Image.HeightProperty, 30.0);
        iconFactory.SetValue(Image.StretchProperty, System.Windows.Media.Stretch.Uniform);
        grid.Columns.Add(new GridViewColumn { Header = "图标", Width = 46, CellTemplate = new DataTemplate { VisualTree = iconFactory } });
        grid.Columns.Add(new GridViewColumn { Header = "模组名称", DisplayMemberBinding = new System.Windows.Data.Binding("Title"), Width = 180 });
        grid.Columns.Add(new GridViewColumn { Header = "简介", DisplayMemberBinding = new System.Windows.Data.Binding("Description"), Width = 440 });
        grid.Columns.Add(new GridViewColumn { Header = "下载量", DisplayMemberBinding = new System.Windows.Data.Binding("Downloads"), Width = 100 }); results.View = grid; root.Children.Add(results);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(bottom, Dock.Bottom);
        var install = new Button { Content = "下载并安装选中模组", Background = System.Windows.Media.Brushes.SeaGreen, Foreground = System.Windows.Media.Brushes.White };
        var openProject = new Button { Content = "打开项目网页" }; var status = new TextBlock { Text = "正在加载模组列表……", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10,0,0,0) };
        var progress = new ProgressBar { Width = 170, Height = 14, Margin = new Thickness(8,8,0,0) }; bottom.Children.Add(install); bottom.Children.Add(openProject); bottom.Children.Add(progress); bottom.Children.Add(status); root.Children.Add(bottom);
        List<ModrinthHit> hits = new();
        var sortBox = new ComboBox { Width = 120, Padding = new Thickness(6), ItemsSource = new[] { "按下载量", "按最近更新", "按相关度" }, SelectedIndex = 0 };
        controls.Children.Add(new TextBlock { Text = "排序", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,4,4,4) });
        controls.Children.Add(sortBox);

        // 按当前 Minecraft 版本 + 加载器拼 facets；两者留空就不过滤，直接列全部模组。
        string BuildFacets()
        {
            var parts = new List<string> { "[\"project_type:mod\"]" };
            string mcText = mc.Text.Trim();
            if (mcText.Length > 0) parts.Add($"[\"versions:{mcText}\"]");
            if (loader.SelectedItem is string l && l.Length > 0) parts.Add($"[\"categories:{l}\"]");
            return "[" + string.Join(",", parts) + "]";
        }

        string SortIndex() => sortBox.SelectedIndex switch
        {
            1 => "updated",
            2 => "relevance",
            _ => "downloads"
        };

        async Task RunSearchAsync()
        {
            search.IsEnabled = false; install.IsEnabled = false;
            status.Text = "正在从 Modrinth 加载模组……";
            try
            {
                string url = "https://api.modrinth.com/v2/search?query=" + Uri.EscapeDataString(query.Text.Trim())
                    + "&facets=" + Uri.EscapeDataString(BuildFacets())
                    + "&index=" + SortIndex() + "&limit=50";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("XLauncher/4.0 (https://github.com/ppgg6/MC-Launcher-Revival)");
                using var resp = await Http.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                hits = doc.RootElement.GetProperty("hits").EnumerateArray().Select(x => new ModrinthHit(
                    x.GetProperty("project_id").GetString() ?? "",
                    x.GetProperty("title").GetString() ?? "",
                    x.GetProperty("description").GetString() ?? "",
                    x.GetProperty("slug").GetString() ?? "",
                    x.GetProperty("downloads").ToString(),
                    x.TryGetProperty("icon_url", out var icon) ? icon.GetString() ?? "" : "")).ToList();
                results.ItemsSource = null;
                results.ItemsSource = hits;
                status.Text = hits.Count == 0
                    ? "这个条件下一个模组都没有，试着清空关键词或换加载器。"
                    : $"已加载 {hits.Count} 个模组，选中后点「下载并安装」。";
            }
            catch (Exception ex) { status.Text = "加载失败：" + ex.Message; }
            finally { search.IsEnabled = true; install.IsEnabled = true; }
        }

        search.Click += async (_, __) => await RunSearchAsync();
        sortBox.SelectionChanged += async (_, __) => await RunSearchAsync();
        // 打开窗口就先列出热门模组，不用先搜。
        win.Loaded += async (_, __) => await RunSearchAsync();

        openProject.Click += (_,__) => { if (results.SelectedItem is ModrinthHit hit) Process.Start(new ProcessStartInfo("https://modrinth.com/mod/" + Uri.EscapeDataString(hit.Slug)) { UseShellExecute = true }); };
        install.Click += async (_,__) =>
        {
            if (results.SelectedItem is not ModrinthHit hit) { MessageBox.Show(win, "请先选择一个模组。"); return; }
            if (string.IsNullOrWhiteSpace(mc.Text) || loader.SelectedItem is not string loaderName) { MessageBox.Show(win, "请填写 Minecraft 版本并选择加载器。"); return; }
            install.IsEnabled = false; progress.IsIndeterminate = true; status.Text = "正在查找兼容文件……";
            try
            {
                string versionsUrl = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(hit.ProjectId)}/version?game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { mc.Text.Trim() }))}&loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loaderName }))}";
                using var req = new HttpRequestMessage(HttpMethod.Get, versionsUrl); req.Headers.UserAgent.ParseAdd("XLauncher/4.0 (https://github.com/ppgg6/MC-Launcher-Revival)");
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
        var auto = new CheckBox { Content = "启动器启动时自动检查已安装版本", IsChecked = _autoCheckInstalled, Margin = new Thickness(0,14,0,6) }; panel.Children.Add(auto);
        var note = new TextBlock { Text = "提示：修改游戏目录后，下载和启动页面都会使用新目录。Java 路径需指向 java.exe。", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0,8,0,12) }; panel.Children.Add(note);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; var save = new Button { Content = "保存设置", Padding = new Thickness(18,9,18,9), Background = System.Windows.Media.Brushes.SteelBlue, Foreground = System.Windows.Media.Brushes.White }; var cancel = new Button { Content = "取消", Padding = new Thickness(18,9,18,9) }; buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        save.Click += (_,__) => { try { _autoCheckInstalled = auto.IsChecked == true; _gameDir = dir.Text.Trim(); GameDirectoryText.Text = _gameDir; _javaPath = string.IsNullOrWhiteSpace(java.Text) ? null : java.Text.Trim(); OAuthClientIdText.Text = oauth.Text.Trim(); if (memory.SelectedItem is string m) MemoryCombo.SelectedIndex = MemoryIndex(m); Directory.CreateDirectory(_gameDir); SaveSettings(); SetStatus("设置已保存"); win.Close(); } catch(Exception ex) { MessageBox.Show("保存失败：" + ex.Message); } };
        cancel.Click += (_,__) => win.Close(); win.Content = new ScrollViewer { Content = panel }; win.ShowDialog();
    }

    /// <summary>某个版本实际的游戏目录：隔离版本用 versions/&lt;id&gt;，否则用全局目录。</summary>
    private string GameDirForVersion(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_isolatedVersions.Contains(id)) return _gameDir;
        string dir = Path.Combine(_gameDir, "versions", id);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string SelectedVersionId() => (VersionsList.SelectedItem as VersionRow)?.Id ?? "";

    private bool _syncingIsolation;

    /// <summary>把「版本隔离」勾选框同步成当前选中版本的状态。</summary>
    private void SyncIsolationCheck()
    {
        string id = SelectedVersionId();
        _syncingIsolation = true;
        try
        {
            IsolatedCheck.IsEnabled = !string.IsNullOrWhiteSpace(id);
            IsolatedCheck.IsChecked = !string.IsNullOrWhiteSpace(id) && _isolatedVersions.Contains(id);
        }
        finally { _syncingIsolation = false; }
    }

    private void SetIsolation(bool on)
    {
        if (_syncingIsolation) return;
        string id = SelectedVersionId();
        if (string.IsNullOrWhiteSpace(id)) return;
        if (on) _isolatedVersions.Add(id); else _isolatedVersions.Remove(id);
        try { SaveSettings(); } catch { }
        Log(on ? $"已开启版本隔离：{id}（存档与模组放在 versions/{id}/）" : $"已关闭版本隔离：{id}（回到全局游戏目录）");
    }

    private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XLauncher", "settings.json");
    private void LoadSavedSettings() { try { if (!File.Exists(SettingsPath)) return; using var d = JsonDocument.Parse(File.ReadAllText(SettingsPath)); var r=d.RootElement; if(r.TryGetProperty("gameDir",out var gd)&&Directory.Exists(gd.GetString())) { _gameDir=gd.GetString()!; GameDirectoryText.Text=_gameDir; } if(r.TryGetProperty("javaPath",out var jp)) _javaPath=jp.GetString(); if(r.TryGetProperty("oauthClientId",out var oc)) OAuthClientIdText.Text=oc.GetString()??""; if(r.TryGetProperty("autoCheckInstalled",out var ac)) _autoCheckInstalled=ac.ValueKind==JsonValueKind.True; if(r.TryGetProperty("autoJavaOnInstall",out var aj)) AutoJavaCheck.IsChecked=aj.ValueKind==JsonValueKind.True; if(r.TryGetProperty("instances",out var insts)&&insts.ValueKind==JsonValueKind.Object) _instances=JsonSerializer.Deserialize<Dictionary<string,InstanceProfile>>(insts.GetRawText())??new(); if(r.TryGetProperty("isolatedVersions",out var iv)&&iv.ValueKind==JsonValueKind.Array) { _isolatedVersions.Clear(); foreach(var e in iv.EnumerateArray()) if(e.GetString() is string sid) _isolatedVersions.Add(sid); } if(r.TryGetProperty("memory",out var mm)&&int.TryParse(mm.GetString(),out var mem)) MemoryCombo.SelectedIndex=MemoryIndex(mem.ToString()); if(r.TryGetProperty("versionJavaPaths",out var mappings)&&mappings.ValueKind==JsonValueKind.Object) _versionJavaPaths=mappings.EnumerateObject().Where(x=>x.Value.ValueKind==JsonValueKind.String).ToDictionary(x=>x.Name,x=>x.Value.GetString()!,StringComparer.OrdinalIgnoreCase); } catch(Exception ex) { Log("读取设置失败："+ex.Message); } }
    private void SaveSettings() { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { gameDir=_gameDir, javaPath=_javaPath, oauthClientId=OAuthClientIdText.Text, memory=GetMemory().ToString(), autoCheckInstalled=_autoCheckInstalled, autoJavaOnInstall=AutoJavaCheck.IsChecked==true, isolatedVersions=_isolatedVersions.ToArray(), instances=_instances, versionJavaPaths=_versionJavaPaths }, new JsonSerializerOptions { WriteIndented=true })); }

    private void DownloadPage_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window { Title = "X · 下载游戏", Width = 900, Height = 650, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White };
        var root = new DockPanel { Margin = new Thickness(20) }; var head = new StackPanel(); head.Children.Add(new TextBlock { Text="下载游戏版本", FontSize=24, FontWeight=FontWeights.SemiBold }); head.Children.Add(new TextBlock { Text="在这里选择要下载的 Minecraft 版本；下载完成后会出现在‘启动游戏’页面。", Foreground=System.Windows.Media.Brushes.Gray, Margin=new Thickness(0,5,0,12) }); DockPanel.SetDock(head,Dock.Top); root.Children.Add(head);
        var list = new ListView(); list.DisplayMemberPath = "Id"; root.Children.Add(list);
        // 下载页只显示官方版本：加载器版本（fabric/forge/neoforge）属于已安装版本，不在这里出现。
        var officialVersions = _versions.Where(v => v.Type is "release" or "snapshot" or "old_beta" or "old_alpha").ToList();
        list.ItemsSource = officialVersions;
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
