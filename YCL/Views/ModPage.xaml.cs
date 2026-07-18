using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YCL.ViewModels;

namespace YCL.Views
{
    /// <summary>
    /// 模组管理页面：本地模组扫描与管理、在线模组搜索与下载。
    /// </summary>
    public partial class ModPage : UserControl
    {
        public ModPage()
        {
            InitializeComponent();
        }

        /// <summary>页面加载完成：刷新版本列表</summary>
        private void ModPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ModPageViewModel vm)
            {
                _ = vm.RefreshVersionsCommand.ExecuteAsync(null);
            }
        }

        /// <summary>搜索框按回车键直接触发搜索</summary>
        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (DataContext is ModPageViewModel vm && vm.SearchCommand.CanExecute(null))
                {
                    vm.SearchCommand.Execute(null);
                }
                e.Handled = true;
            }
        }
    }
}
