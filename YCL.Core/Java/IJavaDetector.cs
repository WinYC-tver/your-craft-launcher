using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YCL.Core.Java
{
    /// <summary>
    /// Java 检测器接口：扫描系统已安装的 Java 运行时。
    /// 实现会从注册表、常见安装路径、.minecraft/runtime、PATH 等位置查找 javaw.exe，
    /// 并解析每个 Java 的版本信息。
    /// </summary>
    public interface IJavaDetector
    {
        /// <summary>
        /// 异步检测系统已安装的 Java 列表。
        /// 流程：
        /// 1. 扫描注册表 JavaSoft 节点（含 32 位 Wow6432Node）
        /// 2. 扫描常见安装路径（Program Files、Eclipse Adoptium、Microsoft、Zulu、IntelliJ .jdks 等）
        /// 3. 扫描 .minecraft/runtime（Mojang 自带的运行时）
        /// 4. 扫描 PATH 中的 java
        /// 5. 对每个 javaw.exe 运行 -version 解析版本号
        /// 6. 按路径去重
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>已安装 Java 列表（按主版本号降序）</returns>
        Task<List<JavaInfo>> DetectAsync(CancellationToken cancellationToken = default);
    }
}
