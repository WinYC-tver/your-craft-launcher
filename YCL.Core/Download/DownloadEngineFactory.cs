using YCL.Models;

namespace YCL.Core.Download
{
    /// <summary>
    /// 下载引擎工厂：根据 <see cref="AppConfig.DownloadEngine"/> 返回对应的 <see cref="IDownloadEngine"/> 实例。
    ///
    /// 使用方式：
    /// <code>
    /// var engine = DownloadEngineFactory.Create(config.DownloadEngine);
    /// bool ok = await engine.DownloadAsync(url, path, config.DownloadThreads, progress, ct);
    /// </code>
    ///
    /// 未来在 <c>DownloadPageViewModel</c> 或 <c>ResourceDetailPageViewModel</c> 中
    /// 调用 <c>DownloadEngineFactory.Create(config.DownloadEngine).DownloadAsync(...)</c>
    /// 即可切换下载引擎，无需改动现有 13 处对 <see cref="MultiThreadDownloader"/> 的直接调用
    /// （那些调用保持向后兼容，新代码通过工厂用接口）。
    /// </summary>
    public static class DownloadEngineFactory
    {
        /// <summary>
        /// 根据枚举值创建下载引擎实例。
        /// 每次调用都返回新实例（下载器内部有状态，不复用更安全）。
        /// </summary>
        /// <param name="engine">引擎类型（来自 <see cref="AppConfig.DownloadEngine"/>）</param>
        /// <returns>对应引擎的 <see cref="IDownloadEngine"/> 实例</returns>
        public static IDownloadEngine Create(DownloadEngine engine)
        {
            return engine switch
            {
                DownloadEngine.PclCe => new PclCeDownloader(),
                DownloadEngine.Ghost => new GhostDownloader(),
                _ => new DefaultDownloadEngine()
            };
        }
    }
}
