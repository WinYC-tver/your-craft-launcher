using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Versions;

namespace YCL.Core.ModLoaders
{
    /// <summary>
    /// 模组加载器管理服务接口。
    /// 聚合所有加载器（Forge/Fabric/Quilt/NeoForge/LiteLoader）的能力，
    /// 提供"按 Minecraft 版本查询可用加载器"、"安装/卸载加载器"、"检查已装加载器"等统一 API。
    ///
    /// UI 层（HomePageViewModel 等）只依赖此接口，不直接依赖具体加载器实现。
    /// </summary>
    public interface IModLoaderManager
    {
        /// <summary>
        /// 获取指定 Minecraft 版本下所有可用加载器及其版本列表。
        /// 内部会并行调用所有加载器的 ListVersionsAsync，聚合结果。
        /// 各加载器网络错误时返回空列表（不抛异常）。
        /// </summary>
        /// <param name="minecraftVersion">Minecraft 版本号</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>所有可用加载器版本列表</returns>
        Task<List<ModLoaderVersion>> GetAvailableLoadersAsync(string minecraftVersion, CancellationToken cancellationToken = default);

        /// <summary>
        /// 安装指定类型的加载器到指定 Minecraft 版本。
        /// </summary>
        /// <param name="type">加载器类型</param>
        /// <param name="minecraftVersion">Minecraft 版本号</param>
        /// <param name="version">加载器版本信息</param>
        /// <param name="progress">进度报告</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task InstallAsync(
            ModLoaderType type,
            string minecraftVersion,
            ModLoaderVersion version,
            IProgress<InstallProgress>? progress,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 卸载指定类型加载器（删除版本目录）。
        /// </summary>
        /// <param name="type">加载器类型</param>
        /// <param name="minecraftVersion">Minecraft 版本号</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task UninstallAsync(ModLoaderType type, string minecraftVersion, CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查指定 Minecraft 版本装了哪些加载器。
        /// 返回各加载器类型是否已安装的字典。
        /// </summary>
        /// <param name="minecraftVersion">Minecraft 版本号</param>
        /// <returns>加载器类型 → 是否已安装</returns>
        Task<Dictionary<ModLoaderType, bool>> CheckInstalledAsync(string minecraftVersion);

        /// <summary>
        /// 获取指定加载器类型的安装器实例（供 UI 调用更具体的方法）。
        /// </summary>
        /// <param name="type">加载器类型</param>
        /// <returns>对应加载器安装器</returns>
        IModLoaderInstaller GetInstaller(ModLoaderType type);
    }
}
