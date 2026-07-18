using System;
using System.Collections.Generic;
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
    /// Quilt 加载器安装器实现。API 与 Fabric 几乎相同。
    ///
    /// Quilt API 文档：https://meta.quiltmc.org/
    /// - 列出版本：GET https://meta.quiltmc.org/v3/versions/loader/{mcVersion}
    ///   返回 JSON 数组：[{"loader": {"version": "0.20.0", "stable": true}, "intermediary": {...}}]
    /// - 获取版本 JSON：GET https://meta.quiltmc.org/v3/versions/loader/{mcVersion}/{loaderVersion}/profile/json
    ///   返回完整的 Quilt 版本 JSON（与 Fabric profile/json 结构相同）
    ///
    /// 注意：BMCLAPI 未镜像 Quilt，所以统一用官方源。
    /// </summary>
    public class QuiltInstaller : ModLoaderInstallerBase
    {
        private const string OfficialMetaBase = "https://meta.quiltmc.org/v3/versions/loader";

        /// <inheritdoc/>
        public override ModLoaderType Type => ModLoaderType.Quilt;

        public QuiltInstaller(Func<string> minecraftPathProvider, IDownloadSource downloadSource)
            : base(minecraftPathProvider, downloadSource)
        {
        }

        /// <inheritdoc/>
        public override async Task<List<ModLoaderVersion>> ListVersionsAsync(
            string minecraftVersion,
            CancellationToken cancellationToken = default)
        {
            var result = new List<ModLoaderVersion>();
            if (string.IsNullOrEmpty(minecraftVersion)) return result;

            var url = $"{OfficialMetaBase}/{minecraftVersion}";
            Logger.Info($"获取 Quilt 加载器版本列表：{url}");

            string json;
            try
            {
                json = await DownloadTextAsync(url, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Warn($"获取 Quilt 加载器版本列表失败：{ex.Message}");
                return result;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("loader", out var loaderEl)) continue;

                    var version = loaderEl.TryGetProperty("version", out var vEl) ? vEl.GetString() : null;
                    if (string.IsNullOrEmpty(version)) continue;

                    var stable = loaderEl.TryGetProperty("stable", out var sEl) && sEl.GetBoolean();

                    result.Add(new ModLoaderVersion
                    {
                        Type = ModLoaderType.Quilt,
                        Version = version,
                        MinecraftVersion = minecraftVersion,
                        Stable = stable,
                        Recommended = false
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error("解析 Quilt 加载器版本列表失败", ex);
            }

            Logger.Info($"获取到 {result.Count} 个 Quilt 加载器版本");
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

            Logger.Info($"开始安装 Quilt {version.Version} for Minecraft {minecraftVersion}");

            progress?.Report(new InstallProgress
            {
                Phase = InstallPhase.DownloadingJson,
                CurrentFile = "Quilt 版本 JSON",
                TotalFiles = 1,
                CompletedFiles = 0
            });

            // 获取完整的 Quilt 版本 JSON
            var profileUrl = $"{OfficialMetaBase}/{minecraftVersion}/{version.Version}/profile/json";

            string profileJson;
            try
            {
                profileJson = await DownloadTextAsync(profileUrl, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"下载 Quilt 版本 JSON 失败：{ex.Message}", ex);
            }

            // 解析 JSON 获取 id 字段
            string versionId;
            try
            {
                using var doc = JsonDocument.Parse(profileJson);
                versionId = doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()!
                    : $"{minecraftVersion}-quilt-{version.Version}";
            }
            catch
            {
                versionId = $"{minecraftVersion}-quilt-{version.Version}";
            }

            // 写入版本目录与 JSON
            var versionDir = Path.Combine(MinecraftPath, "versions", versionId);
            Directory.CreateDirectory(versionDir);

            var jsonPath = Path.Combine(versionDir, versionId + ".json");
            await File.WriteAllTextAsync(jsonPath, profileJson, cancellationToken);

            progress?.Report(new InstallProgress
            {
                Phase = InstallPhase.Completed,
                CurrentFile = $"Quilt {version.Version}",
                TotalFiles = 1,
                CompletedFiles = 1
            });

            Logger.Info($"Quilt 版本 JSON 已写入：{jsonPath}");
            Logger.Info("Quilt 依赖库将在游戏启动前由 MinecraftFileDownloader 自动下载");
        }

        /// <inheritdoc/>
        protected override string GetVersionDirectoryPrefix(string minecraftVersion)
        {
            return $"{minecraftVersion}-quilt-";
        }
    }
}
