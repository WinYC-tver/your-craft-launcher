using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YCL.Models.Versions;

namespace YCL.Core.Versions
{
    /// <summary>
    /// 安装进度信息。版本安装过程中通过 <c>IProgress&lt;InstallProgress&gt;</c> 报告给调用方。
    /// 包含当前阶段、正在处理的文件、文件数与字节数等。
    /// </summary>
    public class InstallProgress
    {
        /// <summary>当前安装阶段</summary>
        public InstallPhase Phase { get; set; }

        /// <summary>当前正在处理的文件名（用于 UI 显示）</summary>
        public string CurrentFile { get; set; } = string.Empty;

        /// <summary>已完成文件数</summary>
        public int CompletedFiles { get; set; }

        /// <summary>总文件数（未知时为 0）</summary>
        public int TotalFiles { get; set; }

        /// <summary>已下载字节数</summary>
        public long CompletedBytes { get; set; }

        /// <summary>总字节数（未知时为 0）</summary>
        public long TotalBytes { get; set; }

        /// <summary>整体进度百分比（0~100，未知时为 -1）</summary>
        public double Percent
        {
            get
            {
                if (TotalFiles > 0)
                    return (double)CompletedFiles / TotalFiles * 100.0;
                return -1;
            }
        }

        /// <summary>用户可读的阶段名称</summary>
        public string PhaseText => Phase switch
        {
            InstallPhase.FetchingManifest => "获取版本清单",
            InstallPhase.DownloadingJson => "下载版本 JSON",
            InstallPhase.Parsing => "解析版本信息",
            InstallPhase.DownloadingFiles => "下载版本文件",
            InstallPhase.DownloadingAssets => "下载资源文件",
            InstallPhase.Completed => "安装完成",
            _ => Phase.ToString()
        };
    }

    /// <summary>版本安装阶段</summary>
    public enum InstallPhase
    {
        /// <summary>获取版本清单</summary>
        FetchingManifest,

        /// <summary>下载版本 JSON</summary>
        DownloadingJson,

        /// <summary>解析版本信息</summary>
        Parsing,

        /// <summary>下载版本文件（client.jar / libraries / natives / logging）</summary>
        DownloadingFiles,

        /// <summary>下载资源文件（assets objects）</summary>
        DownloadingAssets,

        /// <summary>安装完成</summary>
        Completed
    }

    /// <summary>
    /// 版本管理服务接口。统一管理已安装版本的扫描、在线版本列表获取、
    /// 版本安装、删除、重命名、复制，以及版本隔离目录的维护。
    ///
    /// 所有方法均为异步，且不依赖 UI 线程。调用方应在后台线程调用并自行处理 UI 更新。
    /// </summary>
    public interface IVersionManager
    {
        /// <summary>
        /// 扫描 .minecraft/versions 目录，列出所有已安装的版本。
        /// 每个子目录含 &lt;目录名&gt;.json 即视为一个版本。
        /// 会读取 JSON 提取 id、type、inheritsFrom、libraries（判断是否含模组加载器）。
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>已安装版本信息列表（按 id 字母序升序）</returns>
        Task<List<InstalledVersionInfo>> ListInstalledVersionsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 从官方版本清单获取所有可下载的版本，按 type 分组返回。
        /// 内部复用 <see cref="Download.IVersionManifestService"/>。
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>按类型分组的版本条目字典（键为 release/snapshot/old_beta/old_alpha）</returns>
        Task<Dictionary<string, List<VersionManifestEntry>>> ListAvailableVersionsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 安装指定版本。流程：
        /// 1. 下载版本 JSON 到 .minecraft/versions/&lt;id&gt;/&lt;id&gt;.json
        /// 2. 用 VersionResolver 解析 JSON（合并 inheritsFrom）
        /// 3. 调用 IMinecraftFileDownloader 下载所有依赖文件
        /// 4. 通过 progress 报告进度
        /// </summary>
        /// <param name="entry">要安装的版本清单条目（含下载 URL）</param>
        /// <param name="progress">进度报告回调（可为 null）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>是否安装成功（无失败文件且未取消）</returns>
        Task<bool> InstallVersionAsync(
            VersionManifestEntry entry,
            IProgress<InstallProgress>? progress,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 删除指定版本。会删除 .minecraft/versions/&lt;versionId&gt;/ 整个目录。
        /// 如果启用了版本隔离，隔离目录就在版本目录内，会一并删除。
        /// 注意：此操作不可逆，调用方应在 UI 弹确认对话框，确认后才调此方法。
        /// </summary>
        /// <param name="versionId">要删除的版本 id</param>
        /// <returns>是否删除成功</returns>
        Task<bool> DeleteVersionAsync(string versionId);

        /// <summary>
        /// 重命名版本。会重命名版本目录和 JSON 文件，并更新 JSON 内部的 id 字段。
        /// </summary>
        /// <param name="oldId">原版本 id</param>
        /// <param name="newId">新版本 id</param>
        /// <returns>是否重命名成功</returns>
        Task<bool> RenameVersionAsync(string oldId, string newId);

        /// <summary>
        /// 复制版本。会复制整个版本目录，并更新新版本的 JSON id 字段。
        /// </summary>
        /// <param name="sourceId">源版本 id</param>
        /// <param name="newId">新版本 id</param>
        /// <returns>是否复制成功</returns>
        Task<bool> CopyVersionAsync(string sourceId, string newId);

        /// <summary>
        /// 确保版本隔离目录存在。启用版本隔离时，调用此方法在启动前创建
        /// mods/saves/configs/resourcepacks/shaderpacks 等子目录。
        /// </summary>
        /// <param name="versionId">版本 id</param>
        void EnsureIsolationDirectories(string versionId);

        /// <summary>
        /// 获取指定版本隔离后的游戏目录路径。
        /// 启用隔离时返回 .minecraft/versions/&lt;id&gt;/，禁用时返回 .minecraft/。
        /// </summary>
        /// <param name="versionId">版本 id</param>
        /// <returns>游戏目录绝对路径</returns>
        string GetGameDirectory(string versionId);

        /// <summary>当前 .minecraft 根目录路径（从配置读取，为空则用默认 %AppData%\.minecraft）</summary>
        string MinecraftPath { get; }
    }
}
