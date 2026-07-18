using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;
using YCL.Models.Versions;

namespace YCL.Core.Download
{
    /// <summary>
    /// Minecraft 文件下载器实现。
    /// 根据 <see cref="VersionInfo"/> 生成所有需要下载的文件清单
    /// （client.jar、libraries、assets、logging 配置），交给 <see cref="DownloadTaskScheduler"/> 调度下载。
    ///
    /// 文件清单构造规则：
    /// - client.jar：从 version.downloads.client 取 url/sha1/size，保存到 versions/&lt;id&gt;/&lt;id&gt;.jar
    /// - libraries：遍历 version.libraries，按平台过滤后，取 artifact（jar）和 natives-windows classifier（zip）
    /// - assetIndex：从 version.assetIndex.url 下载索引 JSON，保存到 assets/indexes/&lt;id&gt;.json
    /// - assets objects：解析 assetIndex JSON，对每个 object 下载到 assets/objects/&lt;hash前2位&gt;/&lt;hash&gt;
    /// - logging 配置：从 version.logging.client.file 取 url/sha1/size，保存到 assets/log_configs/&lt;id&gt;
    ///
    /// 注意事项：
    /// - 所有 URL 经 <see cref="IDownloadSource.TransformUrl"/> 重写（支持镜像源）
    /// - assets 按 hash 去重（同一 hash 只下载一次）
    /// - library URL 为空时用 path 拼接官方 libraries 前缀
    /// - 平台过滤：只下载适用于 Windows x64 的库（复制 VersionResolver 的简化逻辑）
    /// </summary>
    public class MinecraftFileDownloader : IMinecraftFileDownloader
    {
        /// <summary>官方 libraries 仓库前缀（library.url 为空时用 path 拼接）</summary>
        private const string OfficialLibrariesPrefix = "https://libraries.minecraft.net/";

        /// <summary>官方 assets 资源前缀（assetIndex 中只有 hash，需拼接此前缀）</summary>
        private const string OfficialAssetsPrefix = "https://resources.download.minecraft.net/";

        /// <summary>natives 映射中的 ${arch} 占位符替换值（x64）</summary>
        private const string ArchPlaceholder = "64";

        private readonly IDownloadSource _downloadSource;
        private readonly int _maxConcurrency;
        private readonly int _retryCount;

        /// <summary>当前正在使用的调度器（下载开始时赋值，结束后置 null）</summary>
        private DownloadTaskScheduler? _currentScheduler;

        /// <inheritdoc/>
        public bool IsDownloading => _currentScheduler != null && _currentScheduler.IsRunning;

        /// <inheritdoc/>
        public bool IsPaused => _currentScheduler != null && _currentScheduler.IsPaused;

        /// <inheritdoc/>
        public event EventHandler<BatchDownloadProgressEventArgs>? ProgressChanged;

        /// <inheritdoc/>
        public event EventHandler<DownloadTaskProgressEventArgs>? TaskProgressChanged;

        /// <inheritdoc/>
        public event EventHandler<DownloadTaskCompletedEventArgs>? TaskCompleted;

        /// <summary>
        /// 构造 Minecraft 文件下载器。
        /// </summary>
        /// <param name="downloadSource">下载源管理器（URL 重写）</param>
        /// <param name="maxConcurrency">最大并发下载数（来自 AppConfig.DownloadThreads）</param>
        /// <param name="retryCount">每个任务失败重试次数（来自 AppConfig.RetryCount）</param>
        public MinecraftFileDownloader(IDownloadSource downloadSource, int maxConcurrency = 8, int retryCount = 3)
        {
            _downloadSource = downloadSource ?? throw new ArgumentNullException(nameof(downloadSource));
            _maxConcurrency = Math.Max(1, maxConcurrency);
            _retryCount = Math.Max(0, retryCount);
            Logger.Info($"Minecraft 文件下载器已初始化（并发={_maxConcurrency}, 重试={_retryCount}）");
        }

        /// <inheritdoc/>
        public void Pause()
        {
            _currentScheduler?.Pause();
        }

        /// <inheritdoc/>
        public void Resume()
        {
            _currentScheduler?.Resume();
        }

