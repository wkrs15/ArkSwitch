using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArkSwitch.Core;

/// <summary>
/// 从鹰角官方启动器 API + 官方 CDN 更新切服差异文件（load/ArkOfficial、load/ArkBilibili）。
/// 机制移植自 XEL-Launcher（Apache-2.0，github.com/lTinchl/Xel-Launcher）：
/// 调用 launcher.hypergryph.com batch_proxy 获取最新版本与资源基址，
/// 下载并解密 game_files 清单（AES-256-CBC），按服务器白名单过滤出渠道 SDK 差异文件，
/// 逐文件 MD5 校验后暂存、原子替换到 load 目录。已有相同 MD5 的文件会复用，避免重复下载。
/// </summary>
public static class PayloadUpdater
{
    private const long MiB = 1024L * 1024L;
    private const string DomesticApiUrl = "https://launcher.hypergryph.com/api/proxy/batch_proxy";
    private const string DomesticLauncherAppCode = "abYeZZ16BPluCFyT";
    private const string ArknightsAppCode = "GzD1CpaWgmSq1wew";
    private const string Sequence = "5";

    private static readonly string[] CommonRootFiles = { "U8CoreUI.dll", "U8SDK.dll", "u8_channel.dll" };
    private static readonly string[] CommonRequiredFiles =
    {
        "U8CoreUI.dll", "U8SDK.dll", "u8_channel.dll",
        "U8Data/config/config.bin", "U8Data/config/config.gryph", "U8Data/config/u8ExtraConfig.bin"
    };

    public sealed class PayloadProfile
    {
        public string DisplayName { get; }
        public string PayloadDirectoryName { get; }
        public string Channel { get; }
        public string SubChannel { get; }
        public IReadOnlyList<string> RootFiles { get; }
        public IReadOnlyList<string> DirectoryPrefixes { get; }
        public IReadOnlyList<string> RequiredFiles { get; }
        public int MaxFileCount { get; }
        public long MaxTotalBytes { get; }
        public long MaxSingleFileBytes { get; }

        internal PayloadProfile(
            string displayName, string payloadDirectoryName, string channel, string subChannel,
            IEnumerable<string> extraRootFiles, IEnumerable<string> directoryPrefixes,
            IEnumerable<string> extraRequiredFiles, bool bilibiliLimits)
        {
            DisplayName = displayName;
            PayloadDirectoryName = payloadDirectoryName;
            Channel = channel;
            SubChannel = subChannel;
            RootFiles = CommonRootFiles.Concat(extraRootFiles).ToArray();
            DirectoryPrefixes = directoryPrefixes.ToArray();
            RequiredFiles = CommonRequiredFiles.Concat(extraRequiredFiles).ToArray();
            MaxFileCount = bilibiliLimits ? 1000 : 128;
            MaxTotalBytes = bilibiliLimits ? 512 * MiB : 128 * MiB;
            MaxSingleFileBytes = bilibiliLimits ? 256 * MiB : 64 * MiB;
        }
    }

    public static readonly PayloadProfile Official = new(
        "官服", "ArkOfficial", "1", "1",
        new[] { "hgsdk.dll", "PlatformProcess.dll", "PlatformProcess.exe", "webviewsdk.dll" },
        new[] { "sdkdata", "U8Data/config" },
        new[] { "hgsdk.dll", "PlatformProcess.dll", "PlatformProcess.exe", "webviewsdk.dll" },
        bilibiliLimits: false);

    public static readonly PayloadProfile Bilibili = new(
        "B服", "ArkBilibili", "2", "2",
        new[] { "PCGameSDK.dll", "PlatformProcess.dll", "PlatformProcess.exe", "webviewsdk.dll" },
        new[] { "BLPlatform64", "U8Data/config" },
        new[] { "PCGameSDK.dll", "PlatformProcess.dll", "PlatformProcess.exe", "webviewsdk.dll", "BLPlatform64/PCGamePlatform.exe" },
        bilibiliLimits: true);

    public static readonly IReadOnlyList<PayloadProfile> All = new[] { Official, Bilibili };

    public static string PayloadRoot => Path.Combine(AppContext.BaseDirectory, "load");
    private static string StateRoot => Path.Combine(PayloadRoot, ".state");

