using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace YCL.Models.Accounts
{
    /// <summary>
    /// 微软账户：通过 OAuth 设备代码流登录正版 Minecraft。
    ///
    /// 令牌刷新流程（由 AccountManager 注入的 <see cref="RefreshHandler"/> 完成）：
    /// 1. 用 MicrosoftRefreshToken 调 OAuth token 接口换取新的 MicrosoftAccessToken
    /// 2. 用 MicrosoftAccessToken 走 Xbox Live 认证拿 XboxLiveToken
    /// 3. 用 XboxLiveToken 走 XSTS 认证拿 XstsToken
    /// 4. 用 XstsToken 换 Minecraft 访问令牌（即基类的 AccessToken）
    /// 每步都更新对应的过期时间。
    /// </summary>
    public class MicrosoftAccount : AccountBase
    {
        /// <summary>
        /// 令牌刷新处理器。由 AccountManager 在启动时注入，
        /// 避免把网络逻辑放到模型层。返回是否刷新成功。
        /// </summary>
        [JsonIgnore]
        internal static Func<MicrosoftAccount, Task<bool>>? RefreshHandler;

        /// <summary>微软 OAuth 访问令牌（用于换取 Xbox Live token）</summary>
        [JsonIgnore]
        public string MicrosoftAccessToken { get; set; } = string.Empty;

        /// <summary>微软 OAuth 刷新令牌（用于获取新的访问令牌，长期有效）</summary>
        [JsonIgnore]
        public string MicrosoftRefreshToken { get; set; } = string.Empty;

        /// <summary>Xbox Live 令牌（用于换取 XSTS token）</summary>
        [JsonIgnore]
        public string XboxLiveToken { get; set; } = string.Empty;

        /// <summary>XSTS 令牌（用于换取 Minecraft 访问令牌）</summary>
        [JsonIgnore]
        public string XstsToken { get; set; } = string.Empty;

        /// <summary>微软 OAuth 访问令牌过期时间</summary>
        public DateTime? MicrosoftTokenExpires { get; set; }

        /// <summary>Xbox Live 令牌过期时间</summary>
        public DateTime? XboxTokenExpires { get; set; }

        /// <summary>XSTS 令牌过期时间</summary>
        public DateTime? XstsTokenExpires { get; set; }

        /// <summary>Minecraft 访问令牌（基类 AccessToken）过期时间</summary>
        public DateTime? McTokenExpires { get; set; }

        /// <summary>
        /// 皮肤 URL（登录时从 Minecraft profile 接口获取，可能为空）。
        /// 皮肤下载失败不应影响启动。
        /// </summary>
        public string? SkinUrl { get; set; }

        /// <summary>默认构造（用于 JSON 反序列化）</summary>
        public MicrosoftAccount()
        {
            Type = AccountType.Microsoft;
        }

        /// <inheritdoc/>
        public override Dictionary<string, string> GetSensitiveTokens()
        {
            return new Dictionary<string, string>
            {
                ["AccessToken"] = AccessToken,
                ["MicrosoftAccessToken"] = MicrosoftAccessToken,
                ["MicrosoftRefreshToken"] = MicrosoftRefreshToken,
                ["XboxLiveToken"] = XboxLiveToken,
                ["XstsToken"] = XstsToken
            };
        }

        /// <inheritdoc/>
        public override void SetSensitiveTokens(Dictionary<string, string> tokens)
        {
            AccessToken = tokens.TryGetValue("AccessToken", out var a) ? a : string.Empty;
            MicrosoftAccessToken = tokens.TryGetValue("MicrosoftAccessToken", out var ma) ? ma : string.Empty;
            MicrosoftRefreshToken = tokens.TryGetValue("MicrosoftRefreshToken", out var mr) ? mr : string.Empty;
            XboxLiveToken = tokens.TryGetValue("XboxLiveToken", out var xl) ? xl : string.Empty;
            XstsToken = tokens.TryGetValue("XstsToken", out var xs) ? xs : string.Empty;
        }

        /// <summary>
        /// 刷新令牌。委托给 AccountManager 注入的 <see cref="RefreshHandler"/>。
        /// 如果未注入处理器（如未初始化），返回 false。
        /// </summary>
        public override Task<bool> RefreshAsync()
        {
            if (RefreshHandler == null)
                return Task.FromResult(false);
            return RefreshHandler(this);
        }

        /// <summary>
        /// 判断 Minecraft 访问令牌是否已过期（或即将过期）。
        /// 提前 5 分钟视为过期，避免启动时刚好失效。
        /// </summary>
        public bool IsTokenExpired()
        {
            if (!McTokenExpires.HasValue)
                return true; // 没有记录过期时间，视为需要刷新
            return DateTime.Now.AddMinutes(5) >= McTokenExpires.Value;
        }
    }
}
