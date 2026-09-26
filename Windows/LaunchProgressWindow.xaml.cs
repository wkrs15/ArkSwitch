using System.Windows;

namespace ArkSwitch.Windows;

public partial class LaunchProgressWindow : Window
{
    private LaunchProgressWindow()
    {
        InitializeComponent();
    }

    private void SetStatus(string text) => Dispatcher.Invoke(() => StatusText.Text = text);

    /// <summary>显示进度窗口并执行启动流程；work 内抛出的异常会以弹窗展示，返回 false。</summary>
    public static async Task<bool> RunAsync(string initialText, Func<Action<string>, Task> work)
    {
        var dlg = new LaunchProgressWindow
        {
            Owner = Application.Current.MainWindow
        };
        dlg.StatusText.Text = initialText;
        dlg.Show();
        dlg.Activate();

        try
        {
            await work(dlg.SetStatus);
            return true;
        }
        catch (Exception ex)
        {
            HandyControl.Controls.MessageBox.Show(ex.Message, "启动失败",
                MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK);
            return false;
        }
        finally
        {
            dlg.Close();
        }
    }
}
