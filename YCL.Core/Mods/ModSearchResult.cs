namespace YCL.Core.Mods
{
    /// <summary>
    /// 模组来源枚举。用于在统一搜索接口中指定从哪些平台搜索。
    /// </summary>
    public enum ModSource
    {
        /// <summary>仅 CurseForge</summary>
        CurseForge = 0,

        /// <summary>仅 Modrinth</summary>
        Modrinth = 1,

        /// <summary>所有平台（同时搜索 CurseForge 与 Modrinth，结果合并）</summary>
        All = 2
    }

    /// <summary>
    /// 统一的模组搜索结果项。
    /// 把 CurseForge 与 Modrinth 两个平台的结果归一化为同一种结构，
    /// 让 UI 不需要关心数据来自哪个平台。
    /// </summary>
    public class ModSearchResult
    {
        /// <summary>来源平台</summary>
        public ModSource Source { get; set; }

        /// <summary>项目 id（CurseForge 是数字字符串，Modrinth 是 base62 字符串）</summary>
        public string ProjectId { get; set; } = string.Empty;

        /// <summary>显示名</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>简介</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>下载次数（用于排序展示）</summary>
        public long DownloadCount { get; set; }

        /// <summary>图标 URL</summary>
        public string? LogoUrl { get; set; }

        /// <summary>项目页面 URL</summary>
        public string WebsiteUrl { get; set; } = string.Empty;

        /// <summary>作者（仅 Modrinth 提供，CurseForge 此字段为空）</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>来源显示文字（"CurseForge" 或 "Modrinth"）</summary>
        public string SourceDisplay => Source.ToString();

        /// <summary>下载次数显示文字（如 "1.2K" / "3.5M"）</summary>
        public string DownloadCountDisplay => FormatDownloadCount(DownloadCount);

        /// <summary>把下载次数格式化为人类可读文字</summary>
        private static string FormatDownloadCount(long count)
        {
            if (count < 1000) return count.ToString();
            if (count < 1_000_000) return (count / 1000.0).ToString("F1") + "K";
            return (count / 1_000_000.0).ToString("F1") + "M";
        }

        public override string ToString() => $"[{Source}] {Name}";
    }
}
