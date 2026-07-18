using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;
using YCL.Core.Mods;
using YCL.Core.Utils;

namespace YCL.Core.Resources
{
    /// <summary>
    /// 资源服务实现：搜索并下载资源包、光影包、地图。
    ///
    /// 实现要点：
    /// - 搜索复用 ModrinthClient.SearchModsAsync，传不同的 projectType
    ///   （resourcepack / shader / world）
    /// - 资源包与光影包：直接下载 zip 文件到对应目录，不解压
    /// - 地图：下载 zip 后解压到 saves/&lt;mapName&gt;/
    /// </summary>
    public class ResourceService : IResourceService
    {
        private readonly IModrinthClient _modrinthClient;
        private readonly ICurseForgeClient _curseForgeClient;
        private readonly MultiThreadDownloader _downloader;

        public ResourceService(
            IModrinthClient modrinthClient,
            ICurseForgeClient curseForgeClient,
            MultiThreadDownloader downloader)
        {
            _modrinthClient = modrinthClient ?? throw new ArgumentNullException(nameof(modrinthClient));
            _curseForgeClient = curseForgeClient ?? throw new ArgumentNullException(nameof(curseForgeClient));
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        }

        /// <inheritdoc/>
        public async Task<List<ModSearchResult>> SearchResourcesAsync(
            string query, ResourceType type, ModSource source,
            string? gameVersion = null,
            CancellationToken cancellationToken = default)
        {
            // 资源类型对应 Modrinth 的 project_type
            var projectType = type switch
            {
                ResourceType.ResourcePack => "resourcepack",
                ResourceType.ShaderPack => "shader",
                ResourceType.World => "modpack", // Modrinth 把地图归在 modpack 类型下（部分地图）
                _ => "resourcepack"
            };

            // CurseForge 的 classId（资源包=12, 光影=6552, 地图=17）
            // 但 CurseForgeClient.SearchModsAsync 写死 classId=6（mod），这里仅用 Modrinth 搜索
            // 如果需要 CurseForge 资源搜索，应扩展 ICurseForgeClient，这里简化处理

            var results = new List<ModSearchResult>();

            // 决定搜索的平台
            bool searchCurseForge = (source == ModSource.CurseForge || source == ModSource.All) && _curseForgeClient.IsConfigured;
            bool searchModrinth = source == ModSource.Modrinth || source == ModSource.All;

            var tasks = new List<Task<List<ModSearchResult>>>();

            if (searchModrinth)
            {
                tasks.Add(SearchModrinthResourcesAsync(query, projectType, gameVersion, cancellationToken));
            }

            // CurseForge 资源搜索需要扩展 API（不同的 classId），暂不实现，仅用 Modrinth
            // 如果未来扩展 ICurseForgeClient 支持资源搜索，可在此添加

            var lists = await Task.WhenAll(tasks);
            foreach (var list in lists)
                results.AddRange(list);

            // 按下载次数排序
            results.Sort((a, b) => b.DownloadCount.CompareTo(a.DownloadCount));

            Logger.Info($"资源搜索 \"{query}\"（type={type}）返回 {results.Count} 个结果");
            return results;
        }

