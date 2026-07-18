using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;
using YCL.Models.Accounts;

namespace YCL.Core.Accounts
{
    /// <summary>
    /// 账户管理服务实现。
    ///
    /// 职责：
    /// - 维护账户列表与当前选中账户
    /// - 持久化账户到 accounts.json（令牌用 DPAPI 加密）
    /// - 提供增删改查与令牌刷新
    /// - 向 UI 通知账户变化
    ///
    /// 令牌加密流程：
    /// 保存前 → 收集每个账户的敏感令牌字典 → JSON 序列化 → DPAPI 加密（CurrentUser 范围）→ Base64 → 存入 EncryptedTokens 字段
    /// 加载后 → 读取 EncryptedTokens → Base64 解码 → DPAPI 解密 → JSON 反序列化 → 回填到账户各字段
    /// </summary>
    public class AccountManager : IAccountManager
    {
        /// <summary>账户文件目录：%AppData%\YCL\</summary>
        private static readonly string DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YCL");

        /// <summary>账户文件路径：%AppData%\YCL\accounts.json</summary>
        private static readonly string AccountsPath = Path.Combine(DataDirectory, "accounts.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly IMicrosoftAuthenticator _msAuthenticator;
        private readonly IYggdrasilAuthenticator _yggdrasilAuthenticator;

        /// <summary>内存中的账户列表</summary>
        private readonly List<AccountBase> _accounts = new();

        /// <summary>当前选中账户 ID</summary>
        private Guid? _currentAccountId;

        /// <summary>当前选中的账户（缓存，避免每次遍历查找）</summary>
        private AccountBase? _currentAccount;

        public AccountManager(
            IMicrosoftAuthenticator microsoftAuthenticator,
            IYggdrasilAuthenticator yggdrasilAuthenticator)
        {
            _msAuthenticator = microsoftAuthenticator;
            _yggdrasilAuthenticator = yggdrasilAuthenticator;

            // 注入令牌刷新处理器到账户类（打破 Models → Core 的循环依赖）
            MicrosoftAccount.RefreshHandler = RefreshMicrosoftAccountAsync;
            YggdrasilAccount.RefreshHandler = RefreshYggdrasilAccountAsync;

            // 启动时加载已保存的账户
            LoadInternal();
        }

        /// <inheritdoc/>
        public AccountBase? CurrentAccount => _currentAccount;

        /// <inheritdoc/>
        public event EventHandler? AccountsChanged;

        /// <inheritdoc/>
        public List<AccountBase> GetAccounts() => _accounts;

        /// <inheritdoc/>
        public AccountBase? GetCurrentAccount() => _currentAccount;

        /// <inheritdoc/>
        public void SetCurrentAccount(Guid accountId)
        {
            if (_currentAccountId == accountId) return;
            _currentAccountId = accountId;
            _currentAccount = _accounts.FirstOrDefault(a => a.AccountId == accountId);
            SaveInternal();
            RaiseAccountsChanged();
            Logger.Info($"当前账户已切换为：{_currentAccount?.Username ?? "无"}");
        }

        /// <inheritdoc/>
        public async Task<AccountBase> AddOfflineAccountAsync(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("玩家名不能为空", nameof(username));

            // 同名离线账户不重复添加（直接复用）
            var existing = _accounts.FirstOrDefault(
                a => a.Type == AccountType.Offline && a.Username == username);
            if (existing != null)
            {
                SetCurrentAccount(existing.AccountId);
                return existing;
            }

            var account = new OfflineAccount(username);
            _accounts.Add(account);

            // 第一个账户自动设为当前
            if (_currentAccount == null)
            {
                _currentAccountId = account.AccountId;
                _currentAccount = account;
            }

            SaveInternal();
            RaiseAccountsChanged();
            Logger.Info($"已添加离线账户：{username}（UUID={account.Uuid}）");
            return await Task.FromResult<AccountBase>(account);
        }

        /// <inheritdoc/>
        public async Task<MicrosoftAccount> AddMicrosoftAccountAsync(
            IProgress<MicrosoftLoginProgress> progress,
            CancellationToken ct)
        {
            var account = await _msAuthenticator.LoginAsync(progress, ct);
            _accounts.Add(account);

            if (_currentAccount == null)
            {
                _currentAccountId = account.AccountId;
                _currentAccount = account;
            }

            SaveInternal();
            RaiseAccountsChanged();
            Logger.Info($"已添加微软账户：{account.Username}");
            return account;
        }

        /// <inheritdoc/>
        public async Task<YggdrasilAccount> AddYggdrasilAccountAsync(string serverUrl, string username, string password)
        {
            var account = await _yggdrasilAuthenticator.AuthenticateAsync(serverUrl, username, password);
            _accounts.Add(account);

            if (_currentAccount == null)
            {
                _currentAccountId = account.AccountId;
                _currentAccount = account;
            }

            SaveInternal();
            RaiseAccountsChanged();
            Logger.Info($"已添加外置账户：{account.Username}@{serverUrl}");
            return account;
        }

        /// <inheritdoc/>
        public void RemoveAccount(Guid accountId)
        {
            var account = _accounts.FirstOrDefault(a => a.AccountId == accountId);
            if (account == null) return;

            _accounts.Remove(account);

            // 删除的是当前账户：切换到第一个剩余账户（或清空）
            if (_currentAccountId == accountId)
            {
                _currentAccountId = _accounts.Count > 0 ? _accounts[0].AccountId : null;
                _currentAccount = _accounts.Count > 0 ? _accounts[0] : null;
            }

            SaveInternal();
            RaiseAccountsChanged();
            Logger.Info($"已删除账户：{account.Username}（{account.Type}）");
        }

        /// <inheritdoc/>
        public async Task<bool> RefreshAccountAsync(Guid accountId)
        {
            var account = _accounts.FirstOrDefault(a => a.AccountId == accountId);
            if (account == null) return false;

            var success = await account.RefreshAsync();
            if (success)
            {
                SaveInternal();
                RaiseAccountsChanged();
            }
            return success;
        }

        /// <summary>
        /// 启动前检查并刷新过期的令牌。
        /// 离线账户无需刷新；微软账户令牌过期则自动刷新。
        /// </summary>
        public async Task<bool> EnsureTokenValidAsync(AccountBase account)
        {
            if (account is MicrosoftAccount msAccount && msAccount.IsTokenExpired())
            {
                Logger.Info($"微软账户令牌已过期，自动刷新：{msAccount.Username}");
                var ok = await msAccount.RefreshAsync();
                if (ok)
                {
                    SaveInternal();
                    RaiseAccountsChanged();
                }
                return ok;
            }
            return true;
        }

        // ===== 微软 / 外置账户令牌刷新实现（注入到账户类） =====

        private async Task<bool> RefreshMicrosoftAccountAsync(MicrosoftAccount account)
        {
            return await _msAuthenticator.RefreshAsync(account);
        }

        private async Task<bool> RefreshYggdrasilAccountAsync(YggdrasilAccount account)
        {
            return await _yggdrasilAuthenticator.RefreshAsync(account);
        }

        // ===== 持久化：加载与保存 =====

        /// <summary>加载账户文件并解密令牌</summary>
        private void LoadInternal()
        {
            try
            {
                if (!File.Exists(AccountsPath))
                {
                    Logger.Info("账户文件不存在，使用空账户列表");
                    return;
                }

                var json = File.ReadAllText(AccountsPath);
                using var doc = JsonDocument.Parse(json);

                var root = doc.RootElement;
                if (root.TryGetProperty("currentAccountId", out var curIdEl)
                    && curIdEl.ValueKind == JsonValueKind.String
                    && Guid.TryParse(curIdEl.GetString(), out var curId))
                {
                    _currentAccountId = curId;
                }

                if (root.TryGetProperty("accounts", out var accountsEl))
                {
                    var jsonText = accountsEl.GetRawText();
                    var accounts = JsonSerializer.Deserialize<List<AccountBase>>(jsonText, JsonOptions);
                    if (accounts != null)
                    {
                        foreach (var account in accounts)
                        {
                            // 解密令牌
                            if (!string.IsNullOrEmpty(account.EncryptedTokens))
                            {
                                DecryptTokensInto(account);
                            }
                            _accounts.Add(account);
                        }

                        // 恢复当前账户引用
                        if (_currentAccountId.HasValue)
                        {
                            _currentAccount = _accounts.FirstOrDefault(a => a.AccountId == _currentAccountId.Value);
                        }
                        // 没有当前账户但有账户列表：选第一个
                        if (_currentAccount == null && _accounts.Count > 0)
                        {
                            _currentAccountId = _accounts[0].AccountId;
                            _currentAccount = _accounts[0];
                        }
                    }
                }

                Logger.Info($"已加载 {_accounts.Count} 个账户");
            }
            catch (Exception ex)
            {
                Logger.Error("加载账户文件失败", ex);
            }
        }

        /// <summary>保存账户列表到文件，保存前加密令牌</summary>
        private void SaveInternal()
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);

