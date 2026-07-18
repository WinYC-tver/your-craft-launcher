namespace YCL.Core.Download
{
    /// <summary>
    /// 单个下载任务的数据描述。
    /// 由 <see cref="MinecraftDownloader"/> 构造好（含原始 URL、目标路径、SHA1 等），
    /// 交给 <see cref="DownloadTaskScheduler"/> 排队执行。
    /// </summary>
    public class DownloadTask
    {
        /// <summary>下载 URL（原始官方 URL，调度器会调用 IDownloadSource 转换）</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>文件保存的完整路径</summary>
        public string TargetPath { get; set; } = string.Empty;

        /// <summary>期望的 SHA1（可空，空则不校验）</summary>
        public string? Sha1 { get; set; }

        /// <summary>期望的文件大小（字节，&lt;=0 表示未知）</summary>
        public long Size { get; set; }

        /// <summary>用于 UI 显示的文件名（一般是 Path.GetFileName(TargetPath)）</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>任务分类（libraries / assets / client / version_json 等，用于 UI 分组显示）</summary>
        public string Category { get; set; } = string.Empty;
    }
}
