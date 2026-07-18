using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YCL.Models.Versions;

namespace YCL.Core.Download
{
    /// <summary>
    /// 版本清单服务接口：负责下载、缓存、查询 Minecraft 官方版本清单。
    /// 版本清单是 Mojang 发布的 version_manifest_v2.json，列出所有可下载的版本
    /// 及其版本 JSON 的 URL。启动器用它知道"有哪些版本可下载"以及"每个版本的 JSON 在哪"。
    /// </summary>
    public interface IVersionManifestService
    {
        /// <summary>清单最后一次更新的本地时间（UTC）。未加载过则为 <see cref="DateTime.MinValue"/></summary>
        DateTime LastUpdated { get; }

        /// <summary>本地缓存是否有效（存在且未过期）</summary>
        bool IsCacheValid { get; }

        /// <summary>
        /// 从本地缓存加载版本清单（不联网）。缓存不存在或损坏时返回 null。
        /// </summary>
        VersionManifest? LoadFromCache();

        /// <summary>
        /// 从远程下载最新版本清单并更新本地缓存。
        /// URL 会经过 <see cref="IDownloadSource"/> 转换（支持镜像源加速）。
        /// </summary>
        /// <param name="forceUpdate">true = 强制下载更新；false = 缓存有效则直接返回缓存</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>下载并解析后的版本清单</returns>
        Task<VersionManifest> FetchAsync(bool forceUpdate, CancellationToken cancellationToken = default);

        /// <summary>
        /// 根据版本 id 查找版本条目。会先尝试从缓存加载，缓存无效则联网。
        /// </summary>
        /// <param name="versionId">版本 id，如 "1.20.4"</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>找到的版本条目；找不到返回 null</returns>
        Task<VersionManifestEntry?> GetVersionAsync(string versionId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取所有版本条目列表。会先尝试从缓存加载，缓存无效则联网。
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>版本条目列表（可能为空，但不会为 null）</returns>
        Task<IReadOnlyList<VersionManifestEntry>> GetVersionsAsync(CancellationToken cancellationToken = default);
    }
}
