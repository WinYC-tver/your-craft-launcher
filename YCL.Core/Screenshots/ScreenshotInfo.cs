using System;

namespace YCL.Core.Screenshots
{
    /// <summary>
    /// 截图信息。对应 gameDir/screenshots/ 下的一个图片文件。
    /// 这是一个纯数据模型，供 UI 直接绑定显示。
    /// </summary>
    public class ScreenshotInfo
    {
        /// <summary>文件名（如 "2024-01-15_14.30.00.png"）</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>文件完整路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>截图时间（取文件最后修改时间，Minecraft 截图名通常含时间）</summary>
        public DateTime CaptureTime { get; set; }

        /// <summary>文件大小（字节）</summary>
        public long SizeBytes { get; set; }

        /// <summary>格式化后的大小字符串（如 "1.23 MB"），供 UI 直接绑定</summary>
        public string SizeDisplay => FormatSize(SizeBytes);

        /// <summary>格式化后的截图时间字符串，供 UI 直接绑定</summary>
        public string CaptureTimeDisplay => CaptureTime.ToString("yyyy-MM-dd HH:mm:ss");

        /// <summary>把字节数格式化为人类可读字符串</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GB";
        }
    }
}
