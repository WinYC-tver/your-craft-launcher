using System.Windows;
using System.Windows.Controls;
using YCL.ViewModels;

namespace YCL.Views
{
    /// <summary>
    /// Java 管理页面：展示已检测到的 Java 列表，支持检测、安装、手动添加、设为默认。
    /// </summary>
    public partial class JavaPage : UserControl
    {
        public JavaPage()
        {
            InitializeComponent();
        }

        /// <summary>页面加载完成：触发一次 Java 列表刷新</summary>
        /// <remarks>
        /// ViewModel 构造函数里已经异步刷新过一次，但那时 UI 可能还没渲染好。
        /// 这里在 Loaded 事件中再刷新一次，确保进入 Java 页时看到的是最新列表。
        /// RefreshCommand 内部有 IsDetecting 防重入检查，不会重复执行。
        /// </remarks>
        private void JavaPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is JavaPageViewModel vm)
            {
                _ = vm.RefreshCommand.ExecuteAsync(null);
            }
        }
    }
}
