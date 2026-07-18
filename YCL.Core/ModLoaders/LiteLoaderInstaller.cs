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
    /// LiteLoader 加载器安装器实现。
    ///
    /// LiteLoader 是最古老的轻量模组加载器，主要支持 1.12.2 及以下版本，现已停止维护。
    /// 它的安装方式与 Forge/Fabric 不同：LiteLoader 不提供 installer.jar，
    /// 而是通过构造一个继承原版版本的 JSON（含 LiteLoader 的 tweakClass 和库）实现。
    ///
    /// API：
    /// - 版本列表：https://dl.liteloader.com/versions/versions.json
    ///   BMCLAPI 镜像：https://bmclapi2.bangbang93.com/versions/versions.json
    ///   返回 JSON 对象，键是 Minecraft 版本号，值含 artefacts/snapshots 子节点。
    ///   示例：
    ///   {
    ///     "1.12.2": {
    ///       "artefacts": {
    ///         "com.mumfrey:liteloader": {
    ///           "1.12.2-SNAPSHOT": {"version": "1.12.2-SNAPSHOT", "tweakClass": "com.mumfrey.liteloader.launch.LiteLoaderTweaker"}
    ///         }
    ///       }
    ///     }
    ///   }
    ///
    /// 安装流程：
    /// 1. 从 versions.json 解析 mcVersion 对应的 LiteLoader 版本与 tweakClass
    /// 2. 构造继承版本的 JSON（inheritsFrom: mcVersion、mainClass: net.minecraft.launchwrapper.Launch、
    ///    加 LiteLoader 库 + --tweakClass 参数）
    /// 3. 写入 .minecraft/versions/{mcVersion}-liteloader-{loaderVersion}/
    /// </summary>
    public class LiteLoaderInstaller : ModLoaderInstallerBase
    {
        private const string OfficialVersionsJsonUrl = "https://dl.liteloader.com/versions/versions.json";
        private const string BmclapiVersionsJsonUrl = "https://bmclapi2.bangbang93.com/versions/versions.json";

        /// <inheritdoc/>
        public override ModLoaderType Type => ModLoaderType.LiteLoader;

        public LiteLoaderInstaller(Func<string> minecraftPathProvider, IDownloadSource downloadSource)
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

            var url = SelectUrl(OfficialVersionsJsonUrl, BmclapiVersionsJsonUrl);
            Logger.Info($"获取 LiteLoader 版本列表：{url}");

            string json;
            try
            {
                json = await DownloadTextAsync(url, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Warn($"获取 LiteLoader 版本列表失败：{ex.Message}");
                return result;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty(minecraftVersion, out var mcEl))
                {
                    // LiteLoader 不支持此 Minecraft 版本（新版本通常不支持）
                    Logger.Info($"LiteLoader 不支持 Minecraft {minecraftVersion}（仅支持 1.12.2 及以下）");
                    return result;
                }

                // 优先从 artefacts 取稳定版，其次从 snapshots 取
                CollectLiteLoaderVersions(mcEl, "artefacts", minecraftVersion, result, stable: true);
                CollectLiteLoaderVersions(mcEl, "snapshots", minecraftVersion, result, stable: false);
            }
            catch (Exception ex)
            {
                Logger.Error("解析 LiteLoader 版本列表失败", ex);
            }

            Logger.Info($"获取到 {result.Count} 个 LiteLoader 版本（Minecraft {minecraftVersion}）");
            return result;
        }

        /// <summary>从 artefacts 或 snapshots 子节点收集 LiteLoader 版本</summary>
        private static void CollectLiteLoaderVersions(
            JsonElement mcElement,
            string sectionName,
            string minecraftVersion,
            List<ModLoaderVersion> result,
            bool stable)
        {
            if (!mcElement.TryGetProperty(sectionName, out var sectionEl)) return;
            if (!sectionEl.TryGetProperty("com.mumfrey:liteloader", out var loaderEl)) return;

            foreach (var verProp in loaderEl.EnumerateObject())
            {
                var version = verProp.Name;
                var verEl = verProp.Value;

                // 取 tweakClass
                var tweakClass = verEl.TryGetProperty("tweakClass", out var tcEl) && tcEl.ValueKind == JsonValueKind.String
                    ? tcEl.GetString() ?? "com.mumfrey.liteloader.launch.LiteLoaderTweaker"
                    : "com.mumfrey.liteloader.launch.LiteLoaderTweaker";

                // 把 tweakClass 存到 RawJson 字段中（InstallAsync 用）
                result.Add(new ModLoaderVersion
                {
                    Type = ModLoaderType.LiteLoader,
                    Version = version,
                    MinecraftVersion = minecraftVersion,
                    Stable = stable,
                    Recommended = stable,
                    RawJson = tweakClass
                });
            }
        }

        /// <inheritdoc/>
        public override Task InstallAsync(
            string minecraftVersion,
            ModLoaderVersion version,
            IProgress<InstallProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            if (version == null || string.IsNullOrEmpty(version.Version))
                throw new ArgumentException("加载器版本信息无效", nameof(version));

            Logger.Info($"开始安装 LiteLoader {version.Version} for Minecraft {minecraftVersion}");

            progress?.Report(new InstallProgress
            {
                Phase = InstallPhase.DownloadingJson,
                CurrentFile = "构造 LiteLoader 版本 JSON",
                TotalFiles = 1,
                CompletedFiles = 0
            });

            var tweakClass = version.RawJson ?? "com.mumfrey.liteloader.launch.LiteLoaderTweaker";
            var versionId = $"{minecraftVersion}-liteloader-{version.Version}";

            // 构造继承版本的 JSON
            // 继承 mcVersion，主类改为 launchwrapper.Launch，加 LiteLoader 库 + --tweakClass 参数
            var versionJson = $$"""
{
  "id": "{{versionId}}",
  "time": "{{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}}",
  "releaseTime": "{{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}}",
  "type": "release",
  "inheritsFrom": "{{minecraftVersion}}",
  "mainClass": "net.minecraft.launchwrapper.Launch",
  "libraries": [
    {
      "name": "com.mumfrey:liteloader:{{version.Version}}",
      "url": "https://repo.liteloader.com/release/"
    }
  ],
  "tweakers": [
    "{{tweakClass}}"
  ],
  "minecraftArguments": "--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory} --assetsDir ${assets_root} --assetIndex ${assets_index_name} --uuid ${auth_uuid} --accessToken ${auth_access_token} --userType ${user_type} --versionType ${version_type} --tweakClass {{tweakClass}}"
}
""";

            // 写入版本目录与 JSON
            var versionDir = Path.Combine(MinecraftPath, "versions", versionId);
            Directory.CreateDirectory(versionDir);

            var jsonPath = Path.Combine(versionDir, versionId + ".json");
            File.WriteAllText(jsonPath, versionJson);

            progress?.Report(new InstallProgress
            {
                Phase = InstallPhase.Completed,
                CurrentFile = $"LiteLoader {version.Version}",
                TotalFiles = 1,
                CompletedFiles = 1
            });

            Logger.Info($"LiteLoader 版本 JSON 已写入：{jsonPath}");
            Logger.Info("LiteLoader 依赖库将在游戏启动前由 MinecraftFileDownloader 自动下载");

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override string GetVersionDirectoryPrefix(string minecraftVersion)
        {
            return $"{minecraftVersion}-liteloader-";
        }
    }
}