                // 保存前：加密每个账户的敏感令牌
                foreach (var account in _accounts)
                {
                    EncryptTokensFrom(account);
                }

                var store = new
                {
                    currentAccountId = _currentAccountId?.ToString(),
                    accounts = _accounts
                };
                var json = JsonSerializer.Serialize(store, JsonOptions);
                File.WriteAllText(AccountsPath, json);

                Logger.Debug($"账户文件已保存：{AccountsPath}（{_accounts.Count} 个账户）");
            }
            catch (Exception ex)
            {
                Logger.Error("保存账户文件失败", ex);
            }
        }

        // ===== DPAPI 令牌加密 / 解密 =====

        /// <summary>把账户的敏感令牌收集、加密后写入 EncryptedTokens 字段</summary>
        private static void EncryptTokensFrom(AccountBase account)
        {
            try
            {
                var tokens = account.GetSensitiveTokens();
                if (tokens == null || tokens.Count == 0)
                {
                    account.EncryptedTokens = null;
                    return;
                }

                var json = JsonSerializer.Serialize(tokens);
                var bytes = Encoding.UTF8.GetBytes(json);
                var cipher = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                account.EncryptedTokens = Convert.ToBase64String(cipher);
            }
            catch (Exception ex)
            {
                // 加密失败不中断保存流程，但令牌会丢失（记日志）
                Logger.Warn($"加密账户令牌失败：{account.Username} - {ex.Message}");
                account.EncryptedTokens = null;
            }
        }

        /// <summary>从 EncryptedTokens 解密令牌并回填到账户字段</summary>
        private static void DecryptTokensInto(AccountBase account)
        {
            try
            {
                if (string.IsNullOrEmpty(account.EncryptedTokens))
                    return;

                var cipher = Convert.FromBase64String(account.EncryptedTokens);
                var bytes = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(bytes);
                var tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (tokens != null)
                {
                    account.SetSensitiveTokens(tokens);
                }
            }
            catch (Exception ex)
            {
                // 解密失败不中断加载（可能是换了 Windows 用户），令牌为空
                Logger.Warn($"解密账户令牌失败：{account.Username} - {ex.Message}");
            }
        }

        /// <summary>触发账户变化事件</summary>
        private void RaiseAccountsChanged()
        {
            AccountsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
