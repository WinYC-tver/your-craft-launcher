using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using YCL.Core.Download;
using YCL.Core.Utils;
using YCL.Core.Versions;

namespace YCL.Core.ModLoaders
{
    /// <summary>
    /// Forge 加载器安装器实现。
    ///
    /// Forge 没有 Fabric 那样直接的版本 JSON API，标准安装方式是下载 installer.jar 并运行。
    /// 这里采用 BMCLAPI 镜像获取版本列表，并通过运行 installer.jar 完成安装。
    ///
    /// API：
    /// - 版本列表（XML）：https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml
    ///   BMCLAPI 镜像：https://bmclapi2.bangbang93.com/forge/maven-metadata.xml
    ///   返回 maven-metadata.xml，&lt;versions&gt;&lt;version&gt;1.18.2-40.2.0&lt;/version&gt;...&lt;/versions&gt;
    ///   版本号格式：{mcVersion}-{forgeVersion}（如 1.18.2-40.2.0）
    /// - installer.jar 下载：https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/{mcVersion}-{forgeVersion}/forge-{mcVersion}-{forgeVersion}-installer.jar
    ///
    /// 安装流程：
    /// 1. 下载 forge-{mcVersion}-{forgeVersion}-installer.jar 到临时目录
    /// 2. 调用 java -jar installer.jar --installClient &lt;minecraftPath&gt;
    /// 3. 等待 installer 完成，会生成 .minecraft/versions/{mcVersion}-forge-{forgeVersion}/ 目录
    ///
    /// 注意：Forge installer 需要调用外部 Java，所以需要 JavaPath 提供者。
    /// </summary>
    public class ForgeInstaller : ModLoaderInstallerBase
    {
        private const string OfficialMavenMetadataUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
        private const string BmclapiMavenMetadataUrl = "https://bmclapi2.bangbang93.com/forge/maven-metadata.xml";
        private const string ForgeMavenBase = "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge";

        /// <summary>Java 路径提供者（运行 installer.jar 需要）</summary>
        private readonly Func<string> _javaPathProvider;

        /// <inheritdoc/>
        public override ModLoaderType Type => ModLoaderType.Forge;

        public ForgeInstaller(
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

            var url = SelectUrl(OfficialMavenMetadataUrl, BmclapiMavenMetadataUrl);
            Logger.Info($"获取 Forge 加载器版本列表：{url}");

            string xml;
            try
            {
                xml = await DownloadTextAsync(url, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Warn($"获取 Forge 加载器版本列表失败：{ex.Message}");
                return result;
            }

            try
            {
                var doc = XDocument.Parse(xml);
                var versionsEl = doc.Root?.Element("versioning")?.Element("versions");
                if (versionsEl == null)
                {
                    Logger.Warn("Forge maven-metadata.xml 格式异常：缺少 versions 节点");
                    return result;
                }

                foreach (var vEl in versionsEl.Elements("version"))
                {
                    var fullVersion = vEl.Value?.Trim();
                    if (string.IsNullOrEmpty(fullVersion)) continue;

                    // fullVersion 格式：{mcVersion}-{forgeVersion}（如 1.18.2-40.2.0）
                    var dashIndex = fullVersion.IndexOf('-');
                    if (dashIndex <= 0) continue;

                    var mcVer = fullVersion.Substring(0, dashIndex);
                    var forgeVer = fullVersion.Substring(dashIndex + 1);

                    // 按 minecraftVersion 过滤
                    if (!string.Equals(mcVer, minecraftVersion, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Add(new ModLoaderVersion
                    {
                        Type = ModLoaderType.Forge,
                        Version = forgeVer,
                        MinecraftVersion = mcVer,
                        Stable = !forgeVer.Contains("beta", StringComparison.OrdinalIgnoreCase)
                                 && !forgeVer.Contains("alpha", StringComparison.OrdinalIgnoreCase),
                        Recommended = false,
                        DownloadUrl = $"{ForgeMavenBase}/{fullVersion}/forge-{fullVersion}-installer.jar"
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error("解析 Forge 加载器版本列表失败", ex);
            }

            Logger.Info($"获取到 {result.Count} 个 Forge 加载器版本（Minecraft {minecraftVersion}）");
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

            Logger.Info($"开始安装 Forge {version.Version} for Minecraft {minecraftVersion}");

            var javaPath = _javaPathProvider();
            if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
            {
                throw new InvalidOperationException(
                    "未配置 Java 路径或 Java 不存在。Forge 安装需要 Java，请先在 Java 页面配置一个可用的 Java。");
            }

            // 下载 installer.jar
            progress?.Report(new InstallProgress
            {
                Phase = InstallPhase.DownloadingFiles,
                CurrentFile = $"Forge installer.jar（{version.Version}）",
                TotalFiles = 2,
                CompletedFiles = 0
            });

            var fullVersion = $"{minecraftVersion}-{version.Version}";
            var installerUrl = $"{ForgeMavenBase}/{fullVersion}/forge-{fullVersion}-installer.jar";
            var tempDir = Path.Combine(Path.GetTempPath(), "YCL-Forge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var installerJarPath = Path.Combine(tempDir, $"forge-{fullVersion}-installer.jar");

            try
            {
                Logger.Info($"下载 Forge installer：{installerUrl}");
                var installerBytes = await DownloadBytesAsync(installerUrl, cancellationToken);
                await File.WriteAllBytesAsync(installerJarPath, installerBytes, cancellationToken);

                // 运行 installer
                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.Parsing,
                    CurrentFile = "运行 Forge installer...",
                    TotalFiles = 2,
                    CompletedFiles = 1
                });

                await RunInstallerAsync(javaPath, installerJarPath, MinecraftPath, cancellationToken);

                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.Completed,
                    CurrentFile = $"Forge {version.Version}",
                    TotalFiles = 2,
                    CompletedFiles = 2
                });

                Logger.Info($"Forge 安装完成：{fullVersion}");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch { /* 忽略清理失败 */ }
            }
        }

        /// <summary>
        /// 调用 java -jar installer.jar --installClient 安装 Forge。
        /// 设置工作目录为 .minecraft，installer 会在其中创建 versions/libraries 目录。
        /// </summary>
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
                throw new InvalidOperationException("Forge installer 进程启动失败");

            // 异步读取输出（避免缓冲区满）
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
                throw new InvalidOperationException("Forge installer 运行超时（超过 10 分钟）");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Logger.Debug($"Forge installer stdout: {stdout}");
            if (!string.IsNullOrEmpty(stderr))
                Logger.Debug($"Forge installer stderr: {stderr}");

            // installer 退出码非 0 视为失败
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Forge installer 运行失败（退出码 {proc.ExitCode}）。" +
                    $"stderr: {stderr}");
            }
        }

        /// <inheritdoc/>
        protected override string GetVersionDirectoryPrefix(string minecraftVersion)
        {
            return $"{minecraftVersion}-forge-";
        }
    }
}