        /// <inheritdoc/>
        public async Task<DownloadResult> DownloadVersionAsync(
            VersionInfo version,
            string minecraftPath,
            CancellationToken cancellationToken = default)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            if (string.IsNullOrEmpty(minecraftPath)) throw new ArgumentException("minecraftPath 不能为空", nameof(minecraftPath));
            if (IsDownloading) throw new InvalidOperationException("已有下载任务正在进行中");

            var versionIdForLog = version.Id ?? "(unknown)";
            Logger.Info($"开始下载版本：{versionIdForLog}");

            // 1. 构造所有下载任务
            var tasks = BuildDownloadTasks(version, minecraftPath);
            Logger.Info($"版本 {version.Id} 共 {tasks.Count} 个下载任务");

            if (tasks.Count == 0)
            {
                Logger.Info($"版本 {version.Id} 无需下载任何文件");
                return new DownloadResult { TotalFiles = 0, SuccessFiles = 0, FailedFiles = 0 };
            }

            // 2. 创建调度器（每次下载用新实例，因为调度器有运行状态）
            var scheduler = new DownloadTaskScheduler(_downloadSource, _maxConcurrency, _retryCount);
            _currentScheduler = scheduler;

            // 3. 累计成功/失败计数
            int successCount = 0;
            int failedCount = 0;
            long downloadedBytes = 0;

            // 4. 转发调度器的三个事件到本类的对应事件
            scheduler.ProgressChanged += (sender, e) => ProgressChanged?.Invoke(this, e);
            scheduler.TaskProgressChanged += (sender, e) => TaskProgressChanged?.Invoke(this, e);
            scheduler.TaskCompleted += (sender, e) =>
            {
                if (e.Success)
                {
                    Interlocked.Increment(ref successCount);
                    if (e.Task.Size > 0)
                        Interlocked.Add(ref downloadedBytes, e.Task.Size);
                }
                else
                {
                    Interlocked.Increment(ref failedCount);
                }
                TaskCompleted?.Invoke(this, e);
            };

            // 5. 入队并执行
            scheduler.EnqueueRange(tasks);

            var result = new DownloadResult
            {
                TotalFiles = tasks.Count
            };

            try
            {
                await scheduler.RunAsync(cancellationToken);
                result.IsCanceled = false;
            }
            catch (OperationCanceledException)
            {
                result.IsCanceled = true;
                Logger.Info($"版本 {version.Id} 下载被取消");
            }
            finally
            {
                _currentScheduler = null;
            }

            result.SuccessFiles = successCount;
            result.FailedFiles = failedCount;
            result.TotalBytes = Interlocked.Read(ref downloadedBytes);

            Logger.Info($"版本 {version.Id} 下载结束：成功 {result.SuccessFiles}，" +
                        $"失败 {result.FailedFiles}，共 {result.TotalFiles}，字节 {result.TotalBytes}");

            return result;
        }

