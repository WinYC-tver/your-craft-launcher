using System.Threading;
using System.Threading.Tasks;
using YCL.Models.Accounts;

namespace YCL.Core.Accounts
{
    /// <summary>
    /// 微软账户认证器接口：负责完整的设备代码流登录与令牌刷新。
    /// </summary>
    public interface IMicrosoftAuthenticator
    {
        /// <summary>
        /// 通过设备代码流登录微软账户，返回登录成功的账户对象。
        /// 流程：设备码 → 用户浏览器登录 → XBL → XSTS → MC token → profile。
        /// 进度通过 <paramref name="progress"/> 回调推送给 UI。
        /// </summary>
        /// <param name="progress">进度回调（显示 user_code、验证网址、当前阶段）</param>
        /// <param name="cancellationToken">取消令牌（用户取消登录时触发）</param>
        /// <returns>登录成功的微软账户（含用户名、UUID、令牌、过期时间）</returns>
        Task<MicrosoftAccount> LoginAsync(
            IProgress<MicrosoftLoginProgress> progress,
            CancellationToken cancellationToken);

        /// <summary>
        /// 刷新已有微软账户的令牌（用 MicrosoftRefreshToken 重新走 OAuth → XBL → XSTS → MC token）。
        /// </summary>
        /// <param name="account">需要刷新的微软账户</param>
        /// <returns>刷新是否成功</returns>
        Task<bool> RefreshAsync(MicrosoftAccount account);
    }
}
