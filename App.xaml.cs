using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArkSwitch.Core;
using ArkSwitch.Windows;

namespace ArkSwitch;

public partial class App : Application
{
    public static bool IsDevMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            LogCrash(a.ExceptionObject as Exception ?? new Exception(a.ExceptionObject?.ToString()));

        try
        {
            StartupCore(e);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            Shutdown(2);
        }
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), "arkswitch_crash.log");
            File.AppendAllText(logPath,
                DateTime.Now + " ===============================" + Environment.NewLine + ex + Environment.NewLine);
        }
        catch { }
    }

    private void StartupCore(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            HandyControl.Controls.MessageBox.Show(args.Exception.ToString(), "未处理异常",
                MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK);
            args.Handled = true;
        };

        // 开发辅助模式
        string[] args = e.Args;
        int dumpIndex = Array.IndexOf(args, "--dump-api");
        if (dumpIndex >= 0 && dumpIndex + 1 < args.Length)
        {
            try
            {
                DumpApi(args[dumpIndex + 1]);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(args[dumpIndex + 1] + ".err.txt", ex.ToString()); } catch { }
            }
            Shutdown();
            return;
        }

        IsDevMode = args.Contains("--devshot");

        // 开发测试：--update-payload <日志文件> 实际执行官方 CDN 切服文件更新后退出
        int updateIndex = Array.IndexOf(args, "--update-payload");
        if (updateIndex >= 0 && updateIndex + 1 < args.Length)
        {
            RunPayloadUpdateTestAsync(args[updateIndex + 1]);
            return;
        }

        ConfigStore.EnsureMigrated();
        Directory.CreateDirectory(ConfigStore.AccountBackupDir);

        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        int shotIndex = Array.IndexOf(args, "--devshot");
        if (shotIndex >= 0 && shotIndex + 1 < args.Length)
            RunDevShotAsync(main, args[shotIndex + 1]);
    }

    private static async void RunPayloadUpdateTestAsync(string logPath)
    {
        var log = new StringBuilder();
        try
        {
            foreach (var profile in PayloadUpdater.All)
            {
                log.AppendLine($"== {profile.DisplayName} ==");
                var result = await PayloadUpdater.UpdateAsync(profile, s => log.AppendLine("  " + s));
                log.AppendLine($"  结果: version={result.Version}, files={result.FileCount}, " +
                               $"downloaded={result.DownloadedBytes}, alreadyCurrent={result.AlreadyCurrent}");
            }
            log.AppendLine("SUCCESS");
        }
        catch (Exception ex)
        {
            log.AppendLine("FAILED: " + ex);
        }
        try { File.WriteAllText(logPath, log.ToString()); } catch { }
        Application.Current.Shutdown();
    }

    private static async void RunDevShotAsync(MainWindow main, string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            await Task.Delay(1200);
            await ShotWindowAsync(main, Path.Combine(dir, "main.png"));

            // 弹出通知后再次截图，验证布局不被挤压
            HandyControl.Controls.Growl.Success("这是一条测试通知，用于验证布局不被挤压", ArkSwitch.MainWindow.GrowlToken);
            await Task.Delay(800);
            await ShotWindowAsync(main, Path.Combine(dir, "main_growl.png"));

            var accounts = new AccountManagerWindow();
            accounts.Show();
            await Task.Delay(500);
            await ShotWindowAsync(accounts, Path.Combine(dir, "accounts.png"));
            accounts.Close();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static async Task ShotWindowAsync(Window window, string path)
    {
        window.UpdateLayout();
        await Task.Delay(250);
        double dpi = VisualTreeHelper.GetDpi(window).PixelsPerDip;
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(window.ActualWidth * dpi),
            (int)Math.Ceiling(window.ActualHeight * dpi),
            96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        rtb.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>导出 HandyControls 的关键类型 API 与主题资源键，供开发期核对，不参与正常启动。</summary>
    private static void DumpApi(string path)
    {
        var sb = new StringBuilder();
        var asm = typeof(HandyControl.Controls.Growl).Assembly;

        foreach (var typeName in new[]
                 {
                     "HandyControl.Controls.NotifyIcon", "HandyControl.Controls.Growl",
                     "HandyControl.Controls.MessageBox"
                 })
        {
            var type = asm.GetType(typeName);
            if (type == null)
            {
                sb.AppendLine($"!! MISSING TYPE {typeName}");
                continue;
            }

            sb.AppendLine($"== {typeName} ==");
            foreach (var m in type.GetMethods().Where(m => !m.IsSpecialName && m.DeclaringType == type))
                sb.AppendLine($"  method {m}");
        }

        sb.AppendLine();
        sb.AppendLine("== Theme resource keys ==");
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        CollectKeys(Application.Current.Resources, keys);
        foreach (string key in keys)
            sb.AppendLine($"  {key}");

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void CollectKeys(ResourceDictionary dict, SortedSet<string> keys)
    {
        foreach (var key in dict.Keys)
            keys.Add(key.ToString() ?? "");
        foreach (var merged in dict.MergedDictionaries)
            CollectKeys(merged, keys);
    }
}
