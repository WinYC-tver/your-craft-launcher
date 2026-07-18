using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;

namespace YCL.Core.Mods
{
    /// <summary>
    /// Modrinth API 客户端接口。
    /// Modrinth 是新兴的 Minecraft 模组托管平台，API 完全公开，无需 API Key。
    ///
    /// 官方文档：https://docs.modrinth.com/
    /// 主要端点：
    /// - GET /v2/search：搜索项目（用 facets 过滤类型 / 加载器 / 版本）
    /// - GET /v2/project/{id}/version：获取项目的所有版本
    /// - 文件下载 URL 直接来自版本响应中的 files[].url
    /// </summary>
    public interface IModrinthClient
    {
        /// <summary>
        /// 搜索 Modrinth 上的项目。
        /// </summary>
        /// <param name="query">搜索关键字</param>
        /// <param name="projectType">项目类型（mod / modpack / resourcepack / shader / world）</param>
        /// <param name="loaderType">加载器过滤（fabric / forge / quilt / neoforge，可空）</param>
        /// <param name="gameVersion">Minecraft 版本过滤（如 "1.20.4"，可空）</param>
        /// <param name="page">页码（从 0 开始）</param>
        /// <param name="pageSize">每页数量（最大 100）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>搜索结果列表</returns>
        Task<List<ModrinthSearchResult>> SearchModsAsync(
            string query,
            string projectType = "mod",
            string? loaderType = null,
            string? gameVersion = null,
            int page = 0, int pageSize = 20,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取指定项目的所有版本列表。
        /// </summary>
        /// <param name="projectId">项目 id（base62 字符串）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>版本列表（按发布时间倒序）</returns>
        Task<List<ModrinthVersion>> GetProjectVersionsAsync(
            string projectId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 下载指定的模组文件到目标路径。
        /// </summary>
        /// <param name="downloadUrl">文件下载 URL</param>
        /// <param name="targetPath">目标完整路径</param>
        /// <param name="progress">下载进度回调</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task DownloadModFileAsync(
            string downloadUrl, string targetPath,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default);
    }
}
