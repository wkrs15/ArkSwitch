using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ArkSwitch.Core;

/// <summary>
/// 切服核心：把 load/ArkOfficial 或 load/ArkBilibili 的 SDK 文件覆盖到游戏根目录。
/// 同一磁盘时优先使用硬链接，避免重复读写；失败时回退为复制。
/// </summary>
public static class ServerSwitcher
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    public static string GetPayloadDirectory(bool isOfficial)
        => Path.Combine(AppContext.BaseDirectory, "load", isOfficial ? "ArkOfficial" : "ArkBilibili");

    /// <summary>应用切服文件，返回是否全程使用了硬链接。</summary>
    public static bool Apply(string targetRoot, bool isOfficial)
    {
        string sourceRoot = GetPayloadDirectory(isOfficial);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"未找到切服文件目录: {sourceRoot}");

        return HardLinkOrCopyDirectory(sourceRoot, targetRoot);
    }

    public static bool HardLinkOrCopyDirectory(string sourceRoot, string targetRoot)
    {
        sourceRoot = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        targetRoot = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar);

        Directory.CreateDirectory(targetRoot);
        bool sameVolume = OnSameVolume(sourceRoot, targetRoot);
        bool usedHardLinkForAllFiles = sameVolume;

        foreach (var sourceFile in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relativePath = sourceFile.Substring(sourceRoot.Length + 1);
            string targetFile = Path.Combine(targetRoot, relativePath);
            string? targetDir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            const int maxRetry = 5;
            for (int i = 0; i < maxRetry; i++)
            {
                try
                {
                    if (!HardLinkOrCopyFile(sourceFile, targetFile, sameVolume))
                        usedHardLinkForAllFiles = false;
                    break;
                }
                catch (IOException) when (i < maxRetry - 1)
                {
                    Thread.Sleep(1000);
                }
            }
        }

        return usedHardLinkForAllFiles;
    }

    private static bool OnSameVolume(string pathA, string pathB)
    {
        string? rootA = Path.GetPathRoot(Path.GetFullPath(pathA));
        string? rootB = Path.GetPathRoot(Path.GetFullPath(pathB));
        return string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HardLinkOrCopyFile(string sourceFile, string targetFile, bool sameVolume)
    {
        if (sameVolume && TryReplaceWithHardLink(sourceFile, targetFile))
            return true;

        ReplaceFileByCopy(sourceFile, targetFile);
        return false;
    }

    private static bool TryReplaceWithHardLink(string sourceFile, string targetFile)
    {
        try
        {
            DeleteTargetFile(targetFile);
            return CreateHardLink(targetFile, sourceFile, IntPtr.Zero);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ReplaceFileByCopy(string sourceFile, string targetFile)
    {
        DeleteTargetFile(targetFile);
        File.Copy(sourceFile, targetFile, true);
    }

    private static void DeleteTargetFile(string targetFile)
    {
        if (!File.Exists(targetFile))
            return;

        File.SetAttributes(targetFile, FileAttributes.Normal);
        File.Delete(targetFile);
    }
}

public static class GameLauncher
{
    public static bool IsValidRootPath(string? rootPath)
        => !string.IsNullOrWhiteSpace(rootPath)
           && Directory.Exists(rootPath)
           && File.Exists(Path.Combine(rootPath, "Arknights.exe"));

    public static void StartArknights(string rootPath)
    {
        string exePath = Path.Combine(rootPath, "Arknights.exe");
        if (!File.Exists(exePath)) throw new FileNotFoundException("未找到 Arknights.exe");

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = rootPath,
            UseShellExecute = true
        });
    }

    public static void KillArknightsProcesses()
    {
        foreach (var proc in Process.GetProcessesByName("Arknights").Concat(Process.GetProcessesByName("PlatformProcess")))
        {
            proc.Kill();
            proc.WaitForExit();
        }
    }
}
