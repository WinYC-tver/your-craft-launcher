using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using YCL.ViewModels;

namespace YCL.Services
{
    /// <summary>
    /// 导航服务实现：维护"页面键 → ViewModel 类型"的映射表，
    /// 通过依赖注入容器（IServiceProvider）解析对应 ViewModel 实例。
    /// </summary>
    public class NavigationService : INavigationService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly Dictionary<string, Type> _pageKeys = new();
        private object? _currentView;
        private string _currentPageTitle = string.Empty;

        public NavigationService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;

            // ====== 五大顶层板块 ======
            _pageKeys["Launch"] = typeof(LaunchPageViewModel);
            _pageKeys["Download"] = typeof(DownloadPageViewModel);
            _pageKeys["News"] = typeof(NewsPageViewModel);
            _pageKeys["Functions"] = typeof(FunctionsHubViewModel);
            _pageKeys["Settings"] = typeof(SettingsPageViewModel);

            // ====== 功能板块下的子页面 ======
            _pageKeys["Multiplayer"] = typeof(MultiplayerPageViewModel);
            _pageKeys["Instance"] = typeof(InstancePanelViewModel);
            _pageKeys["Java"] = typeof(JavaPageViewModel);
            _pageKeys["Account"] = typeof(AccountPageViewModel);
            _pageKeys["Saves"] = typeof(SavesPageViewModel);
            _pageKeys["Screenshots"] = typeof(ScreenshotsPageViewModel);

            // ====== 保留兼容键 ======
            // Home / About 仍保留映射，避免破坏旧代码或 App.xaml 的 DataTemplate 注册。
            // 它们不再出现在顶部导航项中，但调用 NavigateTo 仍可访问。
            _pageKeys["Home"] = typeof(HomePageViewModel);
            _pageKeys["About"] = typeof(AboutPageViewModel);

            // 模组 / 资源 子页面（保留键，便于通过功能板块或别处访问）
            _pageKeys["Mods"] = typeof(ModPageViewModel);
            _pageKeys["Resources"] = typeof(ResourcePageViewModel);
        }

        /// <inheritdoc/>
        public object? CurrentView => _currentView;

        /// <inheritdoc/>
        public string CurrentPageTitle => _currentPageTitle;

        /// <inheritdoc/>
        public event EventHandler? Navigated;

        /// <inheritdoc/>
        public void NavigateTo(string pageKey)
        {
            // 找不到对应页面键则直接返回
            if (!_pageKeys.TryGetValue(pageKey, out var viewModelType))
                return;

            // 从依赖注入容器获取对应 ViewModel 实例
            _currentView = _serviceProvider.GetService(viewModelType);

            // 将英文键翻译为中文标题（v26 新导航对应的板块标题）
            _currentPageTitle = pageKey switch
            {
                "Launch" => "启动",
                "Download" => "下载资源",
                "News" => "新闻",
                "Functions" => "功能",
                "Settings" => "设置",
                "Multiplayer" => "联机",
                "Instance" => "实例面板",
                "Java" => "Java",
                "Account" => "账户",
                "Saves" => "存档",
                "Screenshots" => "截图",
                // 兼容旧键（不在顶部导航出现，但 NavigateTo 仍可调用）
                "Home" => "主页",
                "Mods" => "模组",
                "Resources" => "资源",
                "About" => "关于",
                _ => pageKey
            };

            // 通知外部（如 MainWindowViewModel）更新界面
            Navigated?.Invoke(this, EventArgs.Empty);
        }
    }
}
