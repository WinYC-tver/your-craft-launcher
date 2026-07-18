using System.Windows.Controls;
using YCL.ViewModels;

namespace YCL.Views
{
    /// <summary>
    /// 账户管理页：展示账户列表、添加账户（离线/微软/外置）、切换/刷新/删除账户。
    /// </summary>
    public partial class AccountPage : UserControl
    {
        public AccountPage()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 密码框内容变化时同步到 ViewModel。
        /// WPF 的 PasswordBox 不支持双向绑定（安全考虑），需在代码中读取。
        /// </summary>
        private void YggdrasilPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is AccountPageViewModel vm && sender is PasswordBox pb)
            {
                vm.YggdrasilPassword = pb.Password;
            }
        }
    }
}
