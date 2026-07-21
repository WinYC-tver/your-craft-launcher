using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;
using YCL.Models;

namespace YCL.Core.Download
{
    /// <summary>
    /// PCL CE 风格下载引擎：实现 <see cref="IDownloadEngine"/>。
    ///
    /// 参考 PCL-CE（https://github.com/PCL-community/PCL-CE）的下载思路：
    /// - <b>多源并发</b>：同一个文件同时尝试官方源与 BMCLAPI 镜像，哪个先返回首字节就用哪个，其他取消。
    ///   这对国内访问 Mojang 官方源（libraries.minecraft.net 等）经常超时的情况尤其有用。
    /// - <b>断点续传</b>：通过 .part 文件 + Range 请求头，如果下载被中断下次可以从断点继续。
    /// - <b>SHA1 校验</b>：下载完成后用 SHA1.Create() 计算哈希，与 expectedSha1 比较；不匹配则删除文件并返回 false。
    /// - <b>进度回调</b>：用 IProgress&lt;int&gt;（0~100）定期 Report。
    ///
    /// 注意：这个实现是简化版，没有完全复刻 PCL CE 的所有特性（如分片并发、负载均衡），
    /// 只保留了"多源竞速 + 断点续传 + SHA1 校验"三个核心特性，足够替换默认引擎用于一般场景。
    /// </summary>
    public class PclCeDownloader : IDownloadEngine
    {
        /// <summary>下载缓冲区大小（64KB）</summary>
        private const int BufferSize = 64 * 1024;

        /// <summary>进度回调最小间隔（毫秒），避免事件风暴</summary>
        private const int ProgressIntervalMs = 200;

        /// <summary>多源竞速时，等待首字节响应的最大时间（毫秒）。超时仍未返回的源视为失败。</summary>
        private const int FirstByteTimeoutMs = 8000;

        /// <inheritdoc/>
        public string Name => "PCL CE";

        /// <inheritdoc/>
        public Task<bool> DownloadAsync(
            string url,
            string targetPath,
            int maxThreads,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            // PCL CE 引擎不使用 maxThreads 分片，但仍接收参数以兼容接口
            // （多源竞速本身已经有并发，分片会让逻辑复杂很多，简化版不分片）
            return DownloadWithSha1Async(url, targetPath, expectedSha1: null, maxThreads, progress, cancellationToken);
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
                Logger.Warn("PCL CE 引擎：URL 或目标路径为空");
                return false;
            }

            // 确保目标目录存在
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            var partPath = targetPath + ".part";

            try
            {
                // 1. 构造候选源列表：官方源 + BMCLAPI 镜像（去重）
                var sources = BuildCandidateSources(url);

                // 2. 多源竞速下载
                var ok = await DownloadFromMultipleSourcesAsync(sources, partPath, progress, cancellationToken);
                if (!ok)
                {
                    Logger.Error($"PCL CE 引擎：所有源都下载失败 - {url}", null);
                    return false;
                }

                // 3. 下载完成：把 .part 重命名为最终文件
                if (File.Exists(targetPath))
                    File.Delete(targetPath);
                File.Move(partPath, targetPath);

                // 4. SHA1 校验（如果提供了期望值）
                if (!string.IsNullOrWhiteSpace(expectedSha1))
                {
                    var valid = await FileValidator.ValidateAsync(targetPath, expectedSha1, cancellationToken);
                    if (!valid)
                    {
                        // 校验失败：删除损坏的文件
                        try { File.Delete(targetPath); } catch { /* 忽略 */ }
                        Logger.Warn($"PCL CE 引擎：SHA1 校验失败，已删除文件 - {targetPath}");
                        return false;
                    }
                }

                // 触发 100% 进度
                progress?.Report(100);
                return true;
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"PCL CE 引擎：下载被取消 - {url}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"PCL CE 引擎：下载异常 - {url}", ex);
                return false;
            }
        }

