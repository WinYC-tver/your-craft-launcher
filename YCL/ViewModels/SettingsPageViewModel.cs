using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using YCL.Core.Update;
using YCL.Core.Utils;
using YCL.Models;
using YCL.Services;
// 解决 YCL.Models.ThemeMode 与 System.Windows.ThemeMode 的命名冲突，
// 明确告诉编译器此处用 YCL 自己定义的 ThemeMode 枚举
using ThemeMode = YCL.Models.ThemeMode;

namespace YCL.ViewModels
{
    /// <summary>
    /// 设置页 ViewModel：负责管理启动设置、游戏设置、下载设置、系统设置、主题设置。
    /// 每个设置项变化时立即通过 <see cref="IConfigService"/> 保存到配置文件，
    /// 大部分设置立即生效（主题即时应用，启动参数下次启动游戏时生效）。
    /// </summary>
    public partial class SettingsPageViewModel : ViewModelBase
    {
        private readonly IThemeService _themeService;
        private readonly IConfigService _configService;
        private readonly INavigationService _navigationService;
        private readonly IUpdateChecker? _updateChecker;

        // 开机自启注册表路径与键名
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "YCL";

        public SettingsPageViewModel(
            IThemeService themeService,
            IConfigService configService,
            INavigationService navigationService,
            IUpdateChecker? updateChecker = null)
        {
            _themeService = themeService;
            _configService = configService;
            _navigationService = navigationService;
            _updateChecker = updateChecker;

            var config = _configService.Current;

            // ====== 主题设置（从配置读取当前值）======
            _selectedThemeMode = config.ThemeMode;
            var savedColor = ThemeService.ParseColor(config.AccentColorHex);
            _useSystemAccentColor = !savedColor.HasValue;
            _isCustomAccentColorEnabled = savedColor.HasValue;
            _selectedAccentColor = savedColor ?? DefaultAccentColor;

            // ====== 启动设置 ======
            _javaPath = config.JavaPath;
            _maxMemory = config.MaxMemory;
            _minMemory = config.MinMemory;
            _windowWidth = config.WindowWidth;
            _windowHeight = config.WindowHeight;
            _fullscreenOnLaunch = config.FullscreenOnLaunch;
            _closeAfterLaunch = config.CloseAfterLaunch;
            _extraJvmArgs = config.ExtraJvmArgs;

            // ====== 游戏设置 ======
            _minecraftPath = config.MinecraftPath;
            _enableVersionIsolation = config.EnableVersionIsolation;
            _cleanBeforeLaunch = config.CleanBeforeLaunch;

            // ====== 下载设置 ======
            _selectedDownloadSource = config.DownloadSource;
            _downloadThreads = config.DownloadThreads;
            _retryCount = config.RetryCount;
            _curseForgeApiKey = config.CurseForgeApiKey;

            // ====== 系统设置 ======
            _checkUpdateOnStartup = config.CheckUpdateOnStartup;
            _updateRepo = config.UpdateRepo;
            _launchOnStartup = config.LaunchOnStartup;
        }

        // ================================================================
        // 主题设置（保留原有功能）
        // ================================================================

        /// <summary>默认强调色（与 Windows 系统强调色一致）</summary>
        private static readonly Color DefaultAccentColor = Color.FromRgb(0, 120, 215);

        /// <summary>预设的强调色集合，供界面用色块按钮展示</summary>
        public IReadOnlyList<Color> PresetAccentColors { get; } = new List<Color>
        {
            Color.FromRgb(0, 120, 215),    // Windows 蓝
            Color.FromRgb(232, 17, 35),    // 红石红
            Color.FromRgb(124, 179, 66),   // 草绿
            Color.FromRgb(0, 188, 212),    // 钻石青
            Color.FromRgb(142, 36, 170),   // 末影紫
            Color.FromRgb(253, 216, 53),   // 金色
            Color.FromRgb(251, 140, 0),    // 熔岩橙
            Color.FromRgb(84, 110, 122)    // 墨石灰
        };

        [ObservableProperty]
        private ThemeMode _selectedThemeMode;

        partial void OnSelectedThemeModeChanged(ThemeMode value)
        {
            _themeService.ApplyTheme(value);
            _configService.Current.ThemeMode = value;
            _configService.Save();
        }

        [ObservableProperty]
        private bool _useSystemAccentColor;

        partial void OnUseSystemAccentColorChanged(bool value)
        {
            IsCustomAccentColorEnabled = !value;
            if (value)
            {
                _themeService.ApplyAccentColor(null);
                _configService.Current.AccentColorHex = null;
                _configService.Save();
            }
            else
            {
                _themeService.ApplyAccentColor(SelectedAccentColor);
                _configService.Current.AccentColorHex = ColorToHex(SelectedAccentColor);
                _configService.Save();
            }
        }

