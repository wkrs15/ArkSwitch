using System.Windows;
using ArkSwitch.Core;

namespace ArkSwitch.Windows;

/// <summary>启动游戏时选择本次使用的账号；列表首项为“保留当前登录”。</summary>
public partial class AccountPickerWindow : HandyControl.Controls.Window
{
    /// <summary>选中的账号；选择了“保留当前登录”时为 null。</summary>
    public AccountItem? SelectedAccount => AccountList.SelectedItem as AccountItem;

    public AccountPickerWindow(bool isOfficial, string? preselectId)
    {
        InitializeComponent();

        string serverLabel = isOfficial ? "官服" : "B服";
        Title = $"选择账号（{serverLabel}）";
        HintText.Text = $"选择本次启动{serverLabel}使用的账号";

        var cfg = ConfigStore.Load();
        string server = isOfficial ? "Official" : "Bilibili";

        AccountList.Items.Add("保留当前登录（不切换账号）");

        void AddAccount(string id, string remark, bool isDefault)
        {
            if (cfg.GetAccountServer(id) != server) return;
            AccountList.Items.Add(new AccountItem
            {
                Id = id,
                Remark = isDefault ? remark + " ⭐" : remark,
                Server = server
            });
        }

        if (!string.IsNullOrEmpty(cfg.DefaultAccount) && cfg.Accounts.ContainsKey(cfg.DefaultAccount))
            AddAccount(cfg.DefaultAccount, cfg.Accounts[cfg.DefaultAccount], isDefault: true);

        foreach (var acc in cfg.Accounts)
        {
            if (acc.Key == cfg.DefaultAccount) continue;
            AddAccount(acc.Key, acc.Value, isDefault: false);
        }

        // 预选：与传入账号 ID 匹配的项，否则“保留当前登录”
        var preselect = 0;
        for (int i = 0; i < AccountList.Items.Count; i++)
        {
            if (AccountList.Items[i] is AccountItem item && item.Id == preselectId)
            {
                preselect = i;
                break;
            }
        }
        AccountList.SelectedIndex = preselect;

        Loaded += (_, _) => AccountList.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
