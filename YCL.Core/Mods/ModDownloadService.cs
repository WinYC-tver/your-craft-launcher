using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;
using YCL.Core.Utils;

namespace YCL.Core.Mods
{
    /// <summary>
    /// 模组下载服务实现。
    /// 统一封装 CurseForge 与 Modrinth 两个平台的搜索与下载。
    ///
    /// 设计要点：
    /// - CurseForge 未配置 Key 时自动降级到 Modrinth
    /// - 搜索结果归一化为 ModSearchResult，UI 无需关心数据来源
    /// - 版本列表归一化为 ModVersionInfo
    /// - 下载复用 MultiThreadDownloader（与版本下载共用基础设施）
    /// - 文件下载到 gameDir/mods/ 目录，文件名取自远端文件名
    /// </summary>
    public class ModDownloadService : IModDownloadService
    {
        private readonly ICurseForgeClient _curseForgeClient;
        private readonly IModrinthClient _modrinthClient;
        private readonly ILocalModManager _localModManager;

        public ModDownloadService(
            ICurseForgeClient curseForgeClient,
            IModrinthClient modrinthClient,
            ILocalModManager localModManager)
        {
            _curseForgeClient = curseForgeClient ?? throw new ArgumentNullException(nameof(curseForgeClient));
            _modrinthClient = modrinthClient ?? throw new ArgumentNullException(nameof(modrinthClient));
            _localModManager = localModManager ?? throw new ArgumentNullException(nameof(localModManager));
        }

        /// <inheritdoc/>
        public async Task<List<ModSearchResult>> SearchAsync(
            string query, ModSource source,
            string? loaderType = null, string? gameVersion = null,
            CancellationToken cancellationToken = default)
        {
            var results = new List<ModSearchResult>();

            // 决定实际搜索的平台：CurseForge 未配置 Key 时自动降级到 Modrinth
            bool searchCurseForge = source == ModSource.CurseForge || source == ModSource.All;
            bool searchModrinth = source == ModSource.Modrinth || source == ModSource.All;
            if (searchCurseForge && !_curseForgeClient.IsConfigured)
            {
                Logger.Info("CurseForge 未配置 Key，搜索降级到 Modrinth");
                searchCurseForge = false;
                searchModrinth = true;
            }

            // 并行搜索两个平台
            var tasks = new List<Task<List<ModSearchResult>>>();

            if (searchCurseForge)
            {
                tasks.Add(SearchCurseForgeAsync(query, cancellationToken));
            }
            if (searchModrinth)
            {
                tasks.Add(SearchModrinthAsync(query, loaderType, gameVersion, cancellationToken));
            }

            var lists = await Task.WhenAll(tasks);
            foreach (var list in lists)
                results.AddRange(list);

            // 按下载次数排序
            results.Sort((a, b) => b.DownloadCount.CompareTo(a.DownloadCount));

            Logger.Info($"模组搜索 \"{query}\" 共返回 {results.Count} 个结果");
            return results;
        }

        /// <inheritdoc/>
        public async Task<List<ModVersionInfo>> GetVersionsAsync(
            ModSearchResult result, CancellationToken cancellationToken = default)
        {
            if (result == null) return new List<ModVersionInfo>();

            try
            {
                if (result.Source == ModSource.CurseForge && int.TryParse(result.ProjectId, out var modId))
                {
                    var files = await _curseForgeClient.GetModFilesAsync(modId, cancellationToken);
                    return files.Select(f => new ModVersionInfo
                    {
                        Source = ModSource.CurseForge,
                        VersionId = f.Id.ToString(),
                        Name = f.DisplayName,
                        FileName = f.FileName,
                        DownloadUrl = f.DownloadUrl,
                        GameVersions = f.GameVersions,
                        Loaders = new List<string>(), // CurseForge 文件列表不直接含加载器信息
                        DatePublished = f.FileDate,
                        Downloads = 0,
                        VersionType = f.ReleaseType switch
                        {
                            1 => "release",
                            2 => "beta",
                            3 => "alpha",
                            _ => "release"
                        }
                    }).ToList();
                }

                if (result.Source == ModSource.Modrinth)
                {
                    var versions = await _modrinthClient.GetProjectVersionsAsync(result.ProjectId, cancellationToken);
                    return versions.Select(v => new ModVersionInfo
                    {
                        Source = ModSource.Modrinth,
                        VersionId = v.Id,
                        Name = v.DisplayName,
                        FileName = v.Files.FirstOrDefault()?.Filename ?? v.DisplayName,
                        DownloadUrl = v.Files.FirstOrDefault()?.Url,
                        GameVersions = v.GameVersions,
                        Loaders = v.Loaders,
                        DatePublished = v.DatePublished,
                        Downloads = v.Downloads,
                        VersionType = v.VersionType
                    }).ToList();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"获取模组版本列表失败：{result.Name}", ex);
            }

            return new List<ModVersionInfo>();
        }

