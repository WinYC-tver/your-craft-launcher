using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;
using YCL.Core.Mods;

namespace YCL.Core.Resources
{
    /// <summary>
    /// 资源类型枚举：资源包 / 光影包 / 地图。
    /// </summary>
    public enum ResourceType
    {
        /// <summary>资源包（resourcepack，改变游戏材质、音效等）</summary>
        ResourcePack = 0,

        /// <summary>光影包（shader，改变画面渲染效果）</summary>
        ShaderPack = 1,

        /// <summary>地图（world，存档文件）</summary>
        World = 2
    }

    /// <summary>
    /// 资源服务接口：通过 Modrinth / CurseForge API 搜索并下载资源包、光影包、地图。
    ///
    /// 资源类型与目标目录的对应关系：
    /// - ResourcePack → gameDir/resourcepacks/
    /// - ShaderPack → gameDir/shaderpacks/
    /// - World → gameDir/saves/（需要解压）
    /// </summary>
    public interface IResourceService
    {
        /// <summary>
        /// 搜索资源（资源包 / 光影包 / 地图）。
        /// </summary>
        /// <param name="query">搜索关键字</param>
        /// <param name="type">资源类型</param>
        /// <param name="source">来源（CurseForge / Modrinth / All）</param>
        /// <param name="gameVersion">Minecraft 版本过滤（可空）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>搜索结果列表</returns>
        Task<List<ModSearchResult>> SearchResourcesAsync(
            string query, ResourceType type, ModSource source,
            string? gameVersion = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 下载资源包到 gameDir/resourcepacks/。
        /// </summary>
        Task<string> DownloadResourcePackAsync(
            ModSearchResult result, string gameDir,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 下载光影包到 gameDir/shaderpacks/。
        /// </summary>
        Task<string> DownloadShaderPackAsync(
            ModSearchResult result, string gameDir,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 下载地图并解压到 gameDir/saves/&lt;mapName&gt;/。
        /// </summary>
        Task<string> DownloadMapAsync(
            ModSearchResult result, string gameDir,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default);
    }
}
