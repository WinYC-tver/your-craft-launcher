using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Download;
using YCL.Core.Utils;

namespace YCL.Core.Mods
{
    /// <summary>
    /// CurseForge API 客户端实现。
    ///
    /// 实现要点：
    /// - 共享一个 HttpClient（带 10 分钟超时，适合大文件下载）
    /// - 每个请求附带 X-API-Key 请求头
    /// - API Key 通过构造函数传入（来自 AppConfig.CurseForgeApiKey）
    /// - 未配置 Key 时 IsConfigured 为 false，所有方法返回空列表
    /// - 大文件下载复用 MultiThreadDownloader（与版本下载共用基础设施）
    ///
    /// 注意：CurseForge 文件下载 URL 需要带 API Key 才能访问（CDN 会校验），
    ///      所以这里需要在下载请求中也附加 Key。
    /// </summary>
    public class CurseForgeClient : ICurseForgeClient
    {
        /// <summary>CurseForge API 基础 URL</summary>
        private const string ApiBaseUrl = "https://api.curseforge.com/v1";

        /// <summary>Minecraft 在 CurseForge 中的 gameId（固定为 432）</summary>
        private const int MinecraftGameId = 432;

        /// <summary>CurseForge API Key（可能为空）</summary>
        private readonly string _apiKey;

        /// <summary>共享 HttpClient（避免 socket 耗尽）</summary>
        private readonly HttpClient _httpClient;

        /// <summary>多线程下载器（复用现有基础设施）</summary>
        private readonly MultiThreadDownloader _downloader;

        /// <summary>是否已配置 API Key</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        /// <summary>
        /// 构造 CurseForge 客户端。
        /// </summary>
        /// <param name="apiKey">CurseForge API Key（可空）</param>
        /// <param name="downloader">多线程下载器（由 DI 注入）</param>
        public CurseForgeClient(string apiKey, MultiThreadDownloader downloader)
        {
            _apiKey = apiKey ?? string.Empty;
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
            if (IsConfigured)
            {
                _httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
            }
            // 标识客户端（CurseForge 建议带 User-Agent）
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("YCL-Launcher/1.0");

            Logger.Info($"CurseForge 客户端已初始化（Key 已配置：{IsConfigured}）");
        }

        /// <inheritdoc/>
        public async Task<List<CurseForgeMod>> SearchModsAsync(
            string query, int page = 0, int pageSize = 20,
            CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
            {
                Logger.Warn("CurseForge 未配置 API Key，跳过搜索");
                return new List<CurseForgeMod>();
            }

            // 构造搜索 URL
            // gameId=432 表示 Minecraft，classId=6 表示 Mod（区分资源包/光影/地图等）
            var url = $"{ApiBaseUrl}/mods/search?gameId={MinecraftGameId}" +
                      $"&classId=6" +  // 6 = Mod 类别
                      $"&searchFilter={Uri.EscapeDataString(query ?? string.Empty)}" +
                      $"&index={page * pageSize}" +
                      $"&pageSize={pageSize}" +
                      $"&sortField=2" +  // 2 = Popularity（按热度排序）
                      $"&sortOrder=desc";

            try
            {
                var json = await GetJsonAsync(url, cancellationToken);
                var response = JsonSerializer.Deserialize<CurseForgeSearchResponse>(json);
                var result = response?.Data ?? new List<CurseForgeMod>();
                Logger.Info($"CurseForge 搜索 \"{query}\" 返回 {result.Count} 个结果");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warn($"CurseForge 搜索失败：{ex.Message}");
                return new List<CurseForgeMod>();
            }
        }

        /// <inheritdoc/>
        public async Task<List<CurseForgeFile>> GetModFilesAsync(
            int modId, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
            {
                Logger.Warn("CurseForge 未配置 API Key，跳过获取文件列表");
                return new List<CurseForgeFile>();
            }

            var url = $"{ApiBaseUrl}/mods/{modId}/files?pageSize=50";

            try
            {
                var json = await GetJsonAsync(url, cancellationToken);
                // 文件列表响应结构是 {"data": [...]}，用 JsonDocument 取出 data 后反序列化为文件列表
                using var doc = JsonDocument.Parse(json);
                var result = new List<CurseForgeFile>();
                if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                    dataEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataEl.EnumerateArray())
                    {
                        try
                        {
                            var file = JsonSerializer.Deserialize<CurseForgeFile>(item.GetRawText());
                            if (file != null) result.Add(file);
                        }
                        catch { /* 跳过单个文件解析失败 */ }
                    }
                }
                Logger.Info($"CurseForge 模组 {modId} 返回 {result.Count} 个文件");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warn($"CurseForge 获取文件列表失败：{ex.Message}");
                return new List<CurseForgeFile>();
            }
        }

        /// <inheritdoc/>
        public async Task DownloadModFileAsync(
            CurseForgeFile file, string targetPath,
            IProgress<DownloadProgressEventArgs>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("目标路径不能为空", nameof(targetPath));

            if (!IsConfigured)
            {
                throw new InvalidOperationException("CurseForge 未配置 API Key，无法下载文件");
            }

            // CurseForge 文件下载 URL：如果 response 中已有 downloadUrl，直接用；否则需要请求 API 获取
            var downloadUrl = file.DownloadUrl;
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                downloadUrl = await GetDownloadUrlAsync(file.Id, cancellationToken);
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    throw new InvalidOperationException($"无法获取文件 {file.Id} 的下载 URL");
                }
            }

            // 订阅下载器进度事件，转发给调用方
            EventHandler<DownloadProgressEventArgs> handler = (s, e) => progress?.Report(e);
            _downloader.ProgressChanged += handler;
            try
            {
                await _downloader.DownloadAsync(downloadUrl, targetPath, cancellationToken);
                Logger.Info($"CurseForge 文件下载完成：{file.FileName} → {targetPath}");
            }
            finally
            {
                _downloader.ProgressChanged -= handler;
            }
        }

        /// <summary>调用 CurseForge API 获取文件下载 URL</summary>
        private async Task<string?> GetDownloadUrlAsync(int fileId, CancellationToken ct)
        {
            var url = $"{ApiBaseUrl}/mods/files/{fileId}/download-url";
            try
            {
                var json = await GetJsonAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                    dataEl.ValueKind == JsonValueKind.String)
                {
                    return dataEl.GetString();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"获取 CurseForge 文件下载 URL 失败：fileId={fileId} - {ex.Message}");
            }
            return null;
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
