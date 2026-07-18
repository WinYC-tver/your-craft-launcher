using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using YCL.Core.Accounts;
using YCL.Core.Download;
using YCL.Core.Launch;
using YCL.Core.ModLoaders;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Models;
using YCL.Models.Accounts;
using YCL.Models.Versions;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 主页 ViewModel：版本管理主页，负责已安装版本的扫描、展示、启动、删除、重命名、复制，
    /// 以及在线版本的安装。采用方案 A：把 HomePage 改造成版本管理主页。
    ///
    /// 主要功能：
    /// 1. 扫描并展示已安装版本列表（卡片形式，显示 id/类型/是否含加载器）
    /// 2. 点击卡片设为"当前选中版本"（写入 config.LastSelectedVersion，启动页会默认选中它）
    /// 3. 每个版本卡片右侧操作：启动 / 打开文件夹 / 重命名 / 复制 / 删除
    /// 4. 顶部"安装新版本"按钮：弹 ContentDialog，含版本类型筛选、搜索框、版本列表
    /// 5. 删除前弹 ContentDialog 二次确认
    /// 6. 版本隔离开关：切换后写入 config，启动时据此决定 gameDir
    /// </summary>
    public partial class HomePageViewModel : ViewModelBase
    {
        private readonly IVersionManager _versionManager;
        private readonly IConfigService _configService;
        private readonly IGameLauncher _gameLauncher;
        private readonly IVersionManifestService _manifestService;
        private readonly IAccountManager _accountManager;
        private readonly IModLoaderManager _modLoaderManager;

        /// <summary>安装任务的取消令牌源</summary>
        private CancellationTokenSource? _installCts;

        public HomePageViewModel(
            IVersionManager versionManager,
            IConfigService configService,
            IGameLauncher gameLauncher,
            IVersionManifestService manifestService,
            IAccountManager accountManager,
            IModLoaderManager modLoaderManager)
        {
            _versionManager = versionManager;
            _configService = configService;
            _gameLauncher = gameLauncher;
            _manifestService = manifestService;
            _accountManager = accountManager;
            _modLoaderManager = modLoaderManager;

            // 从配置同步版本隔离开关
            _enableVersionIsolation = _configService.Current.EnableVersionIsolation;

            // 立即异步扫描一次已安装版本
            _ = RefreshAsync();
        }

        /// <summary>已安装版本列表（绑定到界面上的版本卡片列表）</summary>
        public ObservableCollection<InstalledVersionInfo> InstalledVersions { get; } = new();

        /// <summary>当前选中的版本（点击卡片选中，写入 config.LastSelectedVersion）</summary>
        [ObservableProperty]
        private InstalledVersionInfo? _selectedVersion;

        /// <summary>是否正在刷新已安装版本列表</summary>
        [ObservableProperty]
        private bool _isRefreshing;

        /// <summary>是否正在安装版本</summary>
        [ObservableProperty]
        private bool _isInstalling;

        /// <summary>安装状态文字（显示在安装进度条旁边）</summary>
        [ObservableProperty]
        private string _installStatus = "就绪";

        /// <summary>安装进度百分比（0~100，-1 表示不确定）</summary>
        [ObservableProperty]
        private double _installPercent = -1;

        /// <summary>提示信息（如错误提示或空列表引导）</summary>
        [ObservableProperty]
        private string? _hintMessage;

        /// <summary>.minecraft 路径显示文字</summary>
        [ObservableProperty]
        private string _minecraftPathDisplay = string.Empty;

        /// <summary>是否启用版本隔离（绑定到界面开关）</summary>
        [ObservableProperty]
        private bool _enableVersionIsolation;

        /// <summary>版本隔离变化时：写入配置并同步到启动器</summary>
        partial void OnEnableVersionIsolationChanged(bool value)
        {
            _configService.Current.EnableVersionIsolation = value;
            _configService.Save();
            // 同步到启动器（启动时会据此决定 gameDir）
            _gameLauncher.EnableVersionIsolation = value;
            Logger.Info($"版本隔离已{(value ? "启用" : "禁用")}");
        }

        /// <summary>选中版本变化时：写入 config.LastSelectedVersion，让启动页默认选中同一版本</summary>
        partial void OnSelectedVersionChanged(InstalledVersionInfo? value)
        {
            if (value != null)
            {
                _configService.Current.LastSelectedVersion = value.Id;
                _configService.Save();
            }
        }

        /// <summary>刷新已安装版本列表命令</summary>
        [RelayCommand]
        private async Task RefreshAsync()
        {
            if (IsRefreshing) return;
            IsRefreshing = true;
            try
            {
                // 显示当前 .minecraft 路径
                MinecraftPathDisplay = "游戏目录：" + _versionManager.MinecraftPath;

                var versions = await _versionManager.ListInstalledVersionsAsync();
                InstalledVersions.Clear();
                foreach (var v in versions)
                    InstalledVersions.Add(v);

                // 恢复选中版本：优先用配置中的 LastSelectedVersion，否则选第一个
                if (InstalledVersions.Count > 0)
                {
                    var lastSelected = _configService.Current.LastSelectedVersion;
                    SelectedVersion = !string.IsNullOrEmpty(lastSelected)
                        ? InstalledVersions.FirstOrDefault(v => v.Id == lastSelected)
                        : null;
                    SelectedVersion ??= InstalledVersions[0];
                    HintMessage = null;
                }
                else
                {
                    SelectedVersion = null;
                    HintMessage = "还没有安装任何版本。点击\"安装新版本\"来下载一个吧！";
                }

                Logger.Info($"主页扫描到 {InstalledVersions.Count} 个已安装版本");
            }
            catch (Exception ex)
            {
                Logger.Error("主页刷新版本列表失败", ex);
                HintMessage = "刷新版本列表失败：" + ex.Message;
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        /// <summary>安装新版本命令：弹出 ContentDialog 让用户选择版本</summary>
        [RelayCommand(CanExecute = nameof(CanInstall))]
        private async Task InstallNewVersionAsync()
        {
            try
            {
                // 获取所有在线版本
                var allVersions = await _manifestService.GetVersionsAsync();

                // 创建对话框 UI
                var typeCombo = new ComboBox { MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
                typeCombo.Items.Add(new ComboBoxItem { Content = "正式版 (release)", Tag = "release" });
                typeCombo.Items.Add(new ComboBoxItem { Content = "快照版 (snapshot)", Tag = "snapshot" });
                typeCombo.Items.Add(new ComboBoxItem { Content = "旧 Beta 版 (old_beta)", Tag = "old_beta" });
                typeCombo.Items.Add(new ComboBoxItem { Content = "旧 Alpha 版 (old_alpha)", Tag = "old_alpha" });
                typeCombo.SelectedIndex = 0;

                var searchBox = new TextBox
                {
                    MinWidth = 200
                };

                var filteredVersions = new ObservableCollection<VersionManifestEntry>();
                var versionList = new ListBox
                {
                    ItemsSource = filteredVersions,
                    DisplayMemberPath = nameof(VersionManifestEntry.Id),
                    MinHeight = 320,
                    MaxHeight = 450,
                    Margin = new Thickness(0, 8, 0, 0)
                };

                // 筛选函数：根据类型和关键字更新列表
                void UpdateList()
                {
                    var selectedItem = typeCombo.SelectedItem as ComboBoxItem;
                    var selectedType = selectedItem?.Tag as string ?? "release";
                    var keyword = searchBox.Text?.Trim() ?? "";
                    filteredVersions.Clear();
                    foreach (var v in allVersions)
                    {
                        if (!string.Equals(v.Type, selectedType, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.IsNullOrEmpty(keyword) &&
                            v.Id != null &&
                            !v.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                            continue;
                        filteredVersions.Add(v);
                    }
                }

                typeCombo.SelectionChanged += (s, e) => UpdateList();
                searchBox.TextChanged += (s, e) => UpdateList();
                UpdateList();

                var dialog = new ContentDialog
                {
                    Title = "安装新版本",
                    PrimaryButtonText = "安装",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Children = { typeCombo, searchBox }
                            },
                            versionList
                        }
                    }
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    if (versionList.SelectedItem is VersionManifestEntry selected)
                    {
                        await InstallVersionCoreAsync(selected);
                    }
                    else
                    {
                        HintMessage = "请先在列表中选择一个版本再点击安装。";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("打开安装对话框失败", ex);
                HintMessage = "获取版本列表失败：" + ex.Message;
            }
        }

        /// <summary>判断是否可以开始安装（不在安装中、不在刷新中）</summary>
        private bool CanInstall() => !IsInstalling && !IsRefreshing;

        /// <summary>取消安装命令</summary>
        [RelayCommand(CanExecute = nameof(IsInstalling))]
        private void CancelInstall()
        {
            _installCts?.Cancel();
            InstallStatus = "正在取消安装...";
            Logger.Info("用户请求取消安装");
        }

        /// <summary>实际执行版本安装（后台线程，通过 Progress 报告进度）</summary>
        private async Task InstallVersionCoreAsync(VersionManifestEntry entry)
        {
            if (IsInstalling) return;
            IsInstalling = true;
            InstallStatus = "准备安装：" + entry.Id;
            InstallPercent = 0;
            HintMessage = null;

            _installCts = new CancellationTokenSource();

            // 进度回调（在 UI 线程触发，因为 Progress<T> 会自动切到捕获的同步上下文）
            var progress = new Progress<InstallProgress>(p =>
            {
                InstallStatus = $"{p.PhaseText}：{p.CurrentFile}" +
                                (p.TotalFiles > 0 ? $"（{p.CompletedFiles}/{p.TotalFiles}）" : "");
                InstallPercent = p.Percent;
            });

            try
            {
                Logger.Info($"开始安装版本：{entry.Id}");
                var success = await _versionManager.InstallVersionAsync(entry, progress, _installCts.Token);

                if (success)
                {
                    InstallStatus = $"版本 {entry.Id} 安装完成！";
                    InstallPercent = 100;
                    HintMessage = $"版本 {entry.Id} 已成功安装，可以在下方列表中看到它。";
                    // 刷新已安装版本列表
                    await RefreshAsync();
                }
                else
                {
                    InstallStatus = $"版本 {entry.Id} 安装未完成（部分文件下载失败）";
                    HintMessage = $"版本 {entry.Id} 安装未完成，请查看日志或重试。";
                }
            }
            catch (OperationCanceledException)
            {
                InstallStatus = "安装已取消";
                InstallPercent = -1;
                HintMessage = "安装已被取消。";
            }
            catch (Exception ex)
            {
                Logger.Error("安装版本失败", ex);
                InstallStatus = "安装失败：" + ex.Message;
                HintMessage = "安装失败：" + ex.Message;
            }
            finally
            {
                IsInstalling = false;
                _installCts?.Dispose();
                _installCts = null;
            }
        }

        /// <summary>启动指定版本命令（用默认参数快速启动）</summary>
        [RelayCommand]
        private async Task LaunchVersionAsync(InstalledVersionInfo? version)
        {
            if (version == null) return;

            // 已有游戏运行中时拒绝启动
            if (_gameLauncher.State == LaunchState.Running ||
                _gameLauncher.State == LaunchState.Preparing ||
                _gameLauncher.State == LaunchState.ExtractingNatives ||
                _gameLauncher.State == LaunchState.Starting)
            {
                HintMessage = "已有游戏正在启动或运行中，请先关闭再启动。";
                return;
            }

            var config = _configService.Current;
            var minecraftPath = _versionManager.MinecraftPath;
            var javaPath = config.JavaPath;

            if (string.IsNullOrWhiteSpace(javaPath))
            {
                HintMessage = "未配置 Java 路径，请到设置页配置 JavaPath。";
                return;
            }

            // 同步版本隔离开关
            _gameLauncher.EnableVersionIsolation = config.EnableVersionIsolation;

            HintMessage = $"正在启动版本 {version.Id}...";

            // 从账户管理器获取当前账户
            var account = _accountManager.GetCurrentAccount();
            if (account == null)
            {
                HintMessage = "未选择账户，请到“账户”页面添加账户后再启动。";
                return;
            }

            try
            {
                var success = await _gameLauncher.LaunchAsync(
                    minecraftPath, version.Id, account, javaPath,
                    2048, 512);

                if (success)
                    HintMessage = $"版本 {version.Id} 已启动。";
                else
                    HintMessage = $"启动版本 {version.Id} 失败，请查看日志。";
            }
            catch (Exception ex)
            {
                Logger.Error("主页启动版本失败", ex);
                HintMessage = "启动失败：" + ex.Message;
            }
        }

        /// <summary>打开版本文件夹命令</summary>
        [RelayCommand]
        private void OpenVersionFolder(InstalledVersionInfo? version)
        {
            if (version == null) return;
            try
            {
                // 确保目录存在
                if (!System.IO.Directory.Exists(version.Directory))
                {
                    HintMessage = "版本目录不存在：" + version.Directory;
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = version.Directory,
                    UseShellExecute = true,
                    Verb = "open"
                });
                Logger.Info("已打开版本文件夹：" + version.Directory);
            }
            catch (Exception ex)
            {
                Logger.Error("打开版本文件夹失败", ex);
                HintMessage = "打开文件夹失败：" + ex.Message;
            }
        }

        /// <summary>重命名版本命令：弹 ContentDialog 让用户输入新 id</summary>
        [RelayCommand]
        private async Task RenameVersionAsync(InstalledVersionInfo? version)
        {
            if (version == null) return;

            var inputBox = new TextBox
            {
                Text = version.Id,
                MinWidth = 300,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var dialog = new ContentDialog
            {
                Title = "重命名版本",
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"请输入新的版本 id（当前：{version.Id}）：",
                            TextWrapping = TextWrapping.Wrap
                        },
                        inputBox
                    }
                }
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var newId = inputBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(newId))
            {
                HintMessage = "新版本 id 不能为空。";
                return;
            }
            if (string.Equals(newId, version.Id, StringComparison.OrdinalIgnoreCase))
                return;

            HintMessage = $"正在重命名：{version.Id} → {newId}...";
            var success = await _versionManager.RenameVersionAsync(version.Id, newId);
            if (success)
            {
                HintMessage = $"已重命名：{version.Id} → {newId}";
                await RefreshAsync();
            }
            else
            {
                HintMessage = $"重命名失败：{version.Id} → {newId}（可能新 id 已存在或含非法字符）";
            }
        }

        /// <summary>复制版本命令：弹 ContentDialog 让用户输入新 id</summary>
        [RelayCommand]
        private async Task CopyVersionAsync(InstalledVersionInfo? version)
        {
            if (version == null) return;

            var inputBox = new TextBox
            {
                Text = version.Id + "-copy",
                MinWidth = 300,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var dialog = new ContentDialog
            {
                Title = "复制版本",
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"复制版本 {version.Id} 到新目录，请输入新版本 id：",
                            TextWrapping = TextWrapping.Wrap
                        },
                        inputBox
                    }
                }
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var newId = inputBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(newId))
            {
                HintMessage = "新版本 id 不能为空。";
                return;
            }
            if (string.Equals(newId, version.Id, StringComparison.OrdinalIgnoreCase))
            {
                HintMessage = "新版本 id 不能与原版本相同。";
                return;
            }

            HintMessage = $"正在复制：{version.Id} → {newId}...";
            var success = await _versionManager.CopyVersionAsync(version.Id, newId);
            if (success)
            {
                HintMessage = $"已复制：{version.Id} → {newId}";
                await RefreshAsync();
            }
            else
            {
                HintMessage = $"复制失败：{version.Id} → {newId}（可能新 id 已存在或含非法字符）";
            }
        }

        /// <summary>删除版本命令：弹 ContentDialog 二次确认后删除</summary>
        [RelayCommand]
        private async Task DeleteVersionAsync(InstalledVersionInfo? version)
        {
            if (version == null) return;

            // 二次确认对话框（删除不可逆）
            var dialog = new ContentDialog
            {
                Title = "确认删除版本",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                Content = new TextBlock
                {
                    Text = $"确定要删除版本 {version.Id} 吗？\n\n" +
                           "此操作不可撤销，版本目录下的所有文件（含 mods/saves 等）都会被删除。",
                    TextWrapping = TextWrapping.Wrap
                }
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            HintMessage = $"正在删除版本 {version.Id}...";
            var success = await _versionManager.DeleteVersionAsync(version.Id);
            if (success)
            {
                HintMessage = $"已删除版本 {version.Id}";
                await RefreshAsync();
            }
            else
            {
                HintMessage = $"删除版本失败：{version.Id}";
            }
        }

        /// <summary>
        /// 安装模组加载器命令：弹 ContentDialog 让用户选加载器类型与版本，然后安装。
        /// 仅对原版（非继承、非已装加载器）版本生效。
        /// </summary>
        [RelayCommand]
        private async Task InstallModLoaderAsync(InstalledVersionInfo? version)
        {
            if (version == null) return;

            // 已是继承版本或已装加载器的版本不再装加载器
            if (version.HasInheritsFrom || version.IsModded)
            {
                HintMessage = $"版本 {version.Id} 已经是模组版本，无法再安装加载器。";
                return;
            }

            try
            {
                // 第一步：选加载器类型
                var typeCombo = new ComboBox { MinWidth = 200, Margin = new Thickness(0, 8, 0, 0) };
                typeCombo.Items.Add(new ComboBoxItem { Content = "Fabric（轻量，1.14+）", Tag = ModLoaderType.Fabric });
                typeCombo.Items.Add(new ComboBoxItem { Content = "Forge（最老牌）", Tag = ModLoaderType.Forge });
                typeCombo.Items.Add(new ComboBoxItem { Content = "Quilt（Fabric 兼容分叉）", Tag = ModLoaderType.Quilt });
                typeCombo.Items.Add(new ComboBoxItem { Content = "NeoForge（1.20+ Forge 分叉）", Tag = ModLoaderType.NeoForge });
                typeCombo.Items.Add(new ComboBoxItem { Content = "LiteLoader（仅 1.12 及以下）", Tag = ModLoaderType.LiteLoader });
                typeCombo.SelectedIndex = 0;

                var typeDialog = new ContentDialog
                {
                    Title = $"为 {version.Id} 安装模组加载器",
                    PrimaryButtonText = "下一步",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "请选择加载器类型：",
                                TextWrapping = TextWrapping.Wrap
                            },
                            typeCombo
                        }
                    }
                };

                if (await typeDialog.ShowAsync() != ContentDialogResult.Primary) return;

                var selectedType = (ModLoaderType)((ComboBoxItem)typeCombo.SelectedItem).Tag;

                // 第二步：获取该类型在该 Minecraft 版本下的可用版本列表
                InstallStatus = $"正在获取 {selectedType} 版本列表...";
                IsInstalling = true;
                InstallPercent = -1;

                List<ModLoaderVersion> versions;
                try
                {
                    versions = await _modLoaderManager.GetInstaller(selectedType)
                        .ListVersionsAsync(version.Id);
                }
                finally
                {
                    IsInstalling = false;
                }

                if (versions.Count == 0)
                {
                    HintMessage = $"未找到 {selectedType} 在 Minecraft {version.Id} 下的可用版本。" +
                                  "（可能是该加载器不支持此 Minecraft 版本，或网络问题）";
                    return;
                }

                // 第三步：选加载器版本
                var versionList = new ListBox
                {
                    MinHeight = 320,
                    MaxHeight = 450,
                    Margin = new Thickness(0, 8, 0, 0),
                    ItemsSource = versions,
                    DisplayMemberPath = nameof(ModLoaderVersion.DisplayName)
                };
                versionList.SelectedIndex = 0;

                var versionDialog = new ContentDialog
                {
                    Title = $"选择 {selectedType} 版本（Minecraft {version.Id}）",
                    PrimaryButtonText = "安装",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"共 {versions.Count} 个可用版本，请选择一个：",
                                TextWrapping = TextWrapping.Wrap
                            },
                            versionList
                        }
                    }
                };

                if (await versionDialog.ShowAsync() != ContentDialogResult.Primary) return;

                if (versionList.SelectedItem is not ModLoaderVersion selectedVersion) return;

                // 第四步：执行安装
                await InstallModLoaderCoreAsync(version.Id, selectedType, selectedVersion);
            }
            catch (Exception ex)
            {
                Logger.Error("安装模组加载器失败", ex);
                HintMessage = "安装加载器失败：" + ex.Message;
                IsInstalling = false;
            }
        }

        /// <summary>实际执行加载器安装（后台线程，通过 Progress 报告进度）</summary>
        private async Task InstallModLoaderCoreAsync(
            string minecraftVersion, ModLoaderType type, ModLoaderVersion loaderVersion)
        {
            IsInstalling = true;
            InstallStatus = $"准备安装 {type} {loaderVersion.Version}...";
            InstallPercent = -1;
            HintMessage = null;

            var progress = new Progress<InstallProgress>(p =>
            {
                InstallStatus = $"{p.PhaseText}：{p.CurrentFile}" +
                                (p.TotalFiles > 0 ? $"（{p.CompletedFiles}/{p.TotalFiles}）" : "");
                InstallPercent = p.Percent;
            });

            try
            {
                Logger.Info($"开始安装 {type} {loaderVersion.Version}（MC {minecraftVersion}）");
                await _modLoaderManager.InstallAsync(type, minecraftVersion, loaderVersion, progress, CancellationToken.None);

                InstallStatus = $"{type} {loaderVersion.Version} 安装完成！";
                InstallPercent = 100;
                HintMessage = $"{type} {loaderVersion.Version} 已为 Minecraft {minecraftVersion} 安装完成。";
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("加载器安装失败", ex);
                InstallStatus = "安装失败：" + ex.Message;
                HintMessage = $"{type} 安装失败：{ex.Message}";
            }
            finally
            {
                IsInstalling = false;
            }
        }

        /// <summary>
        /// 卸载模组加载器命令：先检查已安装的加载器，让用户选一个卸载。
        /// </summary>
        [RelayCommand]
        private async Task UninstallModLoaderAsync(InstalledVersionInfo? version)
        {
            if (version == null) return;

            try
            {
                InstallStatus = $"正在检查 {version.Id} 已安装的加载器...";
                var installed = await _modLoaderManager.CheckInstalledAsync(version.Id);

                // 过滤出已安装的加载器类型
                var installedTypes = installed
                    .Where(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .ToList();

                if (installedTypes.Count == 0)
                {
                    HintMessage = $"未在 {version.Id} 下检测到可卸载的加载器。" +
                                  "（可能是手动管理的版本，或加载器版本目录已被删除）";
                    return;
                }

                // 弹对话框让用户选要卸载的加载器
                var typeCombo = new ComboBox { MinWidth = 200, Margin = new Thickness(0, 8, 0, 0) };
                foreach (var t in installedTypes)
                {
                    typeCombo.Items.Add(new ComboBoxItem { Content = t.ToString(), Tag = t });
                }
                typeCombo.SelectedIndex = 0;

                var dialog = new ContentDialog
                {
                    Title = $"卸载 {version.Id} 的模组加载器",
                    PrimaryButtonText = "卸载",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "请选择要卸载的加载器（将删除对应的版本目录，不可撤销）：",
                                TextWrapping = TextWrapping.Wrap
                            },
                            typeCombo
                        }
                    }
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

                var selectedType = (ModLoaderType)((ComboBoxItem)typeCombo.SelectedItem).Tag;

                HintMessage = $"正在卸载 {selectedType}（MC {version.Id}）...";
                await _modLoaderManager.UninstallAsync(selectedType, version.Id);
                HintMessage = $"{selectedType} 已从 {version.Id} 卸载。";
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("卸载模组加载器失败", ex);
                HintMessage = "卸载加载器失败：" + ex.Message;
            }
        }
    }
}
