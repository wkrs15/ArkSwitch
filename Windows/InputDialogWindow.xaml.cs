using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ArkSwitch.Windows;

public partial class InputDialogWindow : HandyControl.Controls.Window
{
    public string InputValue => InputBox.Text.Trim();

    /// <summary>服务器选择器的当前值："Official" / "Bilibili"。</summary>
    public string SelectedServer => (ServerCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Official";

    private InputDialogWindow(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    /// <summary>显示输入对话框，返回输入内容；取消时返回 null。</summary>
    public static string? Show(string title, string prompt, string initial = "", Window? owner = null)
    {
        var dlg = new InputDialogWindow(title, prompt, initial) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.InputValue : null;
    }

    /// <summary>显示带服务器选择的输入对话框（新增/重命名账号用），取消时返回 null。</summary>
    public static (string Remark, string Server)? ShowWithServer(
        string title, string prompt, string initial, string server, Window? owner = null)
    {
        var dlg = new InputDialogWindow(title, prompt, initial) { Owner = owner };
        dlg.ShowServerSelector(server);
        return dlg.ShowDialog() == true ? (dlg.InputValue, dlg.SelectedServer) : null;
    }

    private void ShowServerSelector(string server)
    {
        ServerPanel.Visibility = Visibility.Visible;
        Height += 36;
        ServerCombo.Items.Add(new ComboBoxItem { Content = "官服", Tag = "Official" });
        ServerCombo.Items.Add(new ComboBoxItem { Content = "B服", Tag = "Bilibili" });
        ServerCombo.SelectedIndex = server == "Bilibili" ? 1 : 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
        else if (e.Key == Key.Escape) DialogResult = false;
    }
}
