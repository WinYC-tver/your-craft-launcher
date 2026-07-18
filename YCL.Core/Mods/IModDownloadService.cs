using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;

namespace YCL.Core.Mods
{
    /// <summary>
    /// 模组下载服务接口。
    /// 统一封装 CurseForge 与 Modrinth 两个平台的搜索与下载能力，
    /// 上层 UI 只依赖此接口，不直接依赖具体平台客户端。
    /// </summary>
    public interface IModDownloadService
    {
        /// <summary>
        /// 跨平台搜索模组。如果 source=All 且 CurseForge 未配置 Key，会自动降级到仅 Modrinth。
        /// </summary>
        /// <param name="query">搜索关键字</param>
        /// <param name="source">来源（CurseForge / Modrinth / All）</param>
        /// <param name="loaderType">加载器过滤（fabric / forge / quilt / neoforge，可空）</param>
        /// <param name="gameVersion">Minecraft 版本过滤（可空）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>归一化的搜索结果列表</returns>
        Task<List<ModSearchResult>> SearchAsync(
            string query, ModSource source,
            string? loaderType = null, string? gameVersion = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 下载一个模组到指定游戏目录的 mods 文件夹。
        /// 会弹出一个版本选择对话框（在 UI 层实现），所以这里只接受选好的下载信息。
        /// </summary>
        /// <param name="result">搜索结果项（含来源平台信息）</param>
        /// <param name="gameDir">游戏目录路径</param>
        /// <param name="progress">下载进度回调</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>下载后的文件完整路径</returns>
        Task<string> DownloadModAsync(
            ModSearchResult result, string gameDir,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取一个模组项目的所有可下载版本（用于 UI 让用户选具体版本）。
        /// 返回统一格式的版本信息列表。
        /// </summary>
        /// <param name="result">搜索结果项</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>可下载版本列表</returns>
        Task<List<ModVersionInfo>> GetVersionsAsync(
            ModSearchResult result, CancellationToken cancellationToken = default);

        /// <summary>
        /// 下载指定版本的模组文件到 mods 文件夹。
        /// </summary>
        /// <param name="result">搜索结果项</param>
        /// <param name="version">选定的版本</param>
        /// <param name="gameDir">游戏目录路径</param>
        /// <param name="progress">下载进度回调</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>下载后的文件完整路径</returns>
        Task<string> DownloadVersionAsync(
            ModSearchResult result, ModVersionInfo version, string gameDir,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 统一格式的模组版本信息。
    /// 把 CurseForge 的 CurseForgeFile 与 Modrinth 的 ModrinthVersion 归一化。
    /// </summary>
    public class ModVersionInfo
    {
        /// <summary>来源平台</summary>
        public ModSource Source { get; set; }

        /// <summary>版本 id（CurseForge 是文件 id，Modrinth 是版本 id）</summary>
        public string VersionId { get; set; } = string.Empty;

        /// <summary>显示名</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>文件名</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>下载 URL（CurseForge 可能为空，需要 API 获取）</summary>
        public string? DownloadUrl { get; set; }

        /// <summary>支持的 Minecraft 版本列表</summary>
        public List<string> GameVersions { get; set; } = new();

        /// <summary>支持的加载器列表</summary>
        public List<string> Loaders { get; set; } = new();

        /// <summary>发布日期（ISO 8601 字符串）</summary>
        public string DatePublished { get; set; } = string.Empty;

        /// <summary>下载次数</summary>
        public long Downloads { get; set; }

        /// <summary>版本类型（release / beta / alpha）</summary>
        public string VersionType { get; set; } = "release";

        /// <summary>显示文字（含版本号、MC 版本、加载器、版本类型）</summary>
        public string DisplayText
        {
            get
            {
                var mcVersions = GameVersions.Count > 0
                    ? string.Join("/", GameVersions)
                    : "未知版本";
                var loaders = Loaders.Count > 0
                    ? string.Join("/", Loaders)
                    : "通用";
                return $"{Name} [{mcVersions} / {loaders}] ({VersionType})";
            }
        }

        public override string ToString() => DisplayText;
    }
}
