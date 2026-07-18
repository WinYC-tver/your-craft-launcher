namespace YCL.Models.Accounts
{
    /// <summary>
    /// 账户类型枚举。区分三种 Minecraft 登录方式。
    /// </summary>
    public enum AccountType
    {
        /// <summary>离线账户：不需要联网验证，用户名随意填，UUID 按算法生成。</summary>
        Offline = 0,

        /// <summary>微软账户：通过 OAuth 设备代码流登录正版 Minecraft。</summary>
        Microsoft = 1,

        /// <summary>外置登录（authlib-injector）：第三方皮肤站账户，如 LittleSkin。</summary>
        Yggdrasil = 2
    }
}
