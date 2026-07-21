using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;

namespace YCL.Core.Download
{
    /// <summary>
    /// Ghost Downloader 3 风格下载引擎：实现 <see cref="IDownloadEngine"/>。
    ///
    /// 参考 Ghost-Downloader-3（https://github.com/XiaoYouChR/Ghost-Downloader-3）的下载思路：
    /// - <b>HEAD 探测</b>：先用 HEAD 请求获取文件大小（Content-Length）与是否支持 Range（Accept-Ranges）。
    /// - <b>分片并发</b>：把文件按大小均分为 N 个分片（N = maxThreads），每个分片用独立 HttpClient 并发下载。
    /// - <b>临时分片文件</b>：每个分片写入独立的 .part{i} 临时文件，避免多线程写同一个文件的冲突。
    /// - <b>合并</b>：所有分片完成后按顺序合并为最终文件，然后删除临时分片。
    /// - <b>进度统计</b>：用线程安全的计数器统计已下载字节数，定期回调 progress。
    /// - <b>取消</b>：支持 CancellationToken 取消，取消后清理临时文件。
    /// - <b>回退</b>：如果服务器不支持 Range（没有 Content-Length 或 Accept-Ranges: none），回退到单线程下载。
    ///
    /// 与 <see cref="MultiThreadDownloader"/> 的区别：
    /// - Ghost 用独立的 .part{i} 分片文件，最后合并；MultiThreadDownloader 用一个 .part 文件 + Seek 写入。
    /// - Ghost 不维护 .meta 续传信息（简化版），每次启动重新分片。
    /// </summary>
    public class GhostDownloader : IDownloadEngine
    {
        /// <summary>分片下载的缓冲区大小（64KB）</summary>
        private const int BufferSize = 64 * 1024;

        /// <summary>触发分片下载的最小文件大小（1MB），小于此值用单线程</summary>
        private const long MultiThreadThreshold = 1 * 1024 * 1024;

        /// <summary>进度回调最小间隔（毫秒）</summary>
        private const int ProgressIntervalMs = 200;

        /// <summary>每个分片失败的重试次数</summary>
        private const int RetryCount = 3;

        /// <inheritdoc/>
        public string Name => "Ghost Downloader 3";

        /// <inheritdoc/>
        public async Task<bool> DownloadAsync(
            string url,
            string targetPath,
            int maxThreads,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return await DownloadWithSha1Async(url, targetPath, expectedSha1: null, maxThreads, progress, cancellationToken);
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
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(targetPath))
            {
                Logger.Warn("Ghost 引擎：URL 或目标路径为空");
                return false;
            }

