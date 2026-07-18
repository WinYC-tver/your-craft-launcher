namespace YCL.Core.Accounts
{
    /// <summary>
    /// 微软账户登录进度信息，通过 IProgress 回调推送给 UI。
    /// 每个阶段对应设备代码流的一个步骤。
    /// </summary>
    public class MicrosoftLoginProgress
    {
        /// <summary>当前登录阶段</summary>
        public MicrosoftLoginStage Stage { get; set; }

        /// <summary>用户代码（如 "ABCD-EFGH"），用户需在浏览器输入此代码完成登录</summary>
        public string? UserCode { get; set; }

        /// <summary>验证网址（如 "https://microsoft.com/link"），用户需在浏览器访问</summary>
        public string? VerificationUri { get; set; }

        /// <summary>进度说明文字（显示在界面上）</summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>微软登录流程的各阶段</summary>
    public enum MicrosoftLoginStage
    {
        /// <summary>等待用户在浏览器完成登录（此时 UI 应显示 user_code 和验证网址）</summary>
        WaitingForUser,

        /// <summary>正在获取 Xbox Live 令牌</summary>
        GettingXboxLiveToken,

        /// <summary>正在获取 XSTS 令牌</summary>
        GettingXstsToken,

        /// <summary>正在获取 Minecraft 访问令牌</summary>
        GettingMinecraftToken,

        /// <summary>正在获取 Minecraft 账户信息（用户名、UUID、皮肤）</summary>
        GettingProfile,

        /// <summary>登录成功完成</summary>
        Completed,

        /// <summary>登录失败</summary>
        Failed
    }
}
