using System.Threading;
using System.Threading.Tasks;
using YCL.Models.Accounts;

namespace YCL.Core.Accounts
{
    /// <summary>
    /// 第三方外置登录（Yggdrasil / authlib-injector）认证器接口。
    /// 对接兼容 authlib-injector 的认证服务器（如 LittleSkin）。
    /// </summary>
    public interface IYggdrasilAuthenticator
    {
        /// <summary>
        /// 登录认证服务器。
        /// </summary>
        /// <param name="serverUrl">认证服务器地址（如 <c>https://littleskin.cn/api/yggdrasil</c>）</param>
        /// <param name="username">用户名或邮箱</param>
        /// <param name="password">密码</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>登录成功的外置账户</returns>
        Task<YggdrasilAccount> AuthenticateAsync(
            string serverUrl,
            string username,
            string password,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 刷新令牌（调用认证服务器的 refresh 接口）。
        /// </summary>
        /// <param name="account">需要刷新的外置账户</param>
        /// <returns>刷新是否成功</returns>
        Task<bool> RefreshAsync(YggdrasilAccount account);

        /// <summary>
        /// 验证令牌是否有效（调用认证服务器的 validate 接口）。
        /// </summary>
        /// <param name="account">需要验证的外置账户</param>
        /// <returns>令牌是否有效</returns>
        Task<bool> ValidateAsync(YggdrasilAccount account);
    }
}
