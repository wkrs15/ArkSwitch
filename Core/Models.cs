using System.IO;
using System.Text.Json;

namespace ArkSwitch.Core;

public class AppConfig
{
    public string RootPath { get; set; } = "";                          // 方舟根目录路径（含 Arknights.exe）
    public Dictionary<string, string> Accounts { get; set; } = new();   // 账号 ID -> 备注
    public Dictionary<string, string> AccountServers { get; set; } = new(); // 账号 ID -> 所属服务器（Official/Bilibili）
    public string DefaultAccount { get; set; } = "";                    // 默认账号 ID（如 "A1"）

    public string GetAccountServer(string accountId)
        => AccountServers.TryGetValue(accountId, out var server) && server == "Bilibili"
            ? "Bilibili"
            : "Official";

    public void SetAccountServer(string accountId, string server)
        => AccountServers[accountId] = server == "Bilibili" ? "Bilibili" : "Official";
}

public class AccountItem
{
    public string Id { get; set; } = "";
    public string Remark { get; set; } = "";
    public string Server { get; set; } = "Official";   // "Official" / "Bilibili"

    public bool IsBilibili => Server == "Bilibili";
    public string ServerLabel => IsBilibili ? "B服" : "官服";

    public override string ToString() => $"{Remark} [{ServerLabel}]";
}

public static class AppVersion
{
    /// <summary>当前版本号，直接取程序集版本（即 csproj 的 &lt;Version&gt;），避免与发布版本脱节。</summary>
    public static readonly Version Current =
        typeof(AppVersion).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
}

public static class ConfigStore
{
    private static readonly string LegacyDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArknightsLauncher");

    /// <summary>测试钩子：重定向配置目录（仅自动化测试使用）。</summary>
    internal static string? ConfigDirForTest { get; set; }

    public static string ConfigDir => ConfigDirForTest ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArkSwitch");

    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public static string AccountBackupDir => Path.Combine(ConfigDir, "AccountBackups");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>首次运行时尝试从旧版 ArknightsLauncher 迁移配置与账号备份。</summary>
    public static void EnsureMigrated()
    {
        if (File.Exists(ConfigFile) || !File.Exists(Path.Combine(LegacyDir, "config.json")))
            return;

        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.Copy(Path.Combine(LegacyDir, "config.json"), ConfigFile);

            string legacyBackups = Path.Combine(LegacyDir, "AccountBackups");
            if (Directory.Exists(legacyBackups) && !Directory.Exists(AccountBackupDir))
                CopyDirectory(legacyBackups, AccountBackupDir);
        }
        catch
        {
            // 迁移失败不影响首次运行
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigFile)) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(cfg, JsonOpts));
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = file.Substring(sourceDir.Length + 1);
            string dest = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }
}
