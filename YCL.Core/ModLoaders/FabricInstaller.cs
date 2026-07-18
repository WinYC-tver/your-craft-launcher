using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;
using YCL.Core.Utils;
using YCL.Core.Versions;

namespace YCL.Core.ModLoaders
{
    /// <summary>
    /// Fabric 加载器安装器实现。
    ///
    /// Fabric API 文档：https://fabricmc.net/develop/
    /// - 列出版本：GET https://meta.fabricmc.net/v2/versions/loader/{mcVersion}
    ///   返回 JSON 数组：[{"loader": {"version": "0.14.21", "stable": true}, "intermediary": {...}}]
    /// - 获取版本 JSON：GET https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json
    ///   返回完整的 Fabric 版本 JSON（含 id、inheritsFrom、mainClass、libraries、arguments）
    ///
    /// BMCLAPI 镜像：
    /// - https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{mcVersion}
    /// - https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json
    ///
    /// 安装流程：
    /// 1. 获取版本 JSON（含完整库信息）
    /// 2. 写入 .minecraft/versions/{mcVersion}-fabric-{loaderVersion}/{mcVersion}-fabric-{loaderVersion}.json
    /// 3. 库（Fabric Loader、Intermediary 等）下载交给 MinecraftFileDownloader（启动前自动下载）
    /// </summary>
    public class FabricInstaller : ModLoaderInstallerBase
    {
        private const string OfficialMetaBase = "https://meta.fabricmc.net/v2/versions/loader";
        private const string BmclapiMetaBase = "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader";

        /// <inheritdoc/>
        public override ModLoaderType Type => ModLoaderType.Fabric;

        public FabricInstaller(Func<string> minecraftPathProvider, IDownloadSource downloadSource)
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

            var url = SelectUrl(
                $"{OfficialMetaBase}/{minecraftVersion}",
                $"{BmclapiMetaBase}/{minecraftVersion}");

            Logger.Info($"获取 Fabric 加载器版本列表：{url}");

            string json;
            try
            {
                json = await DownloadTextAsync(url, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Warn($"获取 Fabric 加载器版本列表失败：{ex.Message}");
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
                        Type = ModLoaderType.Fabric,
                        Version = version,
                        MinecraftVersion = minecraftVersion,
                        Stable = stable,
                        Recommended = false
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error("解析 Fabric 加载器版本列表失败", ex);
            }

            Logger.Info($"获取到 {result.Count} 个 Fabric 加载器版本");
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

            Logger.Info($"开始安装 Fabric {version.Version} for Minecraft {minecraftVersion}");

            // 进度：获取版本 JSON
            progress?.Report(new InstallProgress
            {
                Phase = InstallPhase.DownloadingJson,
                CurrentFile = "Fabric 版本 JSON",
                TotalFiles = 1,
                CompletedFiles = 0
            });

            // 获取完整的 Fabric 版本 JSON
            var profileUrl = SelectUrl(
                $"{OfficialMetaBase}/{minecraftVersion}/{version.Version}/profile/json",
                $"{BmclapiMetaBase}/{minecraftVersion}/{version.Version}/profile/json");

            string profileJson;
            try
            {
                profileJson = await DownloadTextAsync(profileUrl, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"下载 Fabric 版本 JSON 失败：{ex.Message}", ex);
            }

            // 解析 JSON 获取内部的 id 字段（Fabric 用的 id 可能是 "fabric-loader-0.14.21-1.18.2"）
            string versionId;
            try
            {
                using var doc = JsonDocument.Parse(profileJson);
                versionId = doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()!
                    : $"{minecraftVersion}-fabric-{version.Version}";
            }
            catch
            {
                versionId = $"{minecraftVersion}-fabric-{version.Version}";
            }

            // 写入版本目录与 JSON
            var versionDir = Path.Combine(MinecraftPath, "versions", versionId);
            Directory.CreateDirectory(versionDir);

            var jsonPath = Path.Combine(versionDir, versionId + ".json");
            await File.WriteAllTextAsync(jsonPath, profileJson, cancellationToken);

            progress?.Report(new InstallProgress
            {
                Phase = InstallPhase.Completed,
                CurrentFile = $"Fabric {version.Version}",
                TotalFiles = 1,
                CompletedFiles = 1
            });

            Logger.Info($"Fabric 版本 JSON 已写入：{jsonPath}");
            Logger.Info("Fabric 依赖库将在游戏启动前由 MinecraftFileDownloader 自动下载");
        }

        /// <inheritdoc/>
        protected override string GetVersionDirectoryPrefix(string minecraftVersion)
        {
            return $"{minecraftVersion}-fabric-";
        }
    }
}