            // 确保目标目录存在
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            try
            {
                // 1. HEAD 探测
                var (totalSize, supportsRange) = await ProbeAsync(url, cancellationToken);

                bool ok;
                if (!supportsRange || totalSize < MultiThreadThreshold)
                {
                    // 2a. 回退单线程
                    Logger.Info($"Ghost 引擎：回退单线程下载（supportsRange={supportsRange}, size={totalSize}） - {url}");
                    ok = await DownloadSingleThreadAsync(url, targetPath, progress, cancellationToken);
                }
                else
                {
                    // 2b. 多线程分片下载
                    var threads = Math.Max(1, Math.Min(maxThreads, 16)); // 上限 16，避免过度并发
                    Logger.Info($"Ghost 引擎：分片下载（{threads} 线程，{totalSize} 字节） - {url}");
                    ok = await DownloadMultiThreadAsync(url, targetPath, totalSize, threads, progress, cancellationToken);
                }

                if (!ok)
                    return false;

                // 3. SHA1 校验
                if (!string.IsNullOrWhiteSpace(expectedSha1))
                {
                    var valid = await FileValidator.ValidateAsync(targetPath, expectedSha1, cancellationToken);
                    if (!valid)
                    {
                        try { File.Delete(targetPath); } catch { /* 忽略 */ }
                        Logger.Warn($"Ghost 引擎：SHA1 校验失败，已删除文件 - {targetPath}");
                        return false;
                    }
                }

                progress?.Report(100);
                return true;
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"Ghost 引擎：下载被取消 - {url}");
                // 清理可能的临时文件
                CleanupPartFiles(targetPath);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"Ghost 引擎：下载异常 - {url}", ex);
                CleanupPartFiles(targetPath);
                return false;
            }
        }

        /// <summary>
        /// HEAD 探测：获取文件大小与是否支持 Range。
        /// HEAD 失败时返回 (-1, false)，调用方会回退单线程。
        /// </summary>
        private static async Task<(long totalSize, bool supportsRange)> ProbeAsync(string url, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await FileDownloader.SharedClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn($"Ghost 引擎：HEAD 请求失败（{response.StatusCode}） - {url}");
                    return (-1, false);
                }

                var totalSize = response.Content.Headers.ContentLength ?? -1;
                var supportsRange = response.Headers.AcceptRanges?.Contains("bytes") == true;

                // 兜底：如果服务器没声明 Accept-Ranges 但有 Content-Length，也认为可能支持（让分片逻辑试探）
                if (!supportsRange && totalSize > 0)
                    supportsRange = true;

                return (totalSize, supportsRange);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Ghost 引擎：HEAD 请求异常 - {url} - {ex.Message}");
                return (-1, false);
            }
        }

        /// <summary>
        /// 单线程下载（回退用）：直接调用 <see cref="FileDownloader"/>。
        /// </summary>
        private static async Task<bool> DownloadSingleThreadAsync(
            string url, string targetPath, IProgress<int>? progress, CancellationToken ct)
        {
            var downloader = new FileDownloader(RetryCount);

            EventHandler<DownloadProgressEventArgs>? handler = null;
            if (progress != null)
            {
                handler = (_, e) =>
                {
                    if (e.Percent >= 0)
                        progress.Report((int)Math.Clamp(e.Percent, 0, 99));
                };
                downloader.ProgressChanged += handler;
            }

            try
            {
                await downloader.DownloadAsync(url, targetPath, ct);
                return File.Exists(targetPath);
            }
            finally
            {
                if (handler != null)
                    downloader.ProgressChanged -= handler;
            }
        }

        /// <summary>
        /// 多线程分片下载：把文件分成 N 个分片，每个分片独立下载到 .part{i} 文件，最后合并。
        /// </summary>
        private static async Task<bool> DownloadMultiThreadAsync(
            string url, string targetPath, long totalSize, int threadCount,
            IProgress<int>? progress, CancellationToken ct)
        {
            // 1. 划分分片范围
            var ranges = SplitRanges(totalSize, threadCount);

            // 2. 启动所有分片下载任务
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var exceptionQueue = new ConcurrentQueue<Exception>();
            var totalDownloaded = 0L;
            var progressLock = new object();
            var lastReportTime = DateTime.UtcNow;
            int lastReportedPercent = -1;

            var tasks = new List<Task>();
            for (int i = 0; i < ranges.Count; i++)
            {
                var index = i;
                var range = ranges[i];
                var partPath = $"{targetPath}.part{index}";

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await DownloadRangeWithRetryAsync(url, partPath, range.Start, range.End, cts.Token,
                            delta =>
                            {
                                lock (progressLock)
                                {
                                    totalDownloaded += delta;
                                    var now = DateTime.UtcNow;
                                    if ((now - lastReportTime).TotalMilliseconds >= ProgressIntervalMs)
                                    {
                                        lastReportTime = now;
                                        var percent = (int)Math.Clamp(totalDownloaded * 100.0 / totalSize, 0, 99);
                                        if (percent != lastReportedPercent)
                                        {
                                            progress?.Report(percent);
                                            lastReportedPercent = percent;
                                        }
                                    }
                                }
                            });
                    }
                    catch (OperationCanceledException)
                    {
                        // 用户取消：不抛异常，让外层处理
                    }
                    catch (Exception ex)
                    {
                        exceptionQueue.Enqueue(ex);
                        cts.Cancel(); // 取消其他分片
                    }
                }, cts.Token));
            }

            // 3. 等待所有分片完成
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 内部取消（某分片失败导致），不在这里抛
            }

            // 4. 检查异常
            if (!exceptionQueue.IsEmpty)
            {
                Logger.Error($"Ghost 引擎：分片下载失败（{exceptionQueue.Count} 个分片出错） - {url}",
                    exceptionQueue.ToArray()[0]);
                CleanupPartFiles(targetPath);
                return false;
            }

            // 5. 用户取消
            if (ct.IsCancellationRequested)
            {
                CleanupPartFiles(targetPath);
                return false;
            }

            // 6. 合并分片文件
            try
            {
                MergePartFiles(targetPath, ranges.Count);
                Logger.Info($"Ghost 引擎：分片合并完成 - {targetPath}（{totalSize} 字节）");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Ghost 引擎：分片合并失败 - {targetPath}", ex);
                CleanupPartFiles(targetPath);
                return false;
            }
        }

        /// <summary>
        /// 下载单个分片（带重试）。
        /// 用 Range 请求获取 [start, end] 范围，流式写入 .part{i} 文件。
        /// </summary>
        private static async Task DownloadRangeWithRetryAsync(
            string url, string partPath, long rangeStart, long rangeEnd,
            CancellationToken ct, Action<long> onProgress)
        {
            var attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    await DownloadRangeOnceAsync(url, partPath, rangeStart, rangeEnd, ct, onProgress);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt <= RetryCount)
                {
                    var waitMs = attempt * 1000;
                    Logger.Warn($"Ghost 引擎：分片 [{rangeStart}-{rangeEnd}] 下载失败（第 {attempt} 次，{waitMs}ms 后重试） - {ex.Message}");
                    try { await Task.Delay(waitMs, ct); }
                    catch (OperationCanceledException) { throw; }
                }
            }
        }

        /// <summary>执行一次分片下载</summary>
        private static async Task DownloadRangeOnceAsync(
            string url, string partPath, long rangeStart, long rangeEnd,
            CancellationToken ct, Action<long> onProgress)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(rangeStart, rangeEnd);

            using var response = await FileDownloader.SharedClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            response.EnsureSuccessStatusCode();

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new HttpRequestException(
                    $"Ghost 引擎：服务器对 Range 请求返回了 {response.StatusCode}（期望 206），可能不支持分片");
            }

            await using var networkStream = await response.Content.ReadAsStreamAsync(ct);

            // 每个分片写入独立的 .part{i} 文件（覆盖模式，从头写）
            await using var fileStream = new FileStream(
                partPath, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, useAsync: true);

            var buffer = new byte[BufferSize];
            int read;
            while ((read = await networkStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                onProgress(read);
            }
        }

        /// <summary>
        /// 把文件按大小均分为 N 个分片。
        /// 每个分片包含 [Start, End]（含 End），Length = End - Start + 1。
        /// </summary>
        private static List<FileRange> SplitRanges(long totalSize, int count)
        {
            var ranges = new List<FileRange>(count);
            var chunkSize = totalSize / count;
            var remainder = totalSize % count;

            long current = 0;
            for (int i = 0; i < count; i++)
            {
                var length = chunkSize + (i < remainder ? 1 : 0);
                ranges.Add(new FileRange
                {
                    Start = current,
                    End = current + length - 1,
                    Length = length
                });
                current += length;
            }
            return ranges;
        }

        /// <summary>按顺序合并所有 .part{i} 文件为最终文件</summary>
        private static void MergePartFiles(string targetPath, int partCount)
        {
            using var targetStream = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, useAsync: false);

            for (int i = 0; i < partCount; i++)
            {
                var partPath = $"{targetPath}.part{i}";
                if (!File.Exists(partPath))
                {
                    throw new FileNotFoundException($"分片文件不存在：{partPath}", partPath);
                }

                using var partStream = new FileStream(
                    partPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    BufferSize, useAsync: false);

                partStream.CopyTo(targetStream);
            }

            targetStream.Flush();

            // 合并完成后删除分片文件
            for (int i = 0; i < partCount; i++)
            {
                var partPath = $"{targetPath}.part{i}";
                try { File.Delete(partPath); }
                catch (Exception ex) { Logger.Warn($"Ghost 引擎：删除分片文件失败 - {partPath} - {ex.Message}"); }
            }
        }

        /// <summary>清理所有 .part{i} 临时分片文件（取消或失败时调用）</summary>
        private static void CleanupPartFiles(string targetPath)
        {
            var dir = Path.GetDirectoryName(targetPath);
            var fileName = Path.GetFileName(targetPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fileName))
                return;

            try
            {
                var pattern = fileName + ".part*";
                foreach (var f in Directory.EnumerateFiles(dir, pattern))
                {
                    try { File.Delete(f); }
                    catch { /* 忽略 */ }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Ghost 引擎：清理分片文件失败 - {targetPath} - {ex.Message}");
            }
        }

        /// <summary>文件分片范围</summary>
        private class FileRange
        {
            /// <summary>分片起始字节（含）</summary>
            public long Start { get; set; }
            /// <summary>分片结束字节（含）</summary>
            public long End { get; set; }
            /// <summary>分片长度（字节）</summary>
            public long Length { get; set; }
        }
    }
}
