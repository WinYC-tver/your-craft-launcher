using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;
using YCL.Core.Utils;
using YCL.Models.Versions;

namespace YCL.Core.Versions
{
    /// <summary>
    /// 版本管理服务实现。
    /// 职责：
    /// 1. 扫描 .minecraft/versions 目录，列出已安装版本（含类型、是否含模组加载器）
    /// 2. 从官方清单获取可下载的版本列表（按 type 分组）
    /// 3. 安装版本（下载 JSON → 解析 → 下载所有依赖文件，带进度反馈）
    /// 4. 删除版本（删整个版本目录）
    /// 5. 重命名 / 复制版本（重命名/复制目录，更新 JSON 内部 id）
    /// 6. 维护版本隔离目录（mods/saves/configs 等子目录）
    ///
    /// 依赖：
    /// - <see cref="IVersionManifestService"/>：获取在线版本清单
    /// - <see cref="IMinecraftFileDownloader"/>：下载版本 JSON 与所有依赖文件
    /// - <see cref="IVersionResolver"/>：解析版本 JSON（合并 inheritsFrom）
    ///
    /// 注意：由于 YCL.Core 不能引用 YCL.Services（会循环引用），
    /// 通过两个 Func 委托从 IConfigService 读取 MinecraftPath 与 EnableVersionIsolation，
    /// 这样配置变更能被即时感知。
    /// </summary>
    public class VersionManager : IVersionManager
    {
        /// <summary>.minecraft 路径提供者（每次调用读最新配置）</summary>
        private readonly Func<string> _minecraftPathProvider;

        /// <summary>是否启用版本隔离的提供者</summary>
        private readonly Func<bool> _enableVersionIsolationProvider;

        private readonly IVersionManifestService _manifestService;
        private readonly IMinecraftFileDownloader _fileDownloader;
        private readonly IVersionResolver _versionResolver;

        /// <summary>
        /// 构造版本管理器。
        /// </summary>
        /// <param name="minecraftPathProvider">返回当前 .minecraft 路径的委托（为空时用默认值）</param>
        /// <param name="enableVersionIsolationProvider">返回是否启用版本隔离的委托</param>
        /// <param name="manifestService">版本清单服务</param>
        /// <param name="fileDownloader">Minecraft 文件下载器</param>
        /// <param name="versionResolver">版本解析服务</param>
        public VersionManager(
            Func<string> minecraftPathProvider,
            Func<bool> enableVersionIsolationProvider,
            IVersionManifestService manifestService,
            IMinecraftFileDownloader fileDownloader,
            IVersionResolver versionResolver)
        {
            _minecraftPathProvider = minecraftPathProvider ?? throw new ArgumentNullException(nameof(minecraftPathProvider));
            _enableVersionIsolationProvider = enableVersionIsolationProvider ?? throw new ArgumentNullException(nameof(enableVersionIsolationProvider));
            _manifestService = manifestService ?? throw new ArgumentNullException(nameof(manifestService));
            _fileDownloader = fileDownloader ?? throw new ArgumentNullException(nameof(fileDownloader));
            _versionResolver = versionResolver ?? throw new ArgumentNullException(nameof(versionResolver));

            Logger.Info("版本管理服务已初始化");
        }