        [ObservableProperty]
        private bool _isCustomAccentColorEnabled;

        [ObservableProperty]
        private Color _selectedAccentColor;

        partial void OnSelectedAccentColorChanged(Color value)
        {
            if (UseSystemAccentColor) return;
            _themeService.ApplyAccentColor(value);
            _configService.Current.AccentColorHex = ColorToHex(value);
            _configService.Save();
        }

        private static string ColorToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        [RelayCommand]
        private void SelectAccentColor(Color color) => SelectedAccentColor = color;

        // ================================================================
        // 启动设置
        // ================================================================

        [ObservableProperty]
        private string _javaPath;

        partial void OnJavaPathChanged(string value)
        {
            _configService.Current.JavaPath = value ?? string.Empty;
            _configService.Save();
        }

        [ObservableProperty]
        private int _maxMemory;

        partial void OnMaxMemoryChanged(int value)
        {
            _configService.Current.MaxMemory = value;
            _configService.Save();
        }

        [ObservableProperty]
        private int _minMemory;

        partial void OnMinMemoryChanged(int value)
        {
            _configService.Current.MinMemory = value;
            _configService.Save();
        }

        [ObservableProperty]
        private int _windowWidth;

        partial void OnWindowWidthChanged(int value)
        {
            _configService.Current.WindowWidth = value;
            _configService.Save();
        }

        [ObservableProperty]
        private int _windowHeight;

        partial void OnWindowHeightChanged(int value)
        {
            _configService.Current.WindowHeight = value;
            _configService.Save();
        }

        [ObservableProperty]
        private bool _fullscreenOnLaunch;

        partial void OnFullscreenOnLaunchChanged(bool value)
        {
            _configService.Current.FullscreenOnLaunch = value;
            _configService.Save();
        }

        [ObservableProperty]
        private bool _closeAfterLaunch;

        partial void OnCloseAfterLaunchChanged(bool value)
        {
            _configService.Current.CloseAfterLaunch = value;
            _configService.Save();
        }

        [ObservableProperty]
        private string _extraJvmArgs;

        partial void OnExtraJvmArgsChanged(string value)
        {
            _configService.Current.ExtraJvmArgs = value ?? string.Empty;
            _configService.Save();
        }

        /// <summary>浏览选择 javaw.exe / java.exe 文件</summary>
        [RelayCommand]
        private void BrowseJavaPath()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 Java 运行时（javaw.exe 或 java.exe）",
                Filter = "Java 可执行文件|javaw.exe;java.exe|所有文件|*.*",
                CheckFileExists = true
            };
            // 初始目录设为当前 JavaPath 所在目录
            if (!string.IsNullOrWhiteSpace(JavaPath))
            {
                var dir = Path.GetDirectoryName(JavaPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    dialog.InitialDirectory = dir;
            }

            if (dialog.ShowDialog() == true)
            {
                JavaPath = dialog.FileName;
            }
        }

        /// <summary>跳转到 Java 管理页（用户可在那里自动检测 / 安装 Java）</summary>
        [RelayCommand]
        private void GoToJavaPage()
        {
            _navigationService.NavigateTo("Java");
        }

        // ================================================================
        // 游戏设置
        // ================================================================

        [ObservableProperty]
        private string _minecraftPath;

        partial void OnMinecraftPathChanged(string value)
        {
            _configService.Current.MinecraftPath = value ?? string.Empty;
            _configService.Save();
        }

        [ObservableProperty]
        private bool _enableVersionIsolation;

        partial void OnEnableVersionIsolationChanged(bool value)
        {
            _configService.Current.EnableVersionIsolation = value;
            _configService.Save();
        }

        [ObservableProperty]
        private bool _cleanBeforeLaunch;

        partial void OnCleanBeforeLaunchChanged(bool value)
        {
            _configService.Current.CleanBeforeLaunch = value;
            _configService.Save();
        }

