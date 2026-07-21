using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
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
    /// 实例面板 ViewModel：以卡片网格展示每个已安装版本（实例），
    /// 点击卡片在页面下方显示内联详情区（模组/存档/资源包/光影/截图）。
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

        // ===== 实例卡片列表 =====

        /// <summary>实例卡片列表（卡片网格展示）</summary>
        [ObservableProperty]
        private ObservableCollection<InstanceCard> _instances = new();

        /// <summary>是否正在加载实例列表</summary>
        [ObservableProperty]
        private bool _isLoading;

        /// <summary>
        /// 当前选中的实例卡片（点击卡片后设置；为 null 时详情区折叠）。
        /// </summary>
        [ObservableProperty]
        private InstanceCard? _selectedInstance;

        // ===== 详情数据（选中实例后加载，绑定到内联详情区） =====

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

        // ===== 顶部小组件统计值（当前选中实例的统计） =====

        [ObservableProperty] private int _modCount;
        [ObservableProperty] private int _saveCount;
        [ObservableProperty] private int _screenshotCount;
        [ObservableProperty] private int _resourcePackCount;
        [ObservableProperty] private int _shaderCount;
        [ObservableProperty] private string _lastPlayedText = "从未游玩";

        // ===== 顶部全局统计（所有实例累加） =====

        /// <summary>所有实例的模组总数</summary>
        [ObservableProperty] private int _totalMods;
        /// <summary>所有实例的存档总数</summary>
        [ObservableProperty] private int _totalSaves;
        /// <summary>所有实例的截图总数</summary>
        [ObservableProperty] private int _totalScreenshots;

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

                // 累加全局统计
                var sumMods = 0;
                var sumSaves = 0;
                var sumShots = 0;

                foreach (var v in versions)
                {
                    var card = new InstanceCard
                    {
                        Id = v.Id,
                        Type = v.Type,
                        ModLoaderName = v.ModLoaderName ?? "无",
                        IsModded = v.IsModded,
                        Directory = v.Directory,
                        LastPlayedText = GetLastPlayedText(v.Directory)
                    };

                    // 加载卡片的小统计（模组/存档/截图数）
                    try
                    {
                        var gameDir = _versionManager.GetGameDirectory(v.Id);
                        var mods = SafeList(() => _localModManager.ListMods(gameDir));
                        var saves = SafeList(() => _saveManager.ListSaves(gameDir));
                        var screenshots = SafeList(() => _screenshotManager.ListScreenshots(gameDir));
                        card.ModCount = mods.Count;
                        card.SaveCount = saves.Count;
                        card.ScreenshotCount = screenshots.Count;

                        sumMods += mods.Count;
                        sumSaves += saves.Count;
                        sumShots += screenshots.Count;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"加载实例 {v.Id} 统计失败：{ex.Message}");
                    }

                    Instances.Add(card);
                }

                // 更新全局统计
                TotalMods = sumMods;
                TotalSaves = sumSaves;
                TotalScreenshots = sumShots;
            }
            catch (Exception ex)
            {
                Logger.Error("加载实例列表失败", ex);
                Instances.Clear();
                TotalMods = TotalSaves = TotalScreenshots = 0;
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// 点击卡片：设置 SelectedInstance 并加载详情数据，触发页面下方内联详情区显示。
        /// </summary>
        [RelayCommand]
        private async Task ShowInstanceDetailsAsync(object? parameter)
        {
            if (parameter is not InstanceCard card) return;

            // 标记当前选中的卡片（UI 据此高亮 + 显示详情区）
            SelectedInstance = card;

            // 加载详情数据（更新 Mods/Saves/Screenshots 等集合 + 顶部小组件统计）
            var versionInfo = new InstalledVersionInfo
            {
                Id = card.Id,
                Type = card.Type,
                Directory = card.Directory,
                ModLoaderName = card.ModLoaderName == "无" ? null : card.ModLoaderName,
                IsModded = card.IsModded
            };
            await LoadInstanceDetailsAsync(versionInfo);
        }

        /// <summary>关闭详情区命令：清空 SelectedInstance</summary>
        [RelayCommand]
        private void CloseDetails()
        {
            SelectedInstance = null;
            Mods.Clear();
            Saves.Clear();
            Screenshots.Clear();
            ResourcePacks.Clear();
            Shaders.Clear();
            ResetStats();
        }

        /// <summary>从版本目录的最后修改时间获取上次游玩时间</summary>
        private static string GetLastPlayedText(string? directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return "从未游玩";
            try
            {
                return Directory.GetLastWriteTime(directory).ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return "从未游玩";
            }
        }

        /// <summary>加载指定实例的模组/存档/截图/资源包/光影，并更新小组件统计</summary>
        private async Task LoadInstanceDetailsAsync(InstalledVersionInfo instance)
        {
            try
            {
                var gameDir = _versionManager.GetGameDirectory(instance.Id);

                // 上次游玩时间：取版本目录的最后修改时间
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
                Mods.Clear();
                Saves.Clear();
                Screenshots.Clear();
                ResourcePacks.Clear();
                Shaders.Clear();
                ResetStats();
            }
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

    /// <summary>
    /// 实例卡片数据模型：包装 InstalledVersionInfo 上 UI 显示需要的字段，
    /// 并预加载模组/存档/截图数量统计，供卡片网格直接绑定。
    /// </summary>
    public class InstanceCard
    {
        /// <summary>版本 Id（如 "1.20.4"）</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>版本类型（release / snapshot / old_beta / old_alpha）</summary>
        public string Type { get; set; } = "release";

        /// <summary>模组加载器名称（如 "Forge"、"Fabric"），无加载器时为 "无"</summary>
        public string ModLoaderName { get; set; } = "无";

        /// <summary>是否含模组加载器</summary>
        public bool IsModded { get; set; }

        /// <summary>版本目录绝对路径</summary>
        public string Directory { get; set; } = string.Empty;

        /// <summary>模组数量（小统计）</summary>
        public int ModCount { get; set; }

        /// <summary>存档数量（小统计）</summary>
        public int SaveCount { get; set; }

        /// <summary>截图数量（小统计）</summary>
        public int ScreenshotCount { get; set; }

        /// <summary>上次游玩时间字符串（如 "2026-07-17 12:34"）</summary>
        public string LastPlayedText { get; set; } = "从未游玩";

        /// <summary>类型显示文字（release→"正式版"等）</summary>
        public string TypeLabel => Type switch
        {
            "release" => "正式版",
            "snapshot" => "快照版",
            "old_beta" => "旧 Beta 版",
            "old_alpha" => "旧 Alpha 版",
            _ => Type
        };
    }
}
