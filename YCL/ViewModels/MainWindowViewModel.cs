using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 主窗口 ViewModel：维护当前显示的页面 ViewModel 与页面标题，
    /// 并提供导航命令供界面调用。
    /// </summary>
    public partial class MainWindowViewModel : ObservableObject
    {
        private readonly INavigationService _navigationService;

        /// <summary>当前内容区显示的 ViewModel（由 DataTemplate 渲染成对应页面）</summary>
        [ObservableProperty]
        private object? _currentView;

        /// <summary>当前页面的中文标题</summary>
        [ObservableProperty]
        private string _currentPageTitle = string.Empty;

        public MainWindowViewModel(INavigationService navigationService)
        {
            _navigationService = navigationService;
            // 订阅导航完成事件，同步更新本 ViewModel 的属性
            _navigationService.Navigated += OnNavigated;
            // 启动时默认导航到启动页（v26：Home 已从顶部导航移除，改为 Launch）
            _navigationService.NavigateTo("Launch");
        }

        /// <summary>导航服务完成导航后的回调：同步当前视图与标题</summary>
        private void OnNavigated(object? sender, EventArgs e)
        {
            CurrentView = _navigationService.CurrentView;
            CurrentPageTitle = _navigationService.CurrentPageTitle;
        }

        /// <summary>导航命令：根据页面键切换页面（界面通过此命令触发导航）</summary>
        [RelayCommand]
        private void Navigate(string pageKey) => _navigationService.NavigateTo(pageKey);
    }
}
