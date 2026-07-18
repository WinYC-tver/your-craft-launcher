using System;

namespace YCL.Core.Download
{
    /// <summary>
    /// 批量下载的整体进度事件参数。
    /// 由 <see cref="DownloadTaskScheduler"/> 在批量下载过程中定期触发，
    /// 包含"已完成文件数 / 总文件数"、"已下载字节 / 总字节"、当前文件名、速度等信息。
    /// </summary>
    public class BatchDownloadProgressEventArgs : EventArgs
    {
        /// <summary>总任务数</summary>
        public int TotalFiles { get; set; }

        /// <summary>已完成的任务数（含失败）</summary>
        public int CompletedFiles { get; set; }

        /// <summary>失败的任务数</summary>
        public int FailedFiles { get; set; }

        /// <summary>总字节数（所有任务 Size 之和，&lt;0 表示未知）</summary>
        public long TotalBytes { get; set; } = -1;

        /// <summary>已下载字节数（所有任务已下载字节之和）</summary>
        public long DownloadedBytes { get; set; }

        /// <summary>当前正在下载的文件名（可能为空，如并发多个文件时只显示一个）</summary>
        public string CurrentFileName { get; set; } = string.Empty;

        /// <summary>整体下载速度（字节/秒）</summary>
        public double BytesPerSecond { get; set; }

        /// <summary>整体进度百分比（0~100，-1 表示不确定）</summary>
        public double Percent
        {
            get
            {
                if (TotalFiles > 0)
                    return CompletedFiles * 100.0 / TotalFiles;
                if (TotalBytes > 0)
                    return DownloadedBytes * 100.0 / TotalBytes;
                return -1;
            }
        }
    }
}
