using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YCL.Core.Java
{
    /// <summary>
    /// Java 运行时信息。
    /// 表示一个被检测到的 Java 安装（JDK 或 JRE）。
    /// 实现 INotifyPropertyChanged，使 IsCurrent 属性变化能通知 UI 高亮。
    /// </summary>
    public class JavaInfo : INotifyPropertyChanged
    {
        private bool _isCurrent;

        /// <summary>javaw.exe 的完整路径</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>主版本号（如 8 / 17 / 21）</summary>
        public int Version { get; set; }

        /// <summary>完整版本字符串（如 "17.0.1"）</summary>
        public string VersionString { get; set; } = string.Empty;

        /// <summary>是否为 JDK（false 表示 JRE）</summary>
        public bool IsJdk { get; set; }

        /// <summary>
        /// 是否为当前选中的默认 Java（仅用于 UI 高亮显示，由 ViewModel 在刷新/设为默认时更新）。
        /// 此属性变化会触发 PropertyChanged 通知，让 UI 自动更新高亮状态。
        /// </summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent != value)
                {
                    _isCurrent = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>架构（"x64" / "x86" / "arm64" 等，未知时为空字符串）</summary>
        public string Architecture { get; set; } = string.Empty;

        /// <summary>
        /// 显示名（如 "Java 17 (17.0.1)"），方便在 UI 列表中展示。
        /// </summary>
        public string DisplayName
        {
            get
            {
                var jdk = IsJdk ? "JDK" : "JRE";
                var arch = string.IsNullOrEmpty(Architecture) ? "" : $" {Architecture}";
                return $"Java {Version} ({VersionString}, {jdk}{arch})";
            }
        }

        /// <summary>Java 安装目录（javaw.exe 所在目录的父目录）</summary>
        public string? HomeDirectory
        {
            get
            {
                if (string.IsNullOrEmpty(Path)) return null;
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (string.IsNullOrEmpty(dir)) return null;
                // bin 的上一层就是 JAVA_HOME
                return System.IO.Path.GetDirectoryName(dir);
            }
        }

        public override string ToString() => DisplayName + " @ " + Path;

        /// <summary>按路径去重时使用的键</summary>
        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Path ?? string.Empty);

        public override bool Equals(object? obj)
        {
            return obj is JavaInfo other &&
                   string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>触发属性变化通知（CallerMemberName 自动填充调用方属性名）</summary>
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
