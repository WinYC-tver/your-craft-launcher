using System;

namespace YCL.Core.Saves
{
    /// <summary>
    /// 存档信息。对应 .minecraft/saves/ 下的一个子目录。
    /// 这是一个纯数据模型，供 UI 直接绑定显示。
    /// </summary>
    public class SaveInfo
    {
        /// <summary>存档名称（= 文件夹名）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>存档目录的完整路径</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>最后修改时间</summary>
        public DateTime LastModified { get; set; }

        /// <summary>存档总大小（字节）</summary>
        public long SizeBytes { get; set; }

        /// <summary>是否包含 icon.png（存档图标）</summary>
        public bool HasIcon { get; set; }

        /// <summary>icon.png 的完整路径（HasIcon 为 false 时为空字符串）</summary>
        public string IconPath { get; set; } = string.Empty;

        /// <summary>格式化后的大小字符串（如 "1.23 MB"），供 UI 直接绑定</summary>
        public string SizeDisplay => FormatSize(SizeBytes);

        /// <summary>格式化后的最后修改时间字符串，供 UI 直接绑定</summary>
        public string LastModifiedDisplay => LastModified.ToString("yyyy-MM-dd HH:mm");

        /// <summary>把字节数格式化为人类可读字符串</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GB";
        }
    }

    /// <summary>
    /// 备份文件信息。对应 saves_backup 目录下的一个 zip 文件。
    /// </summary>
    public class BackupInfo
    {
        /// <summary>备份文件名（如 "World1_20260717_153000.zip"）</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>备份文件完整路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>备份文件大小（字节）</summary>
        public long SizeBytes { get; set; }

        /// <summary>备份文件的创建时间</summary>
        public DateTime CreatedTime { get; set; }

        /// <summary>从文件名解析出的存档名（去掉时间后缀部分）</summary>
        public string SaveName { get; set; } = string.Empty;

        /// <summary>格式化后的大小字符串，供 UI 直接绑定</summary>
        public string SizeDisplay => SaveInfo.FormatSize(SizeBytes);

        /// <summary>格式化后的创建时间字符串，供 UI 直接绑定</summary>
        public string CreatedTimeDisplay => CreatedTime.ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>
    /// 备份/恢复进度信息。通过 IProgress 回调传给 UI。
    /// </summary>
    public struct BackupProgress
    {
        /// <summary>进度百分比（0~100，-1 表示不确定）</summary>
        public int Percent { get; set; }

        /// <summary>当前状态描述</summary>
        public string Message { get; set; }

        public BackupProgress(int percent, string message)
        {
            Percent = percent;
            Message = message;
        }
    }
}
