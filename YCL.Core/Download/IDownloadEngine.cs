using System;
using System.Threading;
using System.Threading.Tasks;

namespace YCL.Core.Download
{
    /// <summary>
    /// 下载引擎统一接口。PCL CE、Ghost Downloader 3、默认引擎都实现此接口，
    /// 便于在运行时根据 AppConfig.DownloadEngine 切换。
    /// </summary>
    public interface IDownloadEngine
    {
        /// <summary>引擎名称（如 "PCL CE"、"Ghost Downloader 3"）</summary>
        string Name { get; }

        /// <summary>
        /// 下载指定 URL 的文件到目标路径。
        /// </summary>
        /// <param name="url">下载 URL</param>
        /// <param name="targetPath">目标文件完整路径</param>
        /// <param name="maxThreads">最大并发线程数</param>
        /// <param name="progress">进度回调（0~100）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>是否下载成功</returns>
        Task<bool> DownloadAsync(string url, string targetPath, int maxThreads, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

        /// <summary>下载并校验 SHA1（可选）</summary>
        Task<bool> DownloadWithSha1Async(string url, string targetPath, string? expectedSha1, int maxThreads, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
    }
}
