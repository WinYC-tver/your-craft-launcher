using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;
using YCL.Core.ModLoaders;
using YCL.Core.Mods;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Models.Versions;

namespace YCL.Core.Resources
{
    /// <summary>
    /// 整合包服务实现。
    ///
    /// 支持 CurseForge 与 Modrinth 两种整合包格式：
    /// 1. CurseForge 整合包：
    ///    - manifest.json：含 minecraft.version、minecraft.modLoaders、files（projectID+fileID）
    ///    - overrides/：覆盖到游戏目录的文件
    /// 2. Modrinth 整合包：
    ///    - modrinth.index.json：含 dependencies.minecraft、dependencies.{loader}、files（path+downloads+hashes）
    ///    - overrides/：覆盖到游戏目录的文件
    ///
    /// 安装流程：解压 → 解析清单 → 安装 MC 版本 → 安装加载器 → 下载模组 → 覆盖 overrides
    /// </summary>
    public class ModpackService : IModpackService
    {
        private readonly IVersionManager _versionManager;
        private readonly IModLoaderManager _modLoaderManager;
        private readonly IVersionManifestService _manifestService;
        private readonly ICurseForgeClient _curseForgeClient;
        private readonly IModrinthClient _modrinthClient;
        private readonly MultiThreadDownloader _downloader;

        public ModpackService(
            IVersionManager versionManager,
            IModLoaderManager modLoaderManager,
            IVersionManifestService manifestService,
            ICurseForgeClient curseForgeClient,
            IModrinthClient modrinthClient,
            MultiThreadDownloader downloader)
        {
            _versionManager = versionManager ?? throw new ArgumentNullException(nameof(versionManager));
            _modLoaderManager = modLoaderManager ?? throw new ArgumentNullException(nameof(modLoaderManager));
            _manifestService = manifestService ?? throw new ArgumentNullException(nameof(manifestService));
            _curseForgeClient = curseForgeClient ?? throw new ArgumentNullException(nameof(curseForgeClient));
            _modrinthClient = modrinthClient ?? throw new ArgumentNullException(nameof(modrinthClient));
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        }

        /// <inheritdoc/>
        public async Task DownloadModpackAsync(
            string url, string targetPath,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("下载 URL 不能为空", nameof(url));
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("目标路径不能为空", nameof(targetPath));

            EventHandler<DownloadProgressEventArgs> handler = (s, e) => progress?.Report(e);
            _downloader.ProgressChanged += handler;
            try
            {
                await _downloader.DownloadAsync(url, targetPath, cancellationToken);
                Logger.Info($"整合包下载完成：{url} → {targetPath}");
            }
            finally
            {
                _downloader.ProgressChanged -= handler;
            }
        }

