using System.Windows.Controls;

namespace YCL.Views
{
    /// <summary>存档管理页：备份、恢复、导入、导出、删除存档</summary>
    public partial class SavesPage : UserControl
    {
        public SavesPage()
        {
            InitializeComponent();
        }

        /// <summary>页面加载时刷新版本列表（ViewModel 构造时已自动刷新，这里作为兜底）</summary>
        private void SavesPage_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            // ViewModel 构造函数中已调用 RefreshVersionsAsync，此处无需重复
        }
    }
}
