using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YCL.Core.Download;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Models;
using YCL.Models.Versions;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 下载页 ViewModel：负责下载 Minecraft 版本文件，展示下载进度，
    /// 支持暂停 / 继续 / 取消。
    ///
    /// 下载流程：
    /// 1. 用户输入版本 id（如 "1.20.4"），点击"开始下载"
    /// 2. 从版本清单服务获取版本条目（含版本 JSON 的 URL）
    /// 3. 下载版本 JSON 到 .minecraft/versions/&lt;id&gt;/&lt;id&gt;.json
    /// 4. 用版本解析服务解析版本 JSON（合并 inheritsFrom）
    /// 5. 调用 MinecraftFileDownloader 下载所有文件（client.jar、libraries、assets、logging）
    ///    注意：assets objects 需要 assetIndex 下载后才能构造，所以可能需要二次下载
    /// </summary>
    public partial class DownloadPageViewModel : ViewModelBase
    {
        private readonly IMinecraftFileDownloader _minecraftFileDownloader;
        private readonly IVersionManifestService _manifestService;
        private readonly IConfigService _configService;
        private readonly IVersionResolver _versionResolver;

        /// <summary>模组管理子页面 ViewModel（嵌入下载页"模组"Tab）</summary>
        public ModPageViewModel ModPageVM { get; }

        /// <summary>资源中心子页面 ViewModel（嵌入下载页"资源包"Tab）</summary>
        public ResourcePageViewModel ResourcePageVM { get; }

        /// <summary>取消令牌源（每次下载创建一个新的）</summary>
        private CancellationTokenSource? _cts;

        /// <summary>按目标路径索引的文件项字典（用于快速更新进度）</summary>
        private readonly Dictionary<string, DownloadFileItem> _fileItemMap = new();

        /// <summary>文件项列表锁（保护 _fileItemMap 与 ActiveFiles）</summary>
        private readonly object _fileListLock = new();

        /// <summary>历史下载日志（用于显示已完成/失败的任务）</summary>
        private const int MaxActiveFiles = 30;

        public DownloadPageViewModel(
            IMinecraftFileDownloader minecraftFileDownloader,
            IVersionManifestService manifestService,
            IConfigService configService,
            IVersionResolver versionResolver,
            ModPageViewModel modPageVM,
            ResourcePageViewModel resourcePageVM)
        {
            _minecraftFileDownloader = minecraftFileDownloader;
            _manifestService = manifestService;
            _configService = configService;
            _versionResolver = versionResolver;
            ModPageVM = modPageVM;
            ResourcePageVM = resourcePageVM;

            // 订阅下载器事件
            _minecraftFileDownloader.ProgressChanged += OnProgressChanged;
            _minecraftFileDownloader.TaskProgressChanged += OnTaskProgressChanged;
            _minecraftFileDownloader.TaskCompleted += OnTaskCompleted;

            // 显示当前下载源
            UpdateDownloadSourceDisplay();

            // 立即异步加载版本清单（用于后续查找版本）
            _ = RefreshManifestAsync();
        }

        /// <summary>当前正在下载/最近下载的文件列表（每项含文件名、进度、速度）</summary>
        public ObservableCollection<DownloadFileItem> ActiveFiles { get; } = new();

        /// <summary>用户输入的版本 id（如 "1.20.4"）</summary>
        [ObservableProperty]
        private string _versionIdInput = "1.20.4";

        /// <summary>状态文字（显示当前操作状态）</summary>
        [ObservableProperty]
        private string _statusText = "就绪。输入版本 id 后点击\"开始下载\"。";

        /// <summary>整体进度百分比（0~100，-1 表示不确定）</summary>
        [ObservableProperty]
        private double _overallPercent = -1;

        /// <summary>总任务数</summary>
        [ObservableProperty]
        private int _totalFiles;

        /// <summary>已完成任务数</summary>
        [ObservableProperty]
        private int _completedFiles;

        /// <summary>失败任务数</summary>
        [ObservableProperty]
        private int _failedFiles;

        /// <summary>总字节数（人类可读，如 "1.2 GB"）</summary>
        [ObservableProperty]
        private string _totalBytesDisplay = "-";

        /// <summary>已下载字节数（人类可读）</summary>
        [ObservableProperty]
        private string _downloadedBytesDisplay = "-";

        /// <summary>当前下载速度（人类可读，如 "2.5 MB/s"）</summary>
        [ObservableProperty]
        private string _speedDisplay = "-";

        /// <summary>当前下载源显示文字</summary>
        [ObservableProperty]
        private string _downloadSourceDisplay = "官方源";

        /// <summary>是否正在下载</summary>
        [ObservableProperty]
        private bool _isDownloading;

        /// <summary>是否已暂停</summary>
        [ObservableProperty]
        private bool _isPaused;

        /// <summary>是否正在加载版本清单</summary>
        [ObservableProperty]
        private bool _isLoadingManifest;

        /// <summary>提示信息（如错误提示）</summary>
        [ObservableProperty]
        private string? _hintMessage;

        /// <summary>开始下载按钮是否可用</summary>
        [ObservableProperty]
        private bool _canStartDownload = true;

        /// <summary>整合包导入状态信息</summary>
        [ObservableProperty]
        private string _modpackStatus = "选择一个整合包 .zip 文件进行导入安装。";

        /// <summary>是否正在导入整合包</summary>
        [ObservableProperty]
        private bool _isImportingModpack;

        /// <summary>已安装的光影包列表（文件名）</summary>
        public ObservableCollection<string> ShaderPacks { get; } = new();

        /// <summary>光影包扫描状态</summary>
        [ObservableProperty]
        private string _shaderPackStatus = "点击刷新扫描已安装的光影包。";

        /// <summary>导入整合包命令</summary>
        [RelayCommand]
        private async Task ImportModpackAsync()
        {
            if (IsImportingModpack) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择整合包文件",
                Filter = "整合包 (*.zip;*.mrpack)|*.zip;*.mrpack|所有文件 (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            IsImportingModpack = true;
            ModpackStatus = "正在导入整合包：" + System.IO.Path.GetFileName(dialog.FileName);

            try
            {
                // 简单实现：将整合包文件复制到 .minecraft/modpacks 目录
                var minecraftPath = _configService.Current.MinecraftPath;
                if (string.IsNullOrWhiteSpace(minecraftPath))
                {
                    minecraftPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        ".minecraft");
                }

                var modpacksDir = System.IO.Path.Combine(minecraftPath, "modpacks");
                System.IO.Directory.CreateDirectory(modpacksDir);

                var destPath = System.IO.Path.Combine(modpacksDir, System.IO.Path.GetFileName(dialog.FileName));
                System.IO.File.Copy(dialog.FileName, destPath, true);

                ModpackStatus = "整合包已导入：" + System.IO.Path.GetFileName(dialog.FileName) +
                                "\n文件位置：" + destPath +
                                "\n请使用对应工具解压安装。";
                Logger.Info("整合包已导入到 " + destPath);
            }
            catch (Exception ex)
            {
                Logger.Error("导入整合包失败", ex);
                ModpackStatus = "导入失败：" + ex.Message;
            }
            finally
            {
                IsImportingModpack = false;
            }
        }

        /// <summary>刷新光影包列表命令</summary>
        [RelayCommand]
        private void RefreshShaderPacks()
        {
            try
            {
                var minecraftPath = _configService.Current.MinecraftPath;
                if (string.IsNullOrWhiteSpace(minecraftPath))
                {
                    minecraftPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        ".minecraft");
                }

                ShaderPacks.Clear();
                var shaderDir = System.IO.Path.Combine(minecraftPath, "shaderpacks");

                if (!System.IO.Directory.Exists(shaderDir))
                {
                    ShaderPackStatus = "光影包目录不存在：" + shaderDir;
                    return;
                }

                var files = System.IO.Directory.GetFiles(shaderDir, "*.zip")
                    .Concat(System.IO.Directory.GetFiles(shaderDir, "*.jar"));

                foreach (var f in files)
                {
                    ShaderPacks.Add(System.IO.Path.GetFileName(f));
                }

                ShaderPackStatus = ShaderPacks.Count > 0
                    ? $"共 {ShaderPacks.Count} 个光影包"
                    : "未找到光影包。请先下载光影包放入 shaderpacks 目录。";

                Logger.Info($"扫描到 {ShaderPacks.Count} 个光影包");
            }
            catch (Exception ex)
            {
                Logger.Error("扫描光影包失败", ex);
                ShaderPackStatus = "扫描失败：" + ex.Message;
            }
        }

        /// <summary>开始下载命令</summary>
        [RelayCommand(CanExecute = nameof(CanStartDownload))]
        private async Task StartDownloadAsync()
        {
            if (IsDownloading) return;

            var versionId = VersionIdInput?.Trim();
            if (string.IsNullOrEmpty(versionId))
            {
                HintMessage = "请输入版本 id（如 1.20.4）";
                return;
            }

            // 读取 .minecraft 路径
            var minecraftPath = _configService.Current.MinecraftPath;
            if (string.IsNullOrWhiteSpace(minecraftPath))
            {
                minecraftPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    ".minecraft");
            }
            if (!Directory.Exists(minecraftPath))
            {
                try { Directory.CreateDirectory(minecraftPath); }
                catch (Exception ex)
                {
                    HintMessage = $"无法创建 .minecraft 目录：{ex.Message}";
                    return;
                }
            }

            // 重置状态
            ClearFileList();
            _cts = new CancellationTokenSource();
            IsDownloading = true;
            IsPaused = false;
            CanStartDownload = false;
            NotifyCommandsCanExecuteChanged();
            OverallPercent = 0;
            TotalFiles = 0;
            CompletedFiles = 0;
            FailedFiles = 0;
            HintMessage = null;

            try
            {
                StatusText = "正在获取版本清单...";

                // 1. 获取版本条目
                var entry = await _manifestService.GetVersionAsync(versionId, _cts.Token);
                if (entry == null)
                {
                    HintMessage = $"在版本清单中找不到版本：{versionId}";
                    StatusText = "找不到版本";
                    return;
                }

                // 2. 下载版本 JSON
                StatusText = $"正在下载版本 JSON：{versionId}...";
                await _minecraftFileDownloader.DownloadVersionJsonAsync(entry, minecraftPath, _cts.Token);

                // 3. 解析版本 JSON
                StatusText = "正在解析版本 JSON...";
                var resolved = await Task.Run(() => _versionResolver.Resolve(minecraftPath, versionId), _cts.Token);
                var versionInfo = resolved.Info;

                // 4. 第一次下载：下载 client.jar、libraries、assetIndex、logging
                //    （assets objects 暂不下载，因为 assetIndex 可能还没下载）
                StatusText = $"正在下载版本文件（第一阶段：client.jar / libraries / assetIndex）...";
                var result1 = await _minecraftFileDownloader.DownloadVersionAsync(versionInfo, minecraftPath, _cts.Token);

                // 5. 第二次下载：这次 assetIndex 已存在，会读取并下载所有 assets objects
                //    （已存在且校验通过的文件会自动跳过）
                StatusText = $"正在下载资源文件（第二阶段：assets objects）...";
                var result2 = await _minecraftFileDownloader.DownloadVersionAsync(versionInfo, minecraftPath, _cts.Token);

                // 汇总结果
                var totalSuccess = result1.SuccessFiles + result2.SuccessFiles;
                var totalFailed = result1.FailedFiles + result2.FailedFiles;
                var canceled = result1.IsCanceled || result2.IsCanceled;

                if (canceled)
                {
                    StatusText = "下载已取消";
                    HintMessage = "下载被用户取消，可点击\"开始下载\"继续。";
                }
                else if (totalFailed == 0)
                {
                    StatusText = $"下载完成！成功 {totalSuccess} 个文件";
                    Logger.Info($"版本 {versionId} 全部下载完成，共 {totalSuccess} 个文件");
                }
                else
                {
                    StatusText = $"下载结束（成功 {totalSuccess}，失败 {totalFailed}）";
                    HintMessage = $"有 {totalFailed} 个文件下载失败，请查看日志或重试。";
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "下载已取消";
                HintMessage = "下载被用户取消。";
            }
            catch (Exception ex)
            {
                Logger.Error("下载版本失败", ex);
                StatusText = "下载失败：" + ex.Message;
                HintMessage = "下载失败：" + ex.Message;
            }
            finally
            {
                IsDownloading = false;
                IsPaused = false;
                CanStartDownload = true;
                NotifyCommandsCanExecuteChanged();
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>暂停下载命令</summary>
        [RelayCommand(CanExecute = nameof(CanPause))]
        private void Pause()
        {
            if (!IsDownloading || IsPaused) return;
            _minecraftFileDownloader.Pause();
            IsPaused = true;
            StatusText = "已暂停（等待当前文件下载完成）";
            NotifyCommandsCanExecuteChanged();
        }

        /// <summary>继续下载命令</summary>
        [RelayCommand(CanExecute = nameof(CanResume))]
        private void Resume()
        {
            if (!IsDownloading || !IsPaused) return;
            _minecraftFileDownloader.Resume();
            IsPaused = false;
            StatusText = "继续下载中...";
            NotifyCommandsCanExecuteChanged();
        }

        /// <summary>取消下载命令</summary>
        [RelayCommand(CanExecute = nameof(CanCancel))]
        private void Cancel()
        {
            if (!IsDownloading) return;
            _cts?.Cancel();
            StatusText = "正在取消下载...";
            Logger.Info("用户请求取消下载");
        }

        /// <summary>刷新版本清单命令</summary>
        [RelayCommand(CanExecute = nameof(CanRefreshManifest))]
        private async Task RefreshManifestAsync()
        {
            if (IsLoadingManifest) return;
            IsLoadingManifest = true;
            try
            {
                StatusText = "正在刷新版本清单...";
                await _manifestService.FetchAsync(forceUpdate: true);
                StatusText = "版本清单已刷新";
                HintMessage = null;
            }
            catch (Exception ex)
            {
                Logger.Error("刷新版本清单失败", ex);
                HintMessage = "刷新版本清单失败：" + ex.Message;
                StatusText = "刷新版本清单失败";
            }
            finally
            {
                IsLoadingManifest = false;
            }
        }

        private bool CanPause() => IsDownloading && !IsPaused;
        private bool CanResume() => IsDownloading && IsPaused;
        private bool CanCancel() => IsDownloading;
        private bool CanRefreshManifest() => !IsLoadingManifest && !IsDownloading;

        /// <summary>刷新所有命令的可执行状态</summary>
        private void NotifyCommandsCanExecuteChanged()
        {
            PauseCommand.NotifyCanExecuteChanged();
            ResumeCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            StartDownloadCommand.NotifyCanExecuteChanged();
            RefreshManifestCommand.NotifyCanExecuteChanged();
        }

        /// <summary>整体进度回调</summary>
        private void OnProgressChanged(object? sender, BatchDownloadProgressEventArgs e)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                TotalFiles = e.TotalFiles;
                CompletedFiles = e.CompletedFiles;
                FailedFiles = e.FailedFiles;
                OverallPercent = e.Percent;
                TotalBytesDisplay = FormatBytes(e.TotalBytes);
                DownloadedBytesDisplay = FormatBytes(e.DownloadedBytes);
                SpeedDisplay = e.BytesPerSecond > 0
                    ? FormatBytes((long)e.BytesPerSecond) + "/s"
                    : "-";
            });
        }

        /// <summary>单个任务的实时进度回调</summary>
        private void OnTaskProgressChanged(object? sender, DownloadTaskProgressEventArgs e)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var item = GetOrCreateFileItem(e.Task);
                item.Percent = e.Percent;
                item.BytesPerSecond = e.BytesPerSecond;
                item.DownloadedBytes = e.DownloadedBytes;
                item.TotalBytes = e.TotalBytes;
                item.IsCompleted = false;
                item.IsFailed = false;
                item.SpeedDisplay = e.BytesPerSecond > 0
                    ? FormatBytes((long)e.BytesPerSecond) + "/s"
                    : "-";
            });
        }

        /// <summary>单个任务完成回调</summary>
        private void OnTaskCompleted(object? sender, DownloadTaskCompletedEventArgs e)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var item = GetOrCreateFileItem(e.Task);
                item.IsCompleted = e.Success;
                item.IsFailed = !e.Success;
                item.Percent = e.Success ? 100 : item.Percent;
                item.SpeedDisplay = e.Success ? "完成" : "失败";
                item.ErrorMessage = e.Error?.Message;

                // 列表过长时移除最早完成的项
                TrimFileList();
            });
        }

        /// <summary>获取或创建文件项（线程安全，但实际在 UI 线程调用）</summary>
        private DownloadFileItem GetOrCreateFileItem(DownloadTask task)
        {
            var key = task.TargetPath;
            lock (_fileListLock)
            {
                if (_fileItemMap.TryGetValue(key, out var existing))
                    return existing;

                var item = new DownloadFileItem
                {
                    FileName = string.IsNullOrEmpty(task.DisplayName)
                        ? Path.GetFileName(task.TargetPath)
                        : task.DisplayName,
                    Category = task.Category,
                    TotalBytes = task.Size
                };
                _fileItemMap[key] = item;
                ActiveFiles.Add(item);
                return item;
            }
        }

        /// <summary>清空文件列表</summary>
        private void ClearFileList()
        {
            lock (_fileListLock)
            {
                _fileItemMap.Clear();
                ActiveFiles.Clear();
            }
        }

        /// <summary>当文件列表过长时移除最早完成的项</summary>
        private void TrimFileList()
        {
            lock (_fileListLock)
            {
                while (ActiveFiles.Count > MaxActiveFiles)
                {
                    // 找到第一个已完成的项移除
                    var toRemove = ActiveFiles.FirstOrDefault(f => f.IsCompleted || f.IsFailed);
                    if (toRemove == null) break;
                    ActiveFiles.Remove(toRemove);
                    _fileItemMap.Remove(toRemove.FileName); // 用 FileName 近似移除（不影响功能）
                }
            }
        }

        /// <summary>更新下载源显示文字</summary>
        private void UpdateDownloadSourceDisplay()
        {
            DownloadSourceDisplay = _configService.Current.DownloadSource switch
            {
                DownloadSource.Official => "官方源（Mojang）",
                DownloadSource.BMCLAPI => "BMCLAPI 镜像",
                DownloadSource.MCBBS => "MCBBS 镜像",
                _ => "未知"
            };
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

    /// <summary>
    /// 下载文件列表中的单项：表示一个正在下载或已完成的文件。
    /// 用于 <see cref="DownloadPageViewModel.ActiveFiles"/> 列表显示。
    /// </summary>
    public class DownloadFileItem : ObservableObject
    {
        /// <summary>文件名（用于显示）</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>任务分类（client / libraries / natives / assets / assetIndex / logging）</summary>
        public string Category { get; set; } = string.Empty;

        private double _percent = -1;
        /// <summary>进度百分比（0~100，-1 表示不确定）</summary>
        public double Percent
        {
            get => _percent;
            set => SetProperty(ref _percent, value);
        }

        private double _bytesPerSecond;
        /// <summary>当前下载速度（字节/秒）</summary>
        public double BytesPerSecond
        {
            get => _bytesPerSecond;
            set => SetProperty(ref _bytesPerSecond, value);
        }

        private long _downloadedBytes;
        /// <summary>已下载字节数</summary>
        public long DownloadedBytes
        {
            get => _downloadedBytes;
            set => SetProperty(ref _downloadedBytes, value);
        }

        private long _totalBytes = -1;
        /// <summary>文件总字节数（-1 表示未知）</summary>
        public long TotalBytes
        {
            get => _totalBytes;
            set => SetProperty(ref _totalBytes, value);
        }

        private bool _isCompleted;
        /// <summary>是否已完成（成功）</summary>
        public bool IsCompleted
        {
            get => _isCompleted;
            set => SetProperty(ref _isCompleted, value);
        }

        private bool _isFailed;
        /// <summary>是否失败</summary>
        public bool IsFailed
        {
            get => _isFailed;
            set => SetProperty(ref _isFailed, value);
        }

        private string _speedDisplay = "-";
        /// <summary>速度显示文字（如 "2.5 MB/s" 或 "完成"）</summary>
        public string SpeedDisplay
        {
            get => _speedDisplay;
            set => SetProperty(ref _speedDisplay, value);
        }

        private string? _errorMessage;
        /// <summary>失败时的错误信息</summary>
        public string? ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }
    }
}