        /// <inheritdoc/>
        public async Task<ModpackManifest?> ParseModpackAsync(string modpackPath)
        {
            if (!File.Exists(modpackPath))
            {
                Logger.Warn($"整合包文件不存在：{modpackPath}");
                return null;
            }

            try
            {
                using var zip = ZipFile.OpenRead(modpackPath);

                // 先尝试 CurseForge 格式
                var cfEntry = zip.GetEntry("manifest.json");
                if (cfEntry != null)
                {
                    using var stream = cfEntry.Open();
                    using var reader = new StreamReader(stream);
                    var json = reader.ReadToEnd();
                    return ParseCurseForgeManifest(json);
                }

                // 再尝试 Modrinth 格式
                var mrEntry = zip.GetEntry("modrinth.index.json");
                if (mrEntry != null)
                {
                    using var stream = mrEntry.Open();
                    using var reader = new StreamReader(stream);
                    var json = reader.ReadToEnd();
                    return ParseModrinthManifest(json);
                }

                Logger.Warn("整合包格式未识别（既无 manifest.json 也无 modrinth.index.json）");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"解析整合包清单失败：{modpackPath}", ex);
                return null;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> InstallModpackAsync(
            string modpackPath, string gameDir, string versionId,
            IProgress<ModpackInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(modpackPath))
                throw new FileNotFoundException($"整合包文件不存在：{modpackPath}");
            if (string.IsNullOrWhiteSpace(gameDir))
                throw new ArgumentException("游戏目录不能为空", nameof(gameDir));
            if (string.IsNullOrWhiteSpace(versionId))
                throw new ArgumentException("版本 id 不能为空", nameof(versionId));

            // 工作目录：在 gameDir 下创建一个临时解压目录
            var tempDir = Path.Combine(Path.GetTempPath(), "YCL_Modpack_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // 阶段 1：解压整合包
                progress?.Report(new ModpackInstallProgress
                {
                    Phase = ModpackInstallPhase.Extracting,
                    CurrentFile = Path.GetFileName(modpackPath)
                });
                Logger.Info($"开始解压整合包：{modpackPath} → {tempDir}");
                await Task.Run(() => ZipFile.ExtractToDirectory(modpackPath, tempDir, overwriteFiles: true), cancellationToken);

                // 阶段 2：解析清单
                progress?.Report(new ModpackInstallProgress
                {
                    Phase = ModpackInstallPhase.ParsingManifest,
                    CurrentFile = "manifest.json / modrinth.index.json"
                });

                ModpackManifest? manifest = null;
                var cfManifestPath = Path.Combine(tempDir, "manifest.json");
                var mrManifestPath = Path.Combine(tempDir, "modrinth.index.json");

                if (File.Exists(cfManifestPath))
                {
                    manifest = ParseCurseForgeManifest(await File.ReadAllTextAsync(cfManifestPath, cancellationToken));
                }
                else if (File.Exists(mrManifestPath))
                {
                    manifest = ParseModrinthManifest(await File.ReadAllTextAsync(mrManifestPath, cancellationToken));
                }

                if (manifest == null)
                {
                    Logger.Error("无法解析整合包清单文件，安装中止", null);
                    return false;
                }

                Logger.Info($"整合包清单：MC {manifest.MinecraftVersion}, 加载器 {manifest.LoaderType} {manifest.LoaderVersion}, " +
                            $"模组数 {manifest.Files.Count}");

                // 阶段 3：安装 Minecraft 版本
                progress?.Report(new ModpackInstallProgress
                {
                    Phase = ModpackInstallPhase.InstallingMinecraft,
                    CurrentFile = $"Minecraft {manifest.MinecraftVersion}"
                });

                var mcEntry = await _manifestService.GetVersionAsync(manifest.MinecraftVersion, cancellationToken);
                if (mcEntry == null)
                {
                    Logger.Error($"找不到 Minecraft 版本：{manifest.MinecraftVersion}", null);
                    return false;
                }

                // 用 VersionManager 安装基础版本（如果已安装会跳过）
                var versionProgress = new Progress<InstallProgress>(p =>
                {
                    progress?.Report(new ModpackInstallProgress
                    {
                        Phase = ModpackInstallPhase.InstallingMinecraft,
                        CurrentFile = $"Minecraft {manifest.MinecraftVersion}：{p.CurrentFile}",
                        CompletedFiles = p.CompletedFiles,
                        TotalFiles = p.TotalFiles
                    });
                });

                var installed = await _versionManager.InstallVersionAsync(mcEntry, versionProgress, cancellationToken);
                if (!installed)
                {
                    Logger.Warn($"Minecraft {manifest.MinecraftVersion} 安装未完全成功，继续后续步骤");
                }

                // 阶段 4：安装模组加载器
                if (!string.IsNullOrEmpty(manifest.LoaderType))
                {
                    progress?.Report(new ModpackInstallProgress
                    {
                        Phase = ModpackInstallPhase.InstallingLoader,
                        CurrentFile = $"{manifest.LoaderType} {manifest.LoaderVersion}"
                    });

                    await InstallLoaderAsync(manifest, cancellationToken);
                }

                // 阶段 5：下载所有模组到 mods/
                var modsDir = Path.Combine(gameDir, "mods");
                Directory.CreateDirectory(modsDir);

                progress?.Report(new ModpackInstallProgress
                {
                    Phase = ModpackInstallPhase.DownloadingMods,
                    CurrentFile = $"准备下载 {manifest.Files.Count} 个模组",
                    TotalFiles = manifest.Files.Count,
                    CompletedFiles = 0
                });

                int completed = 0;
                foreach (var file in manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var fileName = !string.IsNullOrEmpty(file.FileName)
                            ? file.FileName
                            : $"mod_{file.ProjectId}_{file.FileId}.jar";
                        var targetPath = Path.Combine(modsDir, SanitizeFileName(fileName));

                        // 如果已存在则跳过（支持断点续装）
                        if (File.Exists(targetPath))
                        {
                            Logger.Debug($"模组文件已存在，跳过：{fileName}");
                        }
                        else if (file.Source == ModpackSource.Modrinth && !string.IsNullOrEmpty(file.DownloadUrl))
                        {
                            // Modrinth 整合包直接提供下载 URL
                            await _modrinthClient.DownloadModFileAsync(file.DownloadUrl, targetPath, null, cancellationToken);
                        }
                        else if (file.Source == ModpackSource.CurseForge && _curseForgeClient.IsConfigured &&
                                 int.TryParse(file.ProjectId, out var pid) && int.TryParse(file.FileId, out var fid))
                        {
                            // CurseForge 整合包：用 projectID+fileID 查 API 获取下载 URL
                            var cfFile = await GetCurseForgeFileAsync(pid, fid, cancellationToken);
                            if (cfFile != null)
                            {
                                await _curseForgeClient.DownloadModFileAsync(cfFile, targetPath, null, cancellationToken);
                            }
                            else
                            {
                                Logger.Warn($"无法获取 CurseForge 文件：projectID={pid}, fileID={fid}");
                            }
                        }
                        else
                        {
                            Logger.Warn($"跳过无法下载的模组文件：{file.FileName}（source={file.Source}）");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"下载模组文件失败：{file.FileName} - {ex.Message}");
                    }

                    completed++;
                    progress?.Report(new ModpackInstallProgress
                    {
                        Phase = ModpackInstallPhase.DownloadingMods,
                        CurrentFile = file.FileName,
                        CompletedFiles = completed,
                        TotalFiles = manifest.Files.Count
                    });
                }

                // 阶段 6：覆盖 overrides/ 文件夹到游戏目录
                progress?.Report(new ModpackInstallProgress
                {
                    Phase = ModpackInstallPhase.ApplyingOverrides,
                    CurrentFile = manifest.OverridesFolder + "/"
                });

                var overridesDir = Path.Combine(tempDir, manifest.OverridesFolder);
                if (Directory.Exists(overridesDir))
                {
                    CopyDirectory(overridesDir, gameDir);
                    Logger.Info($"已覆盖 {manifest.OverridesFolder}/ 到 {gameDir}");
                }

                // 阶段 7：完成
                progress?.Report(new ModpackInstallProgress
                {
                    Phase = ModpackInstallPhase.Completed,
                    CurrentFile = manifest.Name,
                    CompletedFiles = manifest.Files.Count,
                    TotalFiles = manifest.Files.Count
                });

                Logger.Info($"整合包 {manifest.Name} 安装完成");
                return true;
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"整合包安装被取消：{modpackPath}");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error($"整合包安装失败：{modpackPath}", ex);
                return false;
            }
            finally
            {
                // 清理临时目录
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, recursive: true);
                }
                catch { /* 忽略清理失败 */ }
            }
        }

        /// <summary>解析 CurseForge 格式的 manifest.json</summary>
        private ModpackManifest? ParseCurseForgeManifest(string json)
        {
            try
            {
                var node = JsonNode.Parse(json);
                if (node == null) return null;

                var manifest = new ModpackManifest
                {
                    Source = ModpackSource.CurseForge,
                    Name = node["name"]?.GetValue<string>() ?? "CurseForge Modpack",
                    Version = node["version"]?.GetValue<string>() ?? string.Empty,
                    Author = node["author"]?.GetValue<string>() ?? string.Empty,
                    OverridesFolder = node["overrides"]?.GetValue<string>() ?? "overrides"
                };

                // minecraft.version
                var mcNode = node["minecraft"];
                if (mcNode != null)
                {
                    manifest.MinecraftVersion = mcNode["version"]?.GetValue<string>() ?? string.Empty;

                    // minecraft.modLoaders 数组：[{id: "forge-40.2.0", primary: true}]
                    var modLoaders = mcNode["modLoaders"]?.AsArray();
                    if (modLoaders != null)
                    {
                        foreach (var loader in modLoaders)
                        {
                            if (loader == null) continue;
                            var id = loader["id"]?.GetValue<string>();
                            if (string.IsNullOrEmpty(id)) continue;

                            // id 格式如 "forge-40.2.0"、"fabric-loader-0.14.21-1.18.2"
                            var parts = id.Split('-', 2);
                            if (parts.Length >= 1)
                            {
                                manifest.LoaderType = parts[0];
                                if (parts.Length >= 2)
                                {
                                    manifest.LoaderVersion = parts[1];
                                }
                            }

                            // 只取 primary 加载器
                            if (loader["primary"]?.GetValue<bool>() == true) break;
                        }
                    }
                }

                // files 数组：[{projectID: 123, fileID: 456, required: true}]
                var filesNode = node["files"]?.AsArray();
                if (filesNode != null)
                {
                    foreach (var file in filesNode)
                    {
                        if (file == null) continue;
                        manifest.Files.Add(new ModpackFileEntry
                        {
                            Source = ModpackSource.CurseForge,
                            ProjectId = file["projectID"]?.GetValue<int>().ToString() ?? string.Empty,
                            FileId = file["fileID"]?.GetValue<int>().ToString() ?? string.Empty,
                            FileName = string.Empty // CurseForge manifest 不含文件名，需要查 API
                        });
                    }
                }

                return manifest;
            }
            catch (Exception ex)
            {
                Logger.Error("解析 CurseForge manifest 失败", ex);
                return null;
            }
        }

        /// <summary>解析 Modrinth 格式的 modrinth.index.json</summary>
        private ModpackManifest? ParseModrinthManifest(string json)
        {
            try
            {
                var node = JsonNode.Parse(json);
                if (node == null) return null;

                var manifest = new ModpackManifest
                {
                    Source = ModpackSource.Modrinth,
                    Name = node["name"]?.GetValue<string>() ?? "Modrinth Modpack",
                    Version = node["versionId"]?.GetValue<string>() ?? string.Empty,
                    Author = string.Empty,
                    OverridesFolder = "overrides"
                };

                // dependencies.minecraft / dependencies.fabric-loader / dependencies.forge 等
                var deps = node["dependencies"];
                if (deps != null)
                {
                    manifest.MinecraftVersion = deps["minecraft"]?.GetValue<string>() ?? string.Empty;

                    // 找加载器
                    if (deps["fabric-loader"] != null)
                    {
                        manifest.LoaderType = "fabric";
                        manifest.LoaderVersion = deps["fabric-loader"]?.GetValue<string>() ?? string.Empty;
                    }
                    else if (deps["forge"] != null)
                    {
                        manifest.LoaderType = "forge";
                        manifest.LoaderVersion = deps["forge"]?.GetValue<string>() ?? string.Empty;
                    }
                    else if (deps["quilt-loader"] != null)
                    {
                        manifest.LoaderType = "quilt";
                        manifest.LoaderVersion = deps["quilt-loader"]?.GetValue<string>() ?? string.Empty;
                    }
                    else if (deps["neoforge"] != null)
                    {
                        manifest.LoaderType = "neoforge";
                        manifest.LoaderVersion = deps["neoforge"]?.GetValue<string>() ?? string.Empty;
                    }
                }

                // files 数组：[{path: "mods/xxx.jar", downloads: [...], hashes: {...}}]
                var filesNode = node["files"]?.AsArray();
                if (filesNode != null)
                {
                    foreach (var file in filesNode)
                    {
                        if (file == null) continue;

                        var path = file["path"]?.GetValue<string>() ?? string.Empty;
                        var downloads = file["downloads"]?.AsArray();
                        var downloadUrl = downloads?.FirstOrDefault()?.GetValue<string>();

                        string? sha1 = null;
                        var hashes = file["hashes"];
                        if (hashes != null && hashes["sha1"] != null)
                        {
                            sha1 = hashes["sha1"]?.GetValue<string>();
                        }

                        long size = file["fileSize"]?.GetValue<long>() ?? 0;

                        // path 形如 "mods/xxx.jar"，只取文件名
                        var fileName = Path.GetFileName(path);

                        manifest.Files.Add(new ModpackFileEntry
                        {
                            Source = ModpackSource.Modrinth,
                            ProjectId = string.Empty, // Modrinth 整合包文件条目不含 project id
                            FileId = string.Empty,
                            FileName = fileName,
                            DownloadUrl = downloadUrl,
                            Sha1 = sha1,
                            Size = size
                        });
                    }
                }

                return manifest;
            }
            catch (Exception ex)
            {
                Logger.Error("解析 Modrinth manifest 失败", ex);
                return null;
            }
        }

        /// <summary>安装模组加载器</summary>
        private async Task InstallLoaderAsync(ModpackManifest manifest, CancellationToken ct)
        {
            try
            {
                // 把字符串加载器类型转换为 ModLoaderType 枚举
                var loaderType = manifest.LoaderType.ToLowerInvariant() switch
                {
                    "fabric" => ModLoaderType.Fabric,
                    "forge" => ModLoaderType.Forge,
                    "quilt" => ModLoaderType.Quilt,
                    "neoforge" => ModLoaderType.NeoForge,
                    "liteloader" => ModLoaderType.LiteLoader,
                    _ => (ModLoaderType?)null
                };

                if (loaderType == null)
                {
                    Logger.Warn($"不支持的加载器类型：{manifest.LoaderType}，跳过安装");
                    return;
                }

                // 获取该加载器在指定 MC 版本下的可用版本列表
                var installer = _modLoaderManager.GetInstaller(loaderType.Value);
                var versions = await installer.ListVersionsAsync(manifest.MinecraftVersion, ct);
                if (versions.Count == 0)
                {
                    Logger.Warn($"未找到 {loaderType} 在 MC {manifest.MinecraftVersion} 下的可用版本，跳过加载器安装");
                    return;
                }

                // 优先匹配 manifest 中指定的版本，否则用第一个
                var selected = !string.IsNullOrEmpty(manifest.LoaderVersion)
                    ? versions.FirstOrDefault(v =>
                        string.Equals(v.Version, manifest.LoaderVersion, StringComparison.OrdinalIgnoreCase))
                    : null;
                selected ??= versions[0];

                Logger.Info($"安装加载器：{loaderType} {selected.Version} for MC {manifest.MinecraftVersion}");
                await installer.InstallAsync(manifest.MinecraftVersion, selected, null, ct);
            }
            catch (Exception ex)
            {
                Logger.Warn($"安装加载器失败：{manifest.LoaderType} {manifest.LoaderVersion} - {ex.Message}");
            }
        }

        /// <summary>通过 projectID + fileID 查 CurseForge API 获取文件信息</summary>
        private async Task<CurseForgeFile?> GetCurseForgeFileAsync(int projectId, int fileId, CancellationToken ct)
        {
            try
            {
                var files = await _curseForgeClient.GetModFilesAsync(projectId, ct);
                return files.FirstOrDefault(f => f.Id == fileId);
            }
            catch (Exception ex)
            {
                Logger.Warn($"查询 CurseForge 文件失败：projectID={projectId}, fileID={fileId} - {ex.Message}");
                return null;
            }
        }

        /// <summary>递归复制目录</summary>
        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var dirName = Path.GetFileName(dir);
                CopyDirectory(dir, Path.Combine(destDir, dirName));
            }
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
