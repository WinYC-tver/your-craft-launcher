using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;

namespace YCL.Core.Download
{
    /// <summary>
    /// 多线程分片下载器：对大文件用 HTTP Range 请求分成 N 个分片并行下载，
    /// 显著提升大文件下载速度（如 client.jar 几十 MB）。
    ///
    /// 工作流程：
    /// 1. 先用 HEAD 请求获取文件总大小与是否支持 Range（Accept-Ranges）
    /// 2. 如果不支持 Range 或文件 &lt; 1MB，回退到 <see cref="FileDownloader"/> 单线程下载
    /// 3. 否则把文件分成 N 个分片（N 来自 AppConfig.DownloadThreads）
    /// 4. 每个分片独立下载，用 FileStream.Seek 写入 .part 文件的对应位置
    /// 5. 用 .meta 文件记录每个分片已下载字节数，支持断点续传
    /// 6. 所有分片完成后删除 .meta，把 .part 重命名为最终文件
    /// </summary>
    public class MultiThreadDownloader
    {
        /// <summary>触发多线程下载的最小文件大小（1MB）</summary>
        private const long MultiThreadThreshold = 1 * 1024 * 1024;

        /// <summary>分片下载的缓冲区大小（64KB）</summary>
        private const int BufferSize = 64 * 1024;

        /// <summary>进度事件最小间隔（毫秒）</summary>
        private const int ProgressIntervalMs = 200;

        /// <summary>分片数量（来自 AppConfig.DownloadThreads）</summary>
        private readonly int _threadCount;

        /// <summary>失败重试次数</summary>
        private readonly int _retryCount;

        /// <summary>单线程下载器（回退用）</summary>
        private readonly FileDownloader _fallbackDownloader;

        /// <summary>整体下载进度事件（所有分片汇总）</summary>
        public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;

        /// <summary>引擎名称（与 <see cref="IDownloadEngine.Name"/> 保持一致，便于显示）</summary>
        public string Name => "Default";

        /// <summary>
        /// 构造多线程下载器。
        /// </summary>
        /// <param name="threadCount">分片数（来自 AppConfig.DownloadThreads，默认 8）</param>
        /// <param name="retryCount">每个分片失败重试次数</param>
        public MultiThreadDownloader(int threadCount = 8, int retryCount = 3)
        {
            _threadCount = Math.Max(1, threadCount);
            _retryCount = Math.Max(0, retryCount);
            _fallbackDownloader = new FileDownloader(retryCount);
        }

        /// <summary>
        /// 异步下载文件。大文件用多线程分片，小文件或不支持 Range 时回退到单线程。
        /// </summary>
        /// <param name="url">下载 URL（已经过下载源转换）</param>
        /// <param name="targetPath">目标文件完整路径</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task DownloadAsync(string url, string targetPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(url))
                throw new ArgumentException("下载 URL 不能为空", nameof(url));
            if (string.IsNullOrEmpty(targetPath))
                throw new ArgumentException("目标路径不能为空", nameof(targetPath));

            // 确保目标目录存在
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            // 先探测服务器：获取文件大小与是否支持 Range
            var (totalSize, supportsRange) = await ProbeAsync(url, cancellationToken);

            // 不支持 Range 或文件过小：回退到单线程
            if (!supportsRange || totalSize < MultiThreadThreshold)
            {
                Logger.Info($"回退到单线程下载（supportsRange={supportsRange}, size={totalSize}）：{url}");
                // 转发单线程下载器的进度事件
                _fallbackDownloader.ProgressChanged += OnFallbackProgress;
                try
                {
                    await _fallbackDownloader.DownloadAsync(url, targetPath, cancellationToken);
                }
                finally
                {
                    _fallbackDownloader.ProgressChanged -= OnFallbackProgress;
                }
                return;
            }

            // 多线程分片下载
            await DownloadMultiThreadAsync(url, targetPath, totalSize, cancellationToken);
        }

        /// <summary>
        /// 探测服务器：发送 HEAD 请求获取文件大小与是否支持 Range。
        /// 如果 HEAD 请求失败（某些服务器不支持 HEAD），用一个 Range 请求试探。
        /// </summary>
        private async Task<(long totalSize, bool supportsRange)> ProbeAsync(string url, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await FileDownloader.SharedClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    // HEAD 失败，假定不支持分片，回退单线程
                    Logger.Warn($"HEAD 请求失败（{response.StatusCode}），将回退单线程：{url}");
                    return (-1, false);
                }

                var totalSize = response.Content.Headers.ContentLength ?? -1;
                var supportsRange = response.Headers.AcceptRanges?.Contains("bytes") == true;

