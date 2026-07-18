using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using YCL.Core.Download;
using YCL.Core.Mods;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Models.Versions;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 模组管理页 ViewModel：负责本地模组管理与在线模组搜索下载。
    ///
    /// 主要功能：
    /// 1. 选择当前管理的版本（下拉框，从已安装版本列表选）
    /// 2. 本地模组 Tab：扫描 mods 文件夹，卡片式展示，含启用开关、删除、打开文件夹
    /// 3. 在线搜索 Tab：搜索框 + 来源筛选 + 结果列表 + 下载到当前版本 mods/
    /// </summary>
    public partial class ModPageViewModel : ViewModelBase
    {
        private readonly ILocalModManager _localModManager;
        private readonly IModDownloadService _modDownloadService;
        private readonly IVersionManager _versionManager;
        private readonly IConfigService _configService;

        /// <summary>下载任务的取消令牌源</summary>
        private CancellationTokenSource? _downloadCts;

        public ModPageViewModel(
            ILocalModManager localModManager,
            IModDownloadService modDownloadService,
            IVersionManager versionManager,
            IConfigService configService)
        {
            _localModManager = localModManager;
            _modDownloadService = modDownloadService;
            _versionManager = versionManager;
            _configService = configService;

            // 默认搜索源 = All（CurseForge + Modrinth）
            _selectedSource = ModSource.All;

            // 立即异步加载已安装版本列表
            _ = RefreshVersionsAsync();
        }

        /// <summary>已安装版本列表（用于下拉框选择）</summary>
        public ObservableCollection<InstalledVersionInfo> Versions { get; } = new();

        /// <summary>本地模组列表</summary>
        public ObservableCollection<ModInfo> LocalMods { get; } = new();

        /// <summary>在线搜索结果列表</summary>
        public ObservableCollection<ModSearchResult> SearchResults { get; } = new();

        /// <summary>当前选中的版本</summary>
        [ObservableProperty]
        private InstalledVersionInfo? _selectedVersion;

        /// <summary>搜索关键字</summary>
        [ObservableProperty]
        private string _searchQuery = string.Empty;

        /// <summary>选中的搜索来源</summary>
        [ObservableProperty]
        private ModSource _selectedSource;

        /// <summary>是否正在扫描本地模组</summary>
        [ObservableProperty]
        private bool _isScanning;

        /// <summary>是否正在搜索在线模组</summary>
        [ObservableProperty]
        private bool _isSearching;

        /// <summary>是否正在下载模组</summary>
        [ObservableProperty]
        private bool _isDownloading;

        /// <summary>下载进度百分比（0~100，-1 表示不确定）</summary>
        [ObservableProperty]
        private double _downloadPercent = -1;

        /// <summary>状态文字</summary>
        [ObservableProperty]
        private string _statusText = "就绪";

        /// <summary>提示信息</summary>
        [ObservableProperty]
        private string? _hintMessage;

        /// <summary>当前下载的模组文件名</summary>
        [ObservableProperty]
        private string _currentDownloadFile = string.Empty;

        /// <summary>当前游戏目录路径（用于显示）</summary>
        [ObservableProperty]
        private string _currentGameDirDisplay = "未选择版本";

        /// <summary>选中的版本变化时：刷新本地模组列表</summary>
        partial void OnSelectedVersionChanged(InstalledVersionInfo? value)
        {
            if (value == null)
            {
                CurrentGameDirDisplay = "未选择版本";
                LocalMods.Clear();
                return;
            }
            CurrentGameDirDisplay = "游戏目录：" + _versionManager.GetGameDirectory(value.Id);
            _ = RefreshLocalModsAsync();
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

                // 默认选中配置中保存的 LastSelectedVersion，否则选第一个
                if (Versions.Count > 0 && SelectedVersion == null)
                {
                    var lastSelected = _configService.Current.LastSelectedVersion;
                    SelectedVersion = !string.IsNullOrEmpty(lastSelected)
                        ? Versions.FirstOrDefault(v => v.Id == lastSelected)
                        : null;
                    SelectedVersion ??= Versions[0];
                }
                else if (Versions.Count == 0)
                {
                    HintMessage = "尚未安装任何 Minecraft 版本。请先到主页安装一个版本。";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("刷新版本列表失败", ex);
                HintMessage = "刷新版本列表失败：" + ex.Message;
            }
        }

        /// <summary>刷新本地模组列表命令</summary>
        [RelayCommand]
        private async Task RefreshLocalModsAsync()
        {
            if (SelectedVersion == null)
            {
                HintMessage = "请先选择一个版本。";
                return;
            }

            if (IsScanning) return;
            IsScanning = true;
            StatusText = "正在扫描 mods 文件夹...";

            try
            {
                var gameDir = _versionManager.GetGameDirectory(SelectedVersion.Id);
                var mods = await Task.Run(() => _localModManager.ListMods(gameDir));

                LocalMods.Clear();
                foreach (var m in mods)
                    LocalMods.Add(m);

                if (LocalMods.Count > 0)
                {
                    StatusText = $"扫描到 {LocalMods.Count} 个模组";
                    HintMessage = null;
                }
                else
                {
                    StatusText = "mods 文件夹为空";
                    HintMessage = "未找到任何模组。可以切到\"在线搜索\"Tab 下载模组。";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("扫描本地模组失败", ex);
                StatusText = "扫描失败";
                HintMessage = "扫描模组失败：" + ex.Message;
            }
            finally
            {
                IsScanning = false;
            }
        }

        /// <summary>切换模组启用状态命令</summary>
        [RelayCommand]
        private void ToggleMod(ModInfo? mod)
        {
            if (mod == null) return;
            try
            {
                _localModManager.ToggleMod(mod.FilePath, !mod.Enabled);
                mod.Enabled = !mod.Enabled;
                // 重新扫描以更新文件路径（重命名后 FilePath 会变）
                _ = RefreshLocalModsAsync();
                StatusText = $"已{(mod.Enabled ? "启用" : "禁用")}模组：{mod.DisplayName}";
            }
            catch (Exception ex)
            {
                Logger.Error("切换模组状态失败", ex);
                HintMessage = "切换失败：" + ex.Message;
            }
        }

        /// <summary>删除模组命令（带二次确认）</summary>
        [RelayCommand]
        private async Task DeleteModAsync(ModInfo? mod)
        {
            if (mod == null) return;

            var dialog = new ContentDialog
            {
                Title = "确认删除模组",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                Content = new TextBlock
                {
                    Text = $"确定要删除模组 {mod.DisplayName} 吗？\n\n此操作不可撤销。",
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            try
            {
                _localModManager.DeleteMod(mod.FilePath);
                LocalMods.Remove(mod);
                StatusText = $"已删除模组：{mod.DisplayName}";
            }
            catch (Exception ex)
            {
                Logger.Error("删除模组失败", ex);
                HintMessage = "删除失败：" + ex.Message;
            }
        }

        /// <summary>打开 mods 文件夹命令</summary>
        [RelayCommand]
        private void OpenModsFolder()
        {
            if (SelectedVersion == null)
            {
                HintMessage = "请先选择一个版本。";
                return;
            }
            try
            {
                var gameDir = _versionManager.GetGameDirectory(SelectedVersion.Id);
                _localModManager.OpenModsFolder(gameDir);
            }
            catch (Exception ex)
            {
                Logger.Error("打开 mods 文件夹失败", ex);
                HintMessage = "打开文件夹失败：" + ex.Message;
            }
        }

        /// <summary>搜索在线模组命令</summary>
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
            StatusText = $"正在搜索 \"{query}\"（来源：{SelectedSource}）...";
            HintMessage = null;
            SearchResults.Clear();

            try
            {
                var results = await _modDownloadService.SearchAsync(query, SelectedSource);
                foreach (var r in results)
                    SearchResults.Add(r);

                if (SearchResults.Count > 0)
                {
                    StatusText = $"找到 {SearchResults.Count} 个结果";
                }
                else
                {
                    StatusText = "未找到任何结果";
                    HintMessage = "未找到匹配的模组。可以试试其他关键字或切换搜索来源。";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("搜索模组失败", ex);
                StatusText = "搜索失败";
                HintMessage = "搜索失败：" + ex.Message;
            }
            finally
            {
                IsSearching = false;
            }
        }

        /// <summary>下载模组命令：弹版本选择对话框，让用户选具体版本下载</summary>
        [RelayCommand]
        private async Task DownloadModAsync(ModSearchResult? result)
        {
            if (result == null) return;
            if (SelectedVersion == null)
            {
                HintMessage = "请先选择一个版本，再下载模组。";
                return;
            }
            if (IsDownloading)
            {
                HintMessage = "正在下载中，请等待完成。";
                return;
            }

            try
            {
                // 获取该模组的所有版本
                StatusText = $"正在获取 {result.Name} 的版本列表...";
                var versions = await _modDownloadService.GetVersionsAsync(result);
                if (versions.Count == 0)
                {
                    HintMessage = $"未找到 {result.Name} 的可下载版本。";
                    StatusText = "无可下载版本";
                    return;
                }

                // 弹版本选择对话框
                var versionList = new ListBox
                {
                    MinHeight = 280,
                    MaxHeight = 400,
                    Margin = new Thickness(0, 8, 0, 0),
                    ItemsSource = versions,
                    DisplayMemberPath = nameof(ModVersionInfo.DisplayText)
                };
                versionList.SelectedIndex = 0;

                var dialog = new ContentDialog
                {
                    Title = $"下载 {result.Name}",
                    PrimaryButtonText = "下载",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"共 {versions.Count} 个版本，请选择要下载的版本：",
                                TextWrapping = TextWrapping.Wrap
                            },
                            versionList
                        }
                    }
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                if (versionList.SelectedItem is not ModVersionInfo selectedVersion) return;

                await DownloadVersionCoreAsync(result, selectedVersion);
            }
            catch (Exception ex)
            {
                Logger.Error("下载模组失败", ex);
                HintMessage = "下载失败：" + ex.Message;
                StatusText = "下载失败";
                IsDownloading = false;
            }
        }

        /// <summary>实际执行模组下载</summary>
        private async Task DownloadVersionCoreAsync(ModSearchResult result, ModVersionInfo version)
        {
            IsDownloading = true;
            DownloadPercent = 0;
            CurrentDownloadFile = version.FileName;
            StatusText = $"正在下载：{result.Name} {version.Name}";
            HintMessage = null;

            _downloadCts = new CancellationTokenSource();

            var progress = new Progress<DownloadProgressEventArgs>(p =>
            {
                DownloadPercent = p.Percent;
                var mbDone = p.DownloadedBytes / 1024.0 / 1024.0;
                var mbTotal = p.TotalBytes > 0 ? p.TotalBytes / 1024.0 / 1024.0 : 0;
                var speed = p.BytesPerSecond > 0 ? (p.BytesPerSecond / 1024.0 / 1024.0).ToString("F2") + " MB/s" : "-";
                StatusText = $"下载中：{CurrentDownloadFile}（{mbDone:F1}/{mbTotal:F1} MB，{speed}）";
            });

            try
            {
                var gameDir = _versionManager.GetGameDirectory(SelectedVersion!.Id);
                var downloadedPath = await _modDownloadService.DownloadVersionAsync(
                    result, version, gameDir, progress, _downloadCts.Token);

                DownloadPercent = 100;
                StatusText = $"下载完成：{result.Name}";
                HintMessage = $"模组已下载到：{downloadedPath}";

                // 下载完成后刷新本地模组列表
                await RefreshLocalModsAsync();
            }
            catch (OperationCanceledException)
            {
                StatusText = "下载已取消";
                HintMessage = "模组下载被取消。";
                DownloadPercent = -1;
            }
            catch (Exception ex)
            {
                Logger.Error("下载模组文件失败", ex);
                StatusText = "下载失败";
                HintMessage = "下载失败：" + ex.Message;
            }
            finally
            {
                IsDownloading = false;
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
        }

        /// <summary>取消下载命令</summary>
        [RelayCommand(CanExecute = nameof(IsDownloading))]
        private void CancelDownload()
        {
            _downloadCts?.Cancel();
            StatusText = "正在取消下载...";
            Logger.Info("用户请求取消模组下载");
        }
    }
}
