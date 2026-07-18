using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;
using YCL.Core.Utils;
using YCL.Core.Versions;

namespace YCL.Core.ModLoaders
{
    /// <summary>
    /// NeoForge 加载器安装器实现。
    ///
    /// NeoForge 是 Forge 的社区分叉，1.20.1+ 起活跃。安装方式与 Forge 类似：下载 installer.jar 并运行。
    ///
    /// API：
    /// - 版本列表：https://bmclapi2.bangbang93.com/neoforge/version/{mcVersion}
    ///   返回 JSON 数组：[{"build": 47, "version": "47.1.0", "mcversion": "1.20.1", ...}]
    ///   （官方源也用 BMCLAPI 这个端点，因为官方未提供按 mcVersion 过滤的稳定 API）
    /// - installer.jar 下载：
    ///   https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar
    ///   或官方：https://maven.neoforged.org/releases/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar
    /// </summary>
    public class NeoForgeInstaller : ModLoaderInstallerBase
    {
        private const string BmclapiVersionListBase = "https://bmclapi2.bangbang93.com/neoforge/version";
        private const string OfficialMavenBase = "https://maven.neoforged.org/releases/net/neoforged/neoforge";
        private const string BmclapiMavenBase = "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge";

        /// <summary>Java 路径提供者（运行 installer.jar 需要）</summary>
        private readonly Func<string> _javaPathProvider;

        /// <inheritdoc/>
        public override ModLoaderType Type => ModLoaderType.NeoForge;

        public NeoForgeInstaller(
            Func<string> minecraftPathProvider,
            IDownloadSource downloadSource,
            Func<string> javaPathProvider)
            : base(minecraftPathProvider, downloadSource)
        {
            _javaPathProvider = javaPathProvider ?? throw new ArgumentNullException(nameof(javaPathProvider));
        }

        /// <inheritdoc/>
        public override async Task<List<ModLoaderVersion>> ListVersionsAsync(
            string minecraftVersion,
            CancellationToken cancellationToken = default)
        {
            var result = new List<ModLoaderVersion>();
            if (string.IsNullOrEmpty(minecraftVersion)) return result;

            var url = $"{BmclapiVersionListBase}/{minecraftVersion}";
            Logger.Info($"获取 NeoForge 加载器版本列表：{url}");

            string json;
            try
            {
                json = await DownloadTextAsync(url, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Warn($"获取 NeoForge 加载器版本列表失败：{ex.Message}");
                return result;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var version = item.TryGetProperty("version", out var vEl) ? vEl.GetString() : null;
                    if (string.IsNullOrEmpty(version)) continue;

                    var mcVer = item.TryGetProperty("mcversion", out var mcEl) && mcEl.ValueKind == JsonValueKind.String
                        ? mcEl.GetString() ?? minecraftVersion
                        : minecraftVersion;

                    result.Add(new ModLoaderVersion
                    {
                        Type = ModLoaderType.NeoForge,
                        Version = version,
                        MinecraftVersion = mcVer,
                        Stable = !version.Contains("beta", StringComparison.OrdinalIgnoreCase),
                        Recommended = false
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error("解析 NeoForge 加载器版本列表失败", ex);
            }

            // 按版本号降序排序（最新在前）
            result.Sort((a, b) => string.Compare(b.Version, a.Version, StringComparison.OrdinalIgnoreCase));

            Logger.Info($"获取到 {result.Count} 个 NeoForge 加载器版本（Minecraft {minecraftVersion}）");
            return result;
        }

        /// <inheritdoc/>
        public override async Task InstallAsync(
            string minecraftVersion,
            ModLoaderVersion version,
            IProgress<InstallProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            if (version == null || string.IsNullOrEmpty(version.Version))
                throw new ArgumentException("加载器版本信息无效", nameof(version));

            Logger.Info($"开始安装 NeoForge {version.Version} for Minecraft {minecraftVersion}");

            var javaPath = _javaPathProvider();
            if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
            {
                throw new InvalidOperationException(
                    "未配置 Java 路径或 Java 不存在。NeoForge 安装需要 Java，请先在 Java 页面配置一个可用的 Java。");
            }

            // 下载 installer.jar
            progress?.Report(new InstallProgress
            {
                Phase = InstallPhase.DownloadingFiles,
                CurrentFile = $"NeoForge installer.jar（{version.Version}）",
                TotalFiles = 2,
                CompletedFiles = 0
            });

            var mavenBase = SelectUrl(OfficialMavenBase, BmclapiMavenBase);
            var installerUrl = $"{mavenBase}/{version.Version}/neoforge-{version.Version}-installer.jar";
            var tempDir = Path.Combine(Path.GetTempPath(), "YCL-NeoForge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var installerJarPath = Path.Combine(tempDir, $"neoforge-{version.Version}-installer.jar");

            try
            {
                Logger.Info($"下载 NeoForge installer：{installerUrl}");
                var installerBytes = await DownloadBytesAsync(installerUrl, cancellationToken);
                await File.WriteAllBytesAsync(installerJarPath, installerBytes, cancellationToken);

                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.Parsing,
                    CurrentFile = "运行 NeoForge installer...",
                    TotalFiles = 2,
                    CompletedFiles = 1
                });

                await RunInstallerAsync(javaPath, installerJarPath, MinecraftPath, cancellationToken);

                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.Completed,
                    CurrentFile = $"NeoForge {version.Version}",
                    TotalFiles = 2,
                    CompletedFiles = 2
                });

                Logger.Info($"NeoForge 安装完成：{version.Version}");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch { /* 忽略清理失败 */ }
            }
        }

        /// <summary>调用 java -jar installer.jar --installClient 安装 NeoForge</summary>
        private static async Task RunInstallerAsync(
            string javaPath,
            string installerJarPath,
            string minecraftPath,
            CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = minecraftPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-jar");
            psi.ArgumentList.Add(installerJarPath);
            psi.ArgumentList.Add("--installClient");
            psi.ArgumentList.Add(minecraftPath);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start())
                throw new InvalidOperationException("NeoForge installer 进程启动失败");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(10));

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                throw new InvalidOperationException("NeoForge installer 运行超时（超过 10 分钟）");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Logger.Debug($"NeoForge installer stdout: {stdout}");
            if (!string.IsNullOrEmpty(stderr))
                Logger.Debug($"NeoForge installer stderr: {stderr}");

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"NeoForge installer 运行失败（退出码 {proc.ExitCode}）。stderr: {stderr}");
            }
        }

        /// <inheritdoc/>
        protected override string GetVersionDirectoryPrefix(string minecraftVersion)
        {
            return $"{minecraftVersion}-neoforge-";
        }
    }
}