    public static string GetPayloadDirectory(PayloadProfile profile)
        => Path.Combine(PayloadRoot, profile.PayloadDirectoryName);

    private static readonly HttpClient ApiClient = CreateClient(TimeSpan.FromSeconds(30));
    private static readonly HttpClient DownloadClient = CreateClient(TimeSpan.FromMinutes(30));

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ArkSwitch/{AppVersion.Current}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public sealed class UpdateResult
    {
        public string Version { get; init; } = "";
        public int FileCount { get; init; }
        public long DownloadedBytes { get; init; }
        public bool AlreadyCurrent { get; init; }
    }

    public sealed class CheckResult
    {
        public string Version { get; init; } = "";
        public int FileCount { get; init; }
        public long NeededBytes { get; init; }
        public bool AlreadyCurrent { get; init; }
    }

    /// <summary>只检查是否需要更新（查询官方 API、比对本地文件），不下载。</summary>
    public static async Task<CheckResult> CheckAsync(
        PayloadProfile profile, CancellationToken cancellationToken = default)
    {
        var (version, resourceBase) = await GetLatestPackageAsync(profile, cancellationToken);
        var selectedFiles = await GetSelectedFilesAsync(profile, resourceBase, cancellationToken);

        var payloadDirectory = GetPayloadDirectory(profile);
        long neededBytes = 0;
        foreach (var node in selectedFiles)
        {
            var localPath = SafeCombine(payloadDirectory, node.RelativePath);
            if (!File.Exists(localPath) || !await HasExpectedMd5Async(localPath, node.Md5, cancellationToken))
                neededBytes += node.Size;
        }

        var state = GetState(profile);
        bool alreadyCurrent = neededBytes == 0
            && state != null && state.Version == version && state.FileCount == selectedFiles.Count;

        return new CheckResult
        {
            Version = version,
            FileCount = selectedFiles.Count,
            NeededBytes = neededBytes,
            AlreadyCurrent = alreadyCurrent
        };
    }

    // ---------- 更新流程 ----------

    public static async Task<UpdateResult> UpdateAsync(
        PayloadProfile profile, Action<string> report, CancellationToken cancellationToken = default)
    {
        var (version, resourceBase) = await GetLatestPackageAsync(profile, cancellationToken);
        report($"获取到最新版本 v{version}，正在下载文件清单…");

        var selectedFiles = await GetSelectedFilesAsync(profile, resourceBase, cancellationToken);
        report($"共 {selectedFiles.Count} 个切服文件，正在与本地比对…");

        var payloadDirectory = GetPayloadDirectory(profile);
        var seeds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long neededBytes = 0;
        foreach (var node in selectedFiles)
        {
            var localPath = SafeCombine(payloadDirectory, node.RelativePath);
            if (File.Exists(localPath) && await HasExpectedMd5Async(localPath, node.Md5, cancellationToken))
                seeds[node.RelativePath] = localPath;
            else
                neededBytes += node.Size;
        }

        var state = GetState(profile);
        if (neededBytes == 0 && seeds.Count == selectedFiles.Count
            && state != null && state.Version == version && state.FileCount == selectedFiles.Count)
        {
            return new UpdateResult { Version = version, FileCount = selectedFiles.Count, AlreadyCurrent = true };
        }

        // 文件全部一致、仅本地状态过期时，补写状态即可，无需重新暂存
        if (neededBytes == 0 && seeds.Count == selectedFiles.Count)
        {
            WriteState(profile, version, selectedFiles.Count, selectedFiles.Sum(x => x.Size));
            return new UpdateResult { Version = version, FileCount = selectedFiles.Count, AlreadyCurrent = false };
        }        var stagingDirectory = Path.Combine(PayloadRoot, ".staging", profile.PayloadDirectoryName + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            long downloadedBytes = 0;
            int downloadedCount = 0;
            var downloadFiles = selectedFiles.Where(x => !seeds.ContainsKey(x.RelativePath)).ToList();

            foreach (var (node, index) in selectedFiles.Select((x, i) => (x, i)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var destination = SafeCombine(stagingDirectory, node.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                if (seeds.TryGetValue(node.RelativePath, out var sourcePath))
                {
                    File.Copy(sourcePath, destination, true);
                    continue;
                }

                downloadedCount++;
                var startBytes = downloadedBytes;
                var lastReportAt = Environment.TickCount64;
                await DownloadFileAsync(
                    BuildFileUri(resourceBase, node.UrlPath),
                    destination + ".download",
                    node.Size,
                    delta =>
                    {
                        downloadedBytes += delta;
                        var now = Environment.TickCount64;
                        if (now - lastReportAt < 200) return; // 节流，避免频繁刷 UI
                        lastReportAt = now;
                        report($"正在下载 {node.RelativePath}（{index + 1}/{selectedFiles.Count}，{FormatBytes(downloadedBytes)} / {FormatBytes(neededBytes)}）");
                    },
                    cancellationToken);
                downloadedBytes = startBytes + node.Size;

                var tempPath = destination + ".download";
                if (!await HasExpectedMd5Async(tempPath, node.Md5, cancellationToken))
                    throw new InvalidDataException($"下载文件 MD5 校验失败：{node.RelativePath}");
                File.Move(tempPath, destination, true);
            }

            report($"校验 {selectedFiles.Count} 个文件…");
            foreach (var node in selectedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = SafeCombine(stagingDirectory, node.RelativePath);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != node.Size ||
                    !await HasExpectedMd5Async(path, node.Md5, cancellationToken))
                    throw new InvalidDataException($"切服文件校验失败：{node.RelativePath}");
            }

            report("正在应用切服文件…");
            ApplyStagedPayload(profile, stagingDirectory);
            WriteState(profile, version, selectedFiles.Count, selectedFiles.Sum(x => x.Size));

            return new UpdateResult
            {
                Version = version,
                FileCount = selectedFiles.Count,
                DownloadedBytes = downloadedBytes,
                AlreadyCurrent = false
            };
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                try { Directory.Delete(stagingDirectory, true); } catch { /* 留待下次清理 */ }
            }
        }
    }

