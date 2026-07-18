using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YCL.Core.Accounts;
using YCL.Core.Launch;
using YCL.Core.Utils;
using YCL.Models.Accounts;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 启动页 ViewModel：负责扫描版本、管理启动状态、接收游戏日志并展示到界面。
    /// 通过 <see cref="IGameLauncher"/> 完成实际的启动流程，
    /// 通过 <see cref="IConfigService"/> 读取配置（.minecraft 路径、java 路径等），
    /// 通过 <see cref="IAccountManager"/> 获取当前登录账户。
    /// </summary>
    public partial class LaunchPageViewModel : ViewModelBase
    {
        private readonly IGameLauncher _gameLauncher;
        private readonly IConfigService _configService;
        private readonly IAccountManager _accountManager;
        private readonly SkinService _skinService;

        public LaunchPageViewModel(
            IGameLauncher gameLauncher,
            IConfigService configService,
            IAccountManager accountManager,
            SkinService skinService)
        {
            _gameLauncher = gameLauncher;
            _configService = configService;
            _accountManager = accountManager;
            _skinService = skinService;

            // 订阅启动器事件
            _gameLauncher.ProgressChanged += OnProgressChanged;
            _gameLauncher.LogReceived += OnLogReceived;
            _gameLauncher.Exited += OnGameExited;
            _gameLauncher.StateChanged += OnStateChanged;

            // 订阅账户变化，刷新当前账户显示
            _accountManager.AccountsChanged += OnAccountsChanged;

            // 从配置读取默认值（内存、窗口等启动参数）
            _maxMemoryMb = _configService.Current.MaxMemory;
            _minMemoryMb = _configService.Current.MinMemory;

            // 同步版本隔离开关到启动器（启动时会据此决定 gameDir）
            _gameLauncher.EnableVersionIsolation = _configService.Current.EnableVersionIsolation;
            // 同步其他启动参数到启动器
            _gameLauncher.WindowWidth = _configService.Current.WindowWidth;
            _gameLauncher.WindowHeight = _configService.Current.WindowHeight;
            _gameLauncher.FullscreenOnLaunch = _configService.Current.FullscreenOnLaunch;
            _gameLauncher.ExtraJvmArgs = _configService.Current.ExtraJvmArgs ?? string.Empty;
            _gameLauncher.CleanBeforeLaunch = _configService.Current.CleanBeforeLaunch;

            // 加载当前账户信息
            RefreshCurrentAccount();

            // 立即扫描一次版本（在后台线程，避免阻塞 UI）
            _ = RefreshVersionsAsync();
        }

        /// <summary>可启动的版本列表</summary>
        public ObservableCollection<string> Versions { get; } = new();

        /// <summary>游戏日志行列表（每行一条）</summary>
        public ObservableCollection<LogEntry> LogEntries { get; } = new();

        /// <summary>当前选中的版本 id</summary>
        [ObservableProperty]
        private string? _selectedVersion;

        /// <summary>当前账户用户名（显示用，从 IAccountManager 读取）</summary>
        [ObservableProperty]
        private string _currentAccountName = "未选择账户";

        /// <summary>当前账户类型说明（离线/微软/外置）</summary>
        [ObservableProperty]
        private string _currentAccountType = string.Empty;

        /// <summary>当前账户头像（可能为 null，显示默认占位图）</summary>
        [ObservableProperty]
        private ImageSource? _currentAccountAvatar;

        /// <summary>是否已有当前账户</summary>
        [ObservableProperty]
        private bool _hasCurrentAccount;

        /// <summary>最大堆内存（MB）</summary>
        [ObservableProperty]
        private int _maxMemoryMb;

        /// <summary>初始堆内存（MB）</summary>
        [ObservableProperty]
        private int _minMemoryMb;

        /// <summary>当前启动状态文字（显示在按钮下方）</summary>
        [ObservableProperty]
        private string _statusText = "就绪";

        /// <summary>启动进度百分比（0~100，-1 表示不确定）</summary>
        [ObservableProperty]
        private int _progressPercent = -1;

        /// <summary>启动按钮文字</summary>
        [ObservableProperty]
        private string _launchButtonText = "启动游戏";

        /// <summary>启动按钮是否可用</summary>
        [ObservableProperty]
        private bool _isLaunchEnabled = true;

        /// <summary>.minecraft 目录路径（用于在界面上显示）</summary>
        [ObservableProperty]
        private string _minecraftPathDisplay = string.Empty;

        /// <summary>是否处于运行中状态（控制按钮文本）</summary>
        [ObservableProperty]
        private bool _isRunning;

        /// <summary>提示信息（如目录不存在时显示）</summary>
        [ObservableProperty]
        private string? _hintMessage;

        /// <summary>扫描版本是否正在执行</summary>
        [ObservableProperty]
        private bool _isRefreshing;

        /// <summary>刷新版本列表命令</summary>
        [RelayCommand]
        private async Task RefreshVersionsAsync()
        {
            if (IsRefreshing) return;
            IsRefreshing = true;
            try
            {
                // 从配置读取 .minecraft 路径；为空则用默认 %APPDATA%\.minecraft
                var minecraftPath = _configService.Current.MinecraftPath;
                if (string.IsNullOrWhiteSpace(minecraftPath))
                {
                    minecraftPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        ".minecraft");
                }

                MinecraftPathDisplay = "游戏目录：" + minecraftPath;

                if (!Directory.Exists(minecraftPath))
                {
                    HintMessage = $"游戏目录不存在：{minecraftPath}\n请在设置页配置正确的 .minecraft 路径。";
                    Versions.Clear();
                    Logger.Warn("启动页：游戏目录不存在 - " + minecraftPath);
                    return;
                }

                HintMessage = null;

                // 通过启动器（最终通过 IVersionResolver）扫描版本
                // 注意：IVersionResolver 不在 DI 注册给 ViewModel，这里通过反射拿不到，
                //       但 IGameLauncher 没有扫描方法，所以这里直接扫描 versions 目录
                var versions = ScanVersions(minecraftPath);

                Versions.Clear();
                foreach (var v in versions)
                    Versions.Add(v);

                // 默认选中版本：
                // 1. 优先用配置中保存的 LastSelectedVersion（主页选中的版本），如果它在列表中
                // 2. 否则选第一个版本
                if (Versions.Count > 0 && string.IsNullOrEmpty(SelectedVersion))
                {
                    var lastSelected = _configService.Current.LastSelectedVersion;
                    if (!string.IsNullOrEmpty(lastSelected) && Versions.Contains(lastSelected))
                        SelectedVersion = lastSelected;
                    else
                        SelectedVersion = Versions[0];
                }

                if (Versions.Count == 0)
                {
                    HintMessage = "在 " + minecraftPath + " 下没有找到任何 Minecraft 版本。\n请先到主页或下载页下载版本。";
                }

                Logger.Info($"启动页扫描到 {Versions.Count} 个版本");
            }
            catch (Exception ex)
            {
                Logger.Error("扫描版本失败", ex);
                HintMessage = "扫描版本失败：" + ex.Message;
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        /// <summary>扫描 .minecraft/versions 目录下的所有版本</summary>
        private System.Collections.Generic.List<string> ScanVersions(string minecraftPath)
        {
            var result = new System.Collections.Generic.List<string>();
            try
            {
                var versionsDir = Path.Combine(minecraftPath, "versions");
                if (!Directory.Exists(versionsDir))
                    return result;

                foreach (var dir in Directory.GetDirectories(versionsDir))
                {
                    var id = Path.GetFileName(dir);
                    var jsonPath = Path.Combine(dir, id + ".json");
                    if (File.Exists(jsonPath))
                        result.Add(id);
                }

                result.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Logger.Error("扫描 versions 目录出错", ex);
            }
            return result;
        }

        /// <summary>启动游戏命令</summary>
        [RelayCommand]
        private async Task LaunchAsync()
        {
            if (IsRunning)
            {
                // 运行中点击：关闭游戏
                _gameLauncher.Stop();
                return;
            }

            // 校验输入
            if (string.IsNullOrWhiteSpace(SelectedVersion))
            {
                StatusText = "请先选择一个版本";
                MessageBox.Show("请先选择一个要启动的版本。", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 获取当前账户
            var account = _accountManager.GetCurrentAccount();
            if (account == null)
            {
                StatusText = "未选择账户";
                MessageBox.Show(
                    "请先到“账户”页面添加并选择一个账户，再启动游戏。",
                    "YCL 启动器", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 获取配置
            var config = _configService.Current;
            var minecraftPath = config.MinecraftPath;
            if (string.IsNullOrWhiteSpace(minecraftPath))
            {
                minecraftPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    ".minecraft");
            }

            // 同步版本隔离开关（用户可能在设置页改过）
            _gameLauncher.EnableVersionIsolation = config.EnableVersionIsolation;
            // 同步其他启动参数（用户可能在设置页改过，启动时用最新值）
            _gameLauncher.WindowWidth = config.WindowWidth;
            _gameLauncher.WindowHeight = config.WindowHeight;
            _gameLauncher.FullscreenOnLaunch = config.FullscreenOnLaunch;
            _gameLauncher.ExtraJvmArgs = config.ExtraJvmArgs ?? string.Empty;
            _gameLauncher.CleanBeforeLaunch = config.CleanBeforeLaunch;

            var javaPath = config.JavaPath;
            if (string.IsNullOrWhiteSpace(javaPath))
            {
                StatusText = "未配置 Java 路径";
                MessageBox.Show(
                    "未配置 Java 路径，请到设置页填写 JavaPath（指向 javaw.exe 或 java.exe）。\n\n" +
                    "如果你还没装 Java，可以从 https://adoptium.net 下载。",
                    "YCL 启动器", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 禁用按钮，更新状态
            IsLaunchEnabled = false;
            LaunchButtonText = "启动中...";
            StatusText = "正在启动...";

            try
            {
                var success = await _gameLauncher.LaunchAsync(
                    minecraftPath, SelectedVersion, account, javaPath,
                    MaxMemoryMb, MinMemoryMb);

                if (!success)
                {
                    StatusText = "启动失败，请查看日志";
                    LaunchButtonText = "启动游戏";
                    IsLaunchEnabled = true;
                }
                // 成功时按钮文字会在 OnStateChanged 中更新为"运行中"
            }
            catch (Exception ex)
            {
                Logger.Error("启动异常", ex);
                StatusText = "启动异常：" + ex.Message;
                LaunchButtonText = "启动游戏";
                IsLaunchEnabled = true;
            }
        }

        /// <summary>清空日志命令</summary>
        [RelayCommand]
        private void ClearLogs()
        {
            LogEntries.Clear();
        }

        /// <summary>账户列表变化时刷新当前账户显示</summary>
        private void OnAccountsChanged(object? sender, EventArgs e)
        {
            Application.Current?.Dispatcher.Invoke(RefreshCurrentAccount);
        }

        /// <summary>
        /// 从 IAccountManager 读取当前账户并更新界面显示（用户名、类型、头像）。
        /// 头像异步下载，失败显示默认占位图。
        /// </summary>
        private void RefreshCurrentAccount()
        {
            var account = _accountManager.GetCurrentAccount();
            HasCurrentAccount = account != null;

            if (account == null)
            {
                CurrentAccountName = "未选择账户";
                CurrentAccountType = string.Empty;
                CurrentAccountAvatar = null;
                return;
            }

            CurrentAccountName = account.Username;
            CurrentAccountType = account.Type switch
            {
                AccountType.Offline => "离线账户",
                AccountType.Microsoft => "微软账户",
                AccountType.Yggdrasil => "外置账户",
                _ => account.Type.ToString()
            };

            // 异步加载头像
            _ = LoadAvatarAsync(account);
        }

        /// <summary>异步加载当前账户头像</summary>
        private async Task LoadAvatarAsync(AccountBase account)
        {
            string? skinUrl = account switch
            {
                MicrosoftAccount ms => ms.SkinUrl,
                YggdrasilAccount yg => yg.SkinUrl,
                _ => null
            };

            var avatar = await _skinService.GetAvatarAsync(account.Uuid, skinUrl);
            Application.Current?.Dispatcher.Invoke(() =>
            {
                CurrentAccountAvatar = avatar;
            });
        }

        /// <summary>启动进度变化回调</summary>
        private void OnProgressChanged(object? sender, LaunchProgressEventArgs e)
        {
            // 切回 UI 线程更新绑定属性
            Application.Current?.Dispatcher.Invoke(() =>
            {
                StatusText = $"{e.Stage}：{e.Message}";
                ProgressPercent = e.Percent;
            });
        }

        /// <summary>收到游戏日志回调</summary>
        private void OnLogReceived(object? sender, GameLogEventArgs e)
        {
            // 切回 UI 线程添加日志项
            Application.Current?.Dispatcher.Invoke(() =>
            {
                LogEntries.Add(new LogEntry
                {
                    Text = e.Line,
                    IsError = e.IsError
                });

                // 限制日志最大数量，避免内存爆炸
                if (LogEntries.Count > 2000)
                {
                    for (int i = 0; i < 500; i++)
                        LogEntries.RemoveAt(0);
                }
            });
        }

        /// <summary>游戏退出回调</summary>
        private void OnGameExited(object? sender, GameExitedEventArgs e)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsRunning = false;
                IsLaunchEnabled = true;
                LaunchButtonText = "启动游戏";
                StatusText = e.IsSuccess
                    ? $"游戏已正常退出（退出码 {e.ExitCode}）"
                    : $"游戏异常退出（退出码 {e.ExitCode}）";
                ProgressPercent = -1;
            });
        }

        /// <summary>启动状态变化回调</summary>
        private void OnStateChanged(object? sender, LaunchState state)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                switch (state)
                {
                    case LaunchState.Running:
                        IsRunning = true;
                        IsLaunchEnabled = true;
                        LaunchButtonText = "运行中（点击关闭）";
                        StatusText = "游戏运行中";
                        break;
                    case LaunchState.Failed:
                        IsRunning = false;
                        IsLaunchEnabled = true;
                        LaunchButtonText = "启动游戏";
                        break;
                }
            });
        }
    }

    /// <summary>单条日志条目（用于日志面板显示）</summary>
    public class LogEntry
    {
        /// <summary>日志文本</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>是否为错误流（true 标红）</summary>
        public bool IsError { get; set; }
    }
}
