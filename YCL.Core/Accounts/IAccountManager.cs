using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YCL.Models.Accounts;

namespace YCL.Core.Accounts
{
    /// <summary>
    /// 账户管理服务接口：负责账户的增删改查、当前账户切换、令牌刷新与持久化。
    /// 所有账户保存到 accounts.json，令牌用 DPAPI 加密。
    /// </summary>
    public interface IAccountManager
    {
        /// <summary>当前选中的账户（null 表示未选中任何账户）</summary>
        AccountBase? CurrentAccount { get; }

        /// <summary>账户列表变化时通知 UI 刷新</summary>
        event EventHandler? AccountsChanged;

        /// <summary>获取所有账户列表</summary>
        List<AccountBase> GetAccounts();

        /// <summary>获取当前选中账户</summary>
        AccountBase? GetCurrentAccount();

        /// <summary>设置当前选中账户</summary>
        /// <param name="accountId">要设为当前的账户 ID</param>
        void SetCurrentAccount(Guid accountId);

        /// <summary>添加离线账户</summary>
        /// <param name="username">玩家名</param>
        /// <returns>新创建的离线账户</returns>
        Task<AccountBase> AddOfflineAccountAsync(string username);

        /// <summary>
        /// 添加微软账户（走设备代码流登录）。
        /// </summary>
        /// <param name="progress">登录进度回调</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>登录成功的微软账户</returns>
        Task<MicrosoftAccount> AddMicrosoftAccountAsync(
            IProgress<MicrosoftLoginProgress> progress,
            CancellationToken ct);

        /// <summary>添加外置登录账户</summary>
        Task<YggdrasilAccount> AddYggdrasilAccountAsync(string serverUrl, string username, string password);

        /// <summary>删除指定账户</summary>
        /// <param name="accountId">要删除的账户 ID</param>
        void RemoveAccount(Guid accountId);

        /// <summary>刷新指定账户的令牌</summary>
        /// <param name="accountId">要刷新的账户 ID</param>
        /// <returns>刷新是否成功</returns>
        Task<bool> RefreshAccountAsync(Guid accountId);
    }
}
