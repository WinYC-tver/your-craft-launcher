using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;
using YCL.Models.Accounts;

namespace YCL.Core.Accounts
{
    /// <summary>
    /// 第三方外置登录认证器实现。
    ///
    /// 实现 authlib-injector 兼容的 Yggdrasil 认证 API：
    /// - authenticate：用账号密码登录，返回 accessToken + 用户档案
    /// - refresh：用旧 accessToken 换新 accessToken
    /// - validate：检查 accessToken 是否仍然有效
    ///
    /// 接口规范见：https://github.com/yushijinhun/authlib-injector/wiki/Yggdrasil-%E6%9C%8D%E5%8A%A1%E7%AB%AF%E6%8A%80%E6%9C%AF%E8%A7%84%E8%8C%83
    /// </summary>
    public class YggdrasilAuthenticator : IYggdrasilAuthenticator
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        /// <inheritdoc/>
        public async Task<YggdrasilAccount> AuthenticateAsync(
            string serverUrl,
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            serverUrl = NormalizeServerUrl(serverUrl);
            var clientToken = Guid.NewGuid().ToString("N");

            var body = JsonSerializer.Serialize(new
            {
                username = username,
                password = password,
                agent = new { name = "Minecraft", version = 1 },
                clientToken = clientToken,
                requestUser = true
            });

            using var resp = await PostJsonAsync(serverUrl + "/authserver/authenticate", body, cancellationToken);
            var doc = await ParseJsonAsync(resp, cancellationToken);

            var accessToken = GetString(doc, "accessToken");
            var returnedClientToken = GetString(doc, "clientToken");
            if (!string.IsNullOrEmpty(returnedClientToken))
                clientToken = returnedClientToken;

            var profileId = string.Empty;
            var profileName = string.Empty;
            if (doc.RootElement.TryGetProperty("selectedProfile", out var profile))
            {
                profileId = profile.GetProperty("id").GetString() ?? string.Empty;
                profileName = profile.GetProperty("name").GetString() ?? username;
            }

            var account = new YggdrasilAccount
            {
                AccountId = Guid.NewGuid(),
                Username = string.IsNullOrEmpty(profileName) ? username : profileName,
                Uuid = FormatUuid(profileId),
                AccessToken = accessToken,
                ServerUrl = serverUrl,
                ClientToken = clientToken,
                LastRefreshTime = DateTime.Now
            };

            Logger.Info($"外置登录成功：{account.Username}@{serverUrl}");
            return account;
        }

        /// <inheritdoc/>
        public async Task<bool> RefreshAsync(YggdrasilAccount account)
        {
            try
            {
                if (string.IsNullOrEmpty(account.AccessToken))
                    return false;

                var body = JsonSerializer.Serialize(new
                {
                    accessToken = account.AccessToken,
                    clientToken = account.ClientToken,
                    requestUser = true
                });

                using var resp = await PostJsonAsync(account.ServerUrl + "/authserver/refresh", body, default);
                var doc = await ParseJsonAsync(resp, default);

                account.AccessToken = GetString(doc, "accessToken");
                account.LastRefreshTime = DateTime.Now;
                Logger.Info($"外置账户令牌刷新成功：{account.Username}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("外置账户令牌刷新失败", ex);
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> ValidateAsync(YggdrasilAccount account)
        {
            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    accessToken = account.AccessToken,
                    clientToken = account.ClientToken
                });

                using var resp = await PostJsonAsync(account.ServerUrl + "/authserver/validate", body, default);
                // 204 表示有效
                return resp.StatusCode == System.Net.HttpStatusCode.NoContent;
            }
            catch (Exception ex)
            {
                Logger.Warn("外置账户令牌验证异常：" + ex.Message);
                return false;
            }
        }

        // ===== 工具方法 =====

        /// <summary>规范化服务器 URL：去末尾斜杠</summary>
        private static string NormalizeServerUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                throw new ArgumentException("认证服务器地址不能为空");
            return url.TrimEnd('/');
        }

        /// <summary>POST JSON</summary>
        private static async Task<HttpResponseMessage> PostJsonAsync(string url, string jsonBody, CancellationToken ct)
        {
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync(url, content, ct);
            resp.EnsureSuccessStatusCode();
            return resp;
        }

        /// <summary>解析响应</summary>
        private static async Task<JsonDocument> ParseJsonAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(json);
        }

        /// <summary>读取字符串字段</summary>
        private static string GetString(JsonDocument doc, string name)
        {
            return doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? string.Empty
                : string.Empty;
        }

        /// <summary>格式化 UUID 为带连字符的标准格式</summary>
        private static string FormatUuid(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return string.Empty;
            uuid = uuid.Replace("-", "");
            if (uuid.Length != 32) return uuid;
            return $"{uuid.Substring(0, 8)}-{uuid.Substring(8, 4)}-{uuid.Substring(12, 4)}-{uuid.Substring(16, 4)}-{uuid.Substring(20, 12)}";
        }
    }
}
