using System;

namespace YCL.Core.Download
{
    /// <summary>
    /// 单文件下载进度事件参数。
    /// 在下载过程中定期触发，告诉调用方"已经下载了多少字节 / 文件总共多大"。
    /// </summary>
    public class DownloadProgressEventArgs : EventArgs
    {
        /// <summary>已下载的字节数</summary>
        public long DownloadedBytes { get; set; }

        /// <summary>文件总字节数（服务器不返回 Content-Length 时为 -1）</summary>
        public long TotalBytes { get; set; } = -1;

        /// <summary>
        /// 下载速度（字节/秒）。
        /// 由 <see cref="FileDownloader"/> 在触发事件时计算并填充。
        /// </summary>
        public double BytesPerSecond { get; set; }

        /// <summary>进度百分比（0~100）。TotalBytes 为 -1 时为 -1（不确定）</summary>
        public double Percent => TotalBytes > 0 ? DownloadedBytes * 100.0 / TotalBytes : -1;
    }
}
