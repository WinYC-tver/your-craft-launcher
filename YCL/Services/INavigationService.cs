using System;

namespace YCL.Services
{
    /// <summary>
    /// 页面导航服务接口，负责在主窗口中切换不同页面。
    /// 通过这个接口，ViewModel 之间不需要互相依赖，统一由导航服务完成页面跳转。
    /// 支持导航历史栈，配合主窗口的返回按钮实现"返回上一页"。
    /// </summary>
    public interface INavigationService
    {
        /// <summary>当前页面对应的 ViewModel（绑定到内容区进行显示）</summary>
        object? CurrentView { get; }

        /// <summary>当前页面的中文标题（显示在导航栏顶部）</summary>
        string CurrentPageTitle { get; }

        /// <summary>当前页面键（如 "Launch"、"Settings"），用于 BackButton 状态判断</summary>
        string CurrentPageKey { get; }

        /// <summary>是否可以返回上一页（历史栈非空时为 true）</summary>
        bool CanGoBack { get; }

        /// <summary>导航完成后触发的事件，通知外部更新界面绑定</summary>
        event EventHandler? Navigated;

        /// <summary>能否返回的状态变化事件（CanGoBack 从 false→true 或 true→false 时触发）</summary>
        event EventHandler? BackStateChanged;

        /// <summary>根据页面键（如 "Home"、"Settings"）导航到对应页面</summary>
        /// <param name="pageKey">目标页面键</param>
        /// <param name="recordHistory">是否把当前页压入历史栈（默认 true）；GoBack 内部调用时传 false 避免循环</param>
        void NavigateTo(string pageKey, bool recordHistory = true);

        /// <summary>返回上一页；栈为空时无操作</summary>
        /// <returns>是否成功返回（栈非空且能解析到上一页 ViewModel 时返回 true）</returns>
        bool GoBack();
    }
}
