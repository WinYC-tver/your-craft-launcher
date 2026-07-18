using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YCL.Core.Screenshots;
using YCL.Core.Utils;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 截图页 ViewModel：负责扫描截图列表、异步加载缩略图、打开、删除截图。
    /// 缩略图在后台线程加载并 Freeze，避免阻塞 UI 线程和文件被锁住。
    /// </summary>
    public partial class ScreenshotsPageViewModel : ViewModelBase
    {
        private readonly IConfigService _configService;
        private readonly IScreenshotManager _screenshotManager;

        public ScreenshotsPageViewModel(IConfigService configService, IScreenshotManager screenshotManager)
        {
            _configService = configService;
            _screenshotManager = screenshotManager;

            // 立即扫描版本列表（后台线程）
            _ = RefreshVersionsAsync();
        }

        /// <summary>已安装的版本列表</summary>
        public ObservableCollection<string> Versions { get; } = new();

        /// <summary>截图项列表（含缩略图）</summary>
        public ObservableCollection<ScreenshotItem> Screenshots { get; } = new();

        /// <summary>当前选中的版本</summary>
        [ObservableProperty]
        private string? _selectedVersion;

        /// <summary>版本变化时自动刷新截图列表</summary>
        partial void OnSelectedVersionChanged(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                _ = RefreshScreenshotsAsync();
            }
        }

        /// <summary>当前选中的截图项</summary>
        [ObservableProperty]
        private ScreenshotItem? _selectedScreenshot;

        /// <summary>是否正在加载缩略图</summary>
        [ObservableProperty]
        private bool _isLoading;

        /// <summary>提示信息（如目录不存在时显示）</summary>
        [ObservableProperty]
        private string? _hintMessage;

        /// <summary>当前游戏目录（显示用）</summary>
        [ObservableProperty]
        private string _gameDirDisplay = string.Empty;

        // ================================================================
        // 命令
        // ================================================================

        /// <summary>刷新版本列表</summary>
        [RelayCommand]
        private async Task RefreshVersionsAsync()
        {
            try
            {
                var minecraftPath = GetMinecraftPath();
                var versions = ScanVersions(minecraftPath);

                Versions.Clear();
                foreach (var v in versions)
                    Versions.Add(v);

                // 默认选中第一个版本或配置中保存的版本
                if (Versions.Count > 0 && string.IsNullOrEmpty(SelectedVersion))
                {
                    var last = _configService.Current.LastSelectedVersion;
                    if (!string.IsNullOrEmpty(last) && Versions.Contains(last))
                        SelectedVersion = last;
                    else
                        SelectedVersion = Versions[0];
                }

                if (Versions.Count == 0)
                {
                    HintMessage = $"在 {minecraftPath}\\versions 下没有找到任何版本。\n请先下载版本。";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("刷新版本列表失败", ex);
                HintMessage = "刷新版本列表失败：" + ex.Message;
            }
            await Task.CompletedTask;
        }

        /// <summary>刷新截图列表（含异步加载缩略图）</summary>
        [RelayCommand]
        private async Task RefreshScreenshotsAsync()
        {
            var gameDir = GetCurrentGameDir();
            if (string.IsNullOrEmpty(gameDir))
            {
                HintMessage = "请先选择一个版本";
                return;
            }

            GameDirDisplay = "游戏目录：" + Path.Combine(gameDir, "screenshots");

            IsLoading = true;
            HintMessage = null;

            try
            {
                // 后台扫描文件列表
                var screenshots = await Task.Run(() => _screenshotManager.ListScreenshots(gameDir));

                // 清空旧列表
                Screenshots.Clear();

                if (screenshots.Count == 0)
                {
                    HintMessage = $"在 {gameDir}\\screenshots 下没有找到截图。\n在游戏中按 F2 即可截图。";
                    return;
                }

                // 先添加全部项目（缩略图先留空），让用户看到列表
                foreach (var s in screenshots)
                {
                    Screenshots.Add(new ScreenshotItem(s));
                }

                // 异步并行加载所有缩略图（每加载完一个就更新到 UI）
                await LoadThumbnailsAsync(screenshots);
            }
            catch (Exception ex)
            {
                Logger.Error("刷新截图列表失败", ex);
                HintMessage = "刷新截图列表失败：" + ex.Message;
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>打开选中的截图（用系统默认图片查看器）</summary>
        [RelayCommand]
        private void OpenScreenshot()
        {
            if (SelectedScreenshot == null)
            {
                MessageBox.Show("请先选择一张截图。", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                _screenshotManager.OpenScreenshot(SelectedScreenshot.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开截图失败：" + ex.Message, "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>删除选中的截图</summary>
        [RelayCommand]
        private async Task DeleteScreenshotAsync()
        {
            if (SelectedScreenshot == null)
            {
                MessageBox.Show("请先选择一张要删除的截图。", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"确定要删除截图 \"{SelectedScreenshot.Info.FileName}\" 吗？\n此操作不可恢复！",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                _screenshotManager.DeleteScreenshot(SelectedScreenshot.Info);
                Screenshots.Remove(SelectedScreenshot);
                SelectedScreenshot = null;
            }
            catch (Exception ex)
            {
                Logger.Error("删除截图失败", ex);
                MessageBox.Show("删除截图失败：" + ex.Message, "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            await Task.CompletedTask;
        }

        /// <summary>打开截图所在文件夹</summary>
        [RelayCommand]
        private void OpenScreenshotsFolder()
        {
            var gameDir = GetCurrentGameDir();
            if (string.IsNullOrEmpty(gameDir))
            {
                MessageBox.Show("请先选择一个版本。", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                _screenshotManager.OpenScreenshotsFolder(gameDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开截图文件夹失败：" + ex.Message, "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ================================================================
        // 私有辅助方法
        // ================================================================

        /// <summary>异步加载所有截图的缩略图，每加载完一张就更新 UI</summary>
        private async Task LoadThumbnailsAsync(List<ScreenshotInfo> screenshots)
        {
            // 为每张截图启动一个后台任务，并行加载缩略图
            // 用索引找到对应的 ScreenshotItem，加载完后通过 Dispatcher 切回 UI 线程更新
            var tasks = new List<Task>();
            for (int i = 0; i < screenshots.Count; i++)
            {
                var index = i;
                var filePath = screenshots[i].FilePath;
                tasks.Add(Task.Run(() =>
                {
                    var thumbnail = LoadThumbnail(filePath);
                    if (thumbnail != null)
                    {
                        // 切回 UI 线程设置 Thumbnail 属性
                        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (index < Screenshots.Count)
                            {
                                Screenshots[index].Thumbnail = thumbnail;
                            }
                        }));
                    }
                }));
            }
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// 在后台线程加载缩略图。
        /// 用 BitmapCacheOption.OnLoad 避免文件被锁住，
        /// 用 DecodePixelWidth 限制解码尺寸，节省内存。
        /// Freeze 后可跨线程访问。
        /// </summary>
        private static BitmapImage? LoadThumbnail(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;  // 立即读入内存，不锁文件
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.DecodePixelWidth = 200;  // 限制宽度，节省内存
                bmp.UriSource = new Uri(filePath);
                bmp.EndInit();
                bmp.Freeze();  // 冻结，允许跨线程访问
                return bmp;
            }
            catch (Exception ex)
            {
                Logger.Warn($"加载缩略图失败：{filePath} - {ex.Message}");
                return null;
            }
        }

        /// <summary>获取 .minecraft 根目录路径（从配置读取，为空时用默认值）</summary>
        private string GetMinecraftPath()
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

        /// <summary>
        /// 获取当前版本对应的游戏目录。
        /// 版本隔离启用时：.minecraft/versions/<versionId>/
        /// 否则：.minecraft/
        /// </summary>
        private string? GetCurrentGameDir()
        {
            if (string.IsNullOrEmpty(SelectedVersion))
                return null;

            var minecraftPath = GetMinecraftPath();
            if (_configService.Current.EnableVersionIsolation)
            {
                return Path.Combine(minecraftPath, "versions", SelectedVersion);
            }
            return minecraftPath;
        }

        /// <summary>扫描 .minecraft/versions 目录下的所有版本</summary>
        private static List<string> ScanVersions(string minecraftPath)
        {
            var result = new List<string>();
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
    }

    /// <summary>
    /// 截图项：包装 ScreenshotInfo + 缩略图。
    /// Thumbnail 属性在后台线程异步加载，加载完成后通过绑定通知 UI 更新。
    /// </summary>
    public partial class ScreenshotItem : ObservableObject
    {
        /// <summary>原始截图信息</summary>
        public ScreenshotInfo Info { get; }

        /// <summary>文件名（绑定用，转发自 Info）</summary>
        public string FileName => Info.FileName;

        /// <summary>截图时间显示（绑定用，转发自 Info）</summary>
        public string CaptureTimeDisplay => Info.CaptureTimeDisplay;

        /// <summary>大小显示（绑定用，转发自 Info）</summary>
        public string SizeDisplay => Info.SizeDisplay;

        [ObservableProperty]
        private BitmapImage? _thumbnail;

        public ScreenshotItem(ScreenshotInfo info)
        {
            Info = info;
        }
    }
}
