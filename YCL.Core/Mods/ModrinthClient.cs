using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;
using YCL.Core.Utils;

namespace YCL.Core.Mods
{
    /// <summary>
    /// Modrinth API 客户端实现。
    ///
    /// 实现要点：
    /// - 共享一个 HttpClient（带 5 分钟超时）
    /// - 无需 API Key，调用公开端点
    /// - 搜索用 facets 参数过滤项目类型 / 加载器 / 游戏版本
    /// - 大文件下载复用 MultiThreadDownloader
    /// - facets 格式：[["project_type:mod"], ["categories:fabric"]]
    ///   （多个 facet 同组为 OR，不同组为 AND）
    /// </summary>
    public class ModrinthClient : IModrinthClient
    {
        /// <summary>Modrinth API 基础 URL</summary>
        private const string ApiBaseUrl = "https://api.modrinth.com/v2";

        /// <summary>共享 HttpClient</summary>
        private readonly HttpClient _httpClient;

        /// <summary>多线程下载器（复用现有基础设施）</summary>
        private readonly MultiThreadDownloader _downloader;

        /// <summary>JSON 序列化选项（驼峰命名）</summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public ModrinthClient(MultiThreadDownloader downloader)
        {
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("YCL-Launcher/1.0");
            Logger.Info("Modrinth 客户端已初始化");
        }

        /// <inheritdoc/>
        public async Task<List<ModrinthSearchResult>> SearchModsAsync(
            string query,
            string projectType = "mod",
            string? loaderType = null,
            string? gameVersion = null,
            int page = 0, int pageSize = 20,
            CancellationToken cancellationToken = default)
        {
            // 构造 facets 数组：[["project_type:mod"], ["categories:fabric"], ["versions:1.20.4"]]
            // Modrinth 用 categories 字段存加载器（fabric / forge 等是 categories 的一部分）
            var facets = new List<List<string>>();

            if (!string.IsNullOrEmpty(projectType))
            {
                facets.Add(new List<string> { $"project_type:{projectType}" });
            }
            if (!string.IsNullOrEmpty(loaderType))
            {
                facets.Add(new List<string> { $"categories:{loaderType.ToLowerInvariant()}" });
            }
            if (!string.IsNullOrEmpty(gameVersion))
            {
                facets.Add(new List<string> { $"versions:{gameVersion}" });
            }

            // 序列化 facets 为 JSON 字符串
            var facetsJson = JsonSerializer.Serialize(facets);

            // 构造查询 URL
            var url = $"{ApiBaseUrl}/search?" +
                      $"query={Uri.EscapeDataString(query ?? string.Empty)}" +
                      $"&facets={Uri.EscapeDataString(facetsJson)}" +
                      $"&limit={pageSize}" +
                      $"&offset={page * pageSize}";

            try
            {
                var json = await GetJsonAsync(url, cancellationToken);
                var response = JsonSerializer.Deserialize<ModrinthSearchResponse>(json, JsonOptions);
                var result = response?.Hits ?? new List<ModrinthSearchResult>();
                Logger.Info($"Modrinth 搜索 \"{query}\" 返回 {result.Count} 个结果（共 {response?.TotalHits ?? 0} 个匹配）");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Modrinth 搜索失败：{ex.Message}");
                return new List<ModrinthSearchResult>();
            }
        }

        /// <inheritdoc/>
        public async Task<List<ModrinthVersion>> GetProjectVersionsAsync(
            string projectId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return new List<ModrinthVersion>();

            var url = $"{ApiBaseUrl}/project/{Uri.EscapeDataString(projectId)}/version";

            try
            {
                var json = await GetJsonAsync(url, cancellationToken);
                var result = JsonSerializer.Deserialize<List<ModrinthVersion>>(json, JsonOptions);
                Logger.Info($"Modrinth 项目 {projectId} 返回 {result?.Count ?? 0} 个版本");
                return result ?? new List<ModrinthVersion>();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Modrinth 获取版本列表失败：{ex.Message}");
                return new List<ModrinthVersion>();
            }
        }

        /// <inheritdoc/>
        public async Task DownloadModFileAsync(
            string downloadUrl, string targetPath,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
                throw new ArgumentException("下载 URL 不能为空", nameof(downloadUrl));
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("目标路径不能为空", nameof(targetPath));

            // 订阅下载器进度事件
            EventHandler<DownloadProgressEventArgs> handler = (s, e) => progress?.Report(e);
            _downloader.ProgressChanged += handler;
            try
            {
                await _downloader.DownloadAsync(downloadUrl, targetPath, cancellationToken);
                Logger.Info($"Modrinth 文件下载完成：{downloadUrl} → {targetPath}");
            }
            finally
            {
                _downloader.ProgressChanged -= handler;
            }
        }

        /// <summary>发送 GET 请求并返回响应文本</summary>
        private async Task<string> GetJsonAsync(string url, CancellationToken ct)
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
    }
}
