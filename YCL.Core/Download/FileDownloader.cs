using System;
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
    /// 单文件下载器：用 <see cref="HttpClient"/> 流式下载单个文件到磁盘。
    ///
    /// 特性：
    /// - 使用共享静态 HttpClient，避免 socket 耗尽
    /// - 流式写入文件（边下边写，不全部读到内存）
    /// - 支持进度回调（已下载字节 / 总字节 / 速度）
    /// - 支持 CancellationToken 取消
    /// - 失败自动重试（重试次数由构造参数传入，通常来自 AppConfig.RetryCount）
    /// - 断点续传：使用 .part 临时文件，下次启动可从已下载部分继续
    /// </summary>
    public class FileDownloader
    {
        /// <summary>
        /// 全局共享的 HttpClient 实例。
        /// HttpClient 设计为可重用对象（每次 new 会导致 socket 耗尽），
        /// 整个应用共用一个即可。设置 100 秒超时（大文件下载需要更长时间）。
        /// </summary>
        public static readonly HttpClient SharedClient = new()
        {
            Timeout = TimeSpan.FromSeconds(100)
        };

        /// <summary>默认缓冲区大小（80KB，读写都用这个）</summary>
        private const int BufferSize = 80 * 1024;

        /// <summary>进度事件触发的最小间隔（毫秒），避免事件风暴</summary>
        private const int ProgressIntervalMs = 200;

        /// <summary>失败重试次数（从 AppConfig.RetryCount 传入）</summary>
        private readonly int _retryCount;

        /// <summary>下载进度变化事件</summary>
        public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;

        /// <summary>
        /// 构造单文件下载器。
        /// </summary>
        /// <param name="retryCount">失败重试次数（一般来自 AppConfig.RetryCount，默认 3）</param>
        public FileDownloader(int retryCount = 3)
        {
            // 重试次数至少 0 次（即只下载一次不重试）
            _retryCount = Math.Max(0, retryCount);
        }

        /// <summary>
        /// 异步下载文件到指定路径。
        /// 流程：
        /// 1. 检查目标 .part 文件是否已有部分内容（断点续传）
        /// 2. 用 Range 请求从断点继续下载
        /// 3. 流式写入 .part 文件
        /// 4. 下载完成后把 .part 重命名为最终文件名
        /// 5. 失败重试（最多 _retryCount 次）
        /// </summary>
        /// <param name="url">下载 URL（已经过下载源转换）</param>
        /// <param name="targetPath">目标文件完整路径（下载完成后存在于此）</param>
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

            // 临时文件路径：在最终文件名后加 .part
            var partPath = targetPath + ".part";

            // 重试循环
            var attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    await DownloadOnceAsync(url, partPath, targetPath, cancellationToken);
                    return; // 下载成功，返回
                }
                catch (OperationCanceledException)
                {
                    // 用户取消：保留 .part 文件以便下次续传，直接抛出
                    Logger.Info($"下载被取消（保留断点文件）：{targetPath}");
                    throw;
                }
                catch (Exception ex) when (attempt <= _retryCount)
                {
                    // 可重试的异常：等待一段时间后重试
                    var waitMs = attempt * 1000; // 1s, 2s, 3s...
                    Logger.Warn($"下载失败（第 {attempt} 次，{waitMs}ms 后重试）：{url} - {ex.Message}");
                    try
                    {
                        await Task.Delay(waitMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// 执行一次完整的下载尝试（含断点续传）。
        /// 成功时把 .part 重命名为最终文件；失败时抛异常（由调用方决定是否重试）。
        /// </summary>
        private async Task DownloadOnceAsync(string url, string partPath, string targetPath, CancellationToken ct)
        {
            // 1. 检查 .part 文件已有的字节数（断点续传）
            long existingBytes = 0;
            if (File.Exists(partPath))
            {
                existingBytes = new FileInfo(partPath).Length;
                Logger.Debug($"发现断点文件 {partPath}，已有 {existingBytes} 字节，尝试续传");
            }

            // 2. 构造请求
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // 如果有已下载部分，用 Range 请求从断点继续
            if (existingBytes > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingBytes, null);
            }

            // 3. 发送请求，获取响应（HttpCompletionOption.ResponseHeadersRead 表示
            //    收到响应头就返回，不等待整个内容下载完，方便流式处理大文件）
            using var response = await SharedClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            // 4. 处理响应状态码
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 416 Range Not Satisfiable：说明 .part 文件已经下载完成
                // 直接把 .part 重命名为最终文件
                Logger.Info($"断点文件已完成（416），直接重命名：{partPath}");
                File.Move(partPath, targetPath, overwrite: true);
                return;
            }

            response.EnsureSuccessStatusCode();

            // 5. 计算总字节数与起始写入位置
            long totalBytes;
            long startOffset;

            if (existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent)
            {
                // 206 Partial Content：服务器支持断点续传，从断点继续
                // Content-Range: bytes 1000-1999/2000 → total = 2000
                startOffset = existingBytes;
                totalBytes = existingBytes + (response.Content.Headers.ContentLength ?? 0);
                if (response.Content.Headers.ContentRange != null &&
                    response.Content.Headers.ContentRange.HasLength)
                {
                    totalBytes = response.Content.Headers.ContentRange.Length ?? totalBytes;
                }
            }
            else
            {
                // 200 OK：服务器不支持 Range，或没有断点 → 从头下载
                // 此时需要清空 .part 文件
                startOffset = 0;
                totalBytes = response.Content.Headers.ContentLength ?? -1;
                if (File.Exists(partPath))
                {
                    File.Delete(partPath);
                    Logger.Debug($"服务器不支持断点续传（返回 200），删除旧 .part 文件重新下载：{partPath}");
                }
            }

            // 6. 流式写入文件
            // 用 FileStream 以 Append 或 Create 模式打开
            var fileMode = startOffset > 0 ? FileMode.Append : FileMode.Create;
            await using var fileStream = new FileStream(
                partPath, fileMode, FileAccess.Write, FileShare.None,
                BufferSize, useAsync: true);

            await using var networkStream = await response.Content.ReadAsStreamAsync(ct);

            var buffer = new byte[BufferSize];
            long downloadedBytes = startOffset;
            var lastReportTime = DateTime.UtcNow;
            long lastReportBytes = startOffset;

            int read;
            while ((read = await networkStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                downloadedBytes += read;

                // 节流触发进度事件
                var now = DateTime.UtcNow;
                if ((now - lastReportTime).TotalMilliseconds >= ProgressIntervalMs)
                {
                    var elapsed = (now - lastReportTime).TotalSeconds;
                    var bytesPerSecond = elapsed > 0
                        ? (downloadedBytes - lastReportBytes) / elapsed
                        : 0;

                    RaiseProgress(downloadedBytes, totalBytes, bytesPerSecond);

                    lastReportTime = now;
                    lastReportBytes = downloadedBytes;
                }
            }

            // 触发最终的 100% 进度
            RaiseProgress(downloadedBytes, totalBytes, 0);

            // 7. 下载完成，把 .part 重命名为最终文件
            // 如果目标文件已存在则覆盖（一般是损坏的旧文件）
            await fileStream.FlushAsync(ct);
            fileStream.Close();

            File.Move(partPath, targetPath, overwrite: true);
            Logger.Debug($"文件下载完成：{targetPath}（{downloadedBytes} 字节）");
        }

        /// <summary>触发进度事件</summary>
        private void RaiseProgress(long downloaded, long total, double bytesPerSecond)
        {
            ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
            {
                DownloadedBytes = downloaded,
                TotalBytes = total,
                BytesPerSecond = bytesPerSecond
            });
        }
    }
}
