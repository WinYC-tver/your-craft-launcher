using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YCL.Core.Mods;
using YCL.Core.Saves;
using YCL.Core.Screenshots;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Models.Versions;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 实例面板 ViewModel：展示每个已安装版本（实例）的详情，
    /// 包括模组、存档、截图、资源包、光影，以及顶部的小组件统计。
    /// </summary>
    public partial class InstancePanelViewModel : ViewModelBase
    {
        private readonly IVersionManager _versionManager;
        private readonly ILocalModManager _localModManager;
        private readonly ISaveManager _saveManager;
        private readonly IScreenshotManager _screenshotManager;
        private readonly IConfigService _configService;

        public InstancePanelViewModel(
            IVersionManager versionManager,
            ILocalModManager localModManager,
            ISaveManager saveManager,
            IScreenshotManager screenshotManager,
            IConfigService configService)
        {
            _versionManager = versionManager;
            _localModManager = localModManager;
            _saveManager = saveManager;
            _screenshotManager = screenshotManager;
            _configService = configService;

            // 构造函数末尾：立即异步加载已安装版本列表
            _ = LoadInstancesAsync();
        }

        // ===== 实例列表与选中 =====

        /// <summary>已安装实例列表（绑定到左侧列表）</summary>
        [ObservableProperty]
        private ObservableCollection<InstalledVersionInfo> _instances = new();

        /// <summary>当前选中的实例（绑定到左侧列表的 SelectedItem）</summary>
        [ObservableProperty]
        private InstalledVersionInfo? _selectedInstance;

        /// <summary>是否正在加载实例列表</summary>
        [ObservableProperty]
        private bool _isLoading;

        // ===== 顶部小组件统计值 =====

        /// <summary>模组数量（小组件用）</summary>
        [ObservableProperty]
        private int _modCount;

        /// <summary>存档数量（小组件用）</summary>
        [ObservableProperty]
        private int _saveCount;

        /// <summary>截图数量（小组件用）</summary>
        [ObservableProperty]
        private int _screenshotCount;

        /// <summary>资源包数量（小组件用）</summary>
        [ObservableProperty]
        private int _resourcePackCount;

        /// <summary>光影包数量（小组件用）</summary>
        [ObservableProperty]
        private int _shaderCount;

        /// <summary>上次游玩时间文字（小组件用，默认"从未游玩"）</summary>
        [ObservableProperty]
        private string _lastPlayedText = "从未游玩";

        // ===== 右栏详情列表 =====

        /// <summary>当前实例的模组列表</summary>
        [ObservableProperty]
        private ObservableCollection<ModInfo> _mods = new();

        /// <summary>当前实例的存档列表</summary>
        [ObservableProperty]
        private ObservableCollection<SaveInfo> _saves = new();

        /// <summary>当前实例的截图列表</summary>
        [ObservableProperty]
        private ObservableCollection<ScreenshotInfo> _screenshots = new();

        /// <summary>当前实例的资源包列表（仅文件名）</summary>
        [ObservableProperty]
        private ObservableCollection<string> _resourcePacks = new();

        /// <summary>当前实例的光影包列表（仅文件名）</summary>
        [ObservableProperty]
        private ObservableCollection<string> _shaders = new();

        /// <summary>加载已安装版本列表命令（刷新按钮也用这个）</summary>
        [RelayCommand]
        private async Task LoadInstancesAsync()
        {
            if (IsLoading) return;
            IsLoading = true;
            try
            {
                var versions = await _versionManager.ListInstalledVersionsAsync();
                Instances.Clear();
                foreach (var v in versions)
                    Instances.Add(v);

                // 默认选中第一个实例（仅当未选中时）
                if (Instances.Count > 0 && SelectedInstance == null)
                {
                    SelectedInstance = Instances[0];
                }
            }
            catch (Exception ex)
            {
                Logger.Error("加载实例列表失败", ex);
                Instances.Clear();
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>选中实例变化时自动加载该实例的详情</summary>
        partial void OnSelectedInstanceChanged(InstalledVersionInfo? value)
        {
            if (value == null)
            {
                // 没选中：清空所有详情列表与统计
                Mods.Clear();
                Saves.Clear();
                Screenshots.Clear();
                ResourcePacks.Clear();
                Shaders.Clear();
                ResetStats();
                return;
            }
            // fire-and-forget 加载详情
            _ = LoadInstanceDetailsAsync(value);
        }

        /// <summary>重置所有小组件统计值</summary>
        private void ResetStats()
        {
            ModCount = 0;
            SaveCount = 0;
            ScreenshotCount = 0;
            ResourcePackCount = 0;
            ShaderCount = 0;
            LastPlayedText = "从未游玩";
        }

        /// <summary>加载指定实例的模组/存档/截图/资源包/光影，并更新小组件统计</summary>
        private async Task LoadInstanceDetailsAsync(InstalledVersionInfo instance)
        {
            try
            {
                var gameDir = _versionManager.GetGameDirectory(instance.Id);

                // 上次游玩时间：取版本目录的最后修改时间（已是本地时间）
                if (Directory.Exists(instance.Directory))
                {
                    var lastWrite = Directory.GetLastWriteTime(instance.Directory);
                    LastPlayedText = lastWrite.ToString("yyyy-MM-dd HH:mm");
                }
                else
                {
                    LastPlayedText = "从未游玩";
                }

                // 并行扫描各数据，单点失败不影响其他
                var modsTask = Task.Run(() => SafeList(() => _localModManager.ListMods(gameDir)));
                var savesTask = Task.Run(() => SafeList(() => _saveManager.ListSaves(gameDir)));
                var screenshotsTask = Task.Run(() => SafeList(() => _screenshotManager.ListScreenshots(gameDir)));
                var resourcePacksTask = Task.Run(() => ScanZipFiles(Path.Combine(gameDir, "resourcepacks")));
                var shadersTask = Task.Run(() => ScanZipFiles(Path.Combine(gameDir, "shaderpacks")));

                await Task.WhenAll(modsTask, savesTask, screenshotsTask, resourcePacksTask, shadersTask);

                // 更新到 UI 集合
                Mods = ToObservableCollection(modsTask.Result);
                Saves = ToObservableCollection(savesTask.Result);
                Screenshots = ToObservableCollection(screenshotsTask.Result);
                ResourcePacks = ToObservableCollection(resourcePacksTask.Result);
                Shaders = ToObservableCollection(shadersTask.Result);

                // 更新小组件统计
                ModCount = Mods.Count;
                SaveCount = Saves.Count;
                ScreenshotCount = Screenshots.Count;
                ResourcePackCount = ResourcePacks.Count;
                ShaderCount = Shaders.Count;
            }
            catch (Exception ex)
            {
                Logger.Error($"加载实例详情失败：{instance.Id}", ex);
                // 异常时清空详情，保证不崩溃
                Mods.Clear();
                Saves.Clear();
                Screenshots.Clear();
                ResourcePacks.Clear();
                Shaders.Clear();
                ResetStats();
            }
        }

        /// <summary>安全调用一个会返回列表的方法，失败时返回空列表</summary>
        private List<T> SafeList<T>(Func<List<T>> func)
        {
            try
            {
                return func() ?? new List<T>();
            }
            catch (Exception ex)
            {
                Logger.Warn($"扫描数据失败：{ex.Message}");
                return new List<T>();
            }
        }

        /// <summary>扫描指定目录下所有 .zip 文件名（用于资源包/光影）</summary>
        private static List<string> ScanZipFiles(string dir)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return result;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.zip"))
                {
                    result.Add(Path.GetFileName(f));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"扫描 {dir} 失败：{ex.Message}");
            }
            return result;
        }

        /// <summary>把列表转成 ObservableCollection</summary>
        private static ObservableCollection<T> ToObservableCollection<T>(IEnumerable<T> source)
        {
            var col = new ObservableCollection<T>();
            foreach (var item in source)
                col.Add(item);
            return col;
        }
    }
}
