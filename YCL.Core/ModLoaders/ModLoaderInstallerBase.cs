using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Models;

namespace YCL.Core.ModLoaders
{
    /// <summary>
    /// 模组加载器安装器基类：提供所有加载器共用的功能。
    /// - 提供 .minecraft 路径（通过 Func 委托从配置读，避免依赖 YCL.Services）
    /// - 提供下载源感知的 URL 选择（官方 vs BMCLAPI 镜像）
    /// - 提供通用的 IsInstalledAsync / UninstallAsync 实现（按版本目录命名规则扫描）
    /// - 提供共享 HttpClient
    /// </summary>
    public abstract class ModLoaderInstallerBase : IModLoaderInstaller
    {
        /// <summary>.minecraft 路径提供者</summary>
        protected readonly Func<string> MinecraftPathProvider;

        /// <summary>下载源管理器（用于判断当前是官方还是镜像源）</summary>
        protected readonly IDownloadSource DownloadSourceManager;

        /// <summary>共享 HttpClient（10 分钟超时，适合大文件下载）</summary>
        protected static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        /// <summary>当前 .minecraft 路径</summary>
        protected string MinecraftPath
        {
            get
            {
                var p = MinecraftPathProvider();
                if (string.IsNullOrWhiteSpace(p))
                {
                    p = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        ".minecraft");
                }
                return p;
            }
        }

        /// <summary>
        /// 构造加载器安装器基类。
        /// </summary>
        /// <param name="minecraftPathProvider">.minecraft 路径提供者</param>
        /// <param name="downloadSource">下载源管理器</param>
        protected ModLoaderInstallerBase(Func<string> minecraftPathProvider, IDownloadSource downloadSource)
        {
            MinecraftPathProvider = minecraftPathProvider ?? throw new ArgumentNullException(nameof(minecraftPathProvider));
            DownloadSourceManager = downloadSource ?? throw new ArgumentNullException(nameof(downloadSource));
        }

        /// <inheritdoc/>
        public abstract ModLoaderType Type { get; }

        /// <inheritdoc/>
        public abstract Task<List<ModLoaderVersion>> ListVersionsAsync(string minecraftVersion, CancellationToken cancellationToken = default);

        /// <inheritdoc/>
        public abstract Task InstallAsync(
            string minecraftVersion,
            ModLoaderVersion version,
            IProgress<InstallProgress>? progress,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 卸载加载器：删除 .minecraft/versions/&lt;mcVersion&gt;-&lt;prefix&gt;* 整个目录。
        /// 子类通过 <see cref="GetVersionDirectoryPrefix"/> 提供命名前缀。
        /// </summary>
        public virtual Task UninstallAsync(string minecraftVersion, CancellationToken cancellationToken = default)
        {
            var prefix = GetVersionDirectoryPrefix(minecraftVersion);
            var versionsDir = Path.Combine(MinecraftPath, "versions");
            if (!Directory.Exists(versionsDir))
            {
                Logger.Info($"versions 目录不存在，无需卸载 {Type}");
                return Task.CompletedTask;
            }

            // 找到所有匹配 mcVersion-{prefix}* 的目录
            var dirsToDelete = new List<string>();
            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    dirsToDelete.Add(dir);
            }

            foreach (var dir in dirsToDelete)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    Logger.Info($"已删除加载器版本目录：{dir}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"删除加载器版本目录失败：{dir}", ex);
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 检查指定 Minecraft 版本是否安装了此加载器。
        /// 通过扫描版本目录是否含 mcVersion-{prefix}* 命名的子目录判断。
        /// </summary>
        public virtual Task<bool> IsInstalledAsync(string minecraftVersion)
        {
            var prefix = GetVersionDirectoryPrefix(minecraftVersion);
            var versionsDir = Path.Combine(MinecraftPath, "versions");
            if (!Directory.Exists(versionsDir))
                return Task.FromResult(false);

            try
            {
                foreach (var dir in Directory.GetDirectories(versionsDir))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return Task.FromResult(true);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"扫描加载器安装状态失败：{ex.Message}");
            }

            return Task.FromResult(false);
        }

        /// <summary>
        /// 子类实现：返回版本目录命名前缀（如 "1.18.2-fabric-"）。
        /// 用于 IsInstalledAsync 与 UninstallAsync 的扫描。
        /// </summary>
        protected abstract string GetVersionDirectoryPrefix(string minecraftVersion);

        /// <summary>
        /// 根据当前下载源选择 URL：官方源返回 officialUrl，BMCLAPI/MCBBS 返回 bmclapiUrl。
        /// 如果 bmclapiUrl 为空则一律返回 officialUrl。
        /// </summary>
        protected string SelectUrl(string officialUrl, string? bmclapiUrl)
        {
            if (DownloadSourceManager.Source == YCL.Models.DownloadSource.Official || string.IsNullOrEmpty(bmclapiUrl))
                return officialUrl;
            return bmclapiUrl;
        }

        /// <summary>
        /// 下载文本内容（GET 请求），失败抛 HttpRequestException。
        /// </summary>
        protected static async Task<string> DownloadTextAsync(string url, CancellationToken ct)
        {
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }

        /// <summary>
        /// 下载字节数据（GET 请求），失败抛 HttpRequestException。
        /// </summary>
        protected static async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct)
        {
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(ct);
        }

        /// <summary>
        /// 获取加载器版本目录的完整路径：.minecraft/versions/{mcVersion}-{loaderName}-{loaderVersion}/
        /// 同时确保目录存在。
        /// </summary>
        protected string GetVersionDirectory(string minecraftVersion, string loaderVersion)
        {
            var dirName = $"{minecraftVersion}-{Type.ToString().ToLowerInvariant()}-{loaderVersion}";
            var fullPath = Path.Combine(MinecraftPath, "versions", dirName);
            Directory.CreateDirectory(fullPath);
            return fullPath;
        }

        /// <summary>
        /// 写入版本 JSON 文件到指定版本目录。
        /// 文件名与目录同名（标准 Minecraft 版本 JSON 命名）。
        /// </summary>
        protected static void WriteVersionJson(string versionDir, string json)
        {
            var dirName = Path.GetFileName(versionDir);
            var jsonPath = Path.Combine(versionDir, dirName + ".json");
            File.WriteAllText(jsonPath, json);
            Logger.Info($"已写入版本 JSON：{jsonPath}");
        }
    }
}
