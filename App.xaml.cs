using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace MC_Launcher_Revival;

public partial class App : Application
{
    private static readonly object LogLock = new();

    internal static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XLauncher", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteLogLine("未处理异常：" + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteLogLine("未观察的任务异常：" + args.Exception);
            args.SetObserved();
        };
        WriteLogLine("X 启动器启动，版本 " + typeof(App).Assembly.GetName().Version);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLogLine("界面线程异常：" + e.Exception);
        MessageBox.Show(
            "启动器遇到一个未处理的错误，详细信息已写入日志：\n" + LogDirectory + "\n\n" + e.Exception.Message,
            "X 启动器错误", MessageBoxButton.OK, MessageBoxImage.Error);
        // 启动器以文件操作和网络请求为主，单次异常后继续运行比直接闪退对用户更友好。
        e.Handled = true;
    }

    /// <summary>把一行日志追加到 %AppData%/XLauncher/logs/launcher-yyyyMMdd.log；失败时静默忽略。</summary>
    internal static void WriteLogLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(
                    Path.Combine(LogDirectory, $"launcher-{DateTime.Now:yyyyMMdd}.log"),
                    $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不进去不能影响启动器本身。
        }
    }
}
