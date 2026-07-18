using System.Windows;
using iNKORE.UI.WPF.Modern.Controls;
using YCL.ViewModels;

namespace YCL
{
    /// <summary>
    /// 主窗口：使用 iNKORE.UI.WPF.Modern 的现代窗口样式（Mica 背景），
    /// 左侧为 NavigationView 侧边导航，右侧为内容区。
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent();
            // 通过依赖注入传入的 ViewModel 设为数据上下文
            DataContext = viewModel;
        }

        /// <summary>窗口加载完成后，默认选中"主页"导航项</summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 选中第一项会触发 SelectionChanged，从而导航到主页
            NavigationView_Root.SelectedItem = NavigationViewItem_Home;
        }

        /// <summary>导航项切换事件：根据选中项的 Tag 调用 ViewModel 的导航命令</summary>
        private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            // 选中项可能是 NavigationViewItem（也可能是其它类型，所以做类型判断）
            if (sender.SelectedItem is NavigationViewItem item && item.Tag is string pageKey)
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    // 调用导航命令，传入页面键（如 "Home"、"Settings"）
                    vm.NavigateCommand.Execute(pageKey);
                }
            }
        }
    }
}
