using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;

namespace YCL.Core.Download
{
    /// <summary>
    /// 默认下载引擎适配器：包装现有的 <see cref="MultiThreadDownloader"/> 实现 <see cref="IDownloadEngine"/>。
    ///
    /// 这里没有改 <see cref="MultiThreadDownloader"/> 本身（它被 13 处调用方依赖），
    /// 而是套一层适配器把旧的 "事件 + CancellationToken" 风格转换为新的 "IProgress&lt;int&gt; + maxThreads + bool 返回值" 风格。
    /// 这样旧的调用方继续用 <see cref="MultiThreadDownloader"/>，新的代码通过工厂用 <see cref="IDownloadEngine"/>，两边互不影响。
    /// </summary>
    public class DefaultDownloadEngine : IDownloadEngine
    {
        /// <inheritdoc/>
        public string Name => "默认（多线程）";

        /// <inheritdoc/>
        public async Task<bool> DownloadAsync(
            string url,
            string targetPath,
            int maxThreads,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            // 用传入的 maxThreads 构造底层下载器（默认重试 3 次，与历史行为一致）
            var downloader = new MultiThreadDownloader(
                threadCount: Math.Max(1, maxThreads),
                retryCount: 3);

            // 把旧的 ProgressChanged 事件转换为 IProgress<int>（百分比）
            EventHandler<DownloadProgressEventArgs>? handler = null;
            if (progress != null)
            {
                handler = (_, e) =>
                {
                    // Percent 在 TotalBytes<=0 时为 -1，此时不上报，避免 UI 显示异常
                    if (e.Percent >= 0)
                        progress.Report((int)Math.Clamp(e.Percent, 0, 100));
                };
                downloader.ProgressChanged += handler;
            }

            try
            {
                await downloader.DownloadAsync(url, targetPath, cancellationToken);
                return File.Exists(targetPath);
            }
            catch (OperationCanceledException)
            {
                // 取消不算"下载失败"，但文件可能不完整，返回 false
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"默认引擎下载失败：{url}", ex);
                return false;
            }
            finally
            {
                if (handler != null)
                    downloader.ProgressChanged -= handler;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> DownloadWithSha1Async(
            string url,
            string targetPath,
            string? expectedSha1,
            int maxThreads,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            // 1. 先下载文件
            var ok = await DownloadAsync(url, targetPath, maxThreads, progress, cancellationToken);
            if (!ok)
                return false;

            // 2. 如果没提供期望 SHA1，视为校验通过
            if (string.IsNullOrWhiteSpace(expectedSha1))
                return true;

            // 3. 计算并校验 SHA1
            try
            {
                var valid = await FileValidator.ValidateAsync(targetPath, expectedSha1, cancellationToken);
                if (!valid)
                {
                    // 校验失败：删除损坏的文件，避免下次被当成已下载
                    try { File.Delete(targetPath); } catch { /* 忽略删除失败 */ }
                    Logger.Warn($"SHA1 校验失败，已删除文件：{targetPath}");
                }
                return valid;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"SHA1 校验过程异常：{targetPath}", ex);
                return false;
            }
        }
    }
}
