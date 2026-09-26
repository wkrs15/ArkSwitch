using System.Diagnostics;
using System.IO;

namespace ArkSwitch.Core;

/// <summary>
/// 账号备份与切换：账号数据位于 %USERPROFILE%\AppData\LocalLow\Hypergryph\Arknights\sdk_data_*，
/// 备份保存在 %LOCALAPPDATA%\ArkSwitch\AccountBackups\{账号ID}。
/// </summary>
public static class AccountStore
{
    /// <summary>测试钩子：重定向游戏 sdk 数据根目录（仅自动化测试使用）。</summary>
    internal static string? SdkRootForTest;

    public static string GetArknightsSdkRoot()
        => SdkRootForTest ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Hypergryph", "Arknights");

    /// <summary>定位当前登录的游戏账号数据目录 sdk_data_*，找不到返回 null。</summary>
    public static string? GetSdkDataDir()
    {
        string sdkRoot = GetArknightsSdkRoot();
        if (!Directory.Exists(sdkRoot)) return null;
        return Directory.GetDirectories(sdkRoot, "sdk_data_*").FirstOrDefault();
    }

    public static bool IsGameRunning()
        => Process.GetProcessesByName("Arknights").Length > 0
           || Process.GetProcessesByName("PlatformProcess").Length > 0;

    public static void KillGameProcesses()
    {
        foreach (var proc in Process.GetProcessesByName("Arknights").Concat(Process.GetProcessesByName("PlatformProcess")))
        {
            proc.Kill();
            proc.WaitForExit();
        }
    }

    /// <summary>把当前登录的 sdk_data_* 备份到指定账号槽位。返回是否备份成功。</summary>
    public static async Task<bool> BackupCurrentAsync(string accountId)
    {
        string? sdkDir = GetSdkDataDir();
        if (sdkDir == null) return false;

        string target = Path.Combine(ConfigStore.AccountBackupDir, accountId);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        await CopyDirectoryAsync(sdkDir, target);
        return true;
    }

    /// <summary>
    /// 启动官服前把账号备份恢复到 sdk_data_*（沿用原版语义：没有备份则保留当前登录）。
    /// 返回给用户看的提示信息，一切正常时返回 null。
    /// </summary>
    public static async Task<string?> RestoreForLaunchAsync(string accountId)
    {
        string sdkRoot = GetArknightsSdkRoot();
        if (!Directory.Exists(sdkRoot))
            return "未找到 Arknights 数据目录，请先用官方启动器启动一次游戏。";

        string? sdkDir = GetSdkDataDir();
        if (sdkDir == null)
            return "未找到 sdk_data_* 文件夹，请先启动游戏到账号输入界面后再关闭游戏。";

        string backup = Path.Combine(ConfigStore.AccountBackupDir, accountId);
        if (!Directory.Exists(backup) || Directory.GetFiles(backup, "*", SearchOption.AllDirectories).Length == 0)
            return null; // 该账号从未备份过，保留当前登录状态

        await Task.Delay(3000); // 等待游戏进程完全退出，避免文件占用
        await CopyDirectoryAsync(backup, sdkDir);
        return null;
    }

    /// <summary>
    /// 用选中账号的备份覆盖当前登录数据（下次启动游戏即登录该账号）。
    /// 返回给用户看的提示信息，一切正常时返回 null。
    /// </summary>
    public static async Task<string?> SwitchToAccountAsync(string accountId)
    {
        string sdkRoot = GetArknightsSdkRoot();
        if (!Directory.Exists(sdkRoot))
            return "未找到 Arknights 数据目录，请先用官方启动器启动一次游戏。";

        string? sdkDir = GetSdkDataDir();
        if (sdkDir == null)
            return "未找到 sdk_data_* 文件夹，请先启动游戏到账号输入界面后再关闭游戏。";

        string backup = Path.Combine(ConfigStore.AccountBackupDir, accountId);
        if (!Directory.Exists(backup) || Directory.GetFiles(backup, "*", SearchOption.AllDirectories).Length == 0)
            return "该账号还没有备份数据，请先关闭游戏后点击「备份当前账号」。";

        await CopyDirectoryAsync(backup, sdkDir);
        return null;
    }

    public static async Task CopyDirectoryAsync(string sourceDir, string targetDir, int maxRetries = 5)
    {
        sourceDir = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar);
        targetDir = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar);

        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
        {
            string relativePath = file.Substring(sourceDir.Length + 1);
            string destFile = Path.Combine(targetDir, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    File.Copy(file, destFile, true);
                    break;
                }
                catch (IOException) when (i < maxRetries - 1)
                {
                    await Task.Delay(1000); // 游戏未完全退出时文件可能被占用，等待重试
                }
            }
        }
    }
}
