using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;

namespace YCL.Core.Resources
{
    /// <summary>
    /// 整合包服务接口：下载与安装整合包。
    ///
    /// 支持两种整合包格式：
    /// 1. CurseForge 整合包：manifest.json + overrides/ 文件夹
    /// 2. Modrinth 整合包：modrinth.index.json + overrides/ 文件夹
    ///
    /// 安装流程：
    /// 1. 下载整合包 zip（如果是本地文件则跳过）
    /// 2. 解压 zip
    /// 3. 解析清单文件，获取 Minecraft 版本、加载器类型、模组列表
    /// 4. 调用 VersionManager 安装 Minecraft 版本
    /// 5. 调用 ModLoaderManager 安装指定加载器
    /// 6. 调用 CurseForge/Modrinth 客户端下载所有模组到 mods/
    /// 7. 把 overrides/ 文件夹内容覆盖到游戏目录
    /// </summary>
    public interface IModpackService
    {
        /// <summary>
        /// 下载整合包 zip 到本地临时文件。
        /// </summary>
        /// <param name="url">整合包下载 URL</param>
        /// <param name="targetPath">目标文件路径</param>
        /// <param name="progress">下载进度回调</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task DownloadModpackAsync(
            string url, string targetPath,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 安装本地整合包 zip 文件。
        /// 完整流程：解压 → 解析清单 → 装版本 → 装加载器 → 下模组 → 覆盖 overrides。
        /// </summary>
        /// <param name="modpackPath">整合包 zip 文件路径</param>
        /// <param name="gameDir">游戏目录路径（如 .minecraft）</param>
        /// <param name="versionId">要安装成的版本 id（如 "MyModpack-1.0"）</param>
        /// <param name="progress">安装进度回调</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>是否安装成功</returns>
        Task<bool> InstallModpackAsync(
            string modpackPath, string gameDir, string versionId,
            IProgress<ModpackInstallProgress>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 仅解析整合包清单文件，不执行安装。
        /// 用于 UI 先展示整合包信息（含哪些模组、MC 版本等）让用户确认后再安装。
        /// </summary>
        /// <param name="modpackPath">整合包 zip 文件路径</param>
        /// <returns>解析出的清单信息（解析失败返回 null）</returns>
        Task<ModpackManifest?> ParseModpackAsync(string modpackPath);
    }
}
