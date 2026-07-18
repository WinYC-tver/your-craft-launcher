using System;
using System.Collections.Generic;

namespace YCL.Core.Update
{
    /// <summary>
    /// 发布资产信息。对应 GitHub Release 中的一个可下载文件（如 zip 安装包）。
    /// </summary>
    public class ReleaseAsset
    {
        /// <summary>资产名称（如 "YCL-1.2.3.zip"）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>下载链接（browser_download_url）</summary>
        public string DownloadUrl { get; set; } = string.Empty;

        /// <summary>文件大小（字节）</summary>
        public long Size { get; set; }

        /// <summary>格式化后的大小字符串，供 UI 直接绑定</summary>
        public string SizeDisplay
        {
            get
            {
                if (Size < 1024) return Size + " B";
                if (Size < 1024 * 1024) return (Size / 1024.0).ToString("F1") + " KB";
                if (Size < 1024L * 1024 * 1024) return (Size / (1024.0 * 1024)).ToString("F1") + " MB";
                return (Size / (1024.0 * 1024 * 1024)).ToString("F2") + " GB";
            }
        }
    }

    /// <summary>
    /// 版本发布信息。对应 GitHub 上一个 Release。
    /// 当检查到新版本时由 <see cref="IUpdateChecker"/> 返回。
    /// </summary>
    public class ReleaseInfo
    {
        /// <summary>版本号（从 tag_name 解析，去掉可能的 v 前缀，如 "1.2.3"）</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>Release 页面链接（html_url，用户可在浏览器打开查看详情）</summary>
        public string ReleaseUrl { get; set; } = string.Empty;

        /// <summary>发布说明（body，Markdown 格式，展示更新内容）</summary>
        public string ReleaseNotes { get; set; } = string.Empty;

        /// <summary>发布时间（published_at）</summary>
        public DateTime PublishedAt { get; set; }

        /// <summary>该 Release 下的可下载资产列表</summary>
        public List<ReleaseAsset> Assets { get; set; } = new();

        /// <summary>格式化后的发布时间字符串，供 UI 直接绑定</summary>
        public string PublishedAtDisplay => PublishedAt.ToString("yyyy-MM-dd HH:mm");
    }
}
