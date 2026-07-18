using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Versions;

namespace YCL.Core.ModLoaders
{
    /// <summary>
    /// 模组加载器安装器通用接口。
    /// 所有加载器（Forge/Fabric/Quilt/NeoForge/LiteLoader）实现此接口。
    ///
    /// 实现要点：
    /// - ListVersionsAsync：从加载器官方 API（或镜像）获取某 Minecraft 版本下的所有可用加载器版本
    /// - InstallAsync：把版本 JSON 写入 .minecraft/versions/&lt;mcVersion&gt;-&lt;loader&gt;-&lt;loaderVersion&gt;/，
    ///                 依赖（libraries/client.jar）下载交给 MinecraftFileDownloader
    /// - UninstallAsync：删除版本目录
    /// - IsInstalledAsync：扫描版本目录名是否匹配加载器命名规则
    /// </summary>
    public interface IModLoaderInstaller
    {
        /// <summary>加载器类型</summary>
        ModLoaderType Type { get; }

        /// <summary>
        /// 获取指定 Minecraft 版本下可用的加载器版本列表。
        /// 网络错误时返回空列表（不抛异常），调用方应自己处理友好提示。
        /// </summary>
        /// <param name="minecraftVersion">Minecraft 版本号（如 "1.18.2"）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>加载器版本列表</returns>
        Task<List<ModLoaderVersion>> ListVersionsAsync(string minecraftVersion, CancellationToken cancellationToken = default);

        /// <summary>
        /// 安装加载器。流程：
        /// 1. 从 API 获取加载器版本 JSON 模板
        /// 2. 写入 .minecraft/versions/&lt;mcVersion&gt;-&lt;loader&gt;-&lt;loaderVersion&gt;/&lt;同名&gt;.json
        /// 3. 依赖（libraries 等）下载交给 MinecraftFileDownloader（在启动前会自动校验下载）
        /// </summary>
        /// <param name="minecraftVersion">Minecraft 版本号</param>
        /// <param name="version">加载器版本信息</param>
        /// <param name="progress">进度报告</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task InstallAsync(
            string minecraftVersion,
            ModLoaderVersion version,
            IProgress<InstallProgress>? progress,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 卸载加载器：删除 .minecraft/versions/&lt;mcVersion&gt;-&lt;loader&gt;-*&lt;loaderVersion&gt;/ 整个目录。
        /// </summary>
        /// <param name="minecraftVersion">Minecraft 版本号</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task UninstallAsync(string minecraftVersion, CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查指定 Minecraft 版本是否安装了此加载器。
        /// 通过扫描 .minecraft/versions 目录下的命名规则判断。
        /// </summary>
        /// <param name="minecraftVersion">Minecraft 版本号</param>
        /// <returns>是否已安装</returns>
        Task<bool> IsInstalledAsync(string minecraftVersion);
    }
}
