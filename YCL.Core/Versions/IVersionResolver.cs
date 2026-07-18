using YCL.Models.Versions;

namespace YCL.Core.Versions
{
    /// <summary>
    /// 版本解析结果：包含合并后的版本信息和解析出的库文件路径列表。
    /// </summary>
    public class ResolvedVersion
    {
        /// <summary>合并 inheritsFrom 链后的完整版本信息</summary>
        public VersionInfo Info { get; set; } = new();

        /// <summary>所有需要加入 classpath 的库 jar 文件绝对路径列表（不含 natives）</summary>
        public List<string> ClasspathFiles { get; set; } = new();

        /// <summary>所有需要解压到 natives 目录的 zip 文件绝对路径列表</summary>
        public List<string> NativeFiles { get; set; } = new();

        /// <summary>版本 JSON 所在的目录（用于查找客户端 jar 等）</summary>
        public string VersionDirectory { get; set; } = string.Empty;

        /// <summary>客户端 jar 文件的绝对路径（&lt;VersionDirectory&gt;/&lt;id&gt;.jar）</summary>
        public string ClientJarPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// 版本解析服务接口。负责读取版本 JSON、合并 inheritsFrom 父版本、
    /// 解析库文件路径、按平台过滤库等。
    /// </summary>
    public interface IVersionResolver
    {
        /// <summary>
        /// 解析指定版本（合并 inheritsFrom 链 + 解析库文件路径）。
        /// </summary>
        /// <param name="minecraftPath">.minecraft 根目录路径</param>
        /// <param name="versionId">版本 id（即 versions 目录下的子目录名）</param>
        /// <returns>解析后的版本信息（含库文件路径）</returns>
        ResolvedVersion Resolve(string minecraftPath, string versionId);

        /// <summary>
        /// 列出 .minecraft/versions 目录下所有可用的版本 id
        /// （含 .json 文件的子目录视为一个版本）。
        /// </summary>
        /// <param name="minecraftPath">.minecraft 根目录路径</param>
        /// <returns>版本 id 列表（按字母序升序）。目录不存在时返回空列表。</returns>
        List<string> ListVersions(string minecraftPath);
    }
}
