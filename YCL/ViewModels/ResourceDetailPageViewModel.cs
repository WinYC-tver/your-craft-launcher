using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YCL.Core.Download;
using YCL.Core.Mods;
using YCL.Core.Resources;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Models;
using YCL.Models.Versions;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 资源详情页 ViewModel（微软商店风格）：
    /// 由 DownloadPage 在点击"详情"按钮时初始化并内嵌显示。
    ///
    /// 主要功能：
    /// 1. 显示资源大图、名称、作者、下载量、描述、文件大小、游戏版本、加载器要求
    /// 2. "立即获取"按钮：根据资源种类调用对应下载/安装服务
    ///    - 模组/资源包/光影包/数据包：下载到对应目录
    ///    - 整合包：调用 IModpackService 下载并安装
    ///    - 游戏本体：调用 IVersionManager 下载版本
    /// 3. 多线程下载（用 MultiThreadDownloader），显示进度+速度
    /// 4. 文件名按 AppConfig.ResourceFileNameFormat 格式化
    /// 5. 收藏/取消收藏（通过事件回调到 DownloadPageViewModel 处理）
    /// 6. 返回列表（通过事件回调到 DownloadPageViewModel）
    /// </summary>
    public partial class ResourceDetailPageViewModel : ViewModelBase
    {
        private readonly IModDownloadService _modDownloadService;
        private readonly IResourceService _resourceService;
        private readonly IModpackService _modpackService;
        private readonly IVersionManager _versionManager;
        private readonly IConfigService _configService;
        private readonly MultiThreadDownloader _multiThreadDownloader;

        /// <summary>下载任务取消令牌</summary>
        private CancellationTokenSource? _cts;

        public ResourceDetailPageViewModel(
            IModDownloadService modDownloadService,
            IResourceService resourceService,
            IModpackService modpackService,
            IVersionManager versionManager,
            IConfigService configService,
            MultiThreadDownloader multiThreadDownloader)
        {
            _modDownloadService = modDownloadService;
            _resourceService = resourceService;
            _modpackService = modpackService;
            _versionManager = versionManager;
            _configService = configService;
            _multiThreadDownloader = multiThreadDownloader;
        }

        /// <summary>当前展示的资源卡片（用于"立即获取"按钮获取原始数据）</summary>
        public ResourceCard? CurrentCard { get; private set; }

        /// <summary>当前文件名格式模板（由 DownloadPage 在 Initialize 时传入）</summary>
        public string FileNameFormat { get; private set; } = "{name}-{file}";

        // ====== 详情页字段 ======

        /// <summary>资源名称（大字标题）</summary>
        [ObservableProperty]
        private string _detailName = string.Empty;

        /// <summary>作者</summary>
        [ObservableProperty]
        private string _detailAuthor = "-";

        /// <summary>资源描述（纯文本，简单支持换行）</summary>
        [ObservableProperty]
        private string _detailDescription = "暂无描述";

        /// <summary>资源图标 URL（用于顶部 Banner 大图）</summary>
        [ObservableProperty]
        private string? _detailLogoUrl;

        /// <summary>下载量显示文字</summary>
        [ObservableProperty]
        private string _detailDownloadCount = "-";

        /// <summary>更新时间显示文字</summary>
        [ObservableProperty]
        private string _detailUpdatedTime = "-";

        /// <summary>来源显示文字（如 "Modrinth" / "Mojang 官方"）</summary>
        [ObservableProperty]
        private string _detailSource = "-";

        /// <summary>适用游戏版本（如 "1.20.4 / 1.20.1"）</summary>
        [ObservableProperty]
        private string _detailGameVersion = "-";

        /// <summary>加载器要求（如 "Fabric / Forge"）</summary>
        [ObservableProperty]
        private string _detailLoader = "-";

        /// <summary>文件大小显示（如 "2.3 MB"）</summary>
        [ObservableProperty]
        private string _detailFileSize = "-";

        /// <summary>资源种类显示文字（如 "模组" / "整合包" / "资源包"）</summary>
        [ObservableProperty]
        private string _detailKindDisplay = "-";

        /// <summary>是否已收藏</summary>
        [ObservableProperty]
        private bool _isFavorite;

        /// <summary>是否正在下载/安装</summary>
        [ObservableProperty]
        private bool _isDownloading;

        /// <summary>下载进度百分比（0~100，-1 表示不确定）</summary>
        [ObservableProperty]
        private double _downloadPercent = -1;

        /// <summary>下载速度显示文字（如 "2.5 MB/s"）</summary>
        [ObservableProperty]
        private string _downloadSpeedDisplay = "-";

        /// <summary>状态文字（显示当前操作状态）</summary>
        [ObservableProperty]
        private string _statusText = string.Empty;

        /// <summary>提示信息</summary>
        [ObservableProperty]
        private string? _hintMessage;

        // ====== 事件 ======

        /// <summary>请求返回列表事件（由 DownloadPageViewModel 监听以关闭详情页）</summary>
        public event EventHandler? BackRequested;

        /// <summary>收藏状态变化事件（由 DownloadPageViewModel 监听以同步收藏夹）</summary>
        public event EventHandler? FavoriteChanged;

        // ====== 初始化 ======

        /// <summary>
        /// 初始化详情页：从 ResourceCard 提取显示字段。
        /// </summary>
        /// <param name="card">资源卡片</param>
        /// <param name="format">当前文件名格式</param>
        public void Initialize(ResourceCard card, string format)
        {
            CurrentCard = card;
            FileNameFormat = format;

            DetailName = card.Name;
            DetailAuthor = string.IsNullOrEmpty(card.Author) ? "-" : card.Author;
            DetailDescription = string.IsNullOrEmpty(card.Description) ? "暂无描述" : card.Description;
            DetailLogoUrl = card.IconUrl;
            DetailDownloadCount = card.DownloadCountDisplay;
            DetailSource = string.IsNullOrEmpty(card.SourceDisplay) ? "-" : card.SourceDisplay;
            DetailKindDisplay = KindToDisplay(card.Kind);
            IsFavorite = card.IsFavorite;

            // 默认值，异步加载后会被覆盖
            DetailUpdatedTime = "-";
            DetailGameVersion = "-";
            DetailLoader = "-";
            DetailFileSize = "-";

            HintMessage = null;
            StatusText = string.Empty;
            DownloadPercent = -1;
            DownloadSpeedDisplay = "-";
            IsDownloading = false;

            // 异步加载版本/文件信息（仅对模组/整合包/资源类有意义）
            _ = LoadExtraInfoAsync(card);
        }

        /// <summary>把 ResourceKind 转换为中文显示文字</summary>
        private static string KindToDisplay(ResourceKind kind) => kind switch
        {
            ResourceKind.GameVersion => "游戏版本",
            ResourceKind.Modpack => "整合包",
            ResourceKind.Mod => "模组",
            ResourceKind.ResourcePack => "资源包",
            ResourceKind.ShaderPack => "光影包",
            ResourceKind.Datapack => "数据包",
            ResourceKind.World => "世界",
            _ => "未知"
        };

        /// <summary>异步加载额外信息（版本、文件大小、加载器等）</summary>
        private async Task LoadExtraInfoAsync(ResourceCard card)
        {
            try
            {
                if (card.Original is ModSearchResult mod)
                {
                    // 尝试获取最新版本信息
                    var versions = await _modDownloadService.GetVersionsAsync(mod);
                    var latest = versions.Count > 0 ? versions[0] : null;
                    if (latest != null)
                    {
                        DetailGameVersion = latest.GameVersions.Count > 0
                            ? string.Join(" / ", latest.GameVersions)
                            : "-";
                        DetailLoader = latest.Loaders.Count > 0
                            ? string.Join(" / ", latest.Loaders)
                            : "通用";
                        DetailUpdatedTime = string.IsNullOrEmpty(latest.DatePublished)
                            ? "-"
                            : latest.DatePublished;
                    }
                }
                else if (card.Original is VersionManifestEntry entry)
                {
                    DetailGameVersion = entry.Id ?? "-";
                    DetailLoader = "原版（无加载器）";
                    DetailUpdatedTime = entry.ReleaseTime ?? "-";
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"加载资源详情失败（不影响基础显示）：{ex.Message}");
            }
        }

        // ====== 命令 ======

        /// <summary>返回列表命令</summary>
        [RelayCommand]
        private void Back()
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>切换收藏状态命令</summary>
        [RelayCommand]
        private void ToggleFavorite()
        {
            if (CurrentCard == null) return;
            IsFavorite = !IsFavorite;
            CurrentCard.IsFavorite = IsFavorite;
            FavoriteChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>立即获取命令：根据资源种类执行下载或安装</summary>
        [RelayCommand]
        private async Task GetResourceAsync()
        {
            if (CurrentCard == null)
            {
                HintMessage = "未选择资源。";
                return;
            }

            if (IsDownloading)
            {
                HintMessage = "正在处理中，请等待完成。";
                return;
            }

            IsDownloading = true;
            DownloadPercent = 0;
            _cts = new CancellationTokenSource();

            try
            {
                var gameDir = ResolveGameDir();
                if (string.IsNullOrEmpty(gameDir))
                {
                    HintMessage = "未配置 .minecraft 路径，请在设置中先配置。";
                    return;
                }

                switch (CurrentCard.Kind)
                {
                    case ResourceKind.GameVersion:
                        await DownloadGameVersionAsync(gameDir);
                        break;
                    case ResourceKind.Modpack:
                        await DownloadAndInstallModpackAsync(gameDir);
                        break;
                    case ResourceKind.Mod:
                        await DownloadModAsync(gameDir);
                        break;
                    case ResourceKind.ResourcePack:
                        await DownloadResourceAsync(gameDir, "resourcepacks");
                        break;
                    case ResourceKind.ShaderPack:
                        await DownloadResourceAsync(gameDir, "shaderpacks");
                        break;
                    case ResourceKind.Datapack:
                        await DownloadResourceAsync(gameDir, "datapacks");
                        break;
                    case ResourceKind.World:
                        await DownloadWorldAsync(gameDir);
                        break;
                    default:
                        HintMessage = "未知资源种类，无法下载。";
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "已取消";
                HintMessage = "下载/安装已取消。";
                DownloadPercent = -1;
            }
            catch (Exception ex)
            {
                Logger.Error("资源下载失败", ex);
                StatusText = "失败";
                HintMessage = "失败：" + ex.Message;
            }
            finally
            {
                IsDownloading = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>取消下载命令</summary>
        [RelayCommand]
        private void CancelDownload()
        {
            _cts?.Cancel();
            StatusText = "正在取消...";
        }

        // ====== 下载实现 ======

        /// <summary>解析当前游戏目录（启用隔离时返回 .minecraft 根目录，下载到根的子目录）</summary>
        private string ResolveGameDir()
        {
            var path = _configService.Current.MinecraftPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    ".minecraft");
            }
            return path;
        }

        /// <summary>下载游戏版本（调用 IVersionManager.InstallVersionAsync）</summary>
        private async Task DownloadGameVersionAsync(string gameDir)
        {
            if (CurrentCard?.Original is not VersionManifestEntry entry)
            {
                HintMessage = "无法获取版本信息。";
                return;
            }

            StatusText = $"正在安装版本：{entry.Id}...";
            var progress = new Progress<InstallProgress>(p =>
            {
                DownloadPercent = p.Percent;
                StatusText = $"{p.PhaseText}：{p.CurrentFile}" +
                             (p.TotalFiles > 0 ? $"（{p.CompletedFiles}/{p.TotalFiles}）" : "");
                if (p.TotalBytes > 0)
                {
                    DetailFileSize = FormatBytes(p.TotalBytes);
                }
            });

            var success = await _versionManager.InstallVersionAsync(entry, progress, _cts!.Token);
            if (success)
            {
                DownloadPercent = 100;
                StatusText = "安装完成！";
                HintMessage = $"版本 {entry.Id} 已成功安装到 {gameDir}";
            }
            else
            {
                StatusText = "安装失败";
                HintMessage = "安装未完全成功，请查看日志。";
            }
        }

        /// <summary>下载并安装整合包</summary>
        private async Task DownloadAndInstallModpackAsync(string gameDir)
        {
            if (CurrentCard?.Original is not ModSearchResult mod)
            {
                HintMessage = "无法获取整合包信息。";
                return;
            }

            StatusText = "正在获取整合包下载地址...";
            var versions = await _modDownloadService.GetVersionsAsync(mod, _cts!.Token);
            if (versions.Count == 0 || string.IsNullOrEmpty(versions[0].DownloadUrl))
            {
                HintMessage = "无法获取整合包下载 URL。";
                return;
            }

            var latestVersion = versions[0];
            var tempZip = Path.Combine(Path.GetTempPath(), "YCL_Modpack_" + Guid.NewGuid().ToString("N") + ".zip");

            try
            {
                // 1. 下载整合包 zip（占 30% 进度）
                StatusText = "正在下载整合包...";
                var downloadProgress = new Progress<DownloadProgressEventArgs>(p =>
                {
                    DownloadPercent = p.Percent * 0.3;
                    DownloadSpeedDisplay = p.BytesPerSecond > 0 ? FormatBytes((long)p.BytesPerSecond) + "/s" : "-";
                    if (p.TotalBytes > 0) DetailFileSize = FormatBytes(p.TotalBytes);
                    StatusText = $"下载整合包：{p.Percent:F1}%";
                });
                await _modpackService.DownloadModpackAsync(
                    latestVersion.DownloadUrl, tempZip, downloadProgress, _cts.Token);

                // 2. 安装整合包（占 70% 进度）
                StatusText = "正在安装整合包...";
                var versionId = mod.Name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var installProgress = new Progress<ModpackInstallProgress>(p =>
                {
                    DownloadPercent = 30 + p.Percent * 0.7;
                    StatusText = $"{p.PhaseText}：{p.CurrentFile}" +
                                 (p.TotalFiles > 0 ? $"（{p.CompletedFiles}/{p.TotalFiles}）" : "");
                });

                var success = await _modpackService.InstallModpackAsync(
                    tempZip, gameDir, versionId, installProgress, _cts.Token);

                if (success)
                {
                    DownloadPercent = 100;
                    StatusText = "整合包安装完成！";
                    HintMessage = $"整合包 {mod.Name} 已安装为新版本 {versionId}。";
                }
                else
                {
                    StatusText = "整合包安装失败";
                    HintMessage = "整合包安装未完全成功，请查看日志。";
                }
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* 忽略 */ }
            }
        }

        /// <summary>下载模组到 mods 目录</summary>
        private async Task DownloadModAsync(string gameDir)
        {
            if (CurrentCard?.Original is not ModSearchResult mod)
            {
                HintMessage = "无法获取模组信息。";
                return;
            }

            StatusText = $"正在下载模组：{mod.Name}...";
            var progress = new Progress<DownloadProgressEventArgs>(p =>
            {
                DownloadPercent = p.Percent;
                DownloadSpeedDisplay = p.BytesPerSecond > 0 ? FormatBytes((long)p.BytesPerSecond) + "/s" : "-";
                if (p.TotalBytes > 0) DetailFileSize = FormatBytes(p.TotalBytes);
                StatusText = $"下载中：{p.Percent:F1}%";
            });

            var downloadedPath = await _modDownloadService.DownloadModAsync(
                mod, gameDir, progress, _cts!.Token);

            // 应用文件名格式（下载后重命名）
            var renamedPath = ApplyFileNameFormat(downloadedPath, mod.Name);
            DownloadPercent = 100;
            StatusText = "下载完成！";
            HintMessage = $"已下载到：{renamedPath}";
        }

        /// <summary>下载资源（资源包/光影包/数据包）到指定子目录</summary>
        private async Task DownloadResourceAsync(string gameDir, string subDir)
        {
            if (CurrentCard?.Original is not ModSearchResult res)
            {
                HintMessage = "无法获取资源信息。";
                return;
            }

            // 数据包走自己的下载流程（IResourceService 没有专门的数据包方法）
            if (CurrentCard.Kind == ResourceKind.Datapack)
            {
                await DownloadDatapackAsync(res, gameDir, subDir);
                return;
            }

            StatusText = $"正在下载{DetailKindDisplay}：{res.Name}...";
            var progress = new Progress<DownloadProgressEventArgs>(p =>
            {
                DownloadPercent = p.Percent;
                DownloadSpeedDisplay = p.BytesPerSecond > 0 ? FormatBytes((long)p.BytesPerSecond) + "/s" : "-";
                if (p.TotalBytes > 0) DetailFileSize = FormatBytes(p.TotalBytes);
                StatusText = $"下载中：{p.Percent:F1}%";
            });

            string downloadedPath;
            if (CurrentCard.Kind == ResourceKind.ResourcePack)
            {
                downloadedPath = await _resourceService.DownloadResourcePackAsync(res, gameDir, progress, _cts!.Token);
            }
            else if (CurrentCard.Kind == ResourceKind.ShaderPack)
            {
                downloadedPath = await _resourceService.DownloadShaderPackAsync(res, gameDir, progress, _cts!.Token);
            }
            else
            {
                downloadedPath = await _resourceService.DownloadMapAsync(res, gameDir, progress, _cts!.Token);
            }

            var renamedPath = ApplyFileNameFormat(downloadedPath, res.Name);
            DownloadPercent = 100;
            StatusText = "下载完成！";
            HintMessage = $"已下载到：{renamedPath}";
        }

        /// <summary>下载数据包（直接通过 MultiThreadDownloader 下载到 datapacks/）</summary>
        private async Task DownloadDatapackAsync(ModSearchResult res, string gameDir, string subDir)
        {
            // 获取最新版本下载 URL
            var versions = await _modDownloadService.GetVersionsAsync(res, _cts!.Token);
            if (versions.Count == 0 || string.IsNullOrEmpty(versions[0].DownloadUrl))
            {
                HintMessage = "无法获取数据包下载 URL。";
                return;
            }

            var latestVersion = versions[0];
            var targetDir = Path.Combine(gameDir, subDir);
            Directory.CreateDirectory(targetDir);

            var fileName = string.IsNullOrEmpty(latestVersion.FileName)
                ? res.Name + ".zip"
                : latestVersion.FileName;
            // 清理非法字符
            foreach (var c in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');

            var targetPath = Path.Combine(targetDir, fileName);
            if (File.Exists(targetPath)) File.Delete(targetPath);

            StatusText = $"正在下载数据包：{res.Name}...";

            EventHandler<DownloadProgressEventArgs> handler = (s, e) =>
            {
                DownloadPercent = e.Percent;
                DownloadSpeedDisplay = e.BytesPerSecond > 0 ? FormatBytes((long)e.BytesPerSecond) + "/s" : "-";
                if (e.TotalBytes > 0) DetailFileSize = FormatBytes(e.TotalBytes);
                StatusText = $"下载中：{e.Percent:F1}%";
            };
            _multiThreadDownloader.ProgressChanged += handler;
            try
            {
                await _multiThreadDownloader.DownloadAsync(latestVersion.DownloadUrl, targetPath, _cts.Token);
            }
            finally
            {
                _multiThreadDownloader.ProgressChanged -= handler;
            }

            var renamedPath = ApplyFileNameFormat(targetPath, res.Name);
            DownloadPercent = 100;
            StatusText = "下载完成！";
            HintMessage = $"已下载到：{renamedPath}";
        }

        /// <summary>下载世界并解压到 saves/</summary>
        private async Task DownloadWorldAsync(string gameDir)
        {
            if (CurrentCard?.Original is not ModSearchResult res)
            {
                HintMessage = "无法获取世界信息。";
                return;
            }

            StatusText = $"正在下载世界：{res.Name}...";
            var progress = new Progress<DownloadProgressEventArgs>(p =>
            {
                DownloadPercent = p.Percent;
                DownloadSpeedDisplay = p.BytesPerSecond > 0 ? FormatBytes((long)p.BytesPerSecond) + "/s" : "-";
                if (p.TotalBytes > 0) DetailFileSize = FormatBytes(p.TotalBytes);
                StatusText = $"下载中：{p.Percent:F1}%";
            });

            var downloadedPath = await _resourceService.DownloadMapAsync(res, gameDir, progress, _cts!.Token);
            DownloadPercent = 100;
            StatusText = "下载完成！";
            HintMessage = $"世界已解压到：{downloadedPath}";
        }

        // ====== 文件名格式 ======

        /// <summary>根据当前文件名格式重命名已下载的文件（失败时保留原文件名）</summary>
        private string ApplyFileNameFormat(string downloadedPath, string resourceName)
        {
            try
            {
                if (string.IsNullOrEmpty(downloadedPath) || !File.Exists(downloadedPath))
                    return downloadedPath;

                var dir = Path.GetDirectoryName(downloadedPath) ?? string.Empty;
                var ext = Path.GetExtension(downloadedPath);
                var fileId = Path.GetFileNameWithoutExtension(downloadedPath);

                // 用模板替换新文件名（不带扩展名）：{name}=资源名称，{file}=原文件名
                var template = FileNameFormat;
                if (string.IsNullOrEmpty(template)) template = "{name}-{file}";
                var newName = template.Replace("{name}", resourceName).Replace("{file}", fileId);

                var newPath = Path.Combine(dir, newName + ext);
                if (string.Equals(newPath, downloadedPath, StringComparison.OrdinalIgnoreCase))
                    return downloadedPath;

                if (File.Exists(newPath)) File.Delete(newPath);
                File.Move(downloadedPath, newPath);
                return newPath;
            }
            catch (Exception ex)
            {
                Logger.Warn($"应用文件名格式失败，保留原文件名：{ex.Message}");
                return downloadedPath;
            }
        }

        /// <summary>把字节数格式化为人类可读字符串（如 "1.2 GB"）</summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "-";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GB";
        }
    }
}
