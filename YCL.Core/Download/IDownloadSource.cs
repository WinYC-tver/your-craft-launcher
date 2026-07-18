using YCL.Models;

namespace YCL.Core.Download
{
    /// <summary>
    /// 下载源接口：定义把官方 URL 转换为镜像 URL 的能力。
    /// 实现类根据 <see cref="AppConfig.DownloadSource"/> 决定使用哪个镜像源
    /// （官方 / BMCLAPI / MCBBS）。下载器、版本清单服务等在发起请求前，
    /// 都会调用 <see cref="TransformUrl"/> 把官方地址转换为当前配置的镜像地址。
    /// </summary>
    public interface IDownloadSource
    {
        /// <summary>当前生效的下载源</summary>
        DownloadSource Source { get; }

        /// <summary>
        /// 把官方 URL 转换为镜像 URL。
        /// 规则：
        /// - 当前是 <see cref="DownloadSource.Official"/> 时，原样返回；
        /// - 当前是 BMCLAPI / MCBBS 时，按规则替换域名前缀，路径保持不变；
        /// - 未命中任何替换规则的 URL（如第三方地址）原样返回，避免误替换。
        /// </summary>
        /// <param name="url">原始官方 URL</param>
        /// <returns>转换后的 URL（始终非空；输入为空时返回空）</returns>
        string TransformUrl(string url);
    }
}
