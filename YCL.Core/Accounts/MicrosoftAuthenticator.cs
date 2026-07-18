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
    /// 微软账户认证器实现。
    ///
    /// 登录流程（设备代码流 OAuth 2.0）：
    /// 1. 请求设备 code：让用户在浏览器输入 user_code 完成登录
    /// 2. 轮询 token：用户登录成功后拿到微软 OAuth access_token + refresh_token
    /// 3. Xbox Live 认证：用 access_token 换取 XBL token
    /// 4. XSTS 认证：用 XBL token 换取 XSTS token
    /// 5. Minecraft 令牌：用 XSTS token 换取 Minecraft access_token
    /// 6. 获取 profile：拿到用户名、UUID、皮肤 URL
    ///
    /// clientId 使用 PCL 公开的客户端 ID（00000000402b5328），可通用。
    /// </summary>
    public class MicrosoftAuthenticator : IMicrosoftAuthenticator
    {
        /// <summary>OAuth 客户端 ID（PCL 公开 ID）</summary>
        private const string ClientId = "00000000402b5328";

        /// <summary>OAuth scope：XboxLive 登录 + 离线访问（拿 refresh_token）</summary>
        private const string Scope = "XboxLive.signin offline_access";

        /// <summary>设备码请求地址</summary>
        private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";

        /// <summary>令牌请求地址</summary>
        private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

        /// <summary>Xbox Live 认证地址</summary>
        private const string XboxLiveAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";

        /// <summary>XSTS 认证地址</summary>
        private const string XstsAuthUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";

        /// <summary>Minecraft 令牌获取地址</summary>
        private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";

        /// <summary>Minecraft profile 获取地址</summary>
        private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

        /// <summary>专用于认证流程的 HttpClient（独立于文件下载的 SharedClient，便于设置较短超时）</summary>
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        /// <inheritdoc/>
        public async Task<MicrosoftAccount> LoginAsync(
            IProgress<MicrosoftLoginProgress> progress,
            CancellationToken cancellationToken)
        {
            // 阶段 1：请求设备 code
            var deviceCode = await RequestDeviceCodeAsync(cancellationToken);
            progress.Report(new MicrosoftLoginProgress
            {
                Stage = MicrosoftLoginStage.WaitingForUser,
                UserCode = deviceCode.UserCode,
                VerificationUri = deviceCode.VerificationUri,
                Message = $"请在浏览器打开 {deviceCode.VerificationUri} 并输入代码：{deviceCode.UserCode}"
            });

            // 阶段 2：轮询令牌
            var oauth = await PollTokenAsync(deviceCode, cancellationToken);

            // 阶段 3：Xbox Live 认证
            progress.Report(new MicrosoftLoginProgress
            {
                Stage = MicrosoftLoginStage.GettingXboxLiveToken,
                Message = "正在获取 Xbox Live 令牌..."
            });
            var xbl = await AuthenticateXboxLiveAsync(oauth.AccessToken, cancellationToken);

            // 阶段 4：XSTS 认证
            progress.Report(new MicrosoftLoginProgress
            {
                Stage = MicrosoftLoginStage.GettingXstsToken,
                Message = "正在获取 XSTS 令牌..."
            });
            var xsts = await AuthenticateXstsAsync(xbl.Token, cancellationToken);

            // 阶段 5：Minecraft 令牌
            progress.Report(new MicrosoftLoginProgress
            {
                Stage = MicrosoftLoginStage.GettingMinecraftToken,
                Message = "正在获取 Minecraft 令牌..."
            });
            var mc = await GetMinecraftTokenAsync(xsts.UserHash, xsts.Token, cancellationToken);

            // 阶段 6：账户信息
            progress.Report(new MicrosoftLoginProgress
            {
                Stage = MicrosoftLoginStage.GettingProfile,
                Message = "正在获取 Minecraft 账户信息..."
            });
            var profile = await GetProfileAsync(mc.AccessToken, cancellationToken);

            // 组装账户对象
            var account = new MicrosoftAccount
            {
                AccountId = Guid.NewGuid(),
                Username = profile.Username,
                Uuid = profile.Uuid,
                AccessToken = mc.AccessToken,
                MicrosoftAccessToken = oauth.AccessToken,
                MicrosoftRefreshToken = oauth.RefreshToken,
                XboxLiveToken = xbl.Token,
                XstsToken = xsts.Token,
                MicrosoftTokenExpires = DateTime.Now.AddSeconds(oauth.ExpiresIn),
                XboxTokenExpires = DateTime.Now.AddSeconds(xbl.ExpiresIn),
                XstsTokenExpires = DateTime.Now.AddSeconds(xsts.ExpiresIn),
                McTokenExpires = DateTime.Now.AddSeconds(mc.ExpiresIn),
                SkinUrl = profile.SkinUrl,
                LastRefreshTime = DateTime.Now
            };

            progress.Report(new MicrosoftLoginProgress
            {
                Stage = MicrosoftLoginStage.Completed,
                Message = $"登录成功：{account.Username}"
            });

            return account;
        }

        /// <inheritdoc/>
        public async Task<bool> RefreshAsync(MicrosoftAccount account)
        {
            try
            {
                if (string.IsNullOrEmpty(account.MicrosoftRefreshToken))
                {
                    Logger.Warn("微软账户刷新失败：缺少 refresh_token");
                    return false;
                }

                // 1. 用 refresh_token 换新的 OAuth access_token
                var oauth = await RefreshOAuthTokenAsync(account.MicrosoftRefreshToken);

                account.MicrosoftAccessToken = oauth.AccessToken;
                account.MicrosoftRefreshToken = oauth.RefreshToken;
                account.MicrosoftTokenExpires = DateTime.Now.AddSeconds(oauth.ExpiresIn);

                // 2. Xbox Live 认证
                var xbl = await AuthenticateXboxLiveAsync(oauth.AccessToken);
                account.XboxLiveToken = xbl.Token;
                account.XboxTokenExpires = DateTime.Now.AddSeconds(xbl.ExpiresIn);

                // 3. XSTS 认证
                var xsts = await AuthenticateXstsAsync(xbl.Token);
                account.XstsToken = xsts.Token;
                account.XstsTokenExpires = DateTime.Now.AddSeconds(xsts.ExpiresIn);

                // 4. Minecraft 令牌
                var mc = await GetMinecraftTokenAsync(xsts.UserHash, xsts.Token);
                account.AccessToken = mc.AccessToken;
                account.McTokenExpires = DateTime.Now.AddSeconds(mc.ExpiresIn);

                account.LastRefreshTime = DateTime.Now;
                Logger.Info($"微软账户令牌刷新成功：{account.Username}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("微软账户令牌刷新失败", ex);
                return false;
            }
        }

        /// <summary>请求设备 code</summary>
        private async Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken ct)
        {
            var body = $"client_id={ClientId}&scope={Uri.EscapeDataString(Scope)}";
            using var resp = await PostFormAsync(DeviceCodeUrl, body, ct);
            var doc = await ParseJsonAsync(resp, ct);

            return new DeviceCodeResponse
            {
                DeviceCode = GetJsonString(doc, "device_code"),
                UserCode = GetJsonString(doc, "user_code"),
                VerificationUri = GetJsonString(doc, "verification_uri"),
                ExpiresIn = GetJsonInt(doc, "expires_in", 900),
                Interval = GetJsonInt(doc, "interval", 5)
            };
        }

        /// <summary>轮询令牌端点，直到用户完成登录或超时</summary>
        private async Task<OAuthTokenResponse> PollTokenAsync(DeviceCodeResponse deviceCode, CancellationToken ct)
        {
            var interval = deviceCode.Interval;
            var deadline = DateTime.Now.AddSeconds(deviceCode.ExpiresIn);

            while (DateTime.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(interval * 1000, ct);

                var body = $"client_id={ClientId}&scope={Uri.EscapeDataString(Scope)}" +
                           $"&grant_type=urn:ietf:params:oauth:grant-type:device_code" +
                           $"&device_code={deviceCode.DeviceCode}";

                using var resp = await PostFormAsync(TokenUrl, body, ct);
                var doc = await ParseJsonAsync(resp, ct);

                var error = GetJsonString(doc, "error");
                if (!string.IsNullOrEmpty(error))
                {
                    // authorization_pending：用户还没完成登录，继续轮询
                    if (error == "authorization_pending")
                        continue;
                    if (error == "slow_down")
                    {
                        interval += 5; // 服务器要求降速
                        continue;
                    }
                    if (error == "expired_token")
                        throw new InvalidOperationException("登录超时，请重新尝试。");
                    if (error == "authorization_declined")
                        throw new InvalidOperationException("用户取消了登录。");
                    throw new InvalidOperationException($"登录失败：{error} - {GetJsonString(doc, "error_description")}");
                }

                // 成功拿到令牌
                return new OAuthTokenResponse
                {
                    AccessToken = GetJsonString(doc, "access_token"),
                    RefreshToken = GetJsonString(doc, "refresh_token"),
                    ExpiresIn = GetJsonInt(doc, "expires_in", 3600)
                };
            }

            throw new InvalidOperationException("登录超时，请在规定时间内完成浏览器登录。");
        }

        /// <summary>用 refresh_token 刷新 OAuth 令牌</summary>
        private async Task<OAuthTokenResponse> RefreshOAuthTokenAsync(string refreshToken)
        {
            var body = $"client_id={ClientId}&scope={Uri.EscapeDataString(Scope)}" +
                       $"&grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refreshToken)}";

            using var resp = await PostFormAsync(TokenUrl, body, default);
            var doc = await ParseJsonAsync(resp, default);

            var error = GetJsonString(doc, "error");
            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException($"刷新 OAuth 令牌失败：{error}");

            return new OAuthTokenResponse
            {
                AccessToken = GetJsonString(doc, "access_token"),
                RefreshToken = GetJsonString(doc, "refresh_token"),
                ExpiresIn = GetJsonInt(doc, "expires_in", 3600)
            };
        }

        /// <summary>Xbox Live 认证</summary>
        private Task<XblAuthResult> AuthenticateXboxLiveAsync(string msAccessToken, CancellationToken ct = default)
        {
            return AuthenticateXboxLiveInternalAsync(msAccessToken, ct);
        }

        private async Task<XblAuthResult> AuthenticateXboxLiveInternalAsync(string msAccessToken, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new
            {
                Properties = new
                {
                    AuthMethod = "RPS",
                    SiteName = "user.auth.xboxlive.com",
                    RpsTicket = msAccessToken
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT"
            });

            using var resp = await PostJsonAsync(XboxLiveAuthUrl, body, ct);
            var doc = await ParseJsonAsync(resp, ct);

            var token = GetJsonString(doc, "Token");
            var uhs = GetJsonString(doc, "DisplayClaims", "xui", 0, "uhs");
            var expiresIn = GetJsonInt(doc, "NotAfter", 0) == 0
                ? 86400 // XBL token 通常 24 小时有效
                : (int)(GetJsonDate(doc, "NotAfter") - DateTime.Now).TotalSeconds;

            return new XblAuthResult
            {
                Token = token,
                UserHash = uhs,
                ExpiresIn = Math.Max(3600, expiresIn)
            };
        }

        /// <summary>XSTS 认证</summary>
        private async Task<XstsAuthResult> AuthenticateXstsAsync(string xblToken, CancellationToken ct = default)
        {
            var body = JsonSerializer.Serialize(new
            {
                Properties = new
                {
                    SandboxId = "RETAIL",
                    UserTokens = new[] { xblToken }
                },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT"
            });

            using var resp = await PostJsonAsync(XstsAuthUrl, body, ct);
            var doc = await ParseJsonAsync(resp, ct);

            var xErr = GetJsonString(doc, "XErr");
            if (!string.IsNullOrEmpty(xErr))
            {
                // 常见 XErr：2148916233=未购买MC，2148916238=儿童账户需成年人同意
                throw xErr switch
                {
                    "2148916233" => new InvalidOperationException("该微软账户未购买 Minecraft。"),
                    "2148916235" => new InvalidOperationException("该账户所在地区不支持。"),
                    "2148916238" => new InvalidOperationException("这是儿童账户，需要成年人同意才能登录。"),
                    _ => new InvalidOperationException($"XSTS 认证失败：XErr={xErr}")
                };
            }

            return new XstsAuthResult
            {
                Token = GetJsonString(doc, "Token"),
                UserHash = GetJsonString(doc, "DisplayClaims", "xui", 0, "uhs"),
                ExpiresIn = 86400
            };
        }

        /// <summary>获取 Minecraft 访问令牌</summary>
        private async Task<McTokenResult> GetMinecraftTokenAsync(string uhs, string xstsToken, CancellationToken ct = default)
        {
            var body = JsonSerializer.Serialize(new
            {
                identityToken = $"XBL3.0 x={uhs};{xstsToken}"
            });

            using var resp = await PostJsonAsync(McLoginUrl, body, ct);
            var doc = await ParseJsonAsync(resp, ct);

            return new McTokenResult
            {
                AccessToken = GetJsonString(doc, "access_token"),
                ExpiresIn = GetJsonInt(doc, "expires_in", 86400)
            };
        }

        /// <summary>获取 Minecraft 账户信息</summary>
        private async Task<McProfileResult> GetProfileAsync(string mcToken, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
            request.Headers.Add("Authorization", "Bearer " + mcToken);

            using var resp = await Http.SendAsync(request, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException("该账户未购买 Minecraft（profile 接口返回 404）。");

            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var root = doc.RootElement;
            var username = root.GetProperty("name").GetString() ?? "Player";
            var uuid = root.GetProperty("id").GetString() ?? string.Empty;
            // UUID 格式化为带连字符的标准形式
            uuid = FormatUuid(uuid);

            string? skinUrl = null;
            if (root.TryGetProperty("skins", out var skins) && skins.GetArrayLength() > 0)
            {
                skinUrl = skins[0].TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
            }

            return new McProfileResult
            {
                Username = username,
                Uuid = uuid,
                SkinUrl = skinUrl
            };
        }

        // ===== 工具方法 =====

        /// <summary>POST 表单数据</summary>
        private async Task<HttpResponseMessage> PostFormAsync(string url, string formBody, CancellationToken ct)
        {
            using var content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");
            var resp = await Http.PostAsync(url, content, ct);
            resp.EnsureSuccessStatusCode();
            return resp;
        }

        /// <summary>POST JSON 数据</summary>
        private async Task<HttpResponseMessage> PostJsonAsync(string url, string jsonBody, CancellationToken ct)
        {
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync(url, content, ct);
            resp.EnsureSuccessStatusCode();
            return resp;
        }

        /// <summary>解析响应为 JsonDocument</summary>
        private async Task<JsonDocument> ParseJsonAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(json);
        }

        /// <summary>从 JsonDocument 读取字符串字段（支持嵌套路径，如 "DisplayClaims.xui[0].uhs"）</summary>
        private static string GetJsonString(JsonDocument doc, params object[] path)
        {
            try
            {
                var el = NavigatePath(doc.RootElement, path);
                return el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;
            }
            catch { return string.Empty; }
        }

        /// <summary>从 JsonDocument 读取整数字段</summary>
        private static int GetJsonInt(JsonDocument doc, string name, int defaultValue)
        {
            try
            {
                if (doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
                    return el.GetInt32();
            }
            catch { }
            return defaultValue;
        }

        /// <summary>从 JsonDocument 读取整数字段（按路径）</summary>
        private static int GetJsonInt(JsonDocument doc, params object[] path)
        {
            try
            {
                var el = NavigatePath(doc.RootElement, path);
                return el.ValueKind == JsonValueKind.Number ? el.GetInt32() : 0;
            }
            catch { return 0; }
        }

        /// <summary>从 JsonDocument 读取日期字段</summary>
        private static DateTime GetJsonDate(JsonDocument doc, string name)
        {
            try
            {
                if (doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                    return DateTime.Parse(el.GetString() ?? string.Empty);
            }
            catch { }
            return DateTime.MinValue;
        }

        /// <summary>按路径导航 JSON 元素（支持字符串属性名和整数索引）</summary>
        private static JsonElement NavigatePath(JsonElement root, params object[] path)
        {
            var current = root;
            foreach (var seg in path)
            {
                if (seg is string propName)
                {
                    current = current.GetProperty(propName);
                }
                else if (seg is int index)
                {
                    current = current[index];
                }
            }
            return current;
        }

        /// <summary>把无连字符 UUID 格式化为 8-4-4-4-12 标准 UUID</summary>
        private static string FormatUuid(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return string.Empty;
            uuid = uuid.Replace("-", "");
            if (uuid.Length != 32) return uuid;
            return $"{uuid.Substring(0, 8)}-{uuid.Substring(8, 4)}-{uuid.Substring(12, 4)}-{uuid.Substring(16, 4)}-{uuid.Substring(20, 12)}";
        }

        // ===== 响应 DTO =====

        private class DeviceCodeResponse
        {
            public string DeviceCode { get; set; } = string.Empty;
            public string UserCode { get; set; } = string.Empty;
            public string VerificationUri { get; set; } = string.Empty;
            public int ExpiresIn { get; set; }
            public int Interval { get; set; }
        }

        private class OAuthTokenResponse
        {
            public string AccessToken { get; set; } = string.Empty;
            public string RefreshToken { get; set; } = string.Empty;
            public int ExpiresIn { get; set; }
        }

        private class XblAuthResult
        {
            public string Token { get; set; } = string.Empty;
            public string UserHash { get; set; } = string.Empty;
            public int ExpiresIn { get; set; }
        }

        private class XstsAuthResult
        {
            public string Token { get; set; } = string.Empty;
            public string UserHash { get; set; } = string.Empty;
            public int ExpiresIn { get; set; }
        }

        private class McTokenResult
        {
            public string AccessToken { get; set; } = string.Empty;
            public int ExpiresIn { get; set; }
        }

        private class McProfileResult
        {
            public string Username { get; set; } = string.Empty;
            public string Uuid { get; set; } = string.Empty;
            public string? SkinUrl { get; set; }
        }
    }
}
