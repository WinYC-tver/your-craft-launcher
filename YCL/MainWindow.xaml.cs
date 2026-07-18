using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using iNKORE.UI.WPF.Modern.Controls;
using YCL.Services;
using YCL.ViewModels;

namespace YCL
{
    /// <summary>
    /// 主窗口：使用 iNKORE.UI.WPF.Modern 的现代窗口样式（默认 Acrylic 背景），
    /// 顶部为 NavigationView 5 大板块导航，下方为内容区。
    /// 支持自定义壁纸层（位于 NavigationView 背后）。
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private string? _wallpaperPath;
        private double _wallpaperOpacity = 0.3;

        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent();
            // 通过依赖注入传入的 ViewModel 设为数据上下文
            DataContext = viewModel;

            // 订阅主题服务的壁纸变更事件，使设置页改动即时生效
            ThemeService.WallpaperChanged += OnWallpaperChanged;
        }

        /// <summary>自定义壁纸图片路径（为 null 时不显示壁纸）</summary>
        public string? WallpaperPath
        {
            get => _wallpaperPath;
            set
            {
                if (_wallpaperPath != value)
                {
                    _wallpaperPath = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>壁纸不透明度（0~1）</summary>
        public double WallpaperOpacity
        {
            get => _wallpaperOpacity;
            set
            {
                if (Math.Abs(_wallpaperOpacity - value) > 0.001)
                {
                    _wallpaperOpacity = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>壁纸变更事件回调：在 UI 线程更新自身属性</summary>
        private void OnWallpaperChanged(string? path, double opacity)
        {
            // 切到 UI 线程执行（事件可能在任意线程触发）
            Dispatcher.BeginInvoke(new Action(() =>
            {
                WallpaperPath = path;
                WallpaperOpacity = opacity;
            }));
        }

        /// <summary>窗口加载完成后，默认选中"启动"导航项，并应用配置中的壁纸</summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 从应用资源读取初始壁纸（由 ThemeService.ApplyFromConfig 在启动时写入）
            var initialPath = Application.Current.Resources["AppWallpaperPath"] as string;
            var initialOpacity = Application.Current.Resources["AppWallpaperOpacity"] as double? ?? 0.3;
            WallpaperPath = initialPath;
            WallpaperOpacity = initialOpacity;

            // 选中第一项会触发 SelectionChanged，从而导航到启动页
            NavigationView_Root.SelectedItem = NavigationViewItem_Launch;
        }

        /// <summary>导航项切换事件：根据选中项的 Tag 调用 ViewModel 的导航命令</summary>
        private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            // 选中项可能是 NavigationViewItem（也可能是其它类型，所以做类型判断）
            if (sender.SelectedItem is NavigationViewItem item && item.Tag is string pageKey)
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    // 调用导航命令，传入页面键（如 "Launch"、"Settings"）
                    vm.NavigateCommand.Execute(pageKey);
                }
            }
        }

        /// <summary>窗口关闭时取消订阅事件，避免内存泄漏</summary>
        protected override void OnClosed(EventArgs e)
        {
            ThemeService.WallpaperChanged -= OnWallpaperChanged;
            base.OnClosed(e);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>触发属性变更通知（界面绑定会自动更新）</summary>
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
