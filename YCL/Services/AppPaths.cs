using System;
using System.IO;
using YCL.Core.Utils;

namespace YCL.Services
{
    /// <summary>
    /// 应用程序路径集中管理（HMCL 风格）。
    ///
    /// 所有路径都从这里取，方便：
    /// - 统一调整路径策略（如改用 %AppData% 或便携模式）
    /// - 测试时 mock
    /// - 避免 <see cref="AppDomain.CurrentDomain.BaseDirectory"/> 散落在各处
    ///
    /// 便携模式：
    /// 当 exe 所在目录存在一个名为 <c>YCL.portable</c> 的空标记文件时，
    /// 视为便携模式——所有可写数据都放在启动器文件夹下，
    /// 不写入 %AppData%。便携模式对崩溃报告目录没有影响，
    /// 因为 clog 始终位于启动器文件夹下（与便携/非便携无关）。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>便携模式标记文件名（放在 exe 旁边即启用便携模式）</summary>
        private const string PortableMarkerFileName = "YCL.portable";

        /// <summary>
        /// 启动器文件夹路径（exe 所在目录，末尾带分隔符）。
        /// 取自 <see cref="AppDomain.CurrentDomain.BaseDirectory"/>，
        /// 在打包/便携/安装模式下都指向实际 exe 所在目录。
        /// </summary>
        public static string LauncherDirectory =>
            AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>
        /// 是否处于便携模式。
        /// 检测方式：exe 旁边是否存在 <c>YCL.portable</c> 标记文件。
        /// 失败时（如目录读取权限不足）返回 false，按非便携模式处理。
        /// </summary>
        public static bool IsPortableMode
        {
            get
            {
                try
                {
                    return File.Exists(Path.Combine(LauncherDirectory, PortableMarkerFileName));
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// 崩溃报告目录：<c>启动器文件夹/clog/</c>。
        /// 访问时自动创建目录（已存在则无害），调用方无需再创建。
        /// </summary>
        public static string CrashLogsDirectory
        {
            get
            {
                var dir = Path.Combine(LauncherDirectory, "clog");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
    }
}
