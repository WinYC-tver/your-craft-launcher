using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace YCL.Models.Accounts
{
    /// <summary>
    /// 外置登录账户（authlib-injector / Yggdrasil）：
    /// 对接第三方认证服务器（如 LittleSkin）。
    /// 启动游戏时需要注入 authlib-injector.jar，把 Minecraft 的登录请求重定向到第三方服务器。
    /// </summary>
    public class YggdrasilAccount : AccountBase
    {
        /// <summary>
        /// 令牌刷新处理器。由 AccountManager 在启动时注入。
        /// 返回是否刷新成功。
        /// </summary>
        [JsonIgnore]
        internal static Func<YggdrasilAccount, Task<bool>>? RefreshHandler;

        /// <summary>
        /// 认证服务器地址（如 <c>https://littleskin.cn/api/yggdrasil</c>）。
        /// 不含末尾斜杠。
        /// </summary>
        public string ServerUrl { get; set; } = string.Empty;

        /// <summary>
        /// 客户端令牌（登录时由客户端生成，刷新/验证时需要回传）。
        /// 非敏感：无法单独用于登录，需要配合 accessToken。
        /// </summary>
        public string ClientToken { get; set; } = string.Empty;

        /// <summary>
        /// 皮肤 URL（可能为空，部分服务器通过 profile 接口返回）。
        /// </summary>
        public string? SkinUrl { get; set; }

        /// <summary>默认构造（用于 JSON 反序列化）</summary>
        public YggdrasilAccount()
        {
            Type = AccountType.Yggdrasil;
        }

        /// <inheritdoc/>
        public override Dictionary<string, string> GetSensitiveTokens()
        {
            return new Dictionary<string, string>
            {
                ["AccessToken"] = AccessToken
            };
        }

        /// <inheritdoc/>
        public override void SetSensitiveTokens(Dictionary<string, string> tokens)
        {
            AccessToken = tokens.TryGetValue("AccessToken", out var t) ? t : string.Empty;
        }

        /// <summary>
        /// 刷新令牌。委托给 AccountManager 注入的 <see cref="RefreshHandler"/>。
        /// 调用认证服务器的 refresh 接口。
        /// </summary>
        public override Task<bool> RefreshAsync()
        {
            if (RefreshHandler == null)
                return Task.FromResult(false);
            return RefreshHandler(this);
        }
    }
}
