using System;
using YCL.Core.Utils;
using YCL.Models;

namespace YCL.Core.Download
{
    /// <summary>
    /// 下载源管理器实现：根据 <see cref="AppConfig.DownloadSource"/> 把官方 URL
    /// 转换为镜像 URL（主要是 BMCLAPI，国内访问速度更快）。
    ///
    /// BMCLAPI 是 bangbang93 维护的 Minecraft 资源镜像，覆盖了：
    /// - libraries（库 jar 文件）
    /// - 版本 JSON（version_manifest 与各版本 JSON）
    /// - 客户端 jar
    /// - 资源文件（assets）
    /// - 日志配置文件
    ///
    /// MCBBS 镜像源目前已合并到 BMCLAPI（URL 前缀相同），所以两者替换规则一致。
    /// </summary>
    public class DownloadSourceManager : IDownloadSource
    {
        /// <summary>当前下载源（构造时从配置读取，运行期间不变）</summary>
        public DownloadSource Source { get; }

        /// <summary>
        /// 构造下载源管理器。
        /// </summary>
        /// <param name="source">从 AppConfig 读取的下载源设置</param>
        public DownloadSourceManager(DownloadSource source)
        {
            Source = source;
            Logger.Info($"下载源管理器已初始化，当前下载源：{source}");
        }

        /// <inheritdoc/>
        public string TransformUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;

            // 官方源：原样返回，不做任何替换
            if (Source == DownloadSource.Official)
                return url;

            // BMCLAPI 与 MCBBS 使用相同的镜像前缀（MCBBS 已合并到 BMCLAPI）
            // 按顺序尝试每一条替换规则，命中一条就返回
            foreach (var rule in BmclapiReplaceRules)
            {
                if (url.StartsWith(rule.Key, StringComparison.OrdinalIgnoreCase))
                {
                    var transformed = rule.Value + url.Substring(rule.Key.Length);
                    return transformed;
                }
            }

            // 没有命中任何规则，原样返回（可能是 BMCLAPI 已镜像但前缀未列出的 URL，
            // 也可能是第三方 URL，不应擅自替换）
            return url;
        }

        /// <summary>
        /// BMCLAPI 镜像替换规则表。
        /// 键 = 官方源 URL 前缀；值 = 对应的 BMCLAPI 镜像前缀。
        /// 顺序很重要：更具体的前缀放在前面，避免被更短的前缀误匹配。
        ///
        /// 说明：piston-meta 和 piston-data 都指向版本 JSON / 版本 manifest，
        /// 它们在 BMCLAPI 中直接映射到根路径（保留后续路径不变）。
        /// </summary>
        private static readonly System.Collections.Generic.KeyValuePair<string, string>[] BmclapiReplaceRules =
        {
            // libraries（库 jar 文件）
            new("https://libraries.minecraft.net/", "https://bmclapi2.bangbang93.com/libraries/"),

            // launcher.mojang.com 上的对象（client.jar、部分库）
            new("https://launcher.mojang.com/v1/objects/", "https://bmclapi2.bangbang93.com/v1/objects/"),

            // piston-meta.mojang.com（版本 manifest 与版本 JSON）
            new("https://piston-meta.mojang.com/", "https://bmclapi2.bangbang93.com/"),

            // piston-data.mojang.com（部分版本的 client.jar）
            new("https://piston-data.mojang.com/", "https://bmclapi2.bangbang93.com/"),

            // resources.download.minecraft.net（assets 资源文件）
            new("https://resources.download.minecraft.net/", "https://bmclapi2.bangbang93.com/assets/"),
        };
    }
}