        /// <inheritdoc/>
        public async Task DownloadVersionJsonAsync(
            VersionManifestEntry entry,
            string minecraftPath,
            CancellationToken cancellationToken = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.Id)) throw new ArgumentException("版本条目缺少 id", nameof(entry));
            if (string.IsNullOrEmpty(entry.Url)) throw new ArgumentException("版本条目缺少 url", nameof(entry));

            // 保存到 .minecraft/versions/<id>/<id>.json
            var versionDir = Path.Combine(minecraftPath, "versions", entry.Id);
            var jsonPath = Path.Combine(versionDir, entry.Id + ".json");
            Directory.CreateDirectory(versionDir);

            // 经下载源重写后下载
            var url = _downloadSource.TransformUrl(entry.Url);
            Logger.Info($"下载版本 JSON：{entry.Id} → {jsonPath}");

            var downloader = new FileDownloader(_retryCount);
            await downloader.DownloadAsync(url, jsonPath, cancellationToken);

            Logger.Info($"版本 JSON 下载完成：{entry.Id}");
        }

        /// <inheritdoc/>
        public List<DownloadTask> BuildDownloadTasks(VersionInfo version, string minecraftPath)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            if (string.IsNullOrEmpty(minecraftPath)) throw new ArgumentException("minecraftPath 不能为空", nameof(minecraftPath));

            var tasks = new List<DownloadTask>();
            var versionId = version.Id ?? "unknown";

            // 1. client.jar
            AddClientJarTask(version, minecraftPath, versionId, tasks);

            // 2. libraries（artifact + natives-windows）
            AddLibraryTasks(version, minecraftPath, tasks);

            // 3. assetIndex + assets objects
            AddAssetTasks(version, minecraftPath, tasks);

            // 4. logging 配置文件
            AddLoggingTask(version, minecraftPath, tasks);

            return tasks;
        }

        /// <summary>构造 client.jar 下载任务</summary>
        private void AddClientJarTask(VersionInfo version, string minecraftPath, string versionId, List<DownloadTask> tasks)
        {
            var client = version.Downloads?.Client;
            if (client == null || string.IsNullOrEmpty(client.Url))
            {
                Logger.Debug($"版本 {versionId} 缺少 downloads.client，跳过 client.jar 下载任务");
                return;
            }

            var jarPath = Path.Combine(minecraftPath, "versions", versionId, versionId + ".jar");
            tasks.Add(new DownloadTask
            {
                Url = client.Url,
                TargetPath = jarPath,
                Sha1 = client.Sha1,
                Size = client.Size,
                DisplayName = versionId + ".jar",
                Category = "client"
            });
        }

        /// <summary>构造所有 libraries 下载任务（含 natives-windows）</summary>
        private void AddLibraryTasks(VersionInfo version, string minecraftPath, List<DownloadTask> tasks)
        {
            if (version.Libraries == null) return;

            var librariesDir = Path.Combine(minecraftPath, "libraries");
            // 用于去重：同一个文件路径只加入一次（避免 inheritsFrom 合并导致的重复）
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var lib in version.Libraries)
            {
                // 平台过滤：不适用于 Windows 的库跳过
                if (!IsLibraryAllowed(lib))
                    continue;

                // 处理 natives 库：下载 natives-windows classifier
                if (lib.Natives != null && lib.Natives.Count > 0)
                {
                    AddNativeLibraryTask(lib, minecraftPath, librariesDir, tasks, seenPaths);
                    continue;
                }

                // 处理普通 Java 库：下载 artifact
                AddArtifactLibraryTask(lib, librariesDir, tasks, seenPaths);
            }
        }

        /// <summary>添加普通 Java 库（artifact）的下载任务</summary>
        private void AddArtifactLibraryTask(Library lib, string librariesDir, List<DownloadTask> tasks, HashSet<string> seenPaths)
        {
            var artifact = lib.Downloads?.Artifact;
            if (artifact == null)
            {
                Logger.Debug($"库 {lib.Name} 缺少 downloads.artifact，跳过");
                return;
            }

            // 解析文件路径：优先用 artifact.path，否则从 name 推
            var relPath = artifact.Path;
            if (string.IsNullOrEmpty(relPath))
                relPath = GetPathFromName(lib.Name, null);
            if (string.IsNullOrEmpty(relPath))
            {
                Logger.Warn($"库 {lib.Name} 无法解析文件路径，跳过");
                return;
            }

            var targetPath = Path.Combine(librariesDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!seenPaths.Add(targetPath)) return; // 去重

            // URL：artifact.url 为空时用 path 拼接官方 libraries 前缀
            var url = artifact.Url;
            if (string.IsNullOrEmpty(url))
                url = OfficialLibrariesPrefix + relPath;

            tasks.Add(new DownloadTask
            {
                Url = url,
                TargetPath = targetPath,
                Sha1 = artifact.Sha1,
                Size = artifact.Size,
                DisplayName = Path.GetFileName(targetPath),
                Category = "libraries"
            });
        }

        /// <summary>添加 natives 库（natives-windows classifier）的下载任务</summary>
        private void AddNativeLibraryTask(Library lib, string minecraftPath, string librariesDir,
                                          List<DownloadTask> tasks, HashSet<string> seenPaths)
        {
            if (!lib.Natives!.TryGetValue("windows", out var classifier))
            {
                Logger.Debug($"natives 库 {lib.Name} 不含 windows 映射，跳过");
                return;
            }

            // classifier 可能含 ${arch} 占位符，替换为 64
            classifier = classifier.Replace("${arch}", ArchPlaceholder);

            // 从 downloads.classifiers 取下载信息
            var artifact = lib.Downloads?.Classifiers?.GetValueOrDefault(classifier);
            if (artifact == null)
            {
                Logger.Warn($"natives 库 {lib.Name} 找不到 classifier={classifier} 的下载信息，跳过");
                return;
            }

            // 解析文件路径
            var relPath = artifact.Path;
            if (string.IsNullOrEmpty(relPath))
                relPath = GetPathFromName(lib.Name, classifier);
            if (string.IsNullOrEmpty(relPath))
            {
                Logger.Warn($"natives 库 {lib.Name} 无法解析文件路径，跳过");
                return;
            }

            var targetPath = Path.Combine(librariesDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!seenPaths.Add(targetPath)) return; // 去重

            // URL
            var url = artifact.Url;
            if (string.IsNullOrEmpty(url))
                url = OfficialLibrariesPrefix + relPath;

            tasks.Add(new DownloadTask
            {
                Url = url,
                TargetPath = targetPath,
                Sha1 = artifact.Sha1,
                Size = artifact.Size,
                DisplayName = Path.GetFileName(targetPath),
                Category = "natives"
            });
        }

        /// <summary>构造 assetIndex 与所有 assets objects 的下载任务</summary>
        private void AddAssetTasks(VersionInfo version, string minecraftPath, List<DownloadTask> tasks)
        {
            var assetIndex = version.AssetIndex;
            if (assetIndex == null || string.IsNullOrEmpty(assetIndex.Url))
            {
                Logger.Debug($"版本 {version.Id} 缺少 assetIndex，跳过 assets 下载");
                return;
            }

            var indexId = assetIndex.Id ?? version.Assets ?? "legacy";
            var indexesDir = Path.Combine(minecraftPath, "assets", "indexes");
            var indexPath = Path.Combine(indexesDir, indexId + ".json");

            // 1. assetIndex JSON 本身
            tasks.Add(new DownloadTask
            {
                Url = assetIndex.Url,
                TargetPath = indexPath,
                Sha1 = assetIndex.Sha1,
                Size = assetIndex.Size,
                DisplayName = indexId + ".json",
                Category = "assetIndex"
            });

            // 2. 解析 assetIndex，提取所有 assets objects
            //    注意：这里需要先下载 assetIndex 才能知道有哪些 objects，
            //    所以 objects 任务是"延迟构造"的——由调用方在 assetIndex 下载完成后再次调用。
            //    但为了一次下载完成，我们在这里尝试读取已存在的 index 文件；
            //    如果文件不存在，objects 任务为空（需要调用方两步下载）。
            var objects = ReadAssetObjects(indexPath);
            if (objects.Count == 0)
            {
                Logger.Info($"assetIndex 文件尚未下载或无 objects（{indexPath}），" +
                            "assets objects 任务暂不生成。请先下载 assetIndex 后再次调用。");
                return;
            }

            var objectsDir = Path.Combine(minecraftPath, "assets", "objects");
            // 按 hash 去重（不同 assetIndex 可能引用同一资源）
            var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (hash, size) in objects)
            {
                if (!seenHashes.Add(hash)) continue; // 去重

                var prefix = hash.Length >= 2 ? hash.Substring(0, 2) : "00";
                var targetPath = Path.Combine(objectsDir, prefix, hash);
                var url = OfficialAssetsPrefix + prefix + "/" + hash;

                tasks.Add(new DownloadTask
                {
                    Url = url,
                    TargetPath = targetPath,
                    Sha1 = hash, // assets 的 SHA1 就是文件名
                    Size = size,
                    DisplayName = hash,
                    Category = "assets"
                });
            }

            Logger.Info($"从 assetIndex {indexId} 解析出 {seenHashes.Count} 个 assets objects");
        }

        /// <summary>构造 logging 配置文件下载任务</summary>
        private void AddLoggingTask(VersionInfo version, string minecraftPath, List<DownloadTask> tasks)
        {
            var loggingFile = version.Logging?.Client?.File;
            if (loggingFile == null || string.IsNullOrEmpty(loggingFile.Url))
            {
                Logger.Debug($"版本 {version.Id} 缺少 logging 配置，跳过");
                return;
            }

            var logConfigsDir = Path.Combine(minecraftPath, "assets", "log_configs");
            var fileName = string.IsNullOrEmpty(loggingFile.Id)
                ? Path.GetFileName(new Uri(loggingFile.Url).LocalPath)
                : loggingFile.Id;
            if (string.IsNullOrEmpty(fileName))
            {
                // 兜底：用 sha1 前几位命名
                fileName = (loggingFile.Sha1?.Length >= 8 ? loggingFile.Sha1.Substring(0, 8) : "log4j") + ".xml";
            }

            var targetPath = Path.Combine(logConfigsDir, fileName);

            tasks.Add(new DownloadTask
            {
                Url = loggingFile.Url,
                TargetPath = targetPath,
                Sha1 = loggingFile.Sha1,
                Size = loggingFile.Size,
                DisplayName = fileName,
                Category = "logging"
            });
        }

        /// <summary>
        /// 读取 assetIndex JSON 文件，提取所有 objects（hash → size）。
        /// 文件不存在或格式错误时返回空列表。
        /// assetIndex 格式：
        /// <code>{ "objects": { "minecraft/sounds/...": { "hash": "abc...", "size": 123 } } }</code>
        /// </summary>
        private List<(string hash, int size)> ReadAssetObjects(string indexPath)
        {
            var result = new List<(string, int)>();
            if (!File.Exists(indexPath)) return result;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
                if (!doc.RootElement.TryGetProperty("objects", out var objects) ||
                    objects.ValueKind != JsonValueKind.Object)
                    return result;

                foreach (var prop in objects.EnumerateObject())
                {
                    var obj = prop.Value;
                    if (!obj.TryGetProperty("hash", out var hashEl)) continue;
                    var hash = hashEl.GetString();
                    if (string.IsNullOrEmpty(hash)) continue;

                    int size = 0;
                    if (obj.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt32(out var s))
                        size = s;

                    result.Add((hash!, size));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"解析 assetIndex 失败：{indexPath} - {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 判断库是否适用于当前平台（Windows x64）。
        /// 逻辑与 <see cref="Versions.VersionResolver"/> 一致，简化复制以避免耦合。
        /// </summary>
        private static bool IsLibraryAllowed(Library lib)
        {
            if (lib.Rules == null || lib.Rules.Count == 0)
                return true;

            bool hasAllowRule = false;
            foreach (var r in lib.Rules)
                if ((r.Action ?? "allow") == "allow") { hasAllowRule = true; break; }

            bool allowed = !hasAllowRule;
            foreach (var rule in lib.Rules)
            {
                var action = rule.Action ?? "allow";
                var osMatch = IsOsMatch(rule.Os);
                if (action == "allow" && osMatch) allowed = true;
                else if (action == "disallow" && osMatch) allowed = false;
            }
            return allowed;
        }

        /// <summary>判断当前系统是否匹配规则中的 OS（只关心 Windows x64）</summary>
        private static bool IsOsMatch(RuleOs? os)
        {
            if (os == null || string.IsNullOrEmpty(os.Name)) return true;
            if (!string.Equals(os.Name, "windows", StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(os.Arch) &&
                string.Equals(os.Arch, "x86", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        /// <summary>
        /// 根据 Maven 坐标名推出文件相对路径。
        /// 如 "com.mojang:minecraft:1.20.4" → "com/mojang/minecraft/1.20.4/minecraft-1.20.4.jar"
        /// 带 classifier 时追加 "-classifier"。
        /// </summary>
        private static string? GetPathFromName(string? name, string? classifier)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var parts = name.Split(':');
            if (parts.Length < 3) return null;

            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var version = parts[2];

            var fileName = $"{artifact}-{version}";
            if (!string.IsNullOrEmpty(classifier))
                fileName += "-" + classifier;
            fileName += ".jar";

            return $"{group}/{artifact}/{version}/{fileName}";
        }
    }
}
