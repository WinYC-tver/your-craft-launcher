using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YCL.Core.Download;
using YCL.Core.Utils;
using YCL.Models.Versions;

namespace YCL.ViewModels
{
    /// <summary>
    /// 新闻页 ViewModel：展示 Minecraft 版本动态资讯。
    /// 数据来源是 <see cref="IVersionManifestService"/> 拉取的官方版本清单——
    /// 把最近的版本发布记录当成"新闻"展示给用户。
    /// </summary>
    public partial class NewsPageViewModel : ViewModelBase
    {
        private readonly IVersionManifestService _manifestService;

        public NewsPageViewModel(IVersionManifestService manifestService)
        {
            _manifestService = manifestService;

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