        /// <inheritdoc/>
        public async Task<string> DownloadResourcePackAsync(
            ModSearchResult result, string gameDir,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return await DownloadResourceAsync(result, gameDir, "resourcepacks", progress, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<string> DownloadShaderPackAsync(
            ModSearchResult result, string gameDir,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return await DownloadResourceAsync(result, gameDir, "shaderpacks", progress, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<string> DownloadMapAsync(
            ModSearchResult result, string gameDir,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default)
        {
            // 地图下载后需要解压到 saves/<mapName>/
            var tempZipPath = Path.Combine(Path.GetTempPath(), "YCL_Map_" + Guid.NewGuid().ToString("N") + ".zip");

            try
            {
                // 1. 下载 zip 到临时文件
                await DownloadToFileAsync(result, tempZipPath, progress, cancellationToken);

                // 2. 解压到 saves/<mapName>/
                var savesDir = Path.Combine(gameDir, "saves");
                Directory.CreateDirectory(savesDir);

                var mapName = SanitizeFolderName(result.Name);
                var mapDir = Path.Combine(savesDir, mapName);

                // 如果目录已存在，先删除（覆盖）
                if (Directory.Exists(mapDir))
                {
                    Directory.Delete(mapDir, recursive: true);
                }
                Directory.CreateDirectory(mapDir);

                // 解压 zip
                await Task.Run(() => ZipFile.ExtractToDirectory(tempZipPath, mapDir, overwriteFiles: true), cancellationToken);

                // 部分地图 zip 内还有一层顶层目录，需要"提升"一层
                ElevateSingleSubdirectory(mapDir);

                Logger.Info($"地图已解压到：{mapDir}");
                return mapDir;
            }
            finally
            {
                // 清理临时文件
                try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); }
                catch { /* 忽略 */ }
            }
        }

        /// <summary>下载资源（资源包/光影包）到指定子目录</summary>
        private async Task<string> DownloadResourceAsync(
            ModSearchResult result, string gameDir, string subDir,
            IProgress<DownloadProgressEventArgs>? progress,
            CancellationToken cancellationToken)
        {
            var targetDir = Path.Combine(gameDir, subDir);
            Directory.CreateDirectory(targetDir);

            // 获取最新版本的下载 URL
            var (downloadUrl, fileName) = await GetLatestVersionDownloadUrlAsync(result, cancellationToken);
            if (string.IsNullOrEmpty(downloadUrl))
            {
                throw new InvalidOperationException($"无法获取资源 {result.Name} 的下载 URL");
            }

            if (string.IsNullOrEmpty(fileName))
            {
                fileName = SanitizeFileName(result.Name) + ".zip";
            }
            else
            {
                fileName = SanitizeFileName(fileName);
            }

            var targetPath = Path.Combine(targetDir, fileName);
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            // 订阅下载器进度
            EventHandler<DownloadProgressEventArgs> handler = (s, e) => progress?.Report(e);
            _downloader.ProgressChanged += handler;
            try
            {
                await _downloader.DownloadAsync(downloadUrl, targetPath, cancellationToken);
                Logger.Info($"资源已下载：{result.Name} → {targetPath}");
                return targetPath;
            }
            finally
            {
                _downloader.ProgressChanged -= handler;
            }
        }

        /// <summary>下载资源到指定路径（用于地图下载到临时文件）</summary>
        private async Task DownloadToFileAsync(
            ModSearchResult result, string targetPath,
            IProgress<DownloadProgressEventArgs>? progress,
            CancellationToken cancellationToken)
        {
            var (downloadUrl, _) = await GetLatestVersionDownloadUrlAsync(result, cancellationToken);
            if (string.IsNullOrEmpty(downloadUrl))
            {
                throw new InvalidOperationException($"无法获取资源 {result.Name} 的下载 URL");
            }

            EventHandler<DownloadProgressEventArgs> handler = (s, e) => progress?.Report(e);
            _downloader.ProgressChanged += handler;
            try
            {
                await _downloader.DownloadAsync(downloadUrl, targetPath, cancellationToken);
            }
            finally
            {
                _downloader.ProgressChanged -= handler;
            }
        }

        /// <summary>获取资源最新版本的下载 URL 与文件名</summary>
        private async Task<(string? url, string? fileName)> GetLatestVersionDownloadUrlAsync(
            ModSearchResult result, CancellationToken ct)
        {
            if (result.Source == ModSource.Modrinth)
            {
                var versions = await _modrinthClient.GetProjectVersionsAsync(result.ProjectId, ct);
                var latest = versions.FirstOrDefault();
                if (latest?.Files != null && latest.Files.Count > 0)
                {
                    var file = latest.Files.FirstOrDefault(f => f.Primary) ?? latest.Files[0];
                    return (file.Url, file.Filename);
                }
            }
            else if (result.Source == ModSource.CurseForge && _curseForgeClient.IsConfigured &&
                     int.TryParse(result.ProjectId, out var modId))
            {
                var files = await _curseForgeClient.GetModFilesAsync(modId, ct);
                var latest = files.FirstOrDefault();
                if (latest != null)
                {
                    return (latest.DownloadUrl, latest.FileName);
                }
            }

            return (null, null);
        }

        /// <summary>搜索 Modrinth 资源并归一化结果</summary>
        private async Task<List<ModSearchResult>> SearchModrinthResourcesAsync(
            string query, string projectType, string? gameVersion, CancellationToken ct)
        {
            var results = new List<ModSearchResult>();
            try
            {
                var hits = await _modrinthClient.SearchModsAsync(
                    query, projectType, null, gameVersion, 0, 30, ct);
                foreach (var h in hits)
                {
                    results.Add(new ModSearchResult
                    {
                        Source = ModSource.Modrinth,
                        ProjectId = h.ProjectId,
                        Name = h.Title,
                        Description = h.Description,
                        DownloadCount = h.Downloads,
                        LogoUrl = h.IconUrl,
                        WebsiteUrl = $"https://modrinth.com/{projectType}/{h.Slug}",
                        Author = h.Author
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Modrinth 资源搜索失败：{ex.Message}");
            }
            return results;
        }

        /// <summary>如果解压后的目录只有一个子目录，把这个子目录的内容"提升"到外层目录</summary>
        private static void ElevateSingleSubdirectory(string dir)
        {
            try
            {
                var subDirs = Directory.GetDirectories(dir);
                var files = Directory.GetFiles(dir);
                // 仅当外层目录无文件且只有一个子目录时提升
                if (files.Length == 0 && subDirs.Length == 1)
                {
                    var subDir = subDirs[0];
                    var subDirName = Path.GetFileName(subDir);

                    // 把子目录的内容移到外层
                    foreach (var f in Directory.GetFiles(subDir))
                    {
                        File.Move(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true);
                    }
                    foreach (var sd in Directory.GetDirectories(subDir))
                    {
                        Directory.Move(sd, Path.Combine(dir, Path.GetFileName(sd)));
                    }
                    Directory.Delete(subDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"提升子目录失败（不影响安装）：{ex.Message}");
            }
        }

        /// <summary>清理文件名中的非法字符</summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "resource.zip";
            var invalid = Path.GetInvalidFileNameChars();
            var result = name;
            foreach (var c in invalid)
                result = result.Replace(c, '_');
            return result;
        }

        /// <summary>清理文件夹名中的非法字符</summary>
        private static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "World";
            var invalid = Path.GetInvalidPathChars();
            var result = name;
            foreach (var c in invalid)
                result = result.Replace(c, '_');
            // 移除一些可能在路径中引起问题的字符
            foreach (var c in new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
                result = result.Replace(c, '_');
            return result.Trim();
        }
    }
}
