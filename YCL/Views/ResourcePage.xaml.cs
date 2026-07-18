using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YCL.ViewModels;

namespace YCL.Views
{
    /// <summary>
    /// 资源中心页面：搜索并下载资源包、光影包、地图，以及从本地文件安装整合包。
    /// </summary>
    public partial class ResourcePage : UserControl
    {
        public ResourcePage()
        {
            InitializeComponent();
        }

        /// <summary>页面加载完成：刷新版本列表</summary>
        private void ResourcePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ResourcePageViewModel vm)
            {
                _ = vm.RefreshVersionsCommand.ExecuteAsync(null);
            }
        }

        /// <summary>搜索框按回车键直接触发搜索</summary>
        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (DataContext is ResourcePageViewModel vm && vm.SearchCommand.CanExecute(null))
                {
                    vm.SearchCommand.Execute(null);
                }
                e.Handled = true;
            }
        }
    }
}
