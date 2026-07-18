using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace YCL.Models.Accounts
{
    /// <summary>
    /// 账户基类（抽象）。所有类型的 Minecraft 账户都继承自此类。
    ///
    /// 设计说明：
    /// - 用 <see cref="JsonPolymorphic"/> + <see cref="JsonDerivedType"/> 实现多态 JSON 序列化，
    ///   accounts.json 里每个账户会带上 <c>$type</c> 字段标识其真实类型。
    /// - 令牌（AccessToken 等敏感字段）用 <c>[JsonIgnore]</c> 标记，不直接写入文件，
    ///   而是通过 <see cref="EncryptedTokens"/> 字段（DPAPI 加密后的 Base64 字符串）保存。
    /// - <see cref="GetSensitiveTokens"/> / <see cref="SetSensitiveTokens"/> 由各子类实现，
    ///   AccountManager 在保存前收集敏感字段、加载后回填敏感字段。
    /// </summary>
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(OfflineAccount), "offline")]
    [JsonDerivedType(typeof(MicrosoftAccount), "microsoft")]
    [JsonDerivedType(typeof(YggdrasilAccount), "yggdrasil")]
    public abstract class AccountBase
    {
        /// <summary>账户内部唯一 ID（用于在账户列表中区分不同账户）</summary>
        public Guid AccountId { get; set; } = Guid.NewGuid();

        /// <summary>玩家名（显示在游戏内与启动器界面）</summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>游戏内 UUID（用于启动参数 --uuid，离线账户按算法生成）</summary>
        public string Uuid { get; set; } = string.Empty;

        /// <summary>
        /// 访问令牌。离线账户为 "0"；微软账户为 Minecraft 访问令牌；
        /// 外置账户为认证服务器返回的 accessToken。
        /// 用 <c>[JsonIgnore]</c> 防止明文写入文件，实际通过 <see cref="EncryptedTokens"/> 加密保存。
        /// </summary>
        [JsonIgnore]
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>账户类型枚举值</summary>
        public AccountType Type { get; set; }

        /// <summary>上次刷新令牌的时间（null 表示从未刷新）</summary>
        public DateTime? LastRefreshTime { get; set; }

        /// <summary>
        /// DPAPI 加密后的所有敏感令牌（Base64 字符串）。
        /// AccountManager 保存前写入，加载后读取并解密回填到各字段。
        /// </summary>
        public string? EncryptedTokens { get; set; }

        /// <summary>
        /// 启动参数中的 user_type 占位符值。
        /// 离线=offline，微软=msa，外置=mojang。
        /// </summary>
        [JsonIgnore]
        public string UserType => Type switch
        {
            AccountType.Offline => "offline",
            AccountType.Microsoft => "msa",
            AccountType.Yggdrasil => "mojang",
            _ => "legacy"
        };

        /// <summary>
        /// 收集本账户所有敏感令牌字段（键名 → 明文值）。
        /// AccountManager 在保存前调用，把返回值用 DPAPI 加密后存入 <see cref="EncryptedTokens"/>。
        /// </summary>
        public abstract Dictionary<string, string> GetSensitiveTokens();

        /// <summary>
        /// 把解密后的令牌字段回填到本账户的各属性。
        /// AccountManager 在加载后调用，参数是解密后的键值对。
        /// </summary>
        public abstract void SetSensitiveTokens(Dictionary<string, string> tokens);

        /// <summary>
        /// 刷新令牌。离线账户直接返回 true；微软账户走 OAuth 刷新流程；
        /// 外置账户走认证服务器的 refresh 接口。
        /// </summary>
        /// <returns>刷新是否成功</returns>
        public abstract Task<bool> RefreshAsync();
    }
}
