using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YCL.Core.Download;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Models.Versions;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 联机页 ViewModel：集成 Terracotta（陶瓦联机）客户端。
    ///
    /// 主要功能：
    /// 1. 提供项目主页跳转
    /// 2. 从 GitHub Release 下载 Terracotta jar 客户端
    /// 3. 选择已安装版本 + 填写服务器地址，触发联机启动（占位实现）
    /// </summary>
    public partial class MultiplayerPageViewModel : ViewModelBase
    {
        private readonly IVersionManager _versionManager;
        private readonly IConfigService _configService;
        private readonly MultiThreadDownloader _downloader;

        /// <summary>Terracotta jar 存放路径：%AppData%\YCL\terracotta\terracotta.jar</summary>
        private static readonly string TerracottaJarPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YCL", "terracotta", "terracotta.jar");

        /// <summary>
        /// 共享 HttpClient（用于 GitHub API + jar 下载）。
        /// 使用静态实例避免 socket 耗尽。User-Agent 设为 "YCL"（GitHub API 强制要求）。
        /// </summary>
        private static readonly HttpClient Http = new HttpClient
        {
            DefaultRequestHeaders = { { "User-Agent", "YCL" } },
            Timeout = TimeSpan.FromMinutes(5)
        };

        public MultiplayerPageViewModel(
            IVersionManager versionManager,
            IConfigService configService,
            MultiThreadDownloader downloader)
        {
            _versionManager = versionManager;
            _configService = configService;
            _downloader = downloader;

            // 构造函数里检查 Terracotta 是否已下载
            if (File.Exists(TerracottaJarPath))
            {
                IsTerracottaReady = true;
                DownloadStatus = "Terracotta 已就绪";
            }

            // 立即异步加载已安装版本列表
            _ = LoadVersionsAsync();
        }

        // ===== 绑定属性 =====

        /// <summary>已安装版本列表（用于下拉框选择）</summary>
        [ObservableProperty]
        private ObservableCollection<InstalledVersionInfo> _versions = new();

        /// <summary>当前选中的版本</summary>
        [ObservableProperty]
        private InstalledVersionInfo? _selectedVersion;

        /// <summary>是否正在下载 Terracotta</summary>
        [ObservableProperty]
        private bool _isDownloading;

        /// <summary>下载状态文字</summary>
        [ObservableProperty]
        private string _downloadStatus = "Terracotta 未下载";

        /// <summary>下载进度（0~100）</summary>
        [ObservableProperty]
        private double _downloadProgress;

        /// <summary>Terracotta 是否已就绪（即 jar 文件已存在）</summary>
        [ObservableProperty]
        private bool _isTerracottaReady;

        /// <summary>联机服务器地址</summary>
        [ObservableProperty]
        private string _serverAddress = "";

        // ===== 命令 =====

        /// <summary>用系统默认浏览器打开 Terracotta 项目主页</summary>
        [RelayCommand]
        private void OpenTerracotta()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/burningtnt/Terracotta",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error("打开 Terracotta 项目主页失败", ex);
            }
        }

        /// <summary>加载已安装版本列表命令</summary>
        [RelayCommand]
        private async Task LoadVersionsAsync()
        {
            try
            {
                var versions = await _versionManager.ListInstalledVersionsAsync();
                Versions.Clear();
                foreach (var v in versions)
                    Versions.Add(v);

                // 默认选中第一个
                if (Versions.Count > 0 && SelectedVersion == null)
                {
                    SelectedVersion = Versions[0];
                }
            }
            catch (Exception ex)
            {
                Logger.Error("加载版本列表失败", ex);
            }
        }

        /// <summary>下载 Terracotta jar 命令</summary>
        [RelayCommand]
        private async Task DownloadTerracottaAsync()
        {
            if (IsDownloading) return;
            IsDownloading = true;
            DownloadProgress = 0;
            DownloadStatus = "正在获取最新版本信息...";

            try
            {
                // 1. 请求 GitHub API 获取最新 release
                var apiUrl = "https://api.github.com/repos/burningtnt/Terracotta/releases/latest";
                string jsonResponse;
                try
                {
                    jsonResponse = await Http.GetStringAsync(apiUrl);
                }
                catch (Exception ex)
                {
                    DownloadStatus = "获取最新版本失败：" + ex.Message;
                    Logger.Error("请求 GitHub API 失败", ex);
                    return;
                }

                // 2. 解析 JSON，找第一个 .jar 资源的下载地址
                string? downloadUrl = null;
                string? releaseName = null;
                try
                {
                    using var doc = JsonDocument.Parse(jsonResponse);
                    if (doc.RootElement.TryGetProperty("name", out var nameProp))
                        releaseName = nameProp.GetString();

                    if (doc.RootElement.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (!asset.TryGetProperty("name", out var nameEl)) continue;
                            var name = nameEl.GetString();
                            if (string.IsNullOrEmpty(name)) continue;
                            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

                            if (asset.TryGetProperty("browser_download_url", out var urlEl))
                            {
                                downloadUrl = urlEl.GetString();
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DownloadStatus = "解析版本信息失败：" + ex.Message;
                    Logger.Error("解析 GitHub release JSON 失败", ex);
                    return;
                }

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    DownloadStatus = "未找到可下载的 jar 文件";
                    Logger.Warn("Terracotta release 中没有 .jar 资源");
                    return;
                }

                // 3. 确保目录存在
                var dir = Path.GetDirectoryName(TerracottaJarPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // 4. 用 HttpClient 流式下载，带进度报告
                DownloadStatus = $"正在下载 Terracotta{(!string.IsNullOrEmpty(releaseName) ? "（" + releaseName + "）" : "")}...";
                await DownloadFileWithProgressAsync(downloadUrl, TerracottaJarPath);

                // 5. 完成
                IsTerracottaReady = true;
                DownloadStatus = "Terracotta 已就绪";
                DownloadProgress = 100;
                Logger.Info($"Terracotta 下载完成：{TerracottaJarPath}");
            }
            catch (Exception ex)
            {
                Logger.Error("下载 Terracotta 失败", ex);
                DownloadStatus = "下载失败：" + ex.Message;
            }
            finally
            {
                IsDownloading = false;
            }
        }

        /// <summary>
        /// 用 HttpClient 流式下载文件，带进度报告。
        /// 下载到 .part 临时文件，成功后再重命名为目标文件，避免半成品。
        /// </summary>
        private async Task DownloadFileWithProgressAsync(string url, string targetPath)
        {
            var tempPath = targetPath + ".part";

            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            long downloadedBytes = 0;
            var buffer = new byte[81920];
            var lastReportPercent = 0.0;

            await using var networkStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                buffer.Length, useAsync: true);

            int read;
            while ((read = await networkStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                downloadedBytes += read;

                // 更新进度（间隔 1% 以上才更新，减少 UI 抖动）
                if (totalBytes > 0)
                {
                    var percent = (double)downloadedBytes / totalBytes * 100.0;
                    if (percent - lastReportPercent >= 1.0 || percent >= 100.0)
                    {
                        lastReportPercent = percent;
                        DownloadProgress = percent;
                    }
                }
            }

            await fileStream.FlushAsync();

            // 替换文件
            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tempPath, targetPath);

            DownloadProgress = 100;
        }

        /// <summary>启动联机命令（占位实现：弹 MessageBox 提示）</summary>
        [RelayCommand]
        private void LaunchMultiplayer()
        {
            if (!IsTerracottaReady)
            {
                MessageBox.Show("请先下载 Terracotta 客户端。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (SelectedVersion == null)
            {
                MessageBox.Show("请先选择一个 Minecraft 版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var addr = string.IsNullOrWhiteSpace(ServerAddress) ? "（未填写）" : ServerAddress;
            MessageBox.Show(
                "联机启动功能开发中，请先用 Terracotta 客户端配合使用。\n\n" +
                "选择版本：" + SelectedVersion.DisplayName + "\n" +
                "服务器地址：" + addr + "\n\n" +
                "Terracotta jar 路径：\n" + TerracottaJarPath,
                "联机启动",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
