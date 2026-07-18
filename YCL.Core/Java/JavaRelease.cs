namespace YCL.Core.Java
{
    /// <summary>
    /// 可下载的 Java 发行版信息（来自 Adoptium API）。
    /// 表示一个可安装的 Java 主版本（如 8 / 11 / 17 / 21）。
    /// </summary>
    public class JavaRelease
    {
        /// <summary>主版本号（如 8、17、21）</summary>
        public int MajorVersion { get; set; }

        /// <summary>是否为 LTS（长期支持版本）</summary>
        public bool IsLts { get; set; }

        /// <summary>显示名（如 "Java 17 (LTS)"）</summary>
        public string DisplayName => IsLts ? $"Java {MajorVersion} (LTS)" : $"Java {MajorVersion}";

        public override string ToString() => DisplayName;
    }
}
