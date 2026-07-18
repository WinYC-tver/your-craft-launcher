using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;
using YCL.Core.Versions;

namespace YCL.Core.Java
{
    /// <summary>
    /// Java 安装器实现：从 Adoptium（Eclipse Temurin）下载并安装 JDK。
    ///
    /// Adoptium API 文档：https://api.adoptium.net/q/swagger-ui/
    /// - 列出可用版本：GET https://api.adoptium.net/v3/info/available_releases
    /// - 下载最新二进制：GET https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jdk/hotspot/normal/eclipse
    ///
    /// 安装目录：%AppData%\YCL\java\jdk-{majorVersion}\
    /// 安装完成后由调用方（如 JavaPageViewModel）调用 IJavaDetector.DetectAsync 重新扫描。
    /// </summary>
    public class JavaInstaller : IJavaInstaller
    {
        /// <summary>Adoptium API 基址</summary>
        private const string AdoptiumApiBase = "https://api.adoptium.net/v3";

        /// <summary>Java 安装根目录：%AppData%\YCL\java</summary>
        private static readonly string JavaInstallRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YCL", "java");

        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <inheritdoc/>
        public async Task<List<JavaRelease>> ListAvailableAsync(CancellationToken cancellationToken = default)
        {
            var url = AdoptiumApiBase + "/info/available_releases";
            Logger.Info($"获取可用 Java 版本列表：{url}");

            using var response = await HttpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var releases = new List<JavaRelease>();

            // available_releases 字段是数组，含主版本号
            if (root.TryGetProperty("available_releases", out var arrEl) && arrEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in arrEl.EnumerateArray())
                {
                    if (v.TryGetInt32(out var major))
                    {
                        releases.Add(new JavaRelease
                        {
                            MajorVersion = major,
                            IsLts = false
                        });
                    }
                }
            }

            // lts_version 字段是数组，含 LTS 主版本号
            if (root.TryGetProperty("lts_version", out var ltsEl) && ltsEl.ValueKind == JsonValueKind.Array)
            {
                var ltsSet = new HashSet<int>();
                foreach (var v in ltsEl.EnumerateArray())
                {
                    if (v.TryGetInt32(out var lts))
                        ltsSet.Add(lts);
                }

                foreach (var r in releases)
                    r.IsLts = ltsSet.Contains(r.MajorVersion);
            }

            // 按版本号降序排序
            releases.Sort((a, b) => b.MajorVersion.CompareTo(a.MajorVersion));

            Logger.Info($"获取到 {releases.Count} 个可用 Java 版本");
            return releases;
        }

        /// <inheritdoc/>
        public async Task<string> InstallAsync(
            int majorVersion,
            IProgress<InstallProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            Logger.Info($"开始安装 Java {majorVersion}");

            // 下载 URL：获取最新 GA 版本的 zip
            var downloadUrl = $"{AdoptiumApiBase}/binary/latest/{majorVersion}/ga/windows/x64/jdk/hotspot/normal/eclipse";
            Logger.Info($"Java 下载地址：{downloadUrl}");

            // 确保安装根目录存在
            Directory.CreateDirectory(JavaInstallRoot);

            // 安装目录：jdk-{majorVersion}
            var installDir = Path.Combine(JavaInstallRoot, $"jdk-{majorVersion}");

            // 如果已存在同名目录，先删除（重新安装）
            if (Directory.Exists(installDir))
            {
                Logger.Info($"目标目录已存在，将覆盖安装：{installDir}");
                try { Directory.Delete(installDir, recursive: true); }
                catch (Exception ex) { Logger.Warn($"删除旧目录失败：{ex.Message}"); }
            }

            // 临时 zip 文件路径
            var tempZip = Path.Combine(Path.GetTempPath(), $"ycl-jdk-{majorVersion}-{Guid.NewGuid():N}.zip");

            try
            {
                // 阶段 1：下载 zip
                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.DownloadingFiles,
                    CurrentFile = $"Java {majorVersion} JDK 压缩包",
                    TotalFiles = 1,
                    CompletedFiles = 0
                });

                var totalBytes = await DownloadWithProgressAsync(downloadUrl, tempZip, progress, cancellationToken);

                Logger.Info($"Java {majorVersion} 下载完成，大小 {totalBytes} 字节");