                // 某些服务器不返回 Accept-Ranges 头但实际支持，用 totalSize > 0 作为兜底判断
                if (!supportsRange && totalSize > 0)
                {
                    Logger.Debug($"服务器未声明 Accept-Ranges，将通过 Range 请求试探：{url}");
                }

                return (totalSize, supportsRange);
            }
            catch (Exception ex)
            {
                Logger.Warn($"HEAD 请求异常，将回退单线程：{url} - {ex.Message}");
                return (-1, false);
            }
        }

        /// <summary>
        /// 执行多线程分片下载。
        /// </summary>
        private async Task DownloadMultiThreadAsync(string url, string targetPath, long totalSize, CancellationToken ct)
        {
            var partPath = targetPath + ".part";
            var metaPath = targetPath + ".meta";

            // 划分分片范围
            var ranges = SplitRanges(totalSize, _threadCount);

            // 加载断点续传信息（如果 .meta 文件存在）
            var meta = LoadMeta(metaPath, ranges.Count);
            bool resume = meta != null;
            if (resume)
            {
                Logger.Info($"启用断点续传，从上次中断处继续：{targetPath}");
            }

            // 预创建 .part 文件（如果不是续传），并按需扩展到总大小
            EnsurePartFile(partPath, totalSize, resume);

            // 启动所有分片下载任务
            var totalDownloaded = resume ? SumDownloaded(meta!) : 0;
            var progressLock = new object();
            var lastReportTime = DateTime.UtcNow;
            long lastReportBytes = totalDownloaded;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var exceptionHolder = new ConcurrentQueue<Exception>();

            var tasks = new List<Task>();
            for (int i = 0; i < ranges.Count; i++)
            {
                var index = i;
                var range = ranges[i];
                var startOffset = resume ? meta!.Parts[index].DownloadedBytes : 0;
                var actualStart = range.Start + startOffset;

                // 已经完成的分片不再下载
                if (resume && startOffset >= range.Length)
                {
                    Logger.Debug($"分片 {index} 已完成（{startOffset}/{range.Length}），跳过");
                    continue;
                }

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await DownloadRangeAsync(
                            url, partPath, index, actualStart, range.End, range.Length, startOffset,
                            cts.Token,
                            // 进度回调（每个分片每下载一段就汇报一次）
                            delta =>
                            {
                                lock (progressLock)
                                {
                                    totalDownloaded += delta;
                                    meta!.Parts[index].DownloadedBytes += delta;

                                    var now = DateTime.UtcNow;
                                    if ((now - lastReportTime).TotalMilliseconds >= ProgressIntervalMs)
                                    {
                                        var elapsed = (now - lastReportTime).TotalSeconds;
                                        var bps = elapsed > 0
                                            ? (totalDownloaded - lastReportBytes) / elapsed
                                            : 0;
                                        RaiseProgress(totalDownloaded, totalSize, bps);
                                        lastReportTime = now;
                                        lastReportBytes = totalDownloaded;
                                    }
                                }
                            });

                        // 标记分片完成
                        meta!.Parts[index].DownloadedBytes = range.Length;
                    }
                    catch (OperationCanceledException)
                    {
                        // 取消时保存 meta 以便下次续传
                        SaveMeta(metaPath, meta!);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 收集异常，等其他分片结束后统一抛出
                        exceptionHolder.Enqueue(ex);
                        // 取消其他正在下载的分片
                        cts.Cancel();
                    }
                }, cts.Token));
            }

            // 等待所有分片完成（或失败）
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 因为某个分片失败导致的内部取消，不是用户取消
                // 继续往下走，抛出收集到的异常
            }

            // 如果有分片失败，抛出第一个异常
            if (exceptionHolder.TryDequeue(out var firstEx))
            {
                Logger.Error($"多线程下载失败（部分分片出错）：{url}", firstEx);
                throw new AggregateException("多线程下载失败", exceptionHolder);
            }

            // 用户取消
            ct.ThrowIfCancellationRequested();

            // 触发最终 100% 进度
            RaiseProgress(totalSize, totalSize, 0);

            // 下载完成：删除 .meta，把 .part 重命名为最终文件
            if (File.Exists(metaPath))
                File.Delete(metaPath);
            File.Move(partPath, targetPath, overwrite: true);

            Logger.Info($"多线程下载完成（{ranges.Count} 分片）：{targetPath}（{totalSize} 字节）");
        }

        /// <summary>
        /// 下载单个分片。用 Range 请求获取 [start, end] 范围，流式写入 .part 文件的对应位置。
        /// 失败时按 _retryCount 重试。
        /// </summary>
        private async Task DownloadRangeAsync(
            string url, string partPath, int index,
            long rangeStart, long rangeEnd, long rangeLength, long alreadyDownloaded,
            CancellationToken ct, Action<long> onProgress)
        {
            var attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    await DownloadRangeOnceAsync(
                        url, partPath, rangeStart, rangeEnd, alreadyDownloaded,
                        ct, onProgress);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt <= _retryCount)
                {
                    var waitMs = attempt * 1000;
                    Logger.Warn($"分片 {index} 下载失败（第 {attempt} 次，{waitMs}ms 后重试）：{ex.Message}");
                    try { await Task.Delay(waitMs, ct); }
                    catch (OperationCanceledException) { throw; }
                }
            }
        }

        /// <summary>执行一次分片下载</summary>
        private async Task DownloadRangeOnceAsync(
            string url, string partPath, long rangeStart, long rangeEnd,
            long alreadyDownloaded, CancellationToken ct, Action<long> onProgress)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 请求 [rangeStart + alreadyDownloaded, rangeEnd] 范围
            var actualStart = rangeStart + alreadyDownloaded;
            request.Headers.Range = new RangeHeaderValue(actualStart, rangeEnd);

            using var response = await FileDownloader.SharedClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            response.EnsureSuccessStatusCode();

            // 服务器应该返回 206 Partial Content
            // 如果返回 200，说明不支持 Range，这里直接抛异常让上层回退
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new HttpRequestException(
                    $"服务器对 Range 请求返回了 {response.StatusCode}（期望 206），可能不支持分片下载");
            }

            await using var networkStream = await response.Content.ReadAsStreamAsync(ct);

            // 打开 .part 文件，定位到写入位置
            await using var fileStream = new FileStream(
                partPath, FileMode.Open, FileAccess.Write, FileShare.None,
                BufferSize, useAsync: true);
            fileStream.Position = actualStart;

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

        /// <summary>确保 .part 文件存在且大小正确（续传时保留已下载内容）</summary>
        private static void EnsurePartFile(string partPath, long totalSize, bool resume)
        {
            if (resume && File.Exists(partPath))
            {
                // 续传模式：保持现有文件，但要确保大小至少是 totalSize
                var currentSize = new FileInfo(partPath).Length;
                if (currentSize < totalSize)
                {
                    // 用 Sparse 扩展（写入最后一个字节让文件变大）
                    using var fsResume = new FileStream(partPath, FileMode.Open, FileAccess.Write);
                    fsResume.SetLength(totalSize);
                }
                return;
            }

            // 非续传：创建新文件并预分配大小
            using var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write);
            fs.SetLength(totalSize);
        }

        /// <summary>加载断点续传元数据。文件不存在或损坏时返回 null。</summary>
        private static DownloadMeta? LoadMeta(string metaPath, int partCount)
        {
            if (!File.Exists(metaPath)) return null;
            try
            {
                var json = File.ReadAllText(metaPath);
                var meta = JsonSerializer.Deserialize<DownloadMeta>(json);
                if (meta == null || meta.Parts == null || meta.Parts.Count != partCount)
                    return null;
                return meta;
            }
            catch (Exception ex)
            {
                Logger.Warn($"加载断点元数据失败，将重新下载：{metaPath} - {ex.Message}");
                return null;
            }
        }

        /// <summary>保存断点续传元数据</summary>
        private static void SaveMeta(string metaPath, DownloadMeta meta)
        {
            try
            {
                var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(metaPath, json);
            }
            catch (Exception ex)
            {
                Logger.Warn($"保存断点元数据失败：{metaPath} - {ex.Message}");
            }
        }

        /// <summary>汇总所有分片已下载字节数</summary>
        private static long SumDownloaded(DownloadMeta meta)
        {
            long sum = 0;
            foreach (var p in meta.Parts)
                sum += p.DownloadedBytes;
            return sum;
        }

        /// <summary>触发整体进度事件</summary>
        private void RaiseProgress(long downloaded, long total, double bps)
        {
            ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
            {
                DownloadedBytes = downloaded,
                TotalBytes = total,
                BytesPerSecond = bps
            });
        }

        /// <summary>转发单线程下载器的进度事件</summary>
        private void OnFallbackProgress(object? sender, DownloadProgressEventArgs e)
        {
            ProgressChanged?.Invoke(this, e);
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

    /// <summary>
    /// 多线程下载的断点续传元数据，序列化到 .meta 文件。
    /// 记录每个分片已下载的字节数，下次启动时从断点继续。
    /// </summary>
    public class DownloadMeta
    {
        /// <summary>所有分片的进度信息</summary>
        [JsonPropertyName("parts")]
        public List<DownloadMetaPart> Parts { get; set; } = new();
    }

    /// <summary>单个分片的断点续传信息</summary>
    public class DownloadMetaPart
    {
        /// <summary>该分片已下载的字节数</summary>
        [JsonPropertyName("downloaded")]
        public long DownloadedBytes { get; set; }
    }
}
