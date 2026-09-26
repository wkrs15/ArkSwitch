using System.Security.Cryptography;
using ArkSwitch.Core;

// ArkSwitch 核心逻辑自动化测试
//   --fake  沙箱测试：全部使用临时目录，不触碰真实数据（CI 使用）
//   --real  真机非破坏性验证：备份当前登录数据 → MD5 校验 → 恒等恢复（要求本机有 sdk_data 且游戏未运行）

int failures = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $"  ({detail})" : "")}");
    if (!ok) failures++;
}

static string Md5(string path)
{
    using var md5 = System.Security.Cryptography.MD5.Create();
    using var s = File.OpenRead(path);
    return Convert.ToHexString(md5.ComputeHash(s));
}

if (args.Contains("--real"))
{
    // ---------- 真机非破坏性验证 ----------
    Console.WriteLine("== 真机验证（非破坏性：备份 → 校验 → 恒等恢复）==");

    string? sdkDir = AccountStore.GetSdkDataDir();
    Check("真机存在 sdk_data_*", sdkDir != null);
    if (sdkDir == null) return 1;

    Check("游戏未在运行", !AccountStore.IsGameRunning());
    if (AccountStore.IsGameRunning()) return 1;

    var sdkFiles = Directory.GetFiles(sdkDir, "*", SearchOption.AllDirectories);
    Check("sdk_data 内有文件", sdkFiles.Length > 0, $"{sdkFiles.Length} 个文件");
    var before = sdkFiles.ToDictionary(f => f.Substring(sdkDir.Length + 1), Md5);

    Check("备份当前登录", AccountStore.BackupCurrentAsync("REALTEST").Result, "槽位 REALTEST");

    string backupDir = Path.Combine(ConfigStore.AccountBackupDir, "REALTEST");
    var backupFiles = Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories);
    Check("备份文件数量一致", backupFiles.Length == sdkFiles.Length,
        $"备份 {backupFiles.Length} / 源 {sdkFiles.Length}");
    Check("备份内容 MD5 一致",
        backupFiles.All(f => before.TryGetValue(f.Substring(backupDir.Length + 1), out var h) && Md5(f) == h));

    // 恒等恢复：把刚备份的内容原样写回 sdk_data（内容不变，仅验证恢复链路）
    Check("恢复到 sdk_data（恒等）", AccountStore.SwitchToAccountAsync("REALTEST").Result == null);

    var after = Directory.GetFiles(sdkDir, "*", SearchOption.AllDirectories)
        .ToDictionary(f => f.Substring(sdkDir.Length + 1), Md5);
    Check("恢复后 sdk_data 内容不变", after.Count == before.Count &&
        before.All(kv => after.TryGetValue(kv.Key, out var h) && h == kv.Value));

    if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);
    Console.WriteLine(failures == 0 ? "REAL TESTS PASSED" : "REAL TESTS FAILED");
    return failures == 0 ? 0 : 1;
}

// ---------- 沙箱测试（不触碰任何真实数据） ----------
Console.WriteLine("== 沙箱测试（临时目录）==");

string tempRoot = Path.Combine(Path.GetTempPath(), "arkswitch_tests_" + Guid.NewGuid().ToString("N"));
ConfigStore.ConfigDirForTest = Path.Combine(tempRoot, "config");
AccountStore.SdkRootForTest = Path.Combine(tempRoot, "lowlink", "Hypergryph", "Arknights");

