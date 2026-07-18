using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YCL.Models.Versions;

namespace YCL.Core.Download
{
    /// <summary>
    /// Minecraft 文件下载器接口。
    /// 负责根据版本 JSON 生成所有需要下载的文件清单
    /// （client.jar、libraries、assets、logging 配置），
    /// 并通过 <see cref="DownloadTaskScheduler"/> 调度下载。
    /// 支持暂停 / 继续 / 取消，并通过事件通知 UI 进度。
    /// </summary>
    public interface IMinecraftFileDownloader
    {
        /// <summary>是否正在下载中</summary>
        bool IsDownloading { get; }

        /// <summary>是否已暂停（仅在 <see cref="IsDownloading"/> 为 true 时有意义）</summary>
        bool IsPaused { get; }

        /// <summary>整体下载进度变化事件（已完成文件数 / 总数、总字节、速度）</summary>
        event EventHandler<BatchDownloadProgressEventArgs>? ProgressChanged;

        /// <summary>单个任务的实时下载进度事件（每个文件独立的进度与速度）</summary>
        event EventHandler<DownloadTaskProgressEventArgs>? TaskProgressChanged;

        /// <summary>单个任务完成事件（成功或失败都会触发）</summary>
        event EventHandler<DownloadTaskCompletedEventArgs>? TaskCompleted;

        /// <summary>
        /// 下载指定版本的所有必要文件。
        /// 包括：client.jar、所有 libraries（含 natives-windows）、
        /// assetIndex 与所有 assets objects、logging 配置文件。
        /// 所有 URL 会经过下载源重写，已存在且校验通过的文件会跳过。
        /// </summary>
        /// <param name="version">已解析合并（含 inheritsFrom）的版本信息</param>
        /// <param name="minecraftPath">.minecraft 根目录</param>
        /// <param name="cancellationToken">取消令牌（用于取消下载）</param>
        /// <returns>下载结果汇总（成功/失败计数等）</returns>
        Task<DownloadResult> DownloadVersionAsync(
            VersionInfo version,
            string minecraftPath,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 仅下载版本 JSON 本身（根据版本清单条目的 url）。
        /// 用于"先下载版本 JSON，再下载完整版本"的两步流程。
        /// 保存到 .minecraft/versions/&lt;id&gt;/&lt;id&gt;.json。
        /// </summary>
        /// <param name="entry">版本清单中的条目（含下载 URL）</param>
        /// <param name="minecraftPath">.minecraft 根目录</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task DownloadVersionJsonAsync(
            VersionManifestEntry entry,
            string minecraftPath,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 根据版本 JSON 构造所有需要下载的任务清单（不实际下载）。
        /// 可用于"检查缺失文件"或 UI 预览。
        /// 不会去重已存在的文件（调度器执行时会跳过已存在且校验通过的）。
        /// </summary>
        /// <param name="version">已解析合并的版本信息</param>
        /// <param name="minecraftPath">.minecraft 根目录</param>
        /// <returns>下载任务列表</returns>
        List<DownloadTask> BuildDownloadTasks(VersionInfo version, string minecraftPath);

        /// <summary>暂停当前下载（正在执行的任务会完成，新任务不开始）</summary>
        void Pause();

        /// <summary>继续当前下载</summary>
        void Resume();
    }

    /// <summary>下载结果汇总</summary>
    public class DownloadResult
    {
        /// <summary>总任务数</summary>
        public int TotalFiles { get; set; }

        /// <summary>成功的任务数</summary>
        public int SuccessFiles { get; set; }

        /// <summary>失败的任务数</summary>
        public int FailedFiles { get; set; }

        /// <summary>总下载字节数（已存在跳过的文件不计入）</summary>
        public long TotalBytes { get; set; }

        /// <summary>是否被用户取消</summary>
        public bool IsCanceled { get; set; }

        /// <summary>是否完全成功（无失败且未取消）</summary>
        public bool IsSuccess => FailedFiles == 0 && !IsCanceled;
    }
}