        /// <inheritdoc/>
        public string MinecraftPath
        {
            get
            {
                var path = _minecraftPathProvider();
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        ".minecraft");
                }
                return path;
            }
        }

        /// <summary>当前是否启用版本隔离（每次读最新配置）</summary>
        private bool EnableVersionIsolation => _enableVersionIsolationProvider();

        /// <inheritdoc/>
        public async Task<List<InstalledVersionInfo>> ListInstalledVersionsAsync(CancellationToken cancellationToken = default)
        {
            var result = new List<InstalledVersionInfo>();
            var minecraftPath = MinecraftPath;
            var versionsDir = Path.Combine(minecraftPath, "versions");

            if (!Directory.Exists(versionsDir))
            {
                Logger.Info($"versions 目录不存在：{versionsDir}（尚未安装任何版本）");
                return result;
            }

            // 切到后台线程执行目录扫描与 JSON 读取，避免阻塞 UI
            await Task.Run(() =>
            {
                foreach (var dir in Directory.GetDirectories(versionsDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var dirName = Path.GetFileName(dir);
                    // 优先查找 <目录名>.json
                    var jsonPath = Path.Combine(dir, dirName + ".json");
                    if (!File.Exists(jsonPath))
                    {
                        // 回退：找目录下任意 .json 文件（兼容 Forge/Fabric 等命名情况）
                        var jsonFiles = Directory.GetFiles(dir, "*.json");
                        if (jsonFiles.Length == 0) continue;
                        jsonPath = jsonFiles[0];
                    }

                    var info = ReadInstalledVersionInfo(dirName, dir, jsonPath);
                    if (info != null)
                        result.Add(info);
                }
            }, cancellationToken);

            result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            Logger.Info($"扫描到 {result.Count} 个已安装版本：{versionsDir}");
            return result;
        }

        /// <inheritdoc/>
        public async Task<Dictionary<string, List<VersionManifestEntry>>> ListAvailableVersionsAsync(CancellationToken cancellationToken = default)
        {
            var versions = await _manifestService.GetVersionsAsync(cancellationToken);

            // 按 type 分组
            var grouped = new Dictionary<string, List<VersionManifestEntry>>(StringComparer.OrdinalIgnoreCase)
            {
                ["release"] = new List<VersionManifestEntry>(),
                ["snapshot"] = new List<VersionManifestEntry>(),
                ["old_beta"] = new List<VersionManifestEntry>(),
                ["old_alpha"] = new List<VersionManifestEntry>()
            };

            foreach (var v in versions)
            {
                var type = v.Type ?? "release";
                if (!grouped.ContainsKey(type))
                    grouped[type] = new List<VersionManifestEntry>();
                grouped[type].Add(v);
            }

            Logger.Info($"获取到 {versions.Count} 个在线版本（release={grouped["release"].Count}, " +
                        $"snapshot={grouped["snapshot"].Count}, old_beta={grouped["old_beta"].Count}, " +
                        $"old_alpha={grouped["old_alpha"].Count}）");
            return grouped;
        }

        /// <inheritdoc/>
        public async Task<bool> InstallVersionAsync(
            VersionManifestEntry entry,
            IProgress<InstallProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.Id)) throw new ArgumentException("版本条目缺少 id", nameof(entry));
            if (string.IsNullOrEmpty(entry.Url)) throw new ArgumentException("版本条目缺少 url", nameof(entry));

            var minecraftPath = MinecraftPath;
            var versionId = entry.Id;

            // 确保根目录存在
            Directory.CreateDirectory(minecraftPath);

            Logger.Info($"开始安装版本：{versionId}");

            // 阶段 1：下载版本 JSON
            progress?.Report(new InstallProgress
            {
                Phase = InstallPhase.DownloadingJson,
                CurrentFile = versionId + ".json",
                TotalFiles = 1,
                CompletedFiles = 0
            });

            await _fileDownloader.DownloadVersionJsonAsync(entry, minecraftPath, cancellationToken);

            // 阶段 2：解析版本（合并 inheritsFrom）
            progress?.Report(new InstallProgress
            {
                Phase = InstallPhase.Parsing,
                CurrentFile = versionId + ".json",
                TotalFiles = 1,
                CompletedFiles = 1
            });

            var resolved = await Task.Run(() => _versionResolver.Resolve(minecraftPath, versionId), cancellationToken);
            var versionInfo = resolved.Info;

            // 阶段 3：下载版本文件（第一阶段：client.jar / libraries / assetIndex / logging）
            progress?.Report(new InstallProgress
            {
                Phase = InstallPhase.DownloadingFiles,
                CurrentFile = "client.jar / libraries / assetIndex"
            });

            // 订阅下载器的整体进度事件，转发为 InstallProgress
            EventHandler<BatchDownloadProgressEventArgs> progressHandler = (sender, e) =>
            {
                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.DownloadingFiles,
                    CurrentFile = $"下载中：{e.CompletedFiles}/{e.TotalFiles}",
                    CompletedFiles = e.CompletedFiles,
                    TotalFiles = e.TotalFiles,
                    CompletedBytes = e.DownloadedBytes,
                    TotalBytes = e.TotalBytes
                });
            };

            _fileDownloader.ProgressChanged += progressHandler;
            try
            {
                // 第一阶段：下载 client.jar / libraries / assetIndex / logging
                var result1 = await _fileDownloader.DownloadVersionAsync(versionInfo, minecraftPath, cancellationToken);
                if (result1.IsCanceled)
                {
                    Logger.Info($"版本 {versionId} 安装被取消");
                    return false;
                }

                // 阶段 4：下载资源文件（第二阶段：assets objects，需要 assetIndex 先下载完成）
                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.DownloadingAssets,
                    CurrentFile = "assets objects"
                });

                var result2 = await _fileDownloader.DownloadVersionAsync(versionInfo, minecraftPath, cancellationToken);

                // 汇总结果
                var totalSuccess = result1.SuccessFiles + result2.SuccessFiles;
                var totalFailed = result1.FailedFiles + result2.FailedFiles;
                var totalFiles = result1.TotalFiles + result2.TotalFiles;

                if (result2.IsCanceled)
                {
                    Logger.Info($"版本 {versionId} 安装被取消（assets 阶段）");
                    return false;
                }

                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.Completed,
                    CurrentFile = versionId,
                    CompletedFiles = totalSuccess,
                    TotalFiles = totalFiles
                });

                if (totalFailed > 0)
                {
                    Logger.Warn($"版本 {versionId} 安装结束（成功 {totalSuccess}，失败 {totalFailed}）");
                    return false;
                }

                Logger.Info($"版本 {versionId} 安装完成（共 {totalSuccess} 个文件）");
                return true;
            }
            finally
            {
                _fileDownloader.ProgressChanged -= progressHandler;
            }
        }

        /// <inheritdoc/>
        public Task<bool> DeleteVersionAsync(string versionId)
        {
            if (string.IsNullOrEmpty(versionId))
            {
                Logger.Warn("删除版本失败：versionId 为空");
                return Task.FromResult(false);
            }

            // 不允许通过相对路径或 .. 跳出 versions 目录
            if (versionId.Contains('/') || versionId.Contains('\\') ||
                versionId.Contains("..", StringComparison.Ordinal))
            {
                Logger.Warn($"删除版本失败：versionId 含非法字符 {versionId}");
                return Task.FromResult(false);
            }

            var versionDir = Path.Combine(MinecraftPath, "versions", versionId);
            if (!Directory.Exists(versionDir))
            {
                Logger.Warn($"删除版本失败：目录不存在 {versionDir}");
                return Task.FromResult(false);
            }

            try
            {
                Directory.Delete(versionDir, recursive: true);
                Logger.Info($"已删除版本：{versionId}（{versionDir}）");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Logger.Error($"删除版本失败：{versionId}", ex);
                return Task.FromResult(false);
            }
        }

        /// <inheritdoc/>
        public Task<bool> RenameVersionAsync(string oldId, string newId)
        {
            if (string.IsNullOrWhiteSpace(oldId) || string.IsNullOrWhiteSpace(newId))
            {
                Logger.Warn("重命名版本失败：oldId 或 newId 为空");
                return Task.FromResult(false);
            }
            if (string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(true);
            }
            if (newId.Contains('/') || newId.Contains('\\') || newId.Contains("..", StringComparison.Ordinal) ||
                newId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Logger.Warn($"重命名版本失败：newId 含非法字符 {newId}");
                return Task.FromResult(false);
            }

            var minecraftPath = MinecraftPath;
            var oldDir = Path.Combine(minecraftPath, "versions", oldId);
            var newDir = Path.Combine(minecraftPath, "versions", newId);

            if (!Directory.Exists(oldDir))
            {
                Logger.Warn($"重命名版本失败：源目录不存在 {oldDir}");
                return Task.FromResult(false);
            }
            if (Directory.Exists(newDir))
            {
                Logger.Warn($"重命名版本失败：目标目录已存在 {newDir}");
                return Task.FromResult(false);
            }

            try
            {
                // 1. 重命名版本目录
                Directory.Move(oldDir, newDir);

                // 2. 重命名 JSON 文件（<oldId>.json → <newId>.json）
                var oldJson = Path.Combine(newDir, oldId + ".json");
                var newJson = Path.Combine(newDir, newId + ".json");
                if (File.Exists(oldJson))
                {
                    File.Move(oldJson, newJson, overwrite: true);
                    UpdateVersionIdInJson(newJson, newId);
                }
                else
                {
                    // 回退：找目录下任意 .json
                    var jsonFiles = Directory.GetFiles(newDir, "*.json");
                    if (jsonFiles.Length > 0)
                    {
                        File.Move(jsonFiles[0], newJson, overwrite: true);
                        UpdateVersionIdInJson(newJson, newId);
                    }
                }

                // 3. 重命名 jar 文件（如果存在）
                var oldJar = Path.Combine(newDir, oldId + ".jar");
                var newJar = Path.Combine(newDir, newId + ".jar");
                if (File.Exists(oldJar))
                {
                    File.Move(oldJar, newJar, overwrite: true);
                }

                Logger.Info($"已重命名版本：{oldId} → {newId}");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Logger.Error($"重命名版本失败：{oldId} → {newId}", ex);
                return Task.FromResult(false);
            }
        }

        /// <inheritdoc/>
        public Task<bool> CopyVersionAsync(string sourceId, string newId)
        {
            if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(newId))
            {
                Logger.Warn("复制版本失败：sourceId 或 newId 为空");
                return Task.FromResult(false);
            }
            if (string.Equals(sourceId, newId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warn("复制版本失败：sourceId 与 newId 相同");
                return Task.FromResult(false);
            }
            if (newId.Contains('/') || newId.Contains('\\') || newId.Contains("..", StringComparison.Ordinal) ||
                newId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Logger.Warn($"复制版本失败：newId 含非法字符 {newId}");
                return Task.FromResult(false);
            }

            var minecraftPath = MinecraftPath;
            var sourceDir = Path.Combine(minecraftPath, "versions", sourceId);
            var newDir = Path.Combine(minecraftPath, "versions", newId);

            if (!Directory.Exists(sourceDir))
            {
                Logger.Warn($"复制版本失败：源目录不存在 {sourceDir}");
                return Task.FromResult(false);
            }
            if (Directory.Exists(newDir))
            {
                Logger.Warn($"复制版本失败：目标目录已存在 {newDir}");
                return Task.FromResult(false);
            }

            try
            {
                // 1. 递归复制整个版本目录（跳过 natives-xxx 临时解压目录）
                CopyDirectory(sourceDir, newDir);

                // 2. 重命名 JSON 文件（复制后还是源 id 名字）
                var oldJson = Path.Combine(newDir, sourceId + ".json");
                var newJson = Path.Combine(newDir, newId + ".json");
                if (File.Exists(oldJson))
                {
                    File.Move(oldJson, newJson, overwrite: true);
                    UpdateVersionIdInJson(newJson, newId);
                }
                else
                {
                    var jsonFiles = Directory.GetFiles(newDir, "*.json");
                    if (jsonFiles.Length > 0)
                    {
                        File.Move(jsonFiles[0], newJson, overwrite: true);
                        UpdateVersionIdInJson(newJson, newId);
                    }
                }

                // 3. 重命名 jar 文件（如果存在）
                var oldJar = Path.Combine(newDir, sourceId + ".jar");
                var newJar = Path.Combine(newDir, newId + ".jar");
                if (File.Exists(oldJar))
                {
                    File.Move(oldJar, newJar, overwrite: true);
                }

                Logger.Info($"已复制版本：{sourceId} → {newId}");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Logger.Error($"复制版本失败：{sourceId} → {newId}", ex);
                // 清理失败的复制（避免残留半成品目录）
                try { if (Directory.Exists(newDir)) Directory.Delete(newDir, recursive: true); }
                catch { /* 忽略清理失败 */ }
                return Task.FromResult(false);
            }
        }

        /// <inheritdoc/>
        public void EnsureIsolationDirectories(string versionId)
        {
            if (string.IsNullOrEmpty(versionId)) return;

            // 仅在启用版本隔离时创建（禁用时游戏目录是 .minecraft，子目录自然存在）
            if (!EnableVersionIsolation) return;

            var gameDir = GetGameDirectory(versionId);
            Directory.CreateDirectory(gameDir);

            // 创建常用的游戏子目录（不存在则创建，存在则无害）
            foreach (var sub in s_isolationSubDirs)
            {
                Directory.CreateDirectory(Path.Combine(gameDir, sub));
            }

            Logger.Info($"已确保版本隔离目录存在：{gameDir}");
        }

        /// <inheritdoc/>
        public string GetGameDirectory(string versionId)
        {
            if (string.IsNullOrEmpty(versionId)) return MinecraftPath;

            if (EnableVersionIsolation)
            {
                return Path.Combine(MinecraftPath, "versions", versionId);
            }
            return MinecraftPath;
        }

        /// <summary>版本隔离时自动创建的子目录列表</summary>
        private static readonly string[] s_isolationSubDirs =
        {
            "mods", "saves", "configs", "resourcepacks", "shaderpacks",
            "options", "logs", "screenshot"
        };

        /// <summary>
        /// 读取单个已安装版本的信息。
        /// 用 JsonDocument 轻量解析，只取 id/type/inheritsFrom/libraries 字段。
        /// </summary>
        private InstalledVersionInfo? ReadInstalledVersionInfo(string dirName, string dir, string jsonPath)
        {
            try
            {
                var json = File.ReadAllText(jsonPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // id：优先从 JSON 取，没有则用目录名
                string id = dirName;
                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    var jsonId = idEl.GetString();
                    if (!string.IsNullOrEmpty(jsonId)) id = jsonId;
                }

                // type：默认 release
                string type = "release";
                if (root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
                {
                    var t = typeEl.GetString();
                    if (!string.IsNullOrEmpty(t)) type = t;
                }

                // inheritsFrom
                string? inheritsFrom = null;
                if (root.TryGetProperty("inheritsFrom", out var inhEl) && inhEl.ValueKind == JsonValueKind.String)
                {
                    inheritsFrom = inhEl.GetString();
                }

                // libraries：只取 name 字段，用于判断模组加载器
                List<string> libNames = new();
                if (root.TryGetProperty("libraries", out var libsEl) && libsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var libEl in libsEl.EnumerateArray())
                    {
                        if (libEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        {
                            var n = nameEl.GetString();
                            if (!string.IsNullOrEmpty(n)) libNames.Add(n);
                        }
                    }
                }

                var (isModded, loaderName) = DetectModLoader(libNames);

                return new InstalledVersionInfo
                {
                    Id = id,
                    Type = type,
                    Directory = dir,
                    JsonPath = jsonPath,
                    HasInheritsFrom = !string.IsNullOrEmpty(inheritsFrom),
                    ParentVersionId = inheritsFrom,
                    IsModded = isModded,
                    ModLoaderName = loaderName
                };
            }
            catch (Exception ex)
            {
                Logger.Warn($"读取版本 JSON 失败：{jsonPath} - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 根据库的 Maven 坐标名判断是否含模组加载器。
        /// 检查特征前缀：Forge / NeoForge / Fabric / Quilt / LiteLoader。
        /// </summary>
        private static (bool isModded, string? loaderName) DetectModLoader(List<string> libraryNames)
        {
            foreach (var name in libraryNames)
            {
                if (string.IsNullOrEmpty(name)) continue;

                if (name.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase))
                    return (true, "Forge");
                if (name.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase))
                    return (true, "NeoForge");
                if (name.Contains("net.fabricmc", StringComparison.OrdinalIgnoreCase))
                    return (true, "Fabric");
                if (name.Contains("org.quiltmc", StringComparison.OrdinalIgnoreCase))
                    return (true, "Quilt");
                if (name.Contains("com.mumfrey", StringComparison.OrdinalIgnoreCase))
                    return (true, "LiteLoader");
            }
            return (false, null);
        }

        /// <summary>
        /// 更新版本 JSON 内部的 id 字段为新的版本 id。
        /// 用 JsonNode（可变 DOM）修改后重新序列化写回。
        /// </summary>
        private void UpdateVersionIdInJson(string jsonPath, string newId)
        {
            try
            {
                var json = File.ReadAllText(jsonPath);
                var node = JsonNode.Parse(json);
                if (node == null)
                {
                    Logger.Warn($"更新版本 JSON id 失败：解析结果为 null - {jsonPath}");
                    return;
                }

                node["id"] = newId;

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(jsonPath, node.ToJsonString(options));
                Logger.Debug($"已更新版本 JSON 的 id 字段：{jsonPath} → {newId}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"更新版本 JSON id 失败：{jsonPath} - {ex.Message}");
            }
        }

        /// <summary>
        /// 递归复制目录（跳过 natives-xxx 临时解压目录）。
        /// </summary>
        private void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            // 复制所有文件
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
            }

            // 递归复制子目录（跳过 natives-xxx 临时目录）
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var dirName = Path.GetFileName(dir);
                if (dirName.StartsWith("natives-", StringComparison.OrdinalIgnoreCase))
                    continue;
                CopyDirectory(dir, Path.Combine(destDir, dirName));
            }
        }
    }
}