                // 阶段 2：解压
                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.Parsing,
                    CurrentFile = "解压 JDK 压缩包",
                    TotalFiles = 1,
                    CompletedFiles = 0
                });

                Directory.CreateDirectory(installDir);

                // 解压 zip 到 installDir
                // zip 内顶层通常是 jdk-17.0.1/ 这样的目录，我们提取其内容到 installDir
                ExtractZipToDirectory(tempZip, installDir);

                Logger.Info($"Java {majorVersion} 解压完成：{installDir}");

                // 阶段 3：定位 javaw.exe
                var javawPath = FindJavawInDirectory(installDir);
                if (javawPath == null)
                {
                    throw new InvalidOperationException(
                        $"解压完成但找不到 javaw.exe，请检查安装目录：{installDir}");
                }

                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.Completed,
                    CurrentFile = javawPath,
                    TotalFiles = 1,
                    CompletedFiles = 1
                });

                Logger.Info($"Java {majorVersion} 安装完成：{javawPath}");
                return javawPath;
            }
            finally
            {
                // 清理临时 zip
                try { if (File.Exists(tempZip)) File.Delete(tempZip); }
                catch { /* 忽略 */ }
            }
        }

        /// <summary>
        /// 下载文件并报告进度。
        /// 用 HttpClient 流式下载到文件，定期报告已下载字节数。
        /// </summary>
        private static async Task<long> DownloadWithProgressAsync(
            string url,
            string targetPath,
            IProgress<InstallProgress>? progress,
            CancellationToken ct)
        {
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            long completedBytes = 0;
            var lastReport = DateTime.MinValue;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);

            var buffer = new byte[81920];
            int read;
            while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                completedBytes += read;

                // 节流进度报告（每 200ms 一次，避免刷爆 UI）
                var now = DateTime.UtcNow;
                if (now - lastReport > TimeSpan.FromMilliseconds(200))
                {
                    lastReport = now;
                    progress?.Report(new InstallProgress
                    {
                        Phase = InstallPhase.DownloadingFiles,
                        CurrentFile = "下载 JDK 压缩包",
                        TotalFiles = 1,
                        CompletedFiles = 0,
                        CompletedBytes = completedBytes,
                        TotalBytes = totalBytes
                    });
                }
            }

            return completedBytes;
        }

        /// <summary>
        /// 解压 zip 到目标目录。
        /// Adoptium 的 zip 内顶层是 jdk-17.0.1+12/ 这样的目录，
        /// 我们把其下所有内容提取到 installDir（去掉顶层目录前缀）。
        /// </summary>
        private static void ExtractZipToDirectory(string zipPath, string destDir)
        {
            using var archive = ZipFile.OpenRead(zipPath);

            // 找出顶层目录前缀（如 "jdk-17.0.1+12/"）
            string? topDirPrefix = null;
            foreach (var entry in archive.Entries)
            {
                var slashIndex = entry.FullName.IndexOf('/');
                if (slashIndex > 0)
                {
                    topDirPrefix = entry.FullName.Substring(0, slashIndex + 1);
                    break;
                }
                else if (entry.FullName.IndexOf('\\') > 0)
                {
                    topDirPrefix = entry.FullName.Substring(0, entry.FullName.IndexOf('\\') + 1);
                    break;
                }
            }

            foreach (var entry in archive.Entries)
            {
                // 去掉顶层目录前缀
                var relativePath = entry.FullName;
                if (topDirPrefix != null && relativePath.StartsWith(topDirPrefix, StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath.Substring(topDirPrefix.Length);

                if (string.IsNullOrEmpty(relativePath)) continue;

                // 目录
                if (relativePath.EndsWith('/') || relativePath.EndsWith('\\'))
                {
                    var dirPath = Path.Combine(destDir, relativePath);
                    Directory.CreateDirectory(dirPath);
                    continue;
                }

                // 防止 zip slip
                var targetPath = Path.GetFullPath(Path.Combine(destDir, relativePath));
                var destDirFull = Path.GetFullPath(destDir);
                if (!targetPath.StartsWith(destDirFull, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"跳过可疑的 zip 条目：{entry.FullName}");
                    continue;
                }

                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir))
                    Directory.CreateDirectory(targetDir);

                entry.ExtractToFile(targetPath, overwrite: true);
            }
        }

        /// <summary>在目录中查找 javaw.exe（最多深入 3 层）</summary>
        private static string? FindJavawInDirectory(string root)
        {
            // 直接 bin/javaw.exe
            var direct = Path.Combine(root, "bin", "javaw.exe");
            if (File.Exists(direct)) return direct;

            // 子目录下找
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var sub = Path.Combine(dir, "bin", "javaw.exe");
                    if (File.Exists(sub)) return sub;

                    foreach (var inner in Directory.GetDirectories(dir))
                    {
                        var innerJava = Path.Combine(inner, "bin", "javaw.exe");
                        if (File.Exists(innerJava)) return innerJava;
                    }
                }
            }
            catch { /* 忽略 */ }

            return null;
        }
    }
}
