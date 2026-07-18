using System.Threading;
using System.Threading.Tasks;

namespace YCL.Core.Update
{
    /// <summary>
    /// 启动器更新检查器接口。
    /// 通过 GitHub Release API 检查是否有新版本，返回 <see cref="ReleaseInfo"/> 或 null。
    /// </summary>
    public interface IUpdateChecker
    {
        /// <summary>
        /// 检查是否有新版本。
        /// 请求 GitHub Release API（releases/latest），解析返回的 JSON，
        /// 与当前启动器版本（从程序集读取）比较。
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>有新版本时返回 <see cref="ReleaseInfo"/>，无更新或出错时返回 null</returns>
        Task<ReleaseInfo?> CheckForUpdatesAsync(CancellationToken ct);
    }
}
