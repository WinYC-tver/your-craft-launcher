using System;

namespace YCL.Services
{
    /// <summary>
    /// 页面导航服务接口，负责在主窗口中切换不同页面。
    /// 通过这个接口，ViewModel 之间不需要互相依赖，统一由导航服务完成页面跳转。
    /// </summary>
    public interface INavigationService
    {
        /// <summary>当前页面对应的 ViewModel（绑定到内容区进行显示）</summary>
        object? CurrentView { get; }

        /// <summary>当前页面的中文标题（显示在导航栏顶部）</summary>
        string CurrentPageTitle { get; }

        /// <summary>导航完成后触发的事件，通知外部更新界面绑定</summary>
        event EventHandler? Navigated;

        /// <summary>根据页面键（如 "Home"、"Settings"）导航到对应页面</summary>
        void NavigateTo(string pageKey);
    }
}
