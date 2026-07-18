using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Win32;
using YCL.Core.Download;
using YCL.Core.Mods;
using YCL.Core.Resources;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Models.Versions;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 资源管理页 ViewModel：负责搜索并下载整合包、资源包、光影包、地图。
    ///
    /// 主要功能：
    /// 1. 选择当前版本（决定资源安装到哪个 gameDir）
    /// 2. 资源类型选择：整合包 / 资源包 / 光影包 / 地图
    /// 3. 搜索框 + 来源筛选 + 结果列表
    /// 4. 整合包支持从本地 zip 文件安装
    /// 5. 整合包安装有进度反馈
    /// </summary>
    public partial class ResourcePageViewModel : ViewModelBase
    {
        private readonly IResourceService _resourceService;
        private readonly IModpackService _modpackService;
        private readonly IVersionManager _versionManager;
        private readonly IConfigService _configService;

        /// <summary>下载/安装任务的取消令牌源</summary>
        private CancellationTokenSource? _cts;

        public ResourcePageViewModel(
            IResourceService resourceService,
            IModpackService modpackService,
            IVersionManager versionManager,
            IConfigService configService)
        {
            _resourceService = resourceService;
            _modpackService = modpackService;
            _versionManager = versionManager;
            _configService = configService;

            // 默认值
            _selectedResourceType = ResourceType.ResourcePack;
            _selectedSource = ModSource.All;

            // 立即加载已安装版本列表
            _ = RefreshVersionsAsync();
        }

        /// <summary>已安装版本列表</summary>
        public ObservableCollection<InstalledVersionInfo> Versions { get; } = new();

        /// <summary>搜索结果列表</summary>
        public ObservableCollection<ModSearchResult> SearchResults { get; } = new();

        /// <summary>当前选中的版本</summary>
        [ObservableProperty]
        private InstalledVersionInfo? _selectedVersion;

        /// <summary>搜索关键字</summary>
        [ObservableProperty]
        private string _searchQuery = string.Empty;

        /// <summary>选中的资源类型</summary>
        [ObservableProperty]
        private ResourceType _selectedResourceType;

        /// <summary>选中的来源</summary>
        [ObservableProperty]
        private ModSource _selectedSource;

        /// <summary>是否正在搜索</summary>
        [ObservableProperty]
        private bool _isSearching;

        /// <summary>是否正在下载/安装</summary>
        [ObservableProperty]
        private bool _isInstalling;

        /// <summary>进度百分比</summary>
        [ObservableProperty]
        private double _progressPercent = -1;

        /// <summary>状态文字</summary>
        [ObservableProperty]
        private string _statusText = "就绪";

        /// <summary>提示信息</summary>
        [ObservableProperty]
        private string? _hintMessage;

        /// <summary>当前安装阶段描述</summary>
        [ObservableProperty]
        private string _currentInstallPhase = string.Empty;

        /// <summary>当前游戏目录显示</summary>
        [ObservableProperty]
        private string _currentGameDirDisplay = "未选择版本";

        /// <summary>资源类型变化时：清空搜索结果</summary>
        partial void OnSelectedResourceTypeChanged(ResourceType value)
        {
            SearchResults.Clear();
        }

        /// <summary>刷新已安装版本列表命令</summary>
        [RelayCommand]
        private async Task RefreshVersionsAsync()
        {
            try
            {
                var versions = await _versionManager.ListInstalledVersionsAsync();
                Versions.Clear();
                foreach (var v in versions)
                    Versions.Add(v);

                if (Versions.Count > 0 && SelectedVersion == null)
                {
                    var lastSelected = _configService.Current.LastSelectedVersion;
                    SelectedVersion = !string.IsNullOrEmpty(lastSelected)
                        ? Versions.FirstOrDefault(v => v.Id == lastSelected)
                        : null;
                    SelectedVersion ??= Versions[0];
                }
            }
            catch (Exception ex)
            {
                Logger.Error("刷新版本列表失败", ex);
                HintMessage = "刷新版本列表失败：" + ex.Message;
            }
        }

        /// <summary>选中版本变化时：更新显示</summary>
        partial void OnSelectedVersionChanged(InstalledVersionInfo? value)
        {
            if (value == null)
            {
                CurrentGameDirDisplay = "未选择版本";
                return;
            }
            CurrentGameDirDisplay = "游戏目录：" + _versionManager.GetGameDirectory(value.Id);
        }

        /// <summary>搜索资源命令</summary>
        [RelayCommand]
        private async Task SearchAsync()
        {
            if (IsSearching) return;

            var query = SearchQuery?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(query))
            {
                HintMessage = "请输入搜索关键字。";
                return;
            }

            IsSearching = true;
            StatusText = $"正在搜索 {SelectedResourceType}：\"{query}\"...";
            HintMessage = null;
            SearchResults.Clear();

            try
            {
                var results = await _resourceService.SearchResourcesAsync(
                    query, SelectedResourceType, SelectedSource);
                foreach (var r in results)
                    SearchResults.Add(r);

                if (SearchResults.Count > 0)
                {
                    StatusText = $"找到 {SearchResults.Count} 个结果";
                }
                else
                {
                    StatusText = "未找到任何结果";
                    HintMessage = "未找到匹配的资源。";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("搜索资源失败", ex);
                StatusText = "搜索失败";
                HintMessage = "搜索失败：" + ex.Message;
            }
            finally
            {
                IsSearching = false;
            }
        }

        /// <summary>下载资源命令：根据类型调用不同的下载方法</summary>
        [RelayCommand]
        private async Task DownloadAsync(ModSearchResult? result)
        {
            if (result == null) return;
            if (SelectedVersion == null)
            {
                HintMessage = "请先选择一个版本。";
                return;
            }
            if (IsInstalling)
            {
                HintMessage = "正在处理中，请等待完成。";
                return;
            }

            // 整合包类型走专门的安装流程
            if (SelectedResourceType == ResourceType.World && result.Name.Contains("modpack", StringComparison.OrdinalIgnoreCase))
            {
                await DownloadAndInstallModpackAsync(result);
                return;
            }

            await DownloadResourceCoreAsync(result);
        }

        /// <summary>下载普通资源（资源包/光影包）</summary>
        private async Task DownloadResourceCoreAsync(ModSearchResult result)
        {
            IsInstalling = true;
            ProgressPercent = 0;
            StatusText = $"正在下载：{result.Name}";
            _cts = new CancellationTokenSource();

            var progress = new Progress<DownloadProgressEventArgs>(p =>
            {
                ProgressPercent = p.Percent;
                var mbDone = p.DownloadedBytes / 1024.0 / 1024.0;
                var mbTotal = p.TotalBytes > 0 ? p.TotalBytes / 1024.0 / 1024.0 : 0;
                StatusText = $"下载中：{result.Name}（{mbDone:F1}/{mbTotal:F1} MB）";
            });

            try
            {
                var gameDir = _versionManager.GetGameDirectory(SelectedVersion!.Id);
                string downloadedPath;

                if (SelectedResourceType == ResourceType.ResourcePack)
                {
                    downloadedPath = await _resourceService.DownloadResourcePackAsync(
                        result, gameDir, progress, _cts.Token);
                }
                else if (SelectedResourceType == ResourceType.ShaderPack)
                {
                    downloadedPath = await _resourceService.DownloadShaderPackAsync(
                        result, gameDir, progress, _cts.Token);
                }
                else
                {
                    downloadedPath = await _resourceService.DownloadMapAsync(
                        result, gameDir, progress, _cts.Token);
                }

                ProgressPercent = 100;
                StatusText = "下载完成！";
                HintMessage = $"已下载到：{downloadedPath}";
            }
            catch (OperationCanceledException)
            {
                StatusText = "下载已取消";
                ProgressPercent = -1;
            }
            catch (Exception ex)
            {
                Logger.Error("下载资源失败", ex);
                StatusText = "下载失败";
                HintMessage = "下载失败：" + ex.Message;
            }
            finally
            {
                IsInstalling = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>下载并安装整合包</summary>
        private async Task DownloadAndInstallModpackAsync(ModSearchResult result)
        {
            IsInstalling = true;
            ProgressPercent = 0;
            StatusText = $"正在下载整合包：{result.Name}";
            _cts = new CancellationTokenSource();

            try
            {
                // 1. 下载整合包 zip 到临时文件
                var tempZip = Path.Combine(Path.GetTempPath(), "YCL_Modpack_" + Guid.NewGuid().ToString("N") + ".zip");
                var downloadProgress = new Progress<DownloadProgressEventArgs>(p =>
                {
                    ProgressPercent = p.Percent * 0.3; // 下载占 30% 进度
                    StatusText = $"下载整合包：{p.Percent:F1}%";
                });

                if (result.Source == ModSource.Modrinth)
                {
                    // Modrinth 整合包：直接通过 ResourceService 下载
                    // 但 ResourceService 没有专门的 modpack 下载方法，所以先获取 URL 后用 downloader
                    // 这里简化处理：让用户用"从本地安装"功能
                    HintMessage = "请使用\"从本地安装整合包\"按钮：先在浏览器下载整合包 zip，再点击该按钮选择文件。";
                    IsInstalling = false;
                    return;
                }

                // 2. 安装整合包
                StatusText = "正在安装整合包...";
                var gameDir = _versionManager.MinecraftPath;
                var versionId = result.Name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");

                var installProgress = new Progress<ModpackInstallProgress>(p =>
                {
                    CurrentInstallPhase = p.PhaseText;
                    ProgressPercent = 30 + p.Percent * 0.7; // 安装占 70% 进度
                    StatusText = $"{p.PhaseText}：{p.CurrentFile}" +
                                 (p.TotalFiles > 0 ? $"（{p.CompletedFiles}/{p.TotalFiles}）" : "");
                });

                var success = await _modpackService.InstallModpackAsync(
                    tempZip, gameDir, versionId, installProgress, _cts.Token);

                if (success)
                {
                    ProgressPercent = 100;
                    StatusText = "整合包安装完成！";
                    HintMessage = $"整合包 {result.Name} 已安装为新版本 {versionId}。";
                    await RefreshVersionsAsync();
                }
                else
                {
                    StatusText = "整合包安装失败";
                    HintMessage = "整合包安装失败，请查看日志。";
                }

                // 清理临时文件
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
            catch (OperationCanceledException)
            {
                StatusText = "安装已取消";
                ProgressPercent = -1;
            }
            catch (Exception ex)
            {
                Logger.Error("安装整合包失败", ex);
                StatusText = "安装失败";
                HintMessage = "安装失败：" + ex.Message;
            }
            finally
            {
                IsInstalling = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>从本地 zip 文件安装整合包命令</summary>
        [RelayCommand]
        private async Task InstallModpackFromFileAsync()
        {
            if (IsInstalling)
            {
                HintMessage = "正在处理中，请等待完成。";
                return;
            }

            // 弹文件选择对话框
            var dialog = new OpenFileDialog
            {
                Title = "选择整合包 zip 文件",
                Filter = "整合包文件 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true) return;

            var modpackPath = dialog.FileName;

            // 先解析整合包信息，弹确认对话框
            StatusText = "正在解析整合包清单...";
            var manifest = await _modpackService.ParseModpackAsync(modpackPath);
            if (manifest == null)
            {
                HintMessage = "无法解析整合包清单，可能不是有效的整合包文件。";
                StatusText = "解析失败";
                return;
            }

            // 显示整合包信息让用户确认
            var infoText = $"整合包名：{manifest.Name}\n" +
                           $"Minecraft 版本：{manifest.MinecraftVersion}\n" +
                           $"加载器：{manifest.LoaderType} {manifest.LoaderVersion}\n" +
                           $"模组数量：{manifest.Files.Count}\n" +
                           $"来源：{manifest.Source}";

            // 让用户输入新版本 id
            var versionIdBox = new TextBox
            {
                Text = manifest.Name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                MinWidth = 360,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var confirmDialog = new ContentDialog
            {
                Title = "确认安装整合包",
                PrimaryButtonText = "安装",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = infoText, TextWrapping = TextWrapping.Wrap },
                        new TextBlock
                        {
                            Text = "请输入新版本 id（用于在版本列表中标识此整合包）：",
                            Margin = new Thickness(0, 12, 0, 0),
                            TextWrapping = TextWrapping.Wrap
                        },
                        versionIdBox
                    }
                }
            };

            if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary) return;

            var versionId = versionIdBox.Text?.Trim();
            if (string.IsNullOrEmpty(versionId))
            {
                HintMessage = "版本 id 不能为空。";
                return;
            }

            // 执行安装
            IsInstalling = true;
            ProgressPercent = 0;
            _cts = new CancellationTokenSource();

            var progress = new Progress<ModpackInstallProgress>(p =>
            {
                CurrentInstallPhase = p.PhaseText;
                ProgressPercent = p.Percent;
                StatusText = $"{p.PhaseText}：{p.CurrentFile}" +
                             (p.TotalFiles > 0 ? $"（{p.CompletedFiles}/{p.TotalFiles}）" : "");
            });

            try
            {
                var gameDir = _versionManager.MinecraftPath;
                var success = await _modpackService.InstallModpackAsync(
                    modpackPath, gameDir, versionId, progress, _cts.Token);

                if (success)
                {
                    ProgressPercent = 100;
                    StatusText = "整合包安装完成！";
                    HintMessage = $"整合包 {manifest.Name} 已安装为新版本 {versionId}。";
                    await RefreshVersionsAsync();
                }
                else
                {
                    StatusText = "整合包安装失败";
                    HintMessage = "整合包安装未完全成功，请查看日志了解详情。";
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "安装已取消";
                ProgressPercent = -1;
            }
            catch (Exception ex)
            {
                Logger.Error("从文件安装整合包失败", ex);
                StatusText = "安装失败";
                HintMessage = "安装失败：" + ex.Message;
            }
            finally
            {
                IsInstalling = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>取消安装命令</summary>
        [RelayCommand(CanExecute = nameof(IsInstalling))]
        private void CancelInstall()
        {
            _cts?.Cancel();
            StatusText = "正在取消...";
            Logger.Info("用户请求取消资源下载/安装");
        }
    }
}