try
{
    // ---- 账号备份 / 切换 ----
    string sdkRoot = AccountStore.GetArknightsSdkRoot();
    string sdkData = Path.Combine(sdkRoot, "sdk_data_abc123");
    Directory.CreateDirectory(sdkData);
    File.WriteAllText(Path.Combine(sdkData, "token.bin"), "login-token-A");
    Directory.CreateDirectory(Path.Combine(sdkData, "sub"));
    File.WriteAllText(Path.Combine(sdkData, "sub", "pref.bin"), "pref-A");

    Check("GetSdkDataDir 定位到 sdk_data_*", AccountStore.GetSdkDataDir() == sdkData);
    Check("未运行游戏时 IsGameRunning=false", !AccountStore.IsGameRunning());

    Check("备份到 A1", AccountStore.BackupCurrentAsync("A1").Result);
    string backupFile = Path.Combine(ConfigStore.AccountBackupDir, "A1", "token.bin");
    Check("备份内容正确", File.ReadAllText(backupFile) == "login-token-A");

    // 模拟换了另一个账号登录
    File.WriteAllText(Path.Combine(sdkData, "token.bin"), "login-token-B");
    File.Delete(Path.Combine(sdkData, "sub", "pref.bin"));

    var warn = AccountStore.SwitchToAccountAsync("A1").Result;
    Check("切换回 A1 无警告", warn == null, warn ?? "");
    Check("切换后登录数据恢复为 A1", File.ReadAllText(Path.Combine(sdkData, "token.bin")) == "login-token-A");
    Check("切换后子目录文件也恢复", File.ReadAllText(Path.Combine(sdkData, "sub", "pref.bin")) == "pref-A");

    warn = AccountStore.SwitchToAccountAsync("A2").Result;
    Check("未备份的账号切换给出警告", warn != null && warn.Contains("备份"), warn ?? "");

    // ---- 异常路径 ----
    AccountStore.SdkRootForTest = Path.Combine(tempRoot, "empty");
    Check("无数据目录时备份失败", !AccountStore.BackupCurrentAsync("A1").Result);
    warn = AccountStore.RestoreForLaunchAsync("A1").Result;
    Check("无数据目录时恢复给出提示", warn != null && warn.Contains("未找到"));

    AccountStore.SdkRootForTest = sdkRoot;
    Directory.Delete(sdkData, true);
    warn = AccountStore.RestoreForLaunchAsync("A1").Result;
    Check("有备份但缺 sdk_data 时恢复给出提示", warn != null && warn.Contains("sdk_data"), warn ?? "");

    // ---- 配置存取与账号服务器映射 ----
    var cfg = new AppConfig();
    cfg.Accounts["A1"] = "主号";
    cfg.SetAccountServer("A1", "Bilibili");
    cfg.Accounts["A2"] = "小号";
    cfg.RootPath = @"C:\Games\Arknights";
    ConfigStore.Save(cfg);

    var loaded = ConfigStore.Load();
    Check("配置读写一致", loaded.Accounts["A1"] == "主号" && loaded.RootPath == @"C:\Games\Arknights");
    Check("账号服务器标记持久化", loaded.GetAccountServer("A1") == "Bilibili");
    Check("未标记的账号缺省为官服", loaded.GetAccountServer("A2") == "Official");

    // ---- 切服文件复制/硬链接 ----
    string src = Path.Combine(tempRoot, "payload_src");
    string dst = Path.Combine(tempRoot, "payload_dst");
    Directory.CreateDirectory(Path.Combine(src, "U8Data", "config"));
    File.WriteAllText(Path.Combine(src, "U8SDK.dll"), "sdk-content");
    File.WriteAllText(Path.Combine(src, "U8Data", "config", "config.bin"), "cfg-content");

    bool hardLinked = ServerSwitcher.HardLinkOrCopyDirectory(src, dst);
    Check("切服文件覆盖后内容一致",
        File.ReadAllText(Path.Combine(dst, "U8SDK.dll")) == "sdk-content" &&
        File.ReadAllText(Path.Combine(dst, "U8Data", "config", "config.bin")) == "cfg-content",
        hardLinked ? "硬链接" : "复制");
    Check("同盘应使用硬链接", hardLinked);
}
finally
{
    try { Directory.Delete(tempRoot, true); } catch { }
}

Console.WriteLine(failures == 0 ? "ALL FAKE TESTS PASSED" : "FAKE TESTS FAILED");
return failures == 0 ? 0 : 1;