    // ---------- 官方 API ----------

    private static async Task<(string Version, Uri ResourceBase)> GetLatestPackageAsync(
        PayloadProfile profile, CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            seq = Sequence,
            proxy_reqs = new object[]
            {
                new
                {
                    kind = "get_latest_game",
                    get_latest_game_req = new
                    {
                        appcode = ArknightsAppCode,
                        launcher_appcode = DomesticLauncherAppCode,
                        channel = profile.Channel,
                        sub_channel = profile.SubChannel,
                        version = ""
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, DomesticApiUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        using var response = await ApiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        string? version = null, resourceBaseUrl = null;
        if (document.RootElement.TryGetProperty("proxy_rsps", out var proxyRsps) &&
            proxyRsps.ValueKind == JsonValueKind.Array)
        {
            foreach (var rsp in proxyRsps.EnumerateArray())
            {
                if (!rsp.TryGetProperty("kind", out var kind) ||
                    !string.Equals(kind.GetString(), "get_latest_game", StringComparison.OrdinalIgnoreCase) ||
                    !rsp.TryGetProperty("get_latest_game_rsp", out var latest) ||
                    latest.ValueKind != JsonValueKind.Object)
                    continue;

                if (latest.TryGetProperty("version", out var versionElement))
                    version = versionElement.GetString();
                if (latest.TryGetProperty("pkg", out var package) &&
                    package.ValueKind == JsonValueKind.Object &&
                    package.TryGetProperty("file_path", out var filePath))
                    resourceBaseUrl = filePath.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(resourceBaseUrl) ||
            !Uri.TryCreate(resourceBaseUrl, UriKind.Absolute, out var resourceBase) ||
            resourceBase.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("官方启动器 API 未返回有效的版本或资源地址。");

        return (version, resourceBase);
    }

    // ---------- 清单 ----------

    private sealed class ManifestNode
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("md5")] public string Md5 { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    private sealed record RemoteFile(string RelativePath, string UrlPath, string Md5, long Size);

    private static async Task<List<RemoteFile>> GetSelectedFilesAsync(
        PayloadProfile profile, Uri resourceBase, CancellationToken cancellationToken)
    {
        var manifestUri = new Uri(resourceBase.AbsoluteUri.TrimEnd('/') + "/game_files");
        var encrypted = await ApiClient.GetByteArrayAsync(manifestUri, cancellationToken);
        var decrypted = HgCrypto.DecryptBytesToString(encrypted);
        if (string.IsNullOrWhiteSpace(decrypted))
            throw new InvalidDataException("切服文件清单解密失败。");

        var files = new Dictionary<string, RemoteFile>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(decrypted);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var node = JsonSerializer.Deserialize<ManifestNode>(line)
                ?? throw new InvalidDataException("切服文件清单包含空条目。");
            if (string.IsNullOrWhiteSpace(node.Path) || node.Size < 0 ||
                node.Md5.Length != 32 || !node.Md5.All(Uri.IsHexDigit))
                throw new InvalidDataException($"切服文件清单条目无效：{node.Path}");

            var relativePath = NormalizeRelativePath(node.Path);
            files[relativePath] = new RemoteFile(
                relativePath, relativePath.Replace('\\', '/'), node.Md5.ToLowerInvariant(), node.Size);
        }

        var selected = files.Values
            .Where(x => IsAllowedPayloadFile(x.RelativePath, profile))
            .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selected.Count == 0)
            throw new InvalidDataException($"官方清单中未找到 {profile.DisplayName} 的切服文件。");
        if (selected.Count > profile.MaxFileCount)
            throw new InvalidDataException($"{profile.DisplayName} 切服文件数量异常（{selected.Count}）。");
        if (selected.Sum(x => x.Size) > profile.MaxTotalBytes)
            throw new InvalidDataException($"{profile.DisplayName} 切服文件总体积异常。");
        var oversized = selected.FirstOrDefault(x => x.Size > profile.MaxSingleFileBytes);
        if (oversized != null)
            throw new InvalidDataException($"切服文件体积异常：{oversized.RelativePath}");
        var missing = profile.RequiredFiles
            .Where(x => selected.All(f => !string.Equals(f.RelativePath.Replace('\\', '/'), x, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"{profile.DisplayName} 切服文件缺少必需项：{string.Join(", ", missing)}");

        return selected;
    }

    private static bool IsAllowedPayloadFile(string relativePath, PayloadProfile profile)
    {
        var normalized = relativePath.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        if (normalized.Equals("config.ini", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Arknights_Data/", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Arknights.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("baselib.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("UnityPlayer", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("game_files", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("payload-state.json", StringComparison.OrdinalIgnoreCase))
            return false;

        var separator = normalized.IndexOf('/');
        if (separator < 0)
            return profile.RootFiles.Contains(normalized, StringComparer.OrdinalIgnoreCase);

        return profile.DirectoryPrefixes.Any(prefix =>
            normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.StartsWith('/') || normalized.Contains(':'))
            throw new InvalidDataException($"清单包含不安全路径：{path}");

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(IsUnsafePathSegment))
            throw new InvalidDataException($"清单包含不安全路径：{path}");

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static bool IsUnsafePathSegment(string segment)
    {
        if (segment is "." or ".." || segment.Length == 0 || segment.EndsWith("."))
            return true;

        var name = segment.Split('.')[0];
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;

        return name.Length == 4 &&
               (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               name[3] is >= '1' and <= '9';
    }

    // ---------- 下载 / 校验 / 应用 ----------

    private static async Task DownloadFileAsync(
        Uri uri, string tempPath, long expectedSize, Action<long> reportDelta,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var existingLength = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
                if (existingLength > expectedSize)
                {
                    File.Delete(tempPath);
                    existingLength = 0;
                }
                if (existingLength == expectedSize) return;

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (existingLength > 0)
                    request.Headers.Range = new RangeHeaderValue(existingLength, null);

                using var response = await DownloadClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(
                    tempPath, existingLength > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    reportDelta(read);
                }

                await output.FlushAsync(cancellationToken);
                if (output.Length != expectedSize)
                    throw new InvalidDataException($"文件大小不符：{uri}（应为 {expectedSize}，实际 {output.Length}）");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }

        throw new IOException($"下载失败（已重试 {maxAttempts} 次）：{uri}");
    }

    private static async Task<bool> HasExpectedMd5Async(
        string filePath, string expectedMd5, CancellationToken cancellationToken)
    {
        try
        {
            using var md5 = MD5.Create();
            await using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 81920, true);
            var hash = await md5.ComputeHashAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).Equals(expectedMd5, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void ApplyStagedPayload(PayloadProfile profile, string stagingDirectory)
    {
        Directory.CreateDirectory(PayloadRoot);

        var targetDirectory = GetPayloadDirectory(profile);
        var backupDirectory = targetDirectory + ".backup";
        var targetMovedToBackup = false;
        var stagingMovedToTarget = false;

        if (Directory.Exists(backupDirectory))
            Directory.Delete(backupDirectory, true);

        try
        {
            if (Directory.Exists(targetDirectory))
            {
                Directory.Move(targetDirectory, backupDirectory);
                targetMovedToBackup = true;
            }

            Directory.Move(stagingDirectory, targetDirectory);
            stagingMovedToTarget = true;
        }
        catch
        {
            if (stagingMovedToTarget && Directory.Exists(targetDirectory))
                Directory.Delete(targetDirectory, true);
            if (targetMovedToBackup && Directory.Exists(backupDirectory))
                Directory.Move(backupDirectory, targetDirectory);
            throw;
        }

        if (targetMovedToBackup && Directory.Exists(backupDirectory))
        {
            try { Directory.Delete(backupDirectory, true); } catch { /* 下次更新时重试清理 */ }
        }
    }

    private static void WriteState(PayloadProfile profile, string version, int fileCount, long totalBytes)
    {
        Directory.CreateDirectory(StateRoot);
        var statePath = Path.Combine(StateRoot, profile.PayloadDirectoryName + ".json");
        var state = new PayloadState
        {
            Version = version,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            FileCount = fileCount,
            TotalBytes = totalBytes
        };
        var tempState = statePath + ".tmp";
        File.WriteAllText(tempState, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempState, statePath, true);
    }

    private sealed class PayloadState
    {
        public string Version { get; set; } = "";
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public int FileCount { get; set; }
        public long TotalBytes { get; set; }
    }

    private static PayloadState? GetState(PayloadProfile profile)
    {
        var path = Path.Combine(StateRoot, profile.PayloadDirectoryName + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PayloadState>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    // ---------- 工具 ----------

    private static Uri BuildFileUri(Uri baseUri, string relativeUrlPath)
    {
        var escapedPath = string.Join("/", relativeUrlPath.Split('/').Select(Uri.EscapeDataString));
        var uri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/" + escapedPath);
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"不安全的下载地址：{uri}");
        return uri;
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"路径越界：{relativePath}");
        return combined;
    }

    private static string FormatBytes(long bytes)
        => bytes >= MiB ? $"{(double)bytes / MiB:F1} MB" : $"{bytes / 1024.0:F0} KB";
}

/// <summary>鹰角加密文件的 AES-256-CBC 解密（密钥逆向自官方启动器，实现来自 Hi3Helper，Apache-2.0）。</summary>
public static class HgCrypto
{
    private static readonly byte[] AesKey =
    {
        0xC0, 0xF3, 0x0E, 0x1C, 0xE7, 0x63, 0xBB, 0xC2, 0x1C, 0xC3, 0x55, 0xA3, 0x43, 0x03, 0xAC, 0x50,
        0x39, 0x94, 0x44, 0xBF, 0xF6, 0x8C, 0x4A, 0x22, 0xAF, 0x39, 0x8C, 0x0A, 0x16, 0x6E, 0xE1, 0x43
    };

    private static readonly byte[] AesIv =
    {
        0x33, 0x46, 0x78, 0x61, 0x19, 0x27, 0x50, 0x64, 0x95, 0x01, 0x93, 0x72, 0x64, 0x60, 0x84, 0x00
    };

    public static string DecryptBytesToString(byte[] encryptedBytes)
    {
        try
        {
            if (encryptedBytes.Length == 0) return string.Empty;

            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = AesKey;
            aes.IV = AesIv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return string.Empty;
        }
    }
}
