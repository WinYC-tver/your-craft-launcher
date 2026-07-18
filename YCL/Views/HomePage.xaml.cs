using System.Windows;
using System.Windows.Controls;
using YCL.ViewModels;

namespace YCL.Views
{
    /// <summary>
    /// 主页：版本管理页面。展示已安装版本列表，支持安装/删除/重命名/复制/启动等操作。
    /// </summary>
    public partial class HomePage : UserControl
    {
        public HomePage()
        {
            InitializeComponent();
        }

        /// <summary>页面加载完成：触发一次版本列表刷新</summary>
        /// <remarks>
        /// ViewModel 构造函数里已经异步刷新过一次，但那时 UI 可能还没渲染好。
        /// 这里在 Loaded 事件中再刷新一次，确保进入主页时看到的是最新版本列表。
        /// RefreshCommand 内部有 IsRefreshing 防重入检查，不会重复执行。
        /// 通过 RefreshCommand（而非 RefreshAsync）调用，因为后者是 private 方法。
        /// </remarks>
        private void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is HomePageViewModel vm)
            {
                _ = vm.RefreshCommand.ExecuteAsync(null);
            }
        }
    }
}
