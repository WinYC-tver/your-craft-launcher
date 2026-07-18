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
using Microsoft.Win32;
using YCL.Core.Java;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// Java 管理页 ViewModel：负责检测、安装、添加、选择 Java 运行时。
    ///
    /// 主要功能：
    /// 1. 检测系统已安装的 Java（IJavaDetector.DetectAsync）
    /// 2. 安装新 Java（IJavaInstaller.InstallAsync，从 Adoptium 下载）
    /// 3. 手动添加 Java（弹 OpenFileDialog 选 javaw.exe）
    /// 4. 设为当前 Java（更新 config.JavaPath）
    /// </summary>
    public partial class JavaPageViewModel : ViewModelBase
    {
        private readonly IJavaDetector _javaDetector;
        private readonly IJavaInstaller _javaInstaller;
        private readonly IConfigService _configService;

        /// <summary>安装任务的取消令牌源</summary>
        private CancellationTokenSource? _installCts;

        public JavaPageViewModel(
            IJavaDetector javaDetector,
            IJavaInstaller javaInstaller,
            IConfigService configService)
        {
            _javaDetector = javaDetector;
            _javaInstaller = javaInstaller;
            _configService = configService;

            // 立即异步检测一次
            _ = RefreshAsync();
        }

        /// <summary>已检测到的 Java 列表</summary>
        public ObservableCollection<JavaInfo> Javas { get; } = new();

        /// <summary>当前选中的 Java（用于高亮显示当前默认 Java）</summary>
        [ObservableProperty]
        private JavaInfo? _selectedJava;

        /// <summary>是否正在检测 Java</summary>
        [ObservableProperty]
        private bool _isDetecting;

        /// <summary>是否正在安装 Java</summary>
        [ObservableProperty]
        private bool _isInstalling;

        /// <summary>状态消息（用于显示提示或错误）</summary>
        [ObservableProperty]
        private string? _statusMessage;

        /// <summary>安装进度百分比（0~100，-1 表示不确定）</summary>
        [ObservableProperty]
        private double _installPercent = -1;

        /// <summary>当前默认 Java 路径（从 config 读取，用于高亮匹配）</summary>
        public string CurrentJavaPath => _configService.Current.JavaPath ?? string.Empty;

        /// <summary>刷新 Java 列表命令</summary>
        [RelayCommand]
        private async Task RefreshAsync()
        {
            if (IsDetecting) return;
            IsDetecting = true;
            StatusMessage = "正在扫描系统 Java...";
            try
            {
                var javas = await _javaDetector.DetectAsync();

                // 读取当前默认 Java 路径
                var currentPath = _configService.Current.JavaPath;

                Javas.Clear();
                foreach (var j in javas)
                {
                    // 标记当前默认 Java（按路径匹配）
                    j.IsCurrent = !string.IsNullOrEmpty(currentPath) &&
                                  string.Equals(j.Path, currentPath, StringComparison.OrdinalIgnoreCase);
                    Javas.Add(j);
                }

                // 高亮选中当前默认 Java
                SelectedJava = javas.FirstOrDefault(j => j.IsCurrent) ?? Javas.FirstOrDefault();

                if (Javas.Count > 0)
                    StatusMessage = $"检测到 {Javas.Count} 个 Java 运行时";
                else
                    StatusMessage = "未检测到任何 Java，请点击\"安装 Java\"或\"手动添加\"。";

                Logger.Info($"Java 页扫描到 {Javas.Count} 个 Java");
            }
            catch (Exception ex)
            {
                Logger.Error("Java 检测失败", ex);
                StatusMessage = "检测失败：" + ex.Message;
            }
            finally
            {
                IsDetecting = false;
            }
        }

        /// <summary>设为当前 Java 命令：更新 config.JavaPath</summary>
        [RelayCommand]
        private void SetAsCurrent(JavaInfo? java)
        {
            if (java == null) return;

            // 清除其他 Java 的 IsCurrent，设置当前 Java 的 IsCurrent
            foreach (var j in Javas)
                j.IsCurrent = false;
            java.IsCurrent = true;

            _configService.Current.JavaPath = java.Path;
            _configService.Save();
            SelectedJava = java;
            OnPropertyChanged(nameof(CurrentJavaPath));
            StatusMessage = $"已设为默认 Java：{java.DisplayName}";
            Logger.Info($"已设为默认 Java：{java.Path}");
        }

        /// <summary>安装 Java 命令：弹对话框选择版本，下载安装</summary>
        [RelayCommand(CanExecute = nameof(CanInstall))]
        private async Task InstallJavaAsync()
        {
            try
            {
                StatusMessage = "正在获取可用 Java 版本列表...";
                var releases = await _javaInstaller.ListAvailableAsync();

                if (releases.Count == 0)
                {
                    StatusMessage = "未能获取到可用 Java 版本列表，请检查网络连接。";
                    return;
                }

                // 构造对话框 UI
                var versionList = new ListBox
                {
                    MinHeight = 280,
                    MaxHeight = 400,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                foreach (var r in releases)
                {
                    versionList.Items.Add(new
                    {
                        Display = r.DisplayName,
                        Version = r.MajorVersion
                    });
                }
                versionList.DisplayMemberPath = "Display";
                versionList.SelectedValuePath = "Version";
                versionList.SelectedIndex = 0;

                var dialog = new ContentDialog
                {
                    Title = "安装 Java（从 Adoptium/Eclipse Temurin 下载）",
                    PrimaryButtonText = "安装",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "选择要安装的 Java 版本（建议选 LTS 长期支持版本）：",
                                TextWrapping = TextWrapping.Wrap
                            },
                            versionList
                        }
                    }
                };

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                if (versionList.SelectedValue is not int majorVersion)
                {
                    StatusMessage = "请先在列表中选择一个 Java 版本。";
                    return;
                }

                await InstallJavaCoreAsync(majorVersion);
            }
            catch (Exception ex)
            {
                Logger.Error("打开安装 Java 对话框失败", ex);
                StatusMessage = "获取 Java 版本列表失败：" + ex.Message;
            }
        }

        /// <summary>判断是否可以安装（不在安装中、不在检测中）</summary>
        private bool CanInstall() => !IsInstalling && !IsDetecting;

        /// <summary>取消安装命令</summary>
        [RelayCommand(CanExecute = nameof(IsInstalling))]
        private void CancelInstall()
        {
            _installCts?.Cancel();
            StatusMessage = "正在取消安装...";
            Logger.Info("用户请求取消 Java 安装");
        }

        /// <summary>实际执行 Java 安装</summary>
        private async Task InstallJavaCoreAsync(int majorVersion)
        {
            IsInstalling = true;
            InstallPercent = 0;
            StatusMessage = $"准备安装 Java {majorVersion}...";

            _installCts = new CancellationTokenSource();

            var progress = new Progress<InstallProgress>(p =>
            {
                if (p.TotalBytes > 0 && p.CompletedBytes > 0)
                {
                    InstallPercent = (double)p.CompletedBytes / p.TotalBytes * 100.0;
                    var mbDone = p.CompletedBytes / 1024.0 / 1024.0;
                    var mbTotal = p.TotalBytes / 1024.0 / 1024.0;
                    StatusMessage = $"{p.PhaseText}：{p.CurrentFile}（{mbDone:F1}/{mbTotal:F1} MB）";
                }
                else
                {
                    InstallPercent = p.Percent;
                    StatusMessage = $"{p.PhaseText}：{p.CurrentFile}";
                }
            });

            try
            {
                Logger.Info($"开始安装 Java {majorVersion}");
                var javaPath = await _javaInstaller.InstallAsync(majorVersion, progress, _installCts.Token);

                InstallPercent = 100;
                StatusMessage = $"Java {majorVersion} 安装完成！已设为默认 Java。";

                // 自动设为默认 Java
                _configService.Current.JavaPath = javaPath;
                _configService.Save();
                OnPropertyChanged(nameof(CurrentJavaPath));

                // 重新扫描
                await RefreshAsync();
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Java 安装已取消。";
                InstallPercent = -1;
            }
            catch (Exception ex)
            {
                Logger.Error("Java 安装失败", ex);
                StatusMessage = "Java 安装失败：" + ex.Message;
            }
            finally
            {
                IsInstalling = false;
                _installCts?.Dispose();
                _installCts = null;
            }
        }

        /// <summary>手动添加 Java 命令：弹 OpenFileDialog 选 javaw.exe</summary>
        [RelayCommand]
        private void AddManually()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 javaw.exe 文件",
                Filter = "Java 可执行文件 (javaw.exe)|javaw.exe|所有可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                FileName = "javaw.exe",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true) return;

            var javaPath = dialog.FileName;
            if (!File.Exists(javaPath))
            {
                StatusMessage = "所选文件不存在。";
                return;
            }

            // 把手动添加的 Java 加入列表（如果已存在则不重复）
            var existing = Javas.FirstOrDefault(j =>
                string.Equals(j.Path, javaPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                // 已存在：直接设为默认
                SetAsCurrent(existing);
                StatusMessage = "该 Java 已在列表中，已设为默认。";
                return;
            }

            // 添加一个简化信息的 Java（路径已知，其他信息标记为未知，下次刷新会重新探测）
            var info = new JavaInfo
            {
                Path = javaPath,
                Version = 0,
                VersionString = "手动添加（重新检测后显示版本）",
                IsJdk = true,
                Architecture = "未知"
            };

            // 清除其他 Java 的 IsCurrent，标记新添加的为当前
            foreach (var j in Javas)
                j.IsCurrent = false;
            info.IsCurrent = true;
            Javas.Add(info);
            SelectedJava = info;

            // 自动设为默认 Java
            _configService.Current.JavaPath = javaPath;
            _configService.Save();
            OnPropertyChanged(nameof(CurrentJavaPath));

            StatusMessage = $"已添加并设为默认 Java：{javaPath}";
            Logger.Info($"手动添加 Java：{javaPath}");

            // 触发一次刷新以探测版本信息
            _ = RefreshAsync();
        }
    }
}
