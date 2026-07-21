using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
    /// 设置页 ViewModel（v26.1.0.5 重构）：分类排放 + 设置搜索。
    /// 主页面包含六个分类：启动与管理、游戏设置、个性化、下载、高级、关于。
    /// 每个设置项变化时立即通过 <see cref="IConfigService"/> 保存到配置文件，
    /// 大部分设置立即生效（主题即时应用，启动参数下次启动游戏时生效）。
    /// </summary>
    public partial class SettingsPageViewModel : ViewModelBase
    {
        private readonly IThemeService _themeService;
        private readonly IConfigService _configService;
        private readonly INavigationService _navigationService;
        private readonly IUpdateChecker? _updateChecker;
        private readonly LocalizationService? _localizationService;

        // 开机自启注册表路径与键名
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "YCL";

        public SettingsPageViewModel(
            IThemeService themeService,
            IConfigService configService,
            INavigationService navigationService,
            IUpdateChecker? updateChecker = null,
            LocalizationService? localizationService = null)
        {
            _themeService = themeService;
            _configService = configService;
            _navigationService = navigationService;
            _updateChecker = updateChecker;
            _localizationService = localizationService;

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
            _downloadEngine = config.DownloadEngine;

            // ====== 系统设置 ======
            _checkUpdateOnStartup = config.CheckUpdateOnStartup;
            _updateRepo = config.UpdateRepo;
            _launchOnStartup = config.LaunchOnStartup;

            // ====== 个性化设置（v26） ======
            _selectedBackdrop = config.Backdrop;
            _wallpaperPath = config.WallpaperPath;
            _wallpaperOpacity = config.WallpaperOpacity;
            _enableAnimations = config.EnableAnimations;
            _homepageType = config.HomepageType;
            _homepageLocalPath = config.HomepageLocalPath;
            _homepageUrl = config.HomepageUrl;

            // ====== 语言设置（v26.1.0.5）======
            _selectedLanguage = config.Language;
        }

        // ================================================================
        // 分类导航 + 设置搜索（v26.1.0.5）
        // ================================================================

        /// <summary>当前选中的分类 Key（Launch/Game/Personalize/Download/Advanced/About）</summary>
        [ObservableProperty]
        private string _selectedCategoryKey = "Personalize";

        /// <summary>设置搜索关键字（为空时显示所有分类）</summary>
        [ObservableProperty]
        private string _searchKeyword = string.Empty;

        /// <summary>切换分类</summary>
        [RelayCommand]
        private void SelectCategory(string key)
        {
            SelectedCategoryKey = key;
            // 选择分类时清空搜索，避免搜索状态干扰
            if (!string.IsNullOrEmpty(SearchKeyword))
                SearchKeyword = string.Empty;
        }

        /// <summary>跳转到完整关于页</summary>
        [RelayCommand]
        private void OpenAboutPage()
        {
            _navigationService.NavigateTo("About");
        }

        // ================================================================
        // 关于信息（v26.1.0.5）
        // ================================================================

        /// <summary>产品版本号（显示用，来自 InformationalVersion）</summary>
        public string AppVersion
        {
            get
            {
                var ver = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                return string.IsNullOrWhiteSpace(ver) ? typeof(SettingsPageViewModel).Assembly.GetName().Version?.ToString() ?? "未知" : ver;
            }
        }

        /// <summary>编译号（从 InformationalVersion 的 build 段解析，如 v0719.2654.0）</summary>
        public string BuildNumber
        {
            get
            {
                var ver = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (string.IsNullOrWhiteSpace(ver)) return "未知";
                var idx = ver.IndexOf("+build.");
                if (idx >= 0) return "v" + ver.Substring(idx + "+build.".Length);
                return "未知";
            }
        }

        // ================================================================
        // 语言设置（v26.1.0.5 多语言支持）
        // ================================================================

        [ObservableProperty]
        private Language _selectedLanguage;

        partial void OnSelectedLanguageChanged(Language value)
        {
            _localizationService?.SetLanguage(value);
        }

        // ================================================================
        // 主题设置
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
        // 启动与管理
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

        /// <summary>跳转到 Java 管理页（用户可在那里自动检测 / 在线安装 Java）</summary>
        [RelayCommand]
        private void GoToJavaPage()
        {
            _navigationService.NavigateTo("Java");
        }

        /// <summary>
        /// 在线安装 Java：打开 BellSoft 下载页面（Liberica JDK）。
        /// 对应规格：用户没有游戏所需 Java，或在 Java 管理界面点击"在线安装 Java"时调用。
        /// </summary>
        [RelayCommand]
        private void InstallJavaOnline()
        {
            const string bellSoftUrl = "https://bell-sw.com/pages/downloads/?version=java-25&vtabs=true";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = bellSoftUrl,
                    UseShellExecute = true
                });
                Logger.Info($"已打开 BellSoft Java 下载页面：{bellSoftUrl}");
            }
            catch (Exception ex)
            {
                Logger.Error("打开 BellSoft Java 下载页面失败", ex);
                MessageBox.Show($"无法打开浏览器，请手动访问：\n{bellSoftUrl}",
                    "在线安装 Java", MessageBoxButton.OK, MessageBoxImage.Information);
            }
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
            // 限制在 1~64 之间（v26.1.0.5 扩展上限至 64）
            if (value < 1) value = 1;
            if (value > 64) value = 64;
            _configService.Current.DownloadThreads = value;
            _configService.Save();
        }

        [ObservableProperty]
        private int _retryCount;

        partial void OnRetryCountChanged(int value)
        {
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

        /// <summary>资源文件名格式（v26.1.0.5）：{name}=名称，{file}=原文件名</summary>
        [ObservableProperty]
        private string _resourceFileNameFormat = "{name}-{file}";

        partial void OnResourceFileNameFormatChanged(string value)
        {
            _configService.Current.ResourceFileNameFormat = value ?? "{name}-{file}";
            _configService.Save();
        }

        /// <summary>下载引擎（默认 / PCL CE / Ghost Downloader 3）</summary>
        [ObservableProperty]
        private DownloadEngine _downloadEngine;

        partial void OnDownloadEngineChanged(DownloadEngine value)
        {
            _configService.Current.DownloadEngine = value;
            _configService.Save();
        }

        // ================================================================
        // 高级 / 系统
        // ================================================================

        /// <summary>日志文件所在目录（只读显示）</summary>
        public string LogPath => Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "YCL", "logs");

        /// <summary>崩溃报告目录（v26.1.0.5：启动器文件夹/clog）</summary>
        public string CrashLogPath
        {
            get
            {
                try
                {
                    var exeDir = Path.GetDirectoryName(System.Environment.ProcessPath) ?? ".";
                    return Path.Combine(exeDir, "clog");
                }
                catch
                {
                    return "clog";
                }
            }
        }

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
            SetStartupRegistry(value);
        }

        /// <summary>打开日志文件夹</summary>
        [RelayCommand]
        private void OpenLogFolder()
        {
            try
            {
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

        /// <summary>打开崩溃报告文件夹</summary>
        [RelayCommand]
        private void OpenCrashLogFolder()
        {
            try
            {
                Directory.CreateDirectory(CrashLogPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = CrashLogPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error("打开崩溃报告文件夹失败", ex);
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
                    var msg = $"发现新版本 {release.Version}！\n\n" +
                              $"发布时间：{release.PublishedAtDisplay}\n\n" +
                              $"更新内容：\n{release.ReleaseNotes}\n\n" +
                              $"是否前往下载页面？";
                    var result = MessageBox.Show(msg, "发现新版本",
                        MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                    {
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
        // 个性化设置（外观）
        // ================================================================

        /// <summary>背景效果（默认 / 亚克力 / 云母 / 云母Alt）</summary>
        [ObservableProperty]
        private BackdropType _selectedBackdrop;

        partial void OnSelectedBackdropChanged(BackdropType value)
        {
            _configService.Current.Backdrop = value;
            _configService.Save();
            _themeService.ApplyBackdrop(value);
        }

        /// <summary>自定义壁纸路径（为 null 时表示不使用壁纸）</summary>
        [ObservableProperty]
        private string? _wallpaperPath;

        partial void OnWallpaperPathChanged(string? value)
        {
            _configService.Current.WallpaperPath = value;
            _configService.Save();
            _themeService.ApplyWallpaper(value, WallpaperOpacity);
        }

        /// <summary>壁纸不透明度（0~1）</summary>
        [ObservableProperty]
        private double _wallpaperOpacity;

        partial void OnWallpaperOpacityChanged(double value)
        {
            if (value < 0) value = 0;
            if (value > 1) value = 1;
            _configService.Current.WallpaperOpacity = value;
            _configService.Save();
            _themeService.ApplyWallpaper(WallpaperPath, value);
        }

        /// <summary>是否启用页面切换动画</summary>
        [ObservableProperty]
        private bool _enableAnimations;

        partial void OnEnableAnimationsChanged(bool value)
        {
            _configService.Current.EnableAnimations = value;
            _configService.Save();
        }

        /// <summary>主页类型（预设 / 联网 / 本地）</summary>
        [ObservableProperty]
        private HomepageType _homepageType;

        partial void OnHomepageTypeChanged(HomepageType value)
        {
            _configService.Current.HomepageType = value;
            _configService.Save();
        }

        /// <summary>本地主页文件路径（HomepageType=Local 时使用）</summary>
        [ObservableProperty]
        private string _homepageLocalPath;

        partial void OnHomepageLocalPathChanged(string value)
        {
            _configService.Current.HomepageLocalPath = value ?? string.Empty;
            _configService.Save();
        }

        /// <summary>联网主页 URL（HomepageType=Online 时使用）</summary>
        [ObservableProperty]
        private string _homepageUrl;

        partial void OnHomepageUrlChanged(string value)
        {
            _configService.Current.HomepageUrl = value ?? string.Empty;
            _configService.Save();
        }

        /// <summary>浏览选择本地主页文件（HTML 或图片）</summary>
        [RelayCommand]
        private void BrowseHomepageLocalPath()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择本地主页文件",
                Filter = "主页文件|*.html;*.htm;*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
                CheckFileExists = true
            };

            if (!string.IsNullOrWhiteSpace(HomepageLocalPath))
            {
                var dir = Path.GetDirectoryName(HomepageLocalPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    dialog.InitialDirectory = dir;
            }

            if (dialog.ShowDialog() == true)
            {
                HomepageLocalPath = dialog.FileName;
            }
        }

        /// <summary>浏览选择壁纸图片（png/jpg）</summary>
        [RelayCommand]
        private void BrowseWallpaper()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择壁纸图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
                CheckFileExists = true
            };

            if (!string.IsNullOrWhiteSpace(WallpaperPath))
            {
                var dir = Path.GetDirectoryName(WallpaperPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    dialog.InitialDirectory = dir;
            }

            if (dialog.ShowDialog() == true)
            {
                WallpaperPath = dialog.FileName;
            }
        }

        /// <summary>清除当前自定义壁纸</summary>
        [RelayCommand]
        private void ClearWallpaper()
        {
            WallpaperPath = null;
        }

        // ================================================================
        // 私有辅助方法
        // ================================================================

        /// <summary>
        /// 写入或删除开机自启注册表项。
        /// 路径：HKCU\Software\Microsoft\Windows\CurrentVersion\Run
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