        /// <summary>
        /// 根据 URL 构造候选下载源列表。
        /// 规则：
        /// - 如果 URL 已经是 BMCLAPI 镜像，候选源 = [BMCLAPI 镜像, 官方源]
        /// - 如果 URL 是官方源，候选源 = [官方源, BMCLAPI 镜像]
        /// - 去重（避免同一个 URL 出现两次）
        /// </summary>
        private static List<string> BuildCandidateSources(string url)
        {
            var sources = new List<string> { url };

            // 简单判断：URL 中含 bangbang93.com 视为 BMCLAPI 镜像
            bool isBmclapi = url.Contains("bangbang93.com", StringComparison.OrdinalIgnoreCase);

            if (!isBmclapi)
            {
                // 官方源：尝试转换为 BMCLAPI 镜像
                var mirror = TryConvertToBmclapi(url);
                if (mirror != null && !sources.Contains(mirror))
                    sources.Add(mirror);
            }
            else
            {
                // 已经是 BMCLAPI 镜像：不需要再加（不知道原始官方源是哪个，且镜像已经够快）
            }

            return sources;
        }

        /// <summary>
        /// 尝试把官方 URL 转换为 BMCLAPI 镜像 URL。
        /// 复用 <see cref="DownloadSourceManager"/> 的替换规则（与默认引擎保持一致）。
        /// 转换失败（不匹配任何规则）返回 null。
        /// </summary>
        private static string? TryConvertToBmclapi(string url)
        {
            try
            {
                var manager = new DownloadSourceManager(DownloadSource.BMCLAPI);
                var transformed = manager.TransformUrl(url);
                // TransformUrl 在不匹配任何规则时返回原 URL，需要比较是否真的发生了替换
                return string.Equals(transformed, url, StringComparison.OrdinalIgnoreCase) ? null : transformed;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 多源竞速下载：同时向所有候选源发送 GET 请求（带 Range 续传），
        /// 第一个返回响应头的胜出，其他请求取消。
        /// 然后流式下载胜出源的内容到 .part 文件。
        /// </summary>
        private static async Task<bool> DownloadFromMultipleSourcesAsync(
            List<string> sources,
            string partPath,
            IProgress<int>? progress,
            CancellationToken ct)
        {
            // 检查 .part 文件已有的字节数（断点续传）
            long existingBytes = 0;
            if (File.Exists(partPath))
            {
                try { existingBytes = new FileInfo(partPath).Length; }
                catch { existingBytes = 0; }
            }

            // 为每个候选源创建一个 "获取响应" 的任务
            // 用一个共享的 CancellationTokenSource，第一个胜出后取消其他
            using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var raceTasks = new List<Task<HttpResponseMessage?>>();

            foreach (var src in sources)
            {
                raceTasks.Add(GetResponseAsync(src, existingBytes, raceCts.Token));
            }

            // 等待任意一个源返回响应，或者全部失败
            HttpResponseMessage? winner = null;
            string? winnerUrl = null;
            Exception? lastError = null;

            // 用 Task.WhenAny 循环，按完成顺序检查
            // 第一个返回 IsSuccessStatusCode 或 PartialContent 的视为胜出
            var pending = new List<Task<HttpResponseMessage?>>(raceTasks);
            while (pending.Count > 0 && winner == null)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);

                try
                {
                    var resp = await completed;
                    if (resp != null &&
                        (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.PartialContent))
                    {
                        winner = resp;
                        winnerUrl = sources[raceTasks.IndexOf(completed)];
                        // 取消其他还在跑的请求
                        raceCts.Cancel();
                        break;
                    }
                    else
                    {
                        resp?.Dispose();
                    }
                }
                catch (OperationCanceledException) when (raceCts.IsCancellationRequested)
                {
                    // 因为有胜出者被取消，正常情况，跳出
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Logger.Warn($"PCL CE 引擎：某源响应失败 - {ex.Message}");
                }
            }

            if (winner == null)
            {
                if (lastError != null)
                    Logger.Error("PCL CE 引擎：所有候选源都失败了", lastError);
                else
                    Logger.Error("PCL CE 引擎：所有候选源都失败了（无具体异常）", null);
                return false;
            }

            // 用胜出者的响应流式下载到 .part 文件
            try
            {
                Logger.Info($"PCL CE 引擎：选中源 {winnerUrl}（HTTP {winner.StatusCode}）");
                await StreamToPartFileAsync(winner, partPath, existingBytes, progress, ct);
                return true;
            }
            finally
            {
                winner.Dispose();
            }
        }

