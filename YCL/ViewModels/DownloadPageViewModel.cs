using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using YCL.Core.Download;
using YCL.Core.Mods;
using YCL.Core.Resources;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Models;
using YCL.Models.Versions;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 下载页 ViewModel：微软商店风格的资源下载中心。
    ///
    /// 主要功能：
    /// 1. 顶部搜索框 + 分类切换（游戏 / 资源 / 收藏夹）
    /// 2. 游戏分类：游戏本体（版本下载）、整合包
    /// 3. 资源分类：模组、整合包、资源包、光影包、数据包、世界
    /// 4. 收藏夹：本地 JSON 文件存储已收藏资源 id
    /// 5. 搜索结果以卡片样式展示，每张卡片有"详情"按钮进入资源详情页
    /// 6. 多线程下载：使用注入的 MultiThreadDownloader，线程数从配置读取
    /// 7. 文件名格式：根据 AppConfig.ResourceFileNameFormat 生成
    /// </summary>
    public partial class DownloadPageViewModel : ViewModelBase
    {
        private readonly IModDownloadService _modDownloadService;
        private readonly IResourceService _resourceService;
        private readonly IModpackService _modpackService;
        private readonly IVersionManager _versionManager;
        private readonly IVersionManifestService _manifestService;
        private readonly IConfigService _configService;
        private readonly MultiThreadDownloader _multiThreadDownloader;

        /// <summary>资源详情页 ViewModel（由 DownloadPage 内嵌显示）</summary>
        public ResourceDetailPageViewModel DetailVM { get; }

        /// <summary>收藏夹本地存储路径（程序根目录下 favorites.json）</summary>
        private static readonly string FavoritesFilePath =
            Path.Combine(AppContext.BaseDirectory, "favorites.json");

        /// <summary>下载任务取消令牌</summary>
        private CancellationTokenSource? _downloadCts;

        public DownloadPageViewModel(
            IModDownloadService modDownloadService,
            IResourceService resourceService,
            IModpackService modpackService,
            IVersionManager versionManager,
            IVersionManifestService manifestService,
            IConfigService configService,
            MultiThreadDownloader multiThreadDownloader,
            ResourceDetailPageViewModel detailVM)
        {
            _modDownloadService = modDownloadService;
            _resourceService = resourceService;
            _modpackService = modpackService;
            _versionManager = versionManager;
            _manifestService = manifestService;
            _configService = configService;
            _multiThreadDownloader = multiThreadDownloader;
            DetailVM = detailVM;

            // 把详情页 VM 的"返回列表"事件转发到本 VM
            DetailVM.BackRequested += OnDetailBackRequested;
            // 把详情页 VM 的"收藏状态变化"事件转发到本 VM，用于刷新收藏夹列表
            DetailVM.FavoriteChanged += OnDetailFavoriteChanged;

            // 初始化分类列表
            InitCategoryItems();
            SelectedCategoryIndex = 0;

            // 默认下载源为 BMCLAPI（仅在配置仍为默认值 Official 时自动切换）
            if (_configService.Current.DownloadSource == DownloadSource.Official)
            {
                _configService.Current.DownloadSource = DownloadSource.BMCLAPI;
                _configService.Save();
            }
            UpdateDownloadSourceDisplay();

            // 加载收藏夹
            LoadFavorites();
        }

        // ====== 分类管理 ======

        /// <summary>左侧分类列表（按"游戏/资源/收藏"分组显示）</summary>
        public ObservableCollection<CategoryItem> CategoryItems { get; } = new();

        /// <summary>初始化左侧分类列表（索引顺序对应 SelectedCategoryIndex 取值）</summary>
        private void InitCategoryItems()
        {
            // 游戏组：0~1
            CategoryItems.Add(new CategoryItem("🎮", "游戏本体", "游戏"));
            CategoryItems.Add(new CategoryItem("📦", "整合包", "游戏"));
            // 资源组：2~7
            CategoryItems.Add(new CategoryItem("🧩", "模组", "资源"));
            CategoryItems.Add(new CategoryItem("📦", "整合包", "资源"));
            CategoryItems.Add(new CategoryItem("🎨", "资源包", "资源"));
            CategoryItems.Add(new CategoryItem("✨", "光影包", "资源"));
            CategoryItems.Add(new CategoryItem("📊", "数据包", "资源"));
            CategoryItems.Add(new CategoryItem("🌍", "世界", "资源"));
            // 收藏组：8
            CategoryItems.Add(new CategoryItem("⭐", "收藏夹", "收藏"));
        }

        /// <summary>当前选中的左侧分类索引（0~8）</summary>
        [ObservableProperty]
        private int _selectedCategoryIndex;

        /// <summary>分类切换时：自动触发搜索（已有查询时）或清空列表</summary>
        partial void OnSelectedCategoryIndexChanged(int value)
        {
            // 切到收藏夹时刷新收藏列表，其它分类若有搜索词则重新搜索
            if (value == 8)
            {
                RefreshFavorites();
            }
            else if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                _ = SearchAsync();
            }
            else
            {
                SearchResults.Clear();
                HintMessage = "请输入关键字后点击搜索。";
            }
        }

        /// <summary>当前分类对应的资源种类（用于详情页判断下载方式）</summary>
        public ResourceKind CurrentKind => SelectedCategoryIndex switch
        {
            0 => ResourceKind.GameVersion,
            1 => ResourceKind.Modpack,
            2 => ResourceKind.Mod,
            3 => ResourceKind.Modpack,
            4 => ResourceKind.ResourcePack,
            5 => ResourceKind.ShaderPack,
            6 => ResourceKind.Datapack,
            7 => ResourceKind.World,
            _ => ResourceKind.Unknown
        };

        // ====== 搜索 ======

        /// <summary>搜索关键字（双向绑定搜索框）</summary>
        [ObservableProperty]
        private string _searchQuery = string.Empty;

        /// <summary>搜索结果列表（统一卡片格式）</summary>
        public ObservableCollection<ResourceCard> SearchResults { get; } = new();

        /// <summary>是否正在搜索</summary>
        [ObservableProperty]
        private bool _isSearching;

        /// <summary>提示信息</summary>
        [ObservableProperty]
        private string? _hintMessage;

        /// <summary>搜索命令：根据当前分类调用对应服务</summary>
        [RelayCommand]
        private async Task SearchAsync()
        {
            if (IsSearching) return;

            // 收藏夹不参与搜索
            if (SelectedCategoryIndex == 8)
            {
                RefreshFavorites();
                return;
            }

            var query = SearchQuery?.Trim() ?? string.Empty;

            // 游戏本体不依赖关键字也能列出全部版本
            if (SelectedCategoryIndex != 0 && string.IsNullOrEmpty(query))
            {
                HintMessage = "请输入搜索关键字。";
                return;
            }

            IsSearching = true;
            SearchResults.Clear();
            HintMessage = null;
            StatusText = "正在搜索...";

            try
            {
                switch (SelectedCategoryIndex)
                {
                    case 0: // 游戏本体
                        await SearchGameVersionsAsync(query);
                        break;
                    case 1: // 整合包（游戏分类）
                    case 3: // 整合包（资源分类）
                        await SearchResourcesAsync(query, ResourceType.World, ResourceKind.Modpack);
                        break;
                    case 2: // 模组
                        await SearchModsAsync(query);
                        break;
                    case 4: // 资源包
                        await SearchResourcesAsync(query, ResourceType.ResourcePack, ResourceKind.ResourcePack);
                        break;
                    case 5: // 光影包
                        await SearchResourcesAsync(query, ResourceType.ShaderPack, ResourceKind.ShaderPack);
                        break;
                    case 6: // 数据包（暂复用 ResourcePack 类型）
                        await SearchResourcesAsync(query, ResourceType.ResourcePack, ResourceKind.Datapack);
                        break;
                    case 7: // 世界
                        await SearchResourcesAsync(query, ResourceType.World, ResourceKind.World);
                        break;
                }

                StatusText = SearchResults.Count > 0
                    ? $"找到 {SearchResults.Count} 个结果"
                    : "未找到结果";
            }
            catch (Exception ex)
            {
                Logger.Error("搜索资源失败", ex);
                StatusText = "搜索失败";
                HintMessage = "搜索失败：" + ex.Message;
            }
            finally
            {
                IsSearching = false;
            }
        }

        /// <summary>搜索游戏版本（从版本清单获取）</summary>
        private async Task SearchGameVersionsAsync(string query)
        {
            var versions = await _manifestService.GetVersionsAsync();
            var filtered = string.IsNullOrEmpty(query)
                ? versions
                : versions.Where(v => v.Id != null && v.Id.Contains(query, StringComparison.OrdinalIgnoreCase));

            // 优先显示 release 类型
            foreach (var entry in filtered
                         .OrderByDescending(v => v.Type == "release")
                         .ThenByDescending(v => v.ReleaseTime ?? string.Empty)
                         .Take(100))
            {
                SearchResults.Add(new ResourceCard
                {
                    IconUrl = null,
                    Name = entry.Id ?? "未知版本",
                    Author = "Mojang",
                    Description = $"类型：{entry.Type ?? "未知"}    发布时间：{entry.ReleaseTime ?? "未知"}",
                    DownloadCountDisplay = "-",
                    SourceDisplay = "Mojang 官方",
                    Kind = ResourceKind.GameVersion,
                    Original = entry,
                    ProjectId = entry.Id ?? string.Empty,
                    IsFavorite = IsFavorite(BuildFavoriteId(ResourceKind.GameVersion, entry.Id ?? string.Empty))
                });
            }
        }

        /// <summary>搜索模组（调用 IModDownloadService）</summary>
        private async Task SearchModsAsync(string query)
        {
            var results = await _modDownloadService.SearchAsync(query, ModSource.All);
            foreach (var r in results)
            {
                SearchResults.Add(ToCard(r, ResourceKind.Mod));
            }
        }

        /// <summary>搜索资源（调用 IResourceService）</summary>
        private async Task SearchResourcesAsync(string query, ResourceType type, ResourceKind kind)
        {
            var results = await _resourceService.SearchResourcesAsync(query, type, ModSource.All);
            foreach (var r in results)
            {
                SearchResults.Add(ToCard(r, kind));
            }
        }

        /// <summary>把 ModSearchResult 转换为统一卡片</summary>
        private ResourceCard ToCard(ModSearchResult r, ResourceKind kind)
        {
            var favId = BuildFavoriteId(kind, r.ProjectId);
            return new ResourceCard
            {
                IconUrl = r.LogoUrl,
                Name = r.Name,
                Author = string.IsNullOrEmpty(r.Author) ? "-" : r.Author,
                Description = r.Description,
                DownloadCountDisplay = r.DownloadCountDisplay,
                SourceDisplay = r.SourceDisplay,
                Kind = kind,
                Original = r,
                ProjectId = r.ProjectId,
                IsFavorite = IsFavorite(favId)
            };
        }

        // ====== 详情页导航 ======

        /// <summary>详情页是否可见（覆盖整个内容区）</summary>
        [ObservableProperty]
        private bool _isDetailVisible;

        /// <summary>显示资源详情命令：把卡片数据传给详情页 VM 并显示</summary>
        [RelayCommand]
        private void ShowDetail(ResourceCard? card)
        {
            if (card == null) return;
            DetailVM.Initialize(card, _configService.Current.ResourceFileNameFormat);
            IsDetailVisible = true;
        }

        /// <summary>返回列表命令：关闭详情页</summary>
        [RelayCommand]
        private void BackToList()
        {
            IsDetailVisible = false;
        }

        /// <summary>详情页 VM 触发"返回"事件时同步本 VM 状态</summary>
        private void OnDetailBackRequested(object? sender, EventArgs e)
        {
            IsDetailVisible = false;
        }

        /// <summary>详情页 VM 触发"收藏状态变化"事件时刷新收藏夹</summary>
        private void OnDetailFavoriteChanged(object? sender, EventArgs e)
        {
            // 同步当前列表中该资源的收藏状态
            if (DetailVM.CurrentCard != null)
            {
                var favId = BuildFavoriteId(DetailVM.CurrentCard.Kind, DetailVM.CurrentCard.ProjectId);
                var isFav = IsFavorite(favId);
                foreach (var c in SearchResults)
                {
                    if (c.ProjectId == DetailVM.CurrentCard.ProjectId && c.Kind == DetailVM.CurrentCard.Kind)
                    {
                        c.IsFavorite = isFav;
                    }
                }
            }
        }

        // ====== 收藏夹 ======

        /// <summary>收藏的资源 id 集合（运行时内存缓存）</summary>
        private readonly HashSet<string> _favoriteIds = new();

        /// <summary>收藏夹中存储的资源完整信息（用于在收藏夹列表显示卡片）</summary>
        private readonly List<ResourceCard> _favoriteCards = new();

        /// <summary>构造收藏夹 id（用种类+项目id 区分不同类型但同 id 的资源）</summary>
        private static string BuildFavoriteId(ResourceKind kind, string projectId)
        {
            return $"{kind}::{projectId}";
        }

        /// <summary>判断指定资源是否已收藏</summary>
        private bool IsFavorite(string favId) => _favoriteIds.Contains(favId);

        /// <summary>从 favorites.json 加载收藏列表</summary>
        private void LoadFavorites()
        {
            try
            {
                if (!File.Exists(FavoritesFilePath))
                    return;

                var json = File.ReadAllText(FavoritesFilePath);
                var list = JsonSerializer.Deserialize<List<ResourceCard>>(json);
                if (list == null) return;

                _favoriteCards.Clear();
                _favoriteIds.Clear();
                foreach (var card in list)
                {
                    _favoriteCards.Add(card);
                    _favoriteIds.Add(BuildFavoriteId(card.Kind, card.ProjectId));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"加载收藏夹失败：{ex.Message}");
            }
        }

        /// <summary>保存收藏列表到 favorites.json</summary>
        private void SaveFavorites()
        {
            try
            {
                var json = JsonSerializer.Serialize(_favoriteCards,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FavoritesFilePath, json);
            }
            catch (Exception ex)
            {
                Logger.Warn($"保存收藏夹失败：{ex.Message}");
            }
        }

        /// <summary>添加收藏</summary>
        public void AddFavorite(ResourceCard card)
        {
            var favId = BuildFavoriteId(card.Kind, card.ProjectId);
            if (_favoriteIds.Add(favId))
            {
                _favoriteCards.Add(card);
                SaveFavorites();
            }
        }

        /// <summary>取消收藏</summary>
        public void RemoveFavorite(ResourceCard card)
        {
            var favId = BuildFavoriteId(card.Kind, card.ProjectId);
            if (_favoriteIds.Remove(favId))
            {
                _favoriteCards.RemoveAll(c =>
                    c.Kind == card.Kind && c.ProjectId == card.ProjectId);
                SaveFavorites();
            }
        }

        /// <summary>刷新收藏夹列表显示</summary>
        private void RefreshFavorites()
        {
            SearchResults.Clear();
            foreach (var c in _favoriteCards)
            {
                c.IsFavorite = true;
                SearchResults.Add(c);
            }
            StatusText = SearchResults.Count > 0
                ? $"收藏夹中有 {SearchResults.Count} 个资源"
                : "收藏夹为空";
        }

        // ====== 下载设置 ======

        /// <summary>下载源（双向绑定到 AppConfig.DownloadSource）</summary>
        public DownloadSource DownloadSource
        {
            get => _configService.Current.DownloadSource;
            set
            {
                if (_configService.Current.DownloadSource == value) return;
                _configService.Current.DownloadSource = value;
                _configService.Save();
                UpdateDownloadSourceDisplay();
                OnPropertyChanged();
            }
        }

        /// <summary>下载线程数（1~64，双向绑定到 AppConfig.DownloadThreads）</summary>
        public int DownloadThreadCount
        {
            get => _configService.Current.DownloadThreads;
            set
            {
                var clamped = Math.Clamp(value, 1, 64);
                if (_configService.Current.DownloadThreads == clamped) return;
                _configService.Current.DownloadThreads = clamped;
                _configService.Save();
                OnPropertyChanged();
            }
        }

        /// <summary>资源文件名格式（双向绑定到 AppConfig.ResourceFileNameFormat）</summary>
        public string FileNameFormat
        {
            get => _configService.Current.ResourceFileNameFormat;
            set
            {
                if (_configService.Current.ResourceFileNameFormat == value) return;
                _configService.Current.ResourceFileNameFormat = value;
                _configService.Save();
                OnPropertyChanged();
            }
        }

        /// <summary>当前下载源显示文字</summary>
        [ObservableProperty]
        private string _downloadSourceDisplay = "BMCLAPI 镜像";

        /// <summary>状态文字（搜索状态 / 下载状态）</summary>
        [ObservableProperty]
        private string _statusText = "请输入关键字搜索资源。";

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

        /// <summary>
        /// 根据当前文件名格式模板，把"名称"和"文件标识"格式化为最终文件名。
        /// 模板中 {name}=名称，{file}=文件标识；如 "{name}-{file}" → Create-create-1.21.1-6.0.4
        /// </summary>
        public string FormatFileName(string name, string fileId)
        {
            if (string.IsNullOrEmpty(name)) name = string.Empty;
            if (string.IsNullOrEmpty(fileId)) fileId = string.Empty;

            var template = _configService.Current.ResourceFileNameFormat;
            if (string.IsNullOrEmpty(template)) template = "{name}-{file}";
            return template.Replace("{name}", name).Replace("{file}", fileId);
        }

        /// <summary>打开下载设置对话框命令</summary>
        [RelayCommand]
        private async Task OpenDownloadSettingsAsync()
        {
            // 下载源 ComboBox
            var sourceCombo = new ComboBox
            {
                MinWidth = 240,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            sourceCombo.Items.Add(new ComboBoxItem { Content = "官方源（Mojang）", Tag = DownloadSource.Official });
            sourceCombo.Items.Add(new ComboBoxItem { Content = "BMCLAPI 镜像", Tag = DownloadSource.BMCLAPI });
            sourceCombo.Items.Add(new ComboBoxItem { Content = "MCBBS 镜像", Tag = DownloadSource.MCBBS });
            foreach (ComboBoxItem item in sourceCombo.Items)
            {
                if (item.Tag is DownloadSource ds && ds == DownloadSource)
                {
                    sourceCombo.SelectedItem = item;
                    break;
                }
            }

            // 文件名格式 ComboBox
            var formatCombo = new ComboBox
            {
                MinWidth = 280,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0)
            };
            formatCombo.Items.Add(new ComboBoxItem { Content = "{name}-{file}  →  名称-文件名", Tag = (object)"{name}-{file}" });
            formatCombo.Items.Add(new ComboBoxItem { Content = "[{name}{file}  →  [名称文件名", Tag = (object)"[{name}{file}" });
            formatCombo.Items.Add(new ComboBoxItem { Content = "{name}{file}  →  名称文件名", Tag = (object)"{name}{file}" });
            formatCombo.Items.Add(new ComboBoxItem { Content = "{file}-{name}  →  文件名-名称", Tag = (object)"{file}-{name}" });
            formatCombo.Items.Add(new ComboBoxItem { Content = "{file}  →  仅文件名", Tag = (object)"{file}" });
            foreach (ComboBoxItem item in formatCombo.Items)
            {
                if (item.Tag is string fmt && fmt == FileNameFormat)
                {
                    formatCombo.SelectedItem = item;
                    break;
                }
            }

            // 下载线程数 Slider
            var threadSlider = new Slider
            {
                Minimum = 1,
                Maximum = 64,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Value = DownloadThreadCount,
                MinWidth = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var threadValueText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            threadValueText.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding
            {
                Source = threadSlider,
                Path = new System.Windows.PropertyPath(nameof(Slider.Value)),
                StringFormat = "{0:0} 线程"
            });

            var threadRow = new StackPanel { Orientation = Orientation.Horizontal };
            threadRow.Children.Add(threadSlider);
            threadRow.Children.Add(threadValueText);

            var dialog = new ContentDialog
            {
                Title = "下载设置",
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel
                {
                    MinWidth = 360,
                    Children =
                    {
                        new System.Windows.Controls.TextBlock { Text = "下载源", FontWeight = FontWeights.SemiBold },
                        sourceCombo,
                        new System.Windows.Controls.TextBlock { Text = "文件名格式", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 0) },
                        formatCombo,
                        new System.Windows.Controls.TextBlock { Text = "下载线程数", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 0) },
                        threadRow,
                        new System.Windows.Controls.TextBlock
                        {
                            Text = "提示：BMCLAPI 为国内镜像源，下载速度更快。线程数越大下载越快，但占用系统资源越多。",
                            Opacity = 0.6,
                            FontSize = 11,
                            Margin = new Thickness(0, 12, 0, 0),
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                }
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (sourceCombo.SelectedItem is ComboBoxItem sourceItem && sourceItem.Tag is DownloadSource newSource)
                    DownloadSource = newSource;
                if (formatCombo.SelectedItem is ComboBoxItem formatItem && formatItem.Tag is string newFormat)
                    FileNameFormat = newFormat;
                DownloadThreadCount = (int)Math.Round(threadSlider.Value);

                HintMessage = "下载设置已保存。";
            }
        }
    }

    /// <summary>
    /// 资源种类枚举：决定详情页"立即获取"按钮的行为。
    /// </summary>
    public enum ResourceKind
    {
        Unknown = 0,
        GameVersion,
        Modpack,
        Mod,
        ResourcePack,
        ShaderPack,
        Datapack,
        World
    }

    /// <summary>
    /// 资源卡片：搜索结果与收藏夹的统一展示模型。
    /// 包装了原始数据对象（ModSearchResult 或 VersionManifestEntry），
    /// 让 UI 不需要关心资源类型差异。
    /// </summary>
    public class ResourceCard : ObservableObject
    {
        /// <summary>图标 URL（无图标时为 null）</summary>
        public string? IconUrl { get; set; }

        /// <summary>资源名</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>作者</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>简短描述</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>下载量显示文字（如 "1.2K"）</summary>
        public string DownloadCountDisplay { get; set; } = "-";

        /// <summary>来源显示文字（如 "Modrinth" / "Mojang 官方"）</summary>
        public string SourceDisplay { get; set; } = string.Empty;

        /// <summary>资源种类</summary>
        public ResourceKind Kind { get; set; } = ResourceKind.Unknown;

        /// <summary>原始数据对象（ModSearchResult 或 VersionManifestEntry）</summary>
        public object? Original { get; set; }

        /// <summary>项目 id（用于收藏夹去重）</summary>
        public string ProjectId { get; set; } = string.Empty;

        private bool _isFavorite;
        /// <summary>是否已收藏（控制卡片上"收藏"按钮显示状态）</summary>
        public bool IsFavorite
        {
            get => _isFavorite;
            set => SetProperty(ref _isFavorite, value);
        }
    }

    /// <summary>
    /// 左侧分类列表项：图标 + 名称 + 所属分组（"游戏"/"资源"/"收藏"）。
    /// 用于 DownloadPage.xaml 左侧 ListBox 按 Group 分组显示分类。
    /// </summary>
    public class CategoryItem
    {
        /// <summary>图标（emoji 字符）</summary>
        public string Icon { get; set; }

        /// <summary>分类显示名</summary>
        public string Name { get; set; }

        /// <summary>所属分组（用于 GroupStyle 按组分块显示）</summary>
        public string Group { get; set; }

        public CategoryItem(string icon, string name, string group)
        {
            Icon = icon;
            Name = name;
            Group = group;
        }
    }
}
