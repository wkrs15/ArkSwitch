using System.IO;
using System.Windows;
using System.Windows.Controls;
using ArkSwitch.Core;
using ArkSwitch.Windows;
using Growl = HandyControl.Controls.Growl;
using Microsoft.Win32;

namespace ArkSwitch;

public partial class MainWindow : HandyControl.Controls.Window
{
    public const string GrowlToken = "ArkSwitchMain";

    public MainWindow()
    {
        InitializeComponent();
        LoadAccounts();
        RefreshGameDir();

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.IsDevMode) return;
        await AutoCheckPayloadAsync();
    }

    // ---------- 启动时自动检查切服文件 ----------

    private bool _payloadBusy;

    private async Task AutoCheckPayloadAsync()
    {
        var outdated = new List<PayloadUpdater.PayloadProfile>();
        try
        {
            foreach (var profile in PayloadUpdater.All)
            {
                var check = await PayloadUpdater.CheckAsync(profile);
                if (!check.AlreadyCurrent)
                    outdated.Add(profile);
            }
        }
        catch
        {
            return; // 网络异常时静默跳过，可用“更新切服文件”按钮手动重试
        }

        if (outdated.Count == 0 || _payloadBusy) return;
        _payloadBusy = true;
        try
        {
            bool ok = await LaunchProgressWindow.RunAsync("检查到切服文件有更新，正在自动更新…", async status =>
            {
                foreach (var profile in outdated)
                    await PayloadUpdater.UpdateAsync(profile, s => status($"[{profile.DisplayName}] {s}"));
            });
            if (ok)
                Growl.Success($"切服文件已自动更新到最新（{string.Join("、", outdated.Select(x => x.DisplayName))}）", GrowlToken);
        }
        finally
        {
            _payloadBusy = false;
        }
    }

    private void LoadAccounts()
    {
        string selectedId = (AccountCombo.SelectedItem as AccountItem)?.Id ?? ConfigStore.Load().DefaultAccount;

        AccountCombo.Items.Clear();
        var cfg = ConfigStore.Load();

        if (cfg.Accounts.Count == 0)
        {
            cfg.Accounts["A1"] = "默认账号";
            cfg.DefaultAccount = "A1";
            ConfigStore.Save(cfg);
        }

        if (!string.IsNullOrEmpty(cfg.DefaultAccount) && cfg.Accounts.ContainsKey(cfg.DefaultAccount))
            AccountCombo.Items.Add(new AccountItem
            {
                Id = cfg.DefaultAccount,
                Remark = cfg.Accounts[cfg.DefaultAccount] + " ⭐",
                Server = cfg.GetAccountServer(cfg.DefaultAccount)
            });

        foreach (var acc in cfg.Accounts)
        {
            if (acc.Key == cfg.DefaultAccount) continue;
            AccountCombo.Items.Add(new AccountItem
            {
                Id = acc.Key,
                Remark = acc.Value,
                Server = cfg.GetAccountServer(acc.Key)
            });
        }

        for (int i = 0; i < AccountCombo.Items.Count; i++)
        {
            if (AccountCombo.Items[i] is AccountItem item && item.Id == selectedId)
            {
                AccountCombo.SelectedIndex = i;
                return;
            }
        }
        if (AccountCombo.Items.Count > 0)
            AccountCombo.SelectedIndex = 0;
    }

    private void RefreshGameDir()
    {
        string root = ConfigStore.Load().RootPath;
        GameDirBox.Text = GameLauncher.IsValidRootPath(root)
            ? root
            : "";
    }

    private void BrowseGameDir_Click(object sender, RoutedEventArgs e)
    {
        string? picked = SelectGameRootDialog();
        if (picked == null) return;

        var cfg = ConfigStore.Load();
        cfg.RootPath = picked;
        ConfigStore.Save(cfg);
        RefreshGameDir();
    }

    // ---------- 双服启动 ----------

    private async void OfficialBtn_Click(object sender, RoutedEventArgs e) => await LaunchGameAsync(true);

    private async void BilibiliBtn_Click(object sender, RoutedEventArgs e) => await LaunchGameAsync(false);

    private async Task LaunchGameAsync(bool isOfficial)
    {
        if (_payloadBusy)
        {
            Growl.Warning("正在更新切服文件，请稍候再试", GrowlToken);
            return;
        }

        var cfg = ConfigStore.Load();

        try
        {
            GameLauncher.KillArknightsProcesses();
        }
        catch (Exception ex)
        {
            Growl.Warning("关闭 Arknights 进程时出错：" + ex.Message, GrowlToken);
        }

        string rootPath = cfg.RootPath;
        if (!GameLauncher.IsValidRootPath(rootPath))
        {
            string? picked = SelectGameRootDialog();
            if (picked == null) return;
            rootPath = picked;
            cfg = ConfigStore.Load();
            cfg.RootPath = rootPath;
            ConfigStore.Save(cfg);
            RefreshGameDir();
        }

        // 所选账号与所点按钮的服一致时才恢复该账号登录，不一致则保留当前登录
        var account = AccountCombo.SelectedItem as AccountItem;
        string serverName = isOfficial ? "官服" : "B服";

        if (account != null && account.IsBilibili == isOfficial)
        {
            Growl.Warning($"所选账号「{account.Remark.TrimEnd('⭐')}」属于{account.ServerLabel}，本次启动{serverName}将保留当前登录", GrowlToken);
            account = null;
        }

        bool ok = await LaunchProgressWindow.RunAsync($"正在启动{serverName}…", async status =>
        {
            if (account != null)
            {
                status($"正在准备账号「{account.Remark.TrimEnd('⭐')}」…");
                string? warning = await AccountStore.RestoreForLaunchAsync(account.Id);
                if (warning != null) Growl.Warning(warning, GrowlToken);
            }

            await Task.Delay(4000); // 等待游戏进程退出、释放文件锁
            status("正在写入切服文件（同盘优先硬链接）…");
            await Task.Run(() =>
            {
                if (!Directory.Exists(ServerSwitcher.GetPayloadDirectory(isOfficial)))
                {
                    // 本地缺少该服的切服文件时，直接从官方 CDN 拉取
                    var profile = isOfficial ? PayloadUpdater.Official : PayloadUpdater.Bilibili;
                    PayloadUpdater.UpdateAsync(profile, s => status(s)).GetAwaiter().GetResult();
                }

                ServerSwitcher.Apply(rootPath, isOfficial);
            });

            status("正在启动 Arknights…");
            GameLauncher.StartArknights(rootPath);
            await Task.Delay(2500);
        });

        if (ok)
            Growl.Success($"{serverName}已切换并启动游戏", GrowlToken);
    }

    // ---------- 切服文件更新（官方 CDN） ----------

    private async void UpdatePayload_Click(object sender, RoutedEventArgs e)
    {
        if (_payloadBusy)
        {
            Growl.Warning("正在更新切服文件，请稍候", GrowlToken);
            return;
        }

        UpdatePayloadBtn.IsEnabled = false;
        _payloadBusy = true;
        try
        {
            var summary = new List<string>();
            bool ok = await LaunchProgressWindow.RunAsync("正在检查切服文件更新…", async status =>
            {
                foreach (var profile in PayloadUpdater.All)
                {
                    status($"[{profile.DisplayName}] 正在检查…");
                    var result = await PayloadUpdater.UpdateAsync(profile, s => status($"[{profile.DisplayName}] {s}"));
                    summary.Add(result.AlreadyCurrent
                        ? $"[{profile.DisplayName}] 已是最新（v{result.Version}）"
                        : $"[{profile.DisplayName}] 已更新到 v{result.Version}（下载 {result.DownloadedBytes / 1048576.0:F1} MB）");
                }
            });

            if (ok)
                Growl.Success(string.Join("\n", summary), GrowlToken);
        }
        catch (Exception ex)
        {
            Growl.Error("更新切服文件失败：" + ex.Message, GrowlToken);
        }
        finally
        {
            _payloadBusy = false;
            UpdatePayloadBtn.IsEnabled = true;
        }
    }

    internal static string? SelectGameRootDialog()
    {
        while (true)
        {
            var dialog = new OpenFolderDialog { Title = "请选择 Arknights 根目录" };
            if (dialog.ShowDialog() != true) return null;

            string selected = dialog.FolderName;
            if (GameLauncher.IsValidRootPath(selected)) return selected;

            HandyControl.Controls.MessageBox.Show("未找到 'Arknights.exe'，请重新选择游戏根目录", "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK);
        }
    }

    // ---------- 账号管理 ----------

    private void ManageAccounts_Click(object sender, RoutedEventArgs e)
    {
        new AccountManagerWindow { Owner = this }.ShowDialog();
        LoadAccounts();
    }
}
