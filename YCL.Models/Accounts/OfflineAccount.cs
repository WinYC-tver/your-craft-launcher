using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace YCL.Models.Accounts
{
    /// <summary>
    /// 离线账户：不需要联网验证，用户名随意填。
    /// UUID 按 Java 版标准算法生成（与官方启动器一致）：
    /// 取字符串 "OfflinePlayer:" + 用户名 的 MD5 哈希（16 字节），
    /// 设置 version 位为 3（UUID v3）、variant 位为 IETF（10xxxxxx）。
    /// </summary>
    public class OfflineAccount : AccountBase
    {
        /// <summary>离线账户的固定令牌（原版 Minecraft 接受零令牌）</summary>
        private const string OfflineToken = "0";

        /// <summary>
        /// 默认构造（用于 JSON 反序列化）。
        /// </summary>
        public OfflineAccount()
        {
            Type = AccountType.Offline;
            AccessToken = OfflineToken;
        }

        /// <summary>
        /// 用用户名构造离线账户，自动生成 Java 标准离线 UUID。
        /// </summary>
        /// <param name="username">玩家名</param>
        public OfflineAccount(string username) : this()
        {
            Username = username;
            Uuid = GenerateOfflineUuid(username);
        }

        /// <summary>
        /// 按 Java 版离线 UUID 算法生成 UUID（带连字符的标准 8-4-4-4-12 格式）：
        /// 1. 对 "OfflinePlayer:" + 用户名 求 MD5（得到 16 字节）
        /// 2. 把第 7 字节（index 6）高 4 位清零并设为 0x30（表示 version 3，MD5）
        /// 3. 把第 9 字节（index 8）高 2 位清零并设为 0x80（表示 IETF variant）
        /// 4. 按 4-2-2-2-6 字节分组拼成 xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
        /// </summary>
        public static string GenerateOfflineUuid(string username)
        {
            try
            {
                var input = Encoding.UTF8.GetBytes("OfflinePlayer:" + username);
                byte[] hash;
                using (var md5 = MD5.Create())
                {
                    hash = md5.ComputeHash(input); // 16 字节
                }

                // 设置 version = 3（MD5 UUID）
                hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
                // 设置 variant = IETF（10xxxxxx）
                hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

                // 拼成标准 UUID 字符串（带连字符）
                return new Guid(hash).ToString();
            }
            catch
            {
                // 极端情况：返回固定的占位 UUID
                return "00000000-0000-3000-8000-000000000000";
            }
        }

        /// <inheritdoc/>
        public override Dictionary<string, string> GetSensitiveTokens()
        {
            // 离线账户令牌固定为 "0"，不算真正敏感，但仍走加密通道保持结构一致
            return new Dictionary<string, string>
            {
                ["AccessToken"] = AccessToken
            };
        }

        /// <inheritdoc/>
        public override void SetSensitiveTokens(Dictionary<string, string> tokens)
        {
            AccessToken = tokens.TryGetValue("AccessToken", out var t) ? t : OfflineToken;
        }

        /// <inheritdoc/>
        /// <remarks>离线账户无需刷新，直接返回成功。</remarks>
        public override Task<bool> RefreshAsync()
        {
            LastRefreshTime = DateTime.Now;
            return Task.FromResult(true);
        }
    }
}
