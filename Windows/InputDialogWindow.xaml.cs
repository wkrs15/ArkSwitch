using System.Windows;
using System.Windows.Input;

namespace ArkSwitch.Windows;

public partial class InputDialogWindow : HandyControl.Controls.Window
{
    public string InputValue => InputBox.Text.Trim();

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

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
        else if (e.Key == Key.Escape) DialogResult = false;
    }
}
