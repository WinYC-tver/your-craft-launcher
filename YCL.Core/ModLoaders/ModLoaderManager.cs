using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;
using YCL.Core.Versions;

namespace YCL.Core.ModLoaders
{
    /// <summary>
    /// 模组加载器管理服务实现：聚合所有加载器的能力。
    ///
    /// 内部维护一个 ModLoaderType → IModLoaderInstaller 的映射表，
    /// 通过依赖注入容器在构造时注入所有加载器实例。
    /// </summary>
    public class ModLoaderManager : IModLoaderManager
    {
        private readonly Dictionary<ModLoaderType, IModLoaderInstaller> _installers;

        /// <summary>
        /// 构造 ModLoaderManager。
        /// </summary>
        /// <param name="installers">所有加载器安装器实例（DI 容器注入）</param>
        public ModLoaderManager(IEnumerable<IModLoaderInstaller> installers)
        {
            _installers = new Dictionary<ModLoaderType, IModLoaderInstaller>();
            foreach (var installer in installers)
            {
                _installers[installer.Type] = installer;
            }
            Logger.Info($"模组加载器管理服务已初始化，已加载 {_installers.Count} 个加载器：" +
                        string.Join(", ", _installers.Keys));
        }

        /// <inheritdoc/>
        public async Task<List<ModLoaderVersion>> GetAvailableLoadersAsync(
            string minecraftVersion,
            CancellationToken cancellationToken = default)
        {
            var result = new List<ModLoaderVersion>();

            // 并行调用所有加载器的 ListVersionsAsync，每个加载器独立 try-catch
            var tasks = _installers.Values.Select(async installer =>
            {
                try
                {
                    return await installer.ListVersionsAsync(minecraftVersion, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"获取 {installer.Type} 版本列表失败：{ex.Message}");
                    return new List<ModLoaderVersion>();
                }
            }).ToList();

            var lists = await Task.WhenAll(tasks);
            foreach (var list in lists)
                result.AddRange(list);

            Logger.Info($"聚合获取到 {result.Count} 个加载器版本（Minecraft {minecraftVersion}）");
            return result;
        }

        /// <inheritdoc/>
        public Task InstallAsync(
            ModLoaderType type,
            string minecraftVersion,
            ModLoaderVersion version,
            IProgress<InstallProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            if (!_installers.TryGetValue(type, out var installer))
            {
                throw new ArgumentException($"不支持的加载器类型：{type}", nameof(type));
            }
            return installer.InstallAsync(minecraftVersion, version, progress, cancellationToken);
        }

        /// <inheritdoc/>
        public Task UninstallAsync(ModLoaderType type, string minecraftVersion, CancellationToken cancellationToken = default)
        {
            if (!_installers.TryGetValue(type, out var installer))
            {
                throw new ArgumentException($"不支持的加载器类型：{type}", nameof(type));
            }
            return installer.UninstallAsync(minecraftVersion, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<Dictionary<ModLoaderType, bool>> CheckInstalledAsync(string minecraftVersion)
        {
            var result = new Dictionary<ModLoaderType, bool>();
            foreach (var kv in _installers)
            {
                try
                {
                    var installed = await kv.Value.IsInstalledAsync(minecraftVersion);
                    result[kv.Key] = installed;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"检查 {kv.Key} 安装状态失败：{ex.Message}");
                    result[kv.Key] = false;
                }
            }
            return result;
        }

        /// <inheritdoc/>
        public IModLoaderInstaller GetInstaller(ModLoaderType type)
        {
            if (!_installers.TryGetValue(type, out var installer))
            {
                throw new ArgumentException($"不支持的加载器类型：{type}", nameof(type));
            }
            return installer;
        }
    }
}
