using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;
using YCL.Models.Versions;

namespace YCL.Core.Download
{
    /// <summary>
    /// 版本清单服务实现。
    /// 职责：
    /// 1. 从 Mojang（或镜像源）下载 version_manifest_v2.json
    /// 2. 缓存到 %AppData%\YCL\cache\version_manifest.json
    /// 3. 提供按 id 查询、列出所有版本等便捷方法
    /// 缓存策略：默认 6 小时内不重复下载（可通过 forceUpdate 强制刷新）。
    /// </summary>
    public class VersionManifestService : IVersionManifestService
    {
        /// <summary>官方版本清单 URL（Mojang 发布的 version_manifest_v2.json）</summary>
        private const string OfficialManifestUrl =
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

        /// <summary>缓存有效期（6 小时）。超过此时间视为过期，需要重新下载。</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

        /// <summary>缓存目录：%AppData%\YCL\cache\</summary>
        private static readonly string CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YCL", "cache");

        /// <summary>缓存文件完整路径</summary>
        private static readonly string CachePath = Path.Combine(CacheDirectory, "version_manifest.json");

        /// <summary>JSON 反序列化选项（容忍大小写不一致）</summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IDownloadSource _downloadSource;

        /// <summary>内存中缓存的清单对象（加载后保留，避免重复读文件）</summary>
        private VersionManifest? _cached;

        /// <summary>缓存时间（UTC）</summary>
        private DateTime _cacheTime = DateTime.MinValue;

        /// <summary>保护 _cached 与 _cacheTime 的锁</summary>
        private readonly object _lock = new();

        /// <summary>
        /// 构造版本清单服务。
        /// </summary>
        /// <param name="downloadSource">下载源管理器（用于把官方 URL 转换为镜像 URL）</param>
        public VersionManifestService(IDownloadSource downloadSource)
        {
            _downloadSource = downloadSource ?? throw new ArgumentNullException(nameof(downloadSource));
            Logger.Info("版本清单服务已初始化");
        }

        /// <inheritdoc/>
        public DateTime LastUpdated
        {
            get
            {
                lock (_lock) return _cacheTime;
            }
        }

        /// <inheritdoc/>
        public bool IsCacheValid
        {
            get
            {
                lock (_lock)
                {
                    if (_cached == null) return false;
                    return DateTime.UtcNow - _cacheTime < CacheTtl;
                }
            }
        }

        /// <inheritdoc/>
        public VersionManifest? LoadFromCache()
        {
            // 先看内存缓存
            lock (_lock)
            {
                if (_cached != null) return _cached;
            }

            // 内存没有，尝试从文件加载
            if (!File.Exists(CachePath)) return null;

            try
            {
                var json = File.ReadAllText(CachePath);
                var manifest = JsonSerializer.Deserialize<VersionManifest>(json, JsonOptions);
                if (manifest != null)
                {
                    lock (_lock)
                    {
                        _cached = manifest;
                        _cacheTime = File.GetLastWriteTimeUtc(CachePath);
                    }
                    Logger.Info($"已从缓存加载版本清单（{manifest.Versions?.Count ?? 0} 个版本）");
                    return manifest;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("加载版本清单缓存失败：" + ex.Message);
            }
            return null;
        }

        /// <inheritdoc/>
        public async Task<VersionManifest> FetchAsync(bool forceUpdate, CancellationToken cancellationToken = default)
        {
            // 不强制更新时，先检查缓存是否有效
            if (!forceUpdate)
            {
                var cached = LoadFromCache();
                if (cached != null && IsCacheValid)
                {
                    Logger.Debug("版本清单缓存有效，跳过下载");
                    return cached;
                }
            }

            // 转换 URL（镜像源）
            var url = _downloadSource.TransformUrl(OfficialManifestUrl);
            Logger.Info($"开始下载版本清单：{url}");

            // 确保缓存目录存在
            Directory.CreateDirectory(CacheDirectory);

            // 用 FileDownloader.SharedClient 下载（与下载器共用一个 HttpClient，避免 socket 耗尽）
            using var response = await FileDownloader.SharedClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var manifest = JsonSerializer.Deserialize<VersionManifest>(json, JsonOptions);
            if (manifest == null)
                throw new InvalidDataException("版本清单反序列化结果为 null");

            // 保存到缓存文件
            try
            {
                File.WriteAllText(CachePath, json);
            }
            catch (Exception ex)
            {
                Logger.Warn("保存版本清单缓存失败：" + ex.Message);
            }

            lock (_lock)
            {
                _cached = manifest;
                _cacheTime = DateTime.UtcNow;
            }

            Logger.Info($"版本清单下载完成（{manifest.Versions?.Count ?? 0} 个版本）");
            return manifest;
        }

        /// <inheritdoc/>
        public async Task<VersionManifestEntry?> GetVersionAsync(string versionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(versionId)) return null;

            var manifest = await EnsureManifestAsync(cancellationToken);
            if (manifest.Versions == null) return null;

            foreach (var v in manifest.Versions)
            {
                if (string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase))
                    return v;
            }
            return null;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<VersionManifestEntry>> GetVersionsAsync(CancellationToken cancellationToken = default)
        {
            var manifest = await EnsureManifestAsync(cancellationToken);
            return manifest.Versions ?? new List<VersionManifestEntry>();
        }

        /// <summary>
        /// 确保有可用的清单数据：内存缓存有效则直接用，否则从文件加载，
        /// 文件缓存过期或不存在则联网下载。
        /// </summary>
        private async Task<VersionManifest> EnsureManifestAsync(CancellationToken ct)
        {
            var cached = LoadFromCache();
            if (cached != null && IsCacheValid) return cached;
            return await FetchAsync(forceUpdate: false, ct);
        }
    }
}
