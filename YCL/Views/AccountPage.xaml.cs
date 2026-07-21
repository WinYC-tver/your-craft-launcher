using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using YCL.Core.Utils;
using YCL.ViewModels;

namespace YCL.Views
{
    /// <summary>
    /// 账户管理页：展示账户列表、添加账户（离线/微软/外置）、切换/刷新/删除账户。
    /// 顶部包含 Webview2 渲染的 3D 皮肤预览（three.js + skinview3d）。
    /// </summary>
    public partial class AccountPage : UserControl
    {
        public AccountPage()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 页面加载完成时：
        /// 1. 订阅 ViewModel 的 SkinPreviewRequested 事件（用户上传皮肤后刷新预览）
        /// 2. 初始化 Webview2 并导航到 SkinPreview.html
        ///    若 Webview2 运行时未安装，降级显示文字提示。
        /// </summary>
        private async void AccountPage_Loaded(object sender, RoutedEventArgs e)
        {
            // 订阅 ViewModel 事件（DataTemplate 渲染时 DataContext 在 Loaded 时已就绪）
            if (DataContext is AccountPageViewModel vm)
            {
                vm.SkinPreviewRequested -= OnSkinPreviewRequested;
                vm.SkinPreviewRequested += OnSkinPreviewRequested;
            }

            // Webview2 可能已初始化过（页面重入），跳过重复初始化
            if (SkinPreviewWebView.CoreWebView2 != null)
            {
                return;
            }

            try
            {
                await SkinPreviewWebView.EnsureCoreWebView2Async();
                // 初始化成功：显示 Webview2，隐藏降级提示
                SkinPreviewWebView.Visibility = Visibility.Visible;
                SkinPreviewFallback.Visibility = Visibility.Collapsed;
                // 默认导航到 SkinPreview.html（不带 skin 参数，HTML 内部使用默认 Steve 皮肤）
                var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SkinPreview.html");
                if (File.Exists(htmlPath) && SkinPreviewWebView.CoreWebView2 != null)
                {
                    var uri = new Uri(htmlPath).AbsoluteUri;
                    SkinPreviewWebView.CoreWebView2.Navigate(uri);
                }
            }
            catch (Exception ex)
            {
                // Webview2 运行时未安装或其他初始化失败：降级显示文字提示
                Logger.Warn($"Webview2 初始化失败，3D 皮肤预览将不可用：{ex.Message}");
                SkinPreviewWebView.Visibility = Visibility.Collapsed;
                SkinPreviewFallback.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 收到 ViewModel 的皮肤刷新请求后，导航到带 skin 参数的 SkinPreview.html。
        /// 参数 skinPath 是本地皮肤 PNG 文件的绝对路径。
        /// </summary>
        private void OnSkinPreviewRequested(string skinPath)
        {
            try
            {
                if (SkinPreviewWebView?.CoreWebView2 == null) return;
                if (string.IsNullOrEmpty(skinPath) || !File.Exists(skinPath)) return;

                var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SkinPreview.html");
                if (!File.Exists(htmlPath)) return;

                var htmlUri = new Uri(htmlPath).AbsoluteUri;
                var skinUri = new Uri(skinPath).AbsoluteUri;
                var url = $"{htmlUri}?skin={Uri.EscapeDataString(skinUri)}";
                SkinPreviewWebView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                Logger.Warn($"刷新 3D 皮肤预览失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 密码框内容变化时同步到 ViewModel。
        /// WPF 的 PasswordBox 不支持双向绑定（安全考虑），需在代码中读取。
        /// </summary>
        private void YggdrasilPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is AccountPageViewModel vm && sender is PasswordBox pb)
            {
                vm.YggdrasilPassword = pb.Password;
            }
        }
    }
}
