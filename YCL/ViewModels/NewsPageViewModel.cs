using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YCL.Core.Download;
using YCL.Core.Utils;
using YCL.Models;
using YCL.Models.Versions;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 新闻页 ViewModel：展示 Minecraft 版本动态资讯。
    /// 数据来源是 <see cref="IVersionManifestService"/> 拉取的官方版本清单——
    /// 把最近的版本发布记录当成"新闻"展示给用户。
    /// 同时根据 <see cref="HomepageType"/> 配置在顶部显示主页内容：
    /// Preset=内置预设（版本动态），Online=从 URL 拉取，Local=从本地 HTML/图片文件加载。
    /// </summary>
    public partial class NewsPageViewModel : ViewModelBase
    {
        private readonly IVersionManifestService _manifestService;
        private readonly IConfigService _configService;
        private readonly INavigationService _navigationService;

        public NewsPageViewModel(
            IVersionManifestService manifestService,
            IConfigService configService,
            INavigationService navigationService)
        {
            _manifestService = manifestService;
            _configService = configService;
            _navigationService = navigationService;

            // 从配置初始化主页相关属性
            var config = _configService.Current;
            _homepageType = config.HomepageType;
            _homepageUrl = config.HomepageUrl;
            _homepageLocalPath = config.HomepageLocalPath;
            UpdateLocalFileUri();

            // 构造完成后自动刷新一次（异步 fire-and-forget，方法内部已捕获异常）
            _ = RefreshCommand.ExecuteAsync(null);
        }

        /// <summary>是否正在加载（加载中时禁用刷新按钮）</summary>
        [ObservableProperty]
        private bool _isLoading;

        /// <summary>状态文本（显示在标题下方，告诉用户当前状态）</summary>
        [ObservableProperty]
        private string _statusText = "点击刷新加载资讯";

        /// <summary>新闻列表（最近 20 条版本发布记录，按发布时间倒序）</summary>
        [ObservableProperty]
        private IReadOnlyList<NewsItem> _newsItems = new List<NewsItem>();

        /// <summary>
        /// 当前是否有新闻条目（用于 UI 控制"空状态"的显示）。
        /// 依赖 NewsItems，所以需要在 NewsItems 变更时同步通知。
        /// </summary>
        public bool HasNews => NewsItems.Count > 0;

        /// <summary>
        /// 当 NewsItems 变化时同步刷新 HasNews 的属性通知。
        /// CommunityToolkit 的 [ObservableProperty] 在 partial OnNewsItemsChanged 方法中调用。
        /// </summary>
        partial void OnNewsItemsChanged(IReadOnlyList<NewsItem> value)
        {
            OnPropertyChanged(nameof(HasNews));
        }

        /// <summary>
        /// 刷新新闻：强制拉取最新版本清单，取前 20 条按发布时间倒序展示，
        /// 并把"最新正式版 / 最新快照版"信息写到状态文本。
        /// </summary>
        [RelayCommand]
        private async Task RefreshAsync()
        {
            IsLoading = true;
            StatusText = "正在加载...";
            try
            {
                // forceUpdate=true：强制联网刷新，确保拿到最新数据
                var manifest = await _manifestService.FetchAsync(true, CancellationToken.None);

                var versions = manifest.Versions;
                if (versions == null || versions.Count == 0)
                {
                    NewsItems = new List<NewsItem>();
                    StatusText = "暂无资讯";
                    return;
                }

                // 解析 ReleaseTime 成 DateTime，解析失败的排到最后
                var items = versions
                    .Select(v => new { Entry = v, Time = TryParseReleaseTime(v.ReleaseTime) })
                    .OrderByDescending(x => x.Time)
                    .Take(20)
                    .Select(x => new NewsItem(
                        x.Entry.Id ?? "(未知版本)",
                        x.Entry.Type ?? "unknown",
                        x.Time,
                        x.Entry.ReleaseTime))
                    .ToList();

                NewsItems = items;

                // 显示最新正式版 / 最新快照版
                var latest = manifest.Latest;
                if (latest != null)
                {
                    StatusText = $"最新正式版 {latest.Release ?? "?"}  ·  最新快照版 {latest.Snapshot ?? "?"}";
                }
                else
                {
                    StatusText = $"已加载 {items.Count} 条资讯";
                }
            }
            catch (Exception ex)
            {
                StatusText = "加载失败：" + ex.Message;
                Logger.Error("新闻页刷新失败", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>用系统默认浏览器打开 Minecraft 官网</summary>
        [RelayCommand]
        private void OpenMinecraftSite()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.minecraft.net/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error("打开 Minecraft 官网失败", ex);
            }
        }

        /// <summary>
        /// 把 ISO8601 时间字符串解析成本地 DateTime。
        /// 解析失败返回 DateTime.MinValue，调用方据此把条目排到最后。
        /// </summary>
        private static DateTime TryParseReleaseTime(string? iso8601)
        {
            if (string.IsNullOrWhiteSpace(iso8601))
                return DateTime.MinValue;

            if (DateTime.TryParse(iso8601, null, DateTimeStyles.RoundtripKind, out var dt))
                return dt;
            return DateTime.MinValue;
        }

        // ================================================================
        // 主页设置（PCL 全部主页支持）
        // ================================================================

        /// <summary>主页类型（预设 / 联网 / 本地），从 AppConfig 读取</summary>
        [ObservableProperty]
        private HomepageType _homepageType;

        /// <summary>联网主页 URL（HomepageType=Online 时使用）</summary>
        [ObservableProperty]
        private string _homepageUrl;

        /// <summary>本地主页文件路径（HomepageType=Local 时使用）</summary>
        [ObservableProperty]
        private string _homepageLocalPath;

        /// <summary>本地主页文件的 file:// URI（仅 Local 模式有值，供 Image 或 WebView2 显示）</summary>
        [ObservableProperty]
        private string? _localFileUri;

        /// <summary>是否已配置主页（始终为 true，因为默认 Preset 模式下也有内置预设）</summary>
        public bool HasHomepage => true;

        /// <summary>主页是否为 URL 模式（HomepageType=Online）</summary>
        public bool IsHomepageUrl => HomepageType == HomepageType.Online;

        /// <summary>主页是否为预设模式（HomepageType=Preset）</summary>
        public bool IsHomepagePreset => HomepageType == HomepageType.Preset;

        /// <summary>主页是否为本地模式（HomepageType=Local）</summary>
        public bool IsHomepageLocal => HomepageType == HomepageType.Local;

        /// <summary>本地文件是否为图片（仅 Local 模式且文件扩展名匹配时为 true）</summary>
        public bool IsLocalImage => IsHomepageLocal && IsImageFile(HomepageLocalPath);

        /// <summary>本地文件是否为 HTML（仅 Local 模式且文件扩展名匹配时为 true）</summary>
        public bool IsLocalHtml => IsHomepageLocal && IsHtmlFile(HomepageLocalPath);

        /// <summary>主页配置描述（用于 UI 显示当前主页来源）</summary>
        public string HomepageConfig => HomepageType switch
        {
            HomepageType.Preset => "预设（版本动态）",
            HomepageType.Online => string.IsNullOrWhiteSpace(HomepageUrl) ? "联网（未配置 URL）" : $"联网：{HomepageUrl}",
            HomepageType.Local => string.IsNullOrWhiteSpace(HomepageLocalPath) ? "本地（未选择文件）" : $"本地：{Path.GetFileName(HomepageLocalPath)}",
            _ => "未知"
        };

        partial void OnHomepageTypeChanged(HomepageType value)
        {
            UpdateLocalFileUri();
            RefreshHomepageDerived();
        }

        partial void OnHomepageUrlChanged(string value)
        {
            RefreshHomepageDerived();
        }

        partial void OnHomepageLocalPathChanged(string value)
        {
            UpdateLocalFileUri();
            RefreshHomepageDerived();
        }

        /// <summary>刷新主页相关的派生属性通知（让 UI 重新读取计算属性）</summary>
        private void RefreshHomepageDerived()
        {
            OnPropertyChanged(nameof(IsHomepageUrl));
            OnPropertyChanged(nameof(IsHomepagePreset));
            OnPropertyChanged(nameof(IsHomepageLocal));
            OnPropertyChanged(nameof(IsLocalImage));
            OnPropertyChanged(nameof(IsLocalHtml));
            OnPropertyChanged(nameof(HomepageConfig));
        }

        /// <summary>根据当前 HomepageType 和 HomepageLocalPath 更新 LocalFileUri</summary>
        private void UpdateLocalFileUri()
        {
            if (HomepageType == HomepageType.Local && File.Exists(HomepageLocalPath))
            {
                try
                {
                    LocalFileUri = new Uri(HomepageLocalPath).AbsoluteUri;
                }
                catch
                {
                    LocalFileUri = null;
                }
            }
            else
            {
                LocalFileUri = null;
            }
        }

        /// <summary>判断文件是否为图片（按扩展名）</summary>
        private static bool IsImageFile(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" ||
                   ext == ".bmp" || ext == ".gif" || ext == ".webp";
        }

        /// <summary>判断文件是否为 HTML（按扩展名）</summary>
        private static bool IsHtmlFile(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".html" || ext == ".htm";
        }

        /// <summary>在浏览器中打开主页（Online 模式打开 HomepageUrl，未配置时打开 Minecraft 官网）</summary>
        [RelayCommand]
        private void OpenHomepage()
        {
            var url = string.IsNullOrWhiteSpace(HomepageUrl) ? "https://www.minecraft.net/" : HomepageUrl;
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
                Logger.Error("打开主页失败", ex);
            }
        }

        /// <summary>跳转到设置页（让用户配置主页类型与内容）</summary>
        [RelayCommand]
        private void GoToSettings()
        {
            _navigationService.NavigateTo("Settings");
        }
    }

    /// <summary>
    /// 新闻条目：UI 显示用的版本发布信息包装。
    /// 把原始 <see cref="VersionManifestEntry"/> 转成更友好的展示字段。
    /// </summary>
    public sealed class NewsItem
    {
        /// <summary>版本 Id（如 "1.20.4"、"23w13a"），显示为大字标题</summary>
        public string Title { get; }

        /// <summary>原始类型（release / snapshot / old_beta / old_alpha 等）</summary>
        public string Type { get; }

        /// <summary>类型标签：release→"正式版"，snapshot→"快照版"，其它原值</summary>
        public string TypeLabel { get; }

        /// <summary>发布时间（本地时间字符串，用于小字显示）</summary>
        public string DisplayTime { get; }

        public NewsItem(string title, string type, DateTime releaseTime, string? rawReleaseTime)
        {
            Title = title;
            Type = type;
            TypeLabel = type switch
            {
                "release" => "正式版",
                "snapshot" => "快照版",
                "old_beta" => "旧 Beta 版",
                "old_alpha" => "旧 Alpha 版",
                _ => type
            };

            // 解析成功则转本地时间字符串，失败则显示原始字符串
            DisplayTime = releaseTime != DateTime.MinValue
                ? releaseTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : (rawReleaseTime ?? "未知时间");
        }
    }
}