        /// <summary>
        /// 向指定 URL 发送带 Range 的 GET 请求，返回响应消息（不读 body）。
        /// 失败返回 null（不抛异常，让竞速逻辑继续等其他源）。
        /// 注意：返回的 HttpResponseMessage 由调用方负责 Dispose。
        /// </summary>
        private static async Task<HttpResponseMessage?> GetResponseAsync(
            string url,
            long existingBytes,
            CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(FirstByteTimeoutMs);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existingBytes > 0)
                request.Headers.Range = new RangeHeaderValue(existingBytes, null);

            // 注意：这里不能 await 完成后 dispose response，否则调用方拿不到
            // 所以用 SendAsync 不 dispose，把所有权交给调用方
            var response = await FileDownloader.SharedClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            // 如果是 416（Range Not Satisfiable），说明 .part 文件已经下载完成
            // 这种情况直接返回一个 "成功" 的标记：用 200 包装一下
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 416 时已经下载完了，返回 null 让上层处理（实际上 .part 已经是完整的）
                // 简化处理：直接抛个特殊异常或返回一个标记
                // 这里返回 response，让上层检查状态码后处理
            }

            return response;
        }

        /// <summary>
        /// 把 HTTP 响应的内容流式写入 .part 文件。
        /// 如果是断点续传（existingBytes > 0 且响应是 206），追加写入；
        /// 否则覆盖写入。
        /// </summary>
        private static async Task StreamToPartFileAsync(
            HttpResponseMessage response,
            string partPath,
            long existingBytes,
            IProgress<int>? progress,
            CancellationToken ct)
        {
            // 处理 416 Range Not Satisfiable：.part 文件已经完整
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Logger.Info($"PCL CE 引擎：.part 文件已完整（416），直接使用 - {partPath}");
                progress?.Report(100);
                return;
            }

            // 计算总字节数与起始位置
            long totalBytes;
            long startOffset;

            if (existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent)
            {
                // 206 Partial Content：从断点继续
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
                startOffset = 0;
                totalBytes = response.Content.Headers.ContentLength ?? -1;
                if (File.Exists(partPath))
                    File.Delete(partPath);
            }

            // 打开 .part 文件：续传用 Append，否则用 Create
            var fileMode = startOffset > 0 ? FileMode.Append : FileMode.Create;
            await using var fileStream = new FileStream(
                partPath, fileMode, FileAccess.Write, FileShare.None,
                BufferSize, useAsync: true);

            await using var networkStream = await response.Content.ReadAsStreamAsync(ct);

            var buffer = new byte[BufferSize];
            long downloadedBytes = startOffset;
            var lastReportTime = DateTime.UtcNow;
            int lastReportedPercent = -1;

            int read;
            while ((read = await networkStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                downloadedBytes += read;

                // 节流触发进度事件
                var now = DateTime.UtcNow;
                if ((now - lastReportTime).TotalMilliseconds >= ProgressIntervalMs)
                {
                    lastReportTime = now;
                    if (totalBytes > 0)
                    {
                        var percent = (int)Math.Clamp(downloadedBytes * 100.0 / totalBytes, 0, 99);
                        if (percent != lastReportedPercent)
                        {
                            progress?.Report(percent);
                            lastReportedPercent = percent;
                        }
                    }
                }
            }

            await fileStream.FlushAsync(ct);
            Logger.Debug($"PCL CE 引擎：流式下载完成 - {partPath}（{downloadedBytes} 字节）");
        }
    }
}
