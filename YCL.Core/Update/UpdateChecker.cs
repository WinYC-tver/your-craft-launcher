using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;

namespace YCL.Core.Update
{
    /// <summary>
    /// 启动器更新检查器实现。
    /// 请求 GitHub Release API 获取最新版本信息，与当前启动器版本比较。
    /// 注意：GitHub API 要求请求带 User-Agent header，否则返回 403。
    /// </summary>
    public class UpdateChecker : IUpdateChecker
    {
        /// <summary>GitHub API 基础地址</summary>
        private const string GitHubApiBase = "https://api.github.com/repos/";

        /// <summary>HTTP 客户端（静态复用，避免每次检查都创建新实例）</summary>
        private static readonly HttpClient _httpClient = CreateHttpClient();

        /// <summary>获取当前 GitHub 仓库（owner/repo）的委托</summary>
        private readonly Func<string> _getRepo;

        /// <summary>
        /// 构造更新检查器。
        /// </summary>
        /// <param name="getRepo">返回当前配置的 GitHub 仓库（格式 "owner/repo"）</param>
        public UpdateChecker(Func<string> getRepo)
        {
            _getRepo = getRepo ?? throw new ArgumentNullException(nameof(getRepo));
        }

        /// <summary>
        /// 检查是否有新版本。
        /// 请求 GitHub Release API，解析 tag_name/html_url/body/published_at/assets，
        /// 与当前启动器版本比较，有新版本时返回 ReleaseInfo，否则返回 null。
        /// 任何异常都记录日志并返回 null（不中断启动）。
        /// </summary>
        public async Task<ReleaseInfo?> CheckForUpdatesAsync(CancellationToken ct)
        {
            try
            {
                var repo = _getRepo();
                if (string.IsNullOrWhiteSpace(repo))
                {
                    Logger.Warn("更新检查：未配置 GitHub 仓库（owner/repo），跳过检查");
                    return null;
                }

                var url = $"{GitHubApiBase}{repo.Trim('/')}/releases/latest";
                Logger.Info($"开始检查更新：{url}");

                // 发起 GET 请求
                var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn($"更新检查：GitHub API 返回 {response.StatusCode}");
                    return null;
                }

                // 解析 JSON 响应
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // 提取 tag_name（版本号，可能含 v 前缀）
                if (!root.TryGetProperty("tag_name", out var tagNameProp))
                {
                    Logger.Warn("更新检查：响应中缺少 tag_name 字段");
                    return null;
                }
                var tagName = tagNameProp.GetString() ?? string.Empty;
                var remoteVersionStr = NormalizeVersion(tagName);

                // 获取当前启动器版本
                var currentVersion = GetCurrentVersion();
                Logger.Info($"更新检查：当前版本 = {currentVersion}，远程版本 = {remoteVersionStr}");

                // 版本比较
                if (!TryParseVersion(remoteVersionStr, out var remoteVersion))
                {
                    Logger.Warn($"更新检查：无法解析远程版本号 {remoteVersionStr}");
                    return null;
                }

                // 远程版本 <= 当前版本，无更新
                if (remoteVersion <= currentVersion)
                {
                    Logger.Info("更新检查：已是最新版本");
                    return null;
                }

                // 有新版本，构建 ReleaseInfo
                var release = new ReleaseInfo
                {
                    Version = remoteVersionStr,
                    ReleaseUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "" : "",
                    ReleaseNotes = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "",
                    PublishedAt = root.TryGetProperty("published_at", out var dateProp) && DateTime.TryParse(dateProp.GetString(), out var pubDate) ? pubDate : DateTime.Now
                };

                // 解析资产列表
                if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        try
                        {
                            release.Assets.Add(new ReleaseAsset
                            {
                                Name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "",
                                DownloadUrl = asset.TryGetProperty("browser_download_url", out var dlProp) ? dlProp.GetString() ?? "" : "",
                                Size = asset.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0
                            });
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"更新检查：解析资产信息失败 - {ex.Message}");
                        }
                    }
                }

                Logger.Info($"发现新版本：{release.Version}（发布于 {release.PublishedAtDisplay}，{release.Assets.Count} 个资产）");
                return release;
            }
            catch (OperationCanceledException)
            {
                // 正常取消，不记录错误
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error("更新检查失败", ex);
                return null;
            }
        }

        // ====== 私有辅助方法 ======

        /// <summary>
        /// 创建并配置 HttpClient。
        /// GitHub API 要求 User-Agent header，否则返回 403。
        /// </summary>
        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YCL-Launcher", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.Timeout = TimeSpan.FromSeconds(15);  // 超时 15 秒，避免启动卡住
            return client;
        }

        /// <summary>
        /// 获取当前启动器版本。
        /// 从入口程序集（YCL.exe）读取版本号。
        /// </summary>
        private static Version GetCurrentVersion()
        {
            try
            {
                // GetEntryAssembly 返回启动当前进程的程序集（即 YCL.exe）
                var assembly = Assembly.GetEntryAssembly();
                if (assembly != null)
                {
                    var version = assembly.GetName().Version;
                    if (version != null)
                        return version;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"读取当前版本失败：{ex.Message}");
            }

            // fallback：返回 1.0.0
            return new Version(1, 0, 0);
        }

        /// <summary>
        /// 规范化版本号字符串。
        /// 去掉常见的 v/V 前缀（如 "v1.2.3" → "1.2.3"）。
        /// </summary>
        private static string NormalizeVersion(string tagName)
        {
            if (string.IsNullOrEmpty(tagName))
                return "0.0.0";

            var v = tagName.Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase) || v.StartsWith("V"))
                v = v.Substring(1);
            return v.Trim();
        }

        /// <summary>
        /// 尝试解析版本号。
        /// 支持 "major.minor.patch" 和 "major.minor.patch.build" 格式。
        /// </summary>
        private static bool TryParseVersion(string versionStr, out Version version)
        {
            // Version.TryParse 支持 "1.2.3" 和 "1.2.3.4" 格式
            return Version.TryParse(versionStr, out version!);
        }
    }
}
