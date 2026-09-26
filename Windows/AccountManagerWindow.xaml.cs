using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ArkSwitch.Core;
using Growl = HandyControl.Controls.Growl;

namespace ArkSwitch.Windows;

public partial class AccountManagerWindow : HandyControl.Controls.Window
{
    public const string GrowlToken = "ArkSwitchAccounts";

    public AccountManagerWindow()
    {
        InitializeComponent();
        LoadAccounts();
    }

    private AccountItem? SelectedAccount => AccountList.SelectedItem as AccountItem;

    private void LoadAccounts()
    {
        AccountList.Items.Clear();
        var cfg = ConfigStore.Load();

        if (cfg.Accounts.Count == 0)
        {
            cfg.Accounts["A1"] = "默认账号";
            cfg.DefaultAccount = "A1";
            ConfigStore.Save(cfg);
        }

        if (!string.IsNullOrEmpty(cfg.DefaultAccount) && cfg.Accounts.ContainsKey(cfg.DefaultAccount))
            AccountList.Items.Add(new AccountItem { Id = cfg.DefaultAccount, Remark = cfg.Accounts[cfg.DefaultAccount] + " ⭐" });

        foreach (var acc in cfg.Accounts)
        {
            if (acc.Key == cfg.DefaultAccount) continue;
            AccountList.Items.Add(new AccountItem { Id = acc.Key, Remark = acc.Value });
        }

        if (AccountList.Items.Count > 0)
            AccountList.SelectedIndex = 0;
    }

    // ---------- 账号管理 ----------

    private void AccountList_DoubleClick(object sender, MouseButtonEventArgs e) => RenameAccount();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        string? remark = InputDialogWindow.Show("新增账号", "请输入账号备注", owner: this);
        if (string.IsNullOrWhiteSpace(remark)) return;

        var cfg = ConfigStore.Load();
        int index = 1;
        while (cfg.Accounts.ContainsKey("A" + index)) index++;

        cfg.Accounts["A" + index] = remark.Trim();
        ConfigStore.Save(cfg);
        LoadAccounts();
    }

    private void Rename_Click(object sender, RoutedEventArgs e) => RenameAccount();

    private void RenameAccount()
    {
        var selected = SelectedAccount;
        if (selected == null) return;

        string current = selected.Remark.Replace(" ⭐", "");
        string? newRemark = InputDialogWindow.Show($"重命名 {current}", "请输入新的账号备注", current, this);
        if (string.IsNullOrWhiteSpace(newRemark)) return;

        var cfg = ConfigStore.Load();
        cfg.Accounts[selected.Id] = newRemark.Trim();
        ConfigStore.Save(cfg);
        LoadAccounts();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedAccount;
        if (selected == null) return;

        var cfg = ConfigStore.Load();
        if (cfg.Accounts.Count <= 1)
        {
            HandyControl.Controls.MessageBox.Show("至少需要保留一个账号", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);
            return;
        }

        if (HandyControl.Controls.MessageBox.Ask($"确认删除 {selected.Remark.Replace(" ⭐", "")}？其备份数据也会一并删除。", "确认")
            != MessageBoxResult.Yes)
            return;

        cfg.Accounts.Remove(selected.Id);
        if (cfg.DefaultAccount == selected.Id) cfg.DefaultAccount = "";
        ConfigStore.Save(cfg);

        string backupDir = Path.Combine(ConfigStore.AccountBackupDir, selected.Id);
        if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);

        LoadAccounts();
    }

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedAccount;
        if (selected == null) return;

        var cfg = ConfigStore.Load();
        cfg.DefaultAccount = selected.Id;
        ConfigStore.Save(cfg);
        LoadAccounts();
    }

    // ---------- 备份 / 切换 ----------

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedAccount;
        if (selected == null) return;

        if (AccountStore.IsGameRunning()
            && HandyControl.Controls.MessageBox.Ask(
                "游戏正在运行，此时备份可能得到不完整的数据，建议先关闭游戏。\n仍要继续备份吗？", "提示")
                != MessageBoxResult.Yes)
            return;

        BackupBtn.IsEnabled = false;
        try
        {
            bool ok = await AccountStore.BackupCurrentAsync(selected.Id);
            if (ok)
                Growl.Success($"已备份当前登录账号到「{selected.Remark.Replace(" ⭐", "")}」", GrowlToken);
            else
                Growl.Warning("未找到当前登录数据（sdk_data_*），请先用官方启动器启动一次游戏。", GrowlToken);
        }
        catch (Exception ex)
        {
            Growl.Error("备份失败：" + ex.Message, GrowlToken);
        }
        finally
        {
            BackupBtn.IsEnabled = true;
        }
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedAccount;
        if (selected == null) return;

        if (AccountStore.IsGameRunning())
        {
            if (HandyControl.Controls.MessageBox.Ask(
                    "切换账号需要先关闭正在运行的游戏，是否立即结束游戏进程？", "提示")
                != MessageBoxResult.Yes)
                return;

            try
            {
                AccountStore.KillGameProcesses();
            }
            catch (Exception ex)
            {
                Growl.Warning("关闭游戏进程时出错：" + ex.Message, GrowlToken);
                return;
            }
        }

        SwitchBtn.IsEnabled = false;
        try
        {
            string? warning = await AccountStore.SwitchToAccountAsync(selected.Id);
            if (warning == null)
                Growl.Success($"已切换到「{selected.Remark.Replace(" ⭐", "")}」，下次启动游戏即使用该账号", GrowlToken);
            else
                Growl.Warning(warning, GrowlToken);
        }
        catch (Exception ex)
        {
            Growl.Error("切换失败：" + ex.Message, GrowlToken);
        }
        finally
        {
            SwitchBtn.IsEnabled = true;
        }
    }
}
