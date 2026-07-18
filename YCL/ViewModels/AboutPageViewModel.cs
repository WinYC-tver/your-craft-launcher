using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YCL.Core.Update;
using YCL.Core.Utils;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 关于页 ViewModel：展示启动器版本信息、项目介绍，
    /// 并提供"检查更新"按钮（调用 <see cref="IUpdateChecker"/> 检查 GitHub Release）。
    /// </summary>
    public partial class AboutPageViewModel : ViewModelBase
    {
        private readonly IConfigService? _configService;
        private readonly IUpdateChecker? _updateChecker;

        public AboutPageViewModel(IConfigService? configService = null, IUpdateChecker? updateChecker = null)
        {
            _configService = configService;
            _updateChecker = updateChecker;

            // 读取当前启动器版本号（从入口程序集读取，与 UpdateChecker 的版本比较逻辑一致）
            AppVersion = "v" + GetCurrentVersion();
        }

        /// <summary>启动器名称</summary>
        public string AppName => "Your Craft Launcher (YCL)";

        /// <summary>启动器版本号（如 "v1.0.0.0"）</summary>
        public string AppVersion { get; }

        /// <summary>启动器简介</summary>
        public string AppDescription =>
            "一个用 C# / WPF 编写的 Minecraft 启动器。\n" +
            "支持版本下载、模组管理、存档备份、截图浏览、自动更新检查等功能。";

        /// <summary>技术栈信息</summary>
        public string TechStack =>
            "技术栈：.NET 10 / WPF / iNKORE.UI.WPF.Modern / CommunityToolkit.Mvvm";

        /// <summary>
        /// GitHub 仓库链接（从配置的 UpdateRepo 拼接）。
        /// 配置为空时返回空字符串，UI 上隐藏链接。
        /// </summary>
        public string GitHubUrl
        {
            get
            {
                var repo = _configService?.Current?.UpdateRepo;
                return string.IsNullOrWhiteSpace(repo)
                    ? string.Empty
                    : $"https://github.com/{repo.Trim('/')}";
            }
        }

        /// <summary>是否正在检查更新（检查中时禁用按钮）</summary>
        [ObservableProperty]
        private bool _isCheckingUpdate;

        /// <summary>更新检查结果文本（为空时 UI 隐藏结果区）</summary>
        [ObservableProperty]
        private string _updateCheckResult = string.Empty;

        /// <summary>是否发现了新版本（控制"前往下载"按钮的可见性）</summary>
        [ObservableProperty]
        private string? _newVersionDownloadUrl;

        /// <summary>立即检查更新（手动触发）</summary>
        [RelayCommand]
        private async Task CheckUpdateAsync()
        {
            if (_updateChecker == null)
            {
                UpdateCheckResult = "更新检查服务不可用。";
                return;
            }

            IsCheckingUpdate = true;
            NewVersionDownloadUrl = null;
            UpdateCheckResult = "正在检查更新...";
            try
            {
                var release = await _updateChecker.CheckForUpdatesAsync(CancellationToken.None);
                if (release != null)
                {
                    UpdateCheckResult = $"发现新版本 {release.Version}！发布于 {release.PublishedAtDisplay}";
                    NewVersionDownloadUrl = release.ReleaseUrl;

                    // 弹窗提示用户
                    var msg = $"发现新版本 {release.Version}！\n\n" +
                              $"发布时间：{release.PublishedAtDisplay}\n\n" +
                              $"更新内容：\n{release.ReleaseNotes}\n\n" +
                              $"是否前往下载页面？";
                    var result = MessageBox.Show(msg, "发现新版本",
                        MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                    {
                        OpenUrl(release.ReleaseUrl);
                    }
                }
                else
                {
                    UpdateCheckResult = $"当前已是最新版本（{AppVersion}）。";
                }
            }
            catch (Exception ex)
            {
                UpdateCheckResult = "检查更新失败：" + ex.Message;
                Logger.Error("关于页手动检查更新失败", ex);
            }
            finally
            {
                IsCheckingUpdate = false;
            }
        }

        /// <summary>打开 GitHub 仓库页面（按钮点击）</summary>
        [RelayCommand]
        private void OpenGitHub()
        {
            if (!string.IsNullOrEmpty(GitHubUrl))
                OpenUrl(GitHubUrl);
        }

        /// <summary>前往新版本下载页面（按钮点击）</summary>
        [RelayCommand]
        private void GoToDownloadPage()
        {
            if (!string.IsNullOrEmpty(NewVersionDownloadUrl))
                OpenUrl(NewVersionDownloadUrl);
        }

        /// <summary>用系统默认浏览器打开指定 URL</summary>
        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error("打开链接失败：" + url, ex);
            }
        }

        /// <summary>
        /// 获取当前启动器版本号。
        /// 从入口程序集（YCL.exe）读取，与 UpdateChecker 的版本比较逻辑保持一致。
        /// 读取失败时回退到 1.0.0.0。
        /// </summary>
        private static Version GetCurrentVersion()
        {
            try
            {
                var assembly = Assembly.GetEntryAssembly();
                if (assembly != null)
                {
                    var version = assembly.GetName().Version;
                    if (version != null)
                        return version;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"关于页读取当前版本失败：{ex.Message}");
            }
            return new Version(1, 0, 0, 0);
        }
    }
}
