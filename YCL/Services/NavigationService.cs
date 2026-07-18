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

            // 注册每个页面的键与对应 ViewModel 类型
            _pageKeys["Home"] = typeof(HomePageViewModel);
            _pageKeys["Launch"] = typeof(LaunchPageViewModel);
            _pageKeys["Download"] = typeof(DownloadPageViewModel);
            _pageKeys["Mods"] = typeof(ModPageViewModel);
            _pageKeys["Resources"] = typeof(ResourcePageViewModel);
            _pageKeys["Java"] = typeof(JavaPageViewModel);
            _pageKeys["Account"] = typeof(AccountPageViewModel);
            _pageKeys["Saves"] = typeof(SavesPageViewModel);
            _pageKeys["Screenshots"] = typeof(ScreenshotsPageViewModel);
            _pageKeys["Settings"] = typeof(SettingsPageViewModel);
            _pageKeys["About"] = typeof(AboutPageViewModel);
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

            // 将英文键翻译为中文标题
            _currentPageTitle = pageKey switch
            {
                "Home" => "主页",
                "Launch" => "启动",
                "Download" => "下载",
                "Mods" => "模组",
                "Resources" => "资源",
                "Java" => "Java",
                "Account" => "账户",
                "Saves" => "存档",
                "Screenshots" => "截图",
                "Settings" => "设置",
                "About" => "关于",
                _ => pageKey
            };

            // 通知外部（如 MainWindowViewModel）更新界面
            Navigated?.Invoke(this, EventArgs.Empty);
        }
    }
}