        /// <inheritdoc/>
        public async Task<string> DownloadModAsync(
            ModSearchResult result, string gameDir,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            // 先获取版本列表，选第一个（最新）版本下载
            var versions = await GetVersionsAsync(result, cancellationToken);
            if (versions.Count == 0)
            {
                throw new InvalidOperationException($"模组 {result.Name} 没有可用的版本");
            }

            // 选最新版本（GetVersionsAsync 返回的顺序已经是按时间倒序）
            var latest = versions[0];
            return await DownloadVersionAsync(result, latest, gameDir, progress, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<string> DownloadVersionAsync(
            ModSearchResult result, ModVersionInfo version, string gameDir,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (version == null) throw new ArgumentNullException(nameof(version));

            // 确保 mods 目录存在
            var modsDir = _localModManager.GetModsDirectory(gameDir);
            Directory.CreateDirectory(modsDir);

            // 文件名：优先用 version.FileName，没有则用 result.Name + .jar
            var fileName = !string.IsNullOrWhiteSpace(version.FileName)
                ? version.FileName
                : SanitizeFileName(result.Name) + ".jar";
            // 防止文件名含非法字符或路径分隔符
            fileName = SanitizeFileName(fileName);
            var targetPath = Path.Combine(modsDir, fileName);

            // 如果目标文件已存在，先删除（覆盖）
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            if (version.Source == ModSource.CurseForge)
            {
                // 构造 CurseForgeFile 对象调用客户端下载
                var cfFile = new CurseForgeFile
                {
                    Id = int.TryParse(version.VersionId, out var fid) ? fid : 0,
                    FileName = version.FileName,
                    DisplayName = version.Name,
                    DownloadUrl = version.DownloadUrl
                };
                await _curseForgeClient.DownloadModFileAsync(cfFile, targetPath, progress, cancellationToken);
            }
            else if (version.Source == ModSource.Modrinth)
            {
                if (string.IsNullOrWhiteSpace(version.DownloadUrl))
                {
                    throw new InvalidOperationException("Modrinth 版本缺少下载 URL");
                }
                await _modrinthClient.DownloadModFileAsync(version.DownloadUrl, targetPath, progress, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException($"不支持的模组来源：{version.Source}");
            }

            Logger.Info($"模组已下载到：{targetPath}");
            return targetPath;
        }

        /// <summary>搜索 CurseForge 并归一化结果</summary>
        private async Task<List<ModSearchResult>> SearchCurseForgeAsync(
            string query, CancellationToken ct)
        {
            var results = new List<ModSearchResult>();
            try
            {
                var mods = await _curseForgeClient.SearchModsAsync(query, 0, 20, ct);
                foreach (var m in mods)
                {
                    results.Add(new ModSearchResult
                    {
                        Source = ModSource.CurseForge,
                        ProjectId = m.Id.ToString(),
                        Name = m.Name,
                        Description = m.Summary,
                        DownloadCount = m.DownloadCount,
                        LogoUrl = m.LogoUrl,
                        WebsiteUrl = m.WebsiteUrl,
                        Author = string.Empty
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"CurseForge 搜索失败：{ex.Message}");
            }
            return results;
        }

        /// <summary>搜索 Modrinth 并归一化结果</summary>
        private async Task<List<ModSearchResult>> SearchModrinthAsync(
            string query, string? loaderType, string? gameVersion, CancellationToken ct)
        {
            var results = new List<ModSearchResult>();
            try
            {
                var hits = await _modrinthClient.SearchModsAsync(
                    query, "mod", loaderType, gameVersion, 0, 20, ct);
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
                        WebsiteUrl = $"https://modrinth.com/mod/{h.Slug}",
                        Author = h.Author
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Modrinth 搜索失败：{ex.Message}");
            }
            return results;
        }

        /// <summary>清理文件名中的非法字符</summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "mod.jar";
            var invalid = Path.GetInvalidFileNameChars();
            var result = name;
            foreach (var c in invalid)
                result = result.Replace(c, '_');
            return result;
        }
    }
}