        /// <summary>浏览选择 .minecraft 游戏目录</summary>
        [RelayCommand]
        private void BrowseMinecraftPath()
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择 .minecraft 游戏目录",
                ShowNewFolderButton = true
            };
            if (!string.IsNullOrWhiteSpace(MinecraftPath) && Directory.Exists(MinecraftPath))
            {
                dialog.SelectedPath = MinecraftPath;
            }

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                MinecraftPath = dialog.SelectedPath;
            }
        }

        // ================================================================
        // 下载设置
        // ================================================================

        [ObservableProperty]
        private DownloadSource _selectedDownloadSource;

        partial void OnSelectedDownloadSourceChanged(DownloadSource value)
        {
            _configService.Current.DownloadSource = value;
            _configService.Save();
        }

        [ObservableProperty]
        private int _downloadThreads;

        partial void OnDownloadThreadsChanged(int value)
        {
            // 限制在 1~32 之间
            if (value < 1) value = 1;
            if (value > 32) value = 32;
            _configService.Current.DownloadThreads = value;
            _configService.Save();
        }

        [ObservableProperty]
        private int _retryCount;

        partial void OnRetryCountChanged(int value)
        {
            // 限制在 0~10 之间
            if (value < 0) value = 0;
            if (value > 10) value = 10;
            _configService.Current.RetryCount = value;
            _configService.Save();
        }

        [ObservableProperty]
        private string _curseForgeApiKey;

        partial void OnCurseForgeApiKeyChanged(string value)
        {
            _configService.Current.CurseForgeApiKey = value ?? string.Empty;
            _configService.Save();
        }

        // ================================================================
        // 系统设置
        // ================================================================

        /// <summary>日志文件所在目录（只读显示）</summary>
        public string LogPath => Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "YCL", "logs");

        [ObservableProperty]
        private bool _checkUpdateOnStartup;

        partial void OnCheckUpdateOnStartupChanged(bool value)
        {
            _configService.Current.CheckUpdateOnStartup = value;
            _configService.Save();
        }

        [ObservableProperty]
        private string _updateRepo;

        partial void OnUpdateRepoChanged(string value)
        {
            _configService.Current.UpdateRepo = value ?? string.Empty;
            _configService.Save();
        }

        [ObservableProperty]
        private bool _launchOnStartup;

        partial void OnLaunchOnStartupChanged(bool value)
        {
            _configService.Current.LaunchOnStartup = value;
            _configService.Save();
            // 同时写入 / 删除注册表项
            SetStartupRegistry(value);
        }

        /// <summary>打开日志文件夹</summary>
        [RelayCommand]
        private void OpenLogFolder()
        {
            try
            {
                // 目录不存在则创建
                Directory.CreateDirectory(LogPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = LogPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error("打开日志文件夹失败", ex);
            }
        }

        /// <summary>立即检查更新（手动触发）</summary>
        [ObservableProperty]
        private bool _isCheckingUpdate;

        [ObservableProperty]
        private string _updateCheckResult = string.Empty;

        [RelayCommand]
        private async Task CheckUpdateNowAsync()
        {
            if (_updateChecker == null)
            {
                UpdateCheckResult = "更新检查服务不可用";
                return;
            }

            IsCheckingUpdate = true;
            UpdateCheckResult = "正在检查更新...";
            try
            {
                var release = await _updateChecker.CheckForUpdatesAsync(System.Threading.CancellationToken.None);
                if (release != null)
                {
                    UpdateCheckResult = $"发现新版本 {release.Version}！发布于 {release.PublishedAtDisplay}";
                    // 弹窗提示用户
                    var msg = $"发现新版本 {release.Version}！\n\n" +
                              $"发布时间：{release.PublishedAtDisplay}\n\n" +
                              $"更新内容：\n{release.ReleaseNotes}\n\n" +
                              $"是否前往下载页面？";
                    var result = MessageBox.Show(msg, "发现新版本",
                        MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                    {
                        // 用默认浏览器打开发布页面
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = release.ReleaseUrl,
                            UseShellExecute = true
                        });
                    }
                }
                else
                {
                    UpdateCheckResult = "当前已是最新版本";
                }
            }
            catch (Exception ex)
            {
                UpdateCheckResult = "检查更新失败：" + ex.Message;
                Logger.Error("手动检查更新失败", ex);
            }
            finally
            {
                IsCheckingUpdate = false;
            }
        }

        // ================================================================
        // 私有辅助方法
        // ================================================================

        /// <summary>
        /// 写入或删除开机自启注册表项。
        /// 路径：HKCU\Software\Microsoft\Windows\CurrentVersion\Run
        /// 用 try-catch 包裹（可能无权限）。
        /// </summary>
        private void SetStartupRegistry(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key == null)
                {
                    Logger.Warn("无法打开注册表 Run 键（可能无权限）");
                    return;
                }

                if (enable)
                {
                    // 获取当前可执行文件路径
                    var exePath = System.Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue(AppName, $"\"{exePath}\"");
                        Logger.Info($"已设置开机自启：{exePath}");
                    }
                    else
                    {
                        Logger.Warn("无法获取当前可执行文件路径，开机自启设置失败");
                    }
                }
                else
                {
                    key.DeleteValue(AppName, throwOnMissingValue: false);
                    Logger.Info("已取消开机自启");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("设置开机自启注册表失败", ex);
            }
        }
    }
}
