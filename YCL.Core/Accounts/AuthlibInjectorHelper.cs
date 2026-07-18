using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using YCL.Core.Utils;
using YCL.Models.Accounts;

namespace YCL.Core.Accounts
{
    /// <summary>
    /// authlib-injector 注入参数生成与 jar 下载助手。
    ///
    /// authlib-injector 通过 Java agent 机制拦截 Minecraft 的登录请求，
    /// 把官方 authserver 请求重定向到第三方认证服务器，从而实现外置登录。
    /// 启动时需要两个 JVM 参数：
    /// - <c>-javaagent:authlib-injector.jar=服务器地址</c>
    /// - <c>-Dauthlibinjector.yggdrasil.prefetched=服务器元数据的 Base64</c>
    /// （预取元数据避免启动时再联网请求，加快启动速度）
    /// </summary>
    public static class AuthlibInjectorHelper
    {
        /// <summary>authlib-injector 最新版信息地址</summary>
        private const string LatestJsonUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";

        /// <summary>jar 文件存放路径：%AppData%\YCL\authlib-injector.jar</summary>
        private static readonly string JarPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YCL", "authlib-injector.jar");

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        /// <summary>
        /// 缓存：服务器地址 → Base64 编码的元数据，避免每次启动都重新下载。
        /// </summary>
        private static readonly Dictionary<string, string> MetadataCache = new();

        /// <summary>
        /// 获取 authlib-injector 的 jar 文件路径，如果不存在则自动下载。
        /// </summary>
        /// <returns>jar 文件的完整路径</returns>
        public static async Task<string> EnsureJarAsync()
        {
            if (File.Exists(JarPath))
                return JarPath;

            return await DownloadJarAsync();
        }

        /// <summary>下载最新版 authlib-injector.jar</summary>
        public static async Task<string> DownloadJarAsync()
        {
            Logger.Info("开始下载 authlib-injector.jar");

            // 1. 获取最新版信息
            using var resp = await Http.GetAsync(LatestJsonUrl);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var downloadUrl = doc.RootElement.GetProperty("download_url").GetString()
                ?? throw new InvalidOperationException("无法获取 authlib-injector 下载地址");
            var sha1 = doc.RootElement.TryGetProperty("sha1", out var sha1El) ? sha1El.GetString() : null;

            // 2. 下载 jar
            Logger.Info($"下载 authlib-injector.jar：{downloadUrl}");
            var dir = Path.GetDirectoryName(JarPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var jarResp = await Http.GetStreamAsync(downloadUrl);
            await using var fs = new FileStream(JarPath, FileMode.Create, FileAccess.Write);
            await jarResp.CopyToAsync(fs);

            Logger.Info($"authlib-injector.jar 下载完成：{JarPath}");
            return JarPath;
        }

        /// <summary>
        /// 生成 authlib-injector 的 JVM 启动参数。
        /// 参数包含：
        /// - <c>-javaagent:jar路径=服务器地址</c>
        /// - <c>-Dauthlibinjector.yggdrasil.prefetched=Base64元数据</c>
        /// </summary>
        /// <param name="account">外置登录账户</param>
        /// <param name="authlibInjectorJarPath">authlib-injector.jar 的路径</param>
        /// <returns>JVM 参数列表（插入到 JVM 参数最前面）</returns>
        public static async Task<List<string>> GetLaunchArgumentsAsync(
            YggdrasilAccount account,
            string authlibInjectorJarPath)
        {
            var serverUrl = account.ServerUrl.TrimEnd('/');

            // 获取服务器元数据（带缓存）
            var prefetched = await GetPrefetchedMetadataAsync(serverUrl);

            return new List<string>
            {
                $"-javaagent:{authlibInjectorJarPath}={serverUrl}",
                $"-Dauthlibinjector.yggdrasil.prefetched={prefetched}"
            };
        }

        /// <summary>
        /// 获取服务器元数据并 Base64 编码（带内存缓存）。
        /// 元数据是服务器根地址返回的 JSON，包含各 API 端点路径。
        /// </summary>
        private static async Task<string> GetPrefetchedMetadataAsync(string serverUrl)
        {
            if (MetadataCache.TryGetValue(serverUrl, out var cached))
                return cached;

            // 服务器元数据从服务器根地址获取
            using var resp = await Http.GetAsync(serverUrl);
            resp.EnsureSuccessStatusCode();
            var metadata = await resp.Content.ReadAsStringAsync();

            // Base64 编码（UTF-8）
            var bytes = Encoding.UTF8.GetBytes(metadata);
            var base64 = Convert.ToBase64String(bytes);

            MetadataCache[serverUrl] = base64;
            Logger.Debug($"已获取 authlib-injector 元数据：{serverUrl}（{bytes.Length} 字节）");

            return base64;
        }
    }
}
