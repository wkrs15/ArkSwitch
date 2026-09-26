using System.IO;
using System.Text.Json;

namespace ArkSwitch.Core;

public class AppConfig
{
    public string RootPath { get; set; } = "";                          // 方舟根目录路径（含 Arknights.exe）
    public Dictionary<string, string> Accounts { get; set; } = new();   // 账号 ID -> 备注
    public string DefaultAccount { get; set; } = "";                    // 默认账号 ID（如 "A1"）
}

public class AccountItem
{
    public string Id { get; set; } = "";
    public string Remark { get; set; } = "";
    public override string ToString() => Remark;
}

public static class AppVersion
{
    public static readonly Version Current = new("1.1.0.0");
}

public static class ConfigStore
{
    private static readonly string LegacyDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArknightsLauncher");

    public static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArkSwitch");

    public static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");
    public static readonly string AccountBackupDir = Path.Combine(ConfigDir, "AccountBackups");

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
