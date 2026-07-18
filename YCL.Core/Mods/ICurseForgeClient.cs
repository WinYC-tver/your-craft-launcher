using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;

namespace YCL.Core.Mods
{
    /// <summary>
    /// CurseForge API 客户端接口。
    /// CurseForge 是最大的 Minecraft 模组托管平台，API 需要 X-API-Key 请求头。
    ///
    /// 官方文档：https://docs.curseforge.com/
    /// 主要端点：
    /// - GET /v1/mods/search：搜索模组（gameId=432 表示 Minecraft）
    /// - GET /v1/mods/{modId}/files：获取模组的所有文件
    /// - GET /v1/mods/files/{fileId}/download-url：获取文件下载 URL
    ///
    /// 如果未配置 API Key，所有方法会抛出 InvalidOperationException 或返回空列表，
    /// 上层 ModDownloadService 应据此降级到 Modrinth。
    /// </summary>
    public interface ICurseForgeClient
    {
        /// <summary>是否已配置 API Key（未配置则无法调用任何接口）</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// 搜索 CurseForge 上的模组。
        /// </summary>
        /// <param name="query">搜索关键字</param>
        /// <param name="page">页码（从 0 开始）</param>
        /// <param name="pageSize">每页数量（最大 50）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>模组列表（未配置 Key 或网络失败时返回空列表）</returns>
        Task<List<CurseForgeMod>> SearchModsAsync(
            string query, int page = 0, int pageSize = 20,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取指定模组的所有文件列表。
        /// </summary>
        /// <param name="modId">模组 id</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>文件列表（按发布时间倒序）</returns>
        Task<List<CurseForgeFile>> GetModFilesAsync(
            int modId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 下载指定的模组文件到目标路径。
        /// 用 <see cref="MultiThreadDownloader"/> 支持大文件多线程下载。
        /// </summary>
        /// <param name="file">要下载的文件</param>
        /// <param name="targetPath">目标完整路径</param>
        /// <param name="progress">下载进度回调</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task DownloadModFileAsync(
            CurseForgeFile file, string targetPath,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default);
    }
}
