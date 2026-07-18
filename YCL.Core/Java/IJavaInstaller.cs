using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Versions;

namespace YCL.Core.Java
{
    /// <summary>
    /// Java 安装器接口：从 Adoptium（Eclipse Temurin）下载并安装 JDK。
    /// </summary>
    public interface IJavaInstaller
    {
        /// <summary>
        /// 获取可下载的 Java 版本列表（从 Adoptium API）。
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>可下载的 Java 主版本列表</returns>
        Task<List<JavaRelease>> ListAvailableAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 下载并安装指定主版本的 JDK。
        /// 流程：
        /// 1. 从 Adoptium API 获取最新 GA 版本的 zip 下载链接
        /// 2. 下载 zip 到临时文件
        /// 3. 解压到 %AppData%\YCL\java\jdk-&lt;majorVersion&gt;\
        /// 4. 返回安装后的 javaw.exe 路径
        /// </summary>
        /// <param name="majorVersion">主版本号（如 8、17、21）</param>
        /// <param name="progress">进度报告（复用 Versions.InstallProgress）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>安装后的 javaw.exe 完整路径</returns>
        Task<string> InstallAsync(
            int majorVersion,
            IProgress<InstallProgress>? progress,
            CancellationToken cancellationToken = default);
    }
}
