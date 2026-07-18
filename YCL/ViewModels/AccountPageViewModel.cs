using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YCL.Core.Accounts;
using YCL.Core.Utils;
using YCL.Models.Accounts;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 账户管理页 ViewModel：负责账户的增删改查、切换、刷新与界面展示。
    /// 支持三种账户类型：离线、微软（设备代码流）、外置登录。
    /// </summary>
    public partial class AccountPageViewModel : ViewModelBase
    {
        private readonly IAccountManager _accountManager;
        private readonly SkinService _skinService;

        /// <summary>微软登录的取消令牌源（登录过程中可取消）</summary>
        private CancellationTokenSource? _msLoginCts;

        public AccountPageViewModel(IAccountManager accountManager, SkinService skinService)
        {
            _accountManager = accountManager;
            _skinService = skinService;

            _accountManager.AccountsChanged += OnAccountsChanged;
            RefreshAccountList();
        }

        /// <summary>账户列表（用于界面显示，每项含头像与类型文字）</summary>
        public ObservableCollection<AccountDisplayItem> Accounts { get; } = new();

        /// <summary>当前选中账户 ID（用于界面高亮）</summary>
        [ObservableProperty]
        private Guid? _currentAccountId;

        // ===== 添加账户对话框相关 =====

        /// <summary>添加账户面板是否可见</summary>
        [ObservableProperty]
        private bool _isAddPanelVisible;

        /// <summary>选中的添加账户类型（0=离线 1=微软 2=外置）</summary>
        [ObservableProperty]
        private int _selectedAddTypeIndex;

        /// <summary>是否选中离线账户类型</summary>
        [ObservableProperty]
        private bool _isOfflineSelected = true;

        /// <summary>是否选中微软账户类型</summary>
        [ObservableProperty]
        private bool _isMicrosoftSelected;

        /// <summary>是否选中外置登录类型</summary>
        [ObservableProperty]
        private bool _isYggdrasilSelected;

        /// <summary>账户列表是否为空（用于显示空提示）</summary>
        [ObservableProperty]
        private bool _hasNoAccounts = true;

        /// <summary>类型选择变化时同步派生属性</summary>
        partial void OnSelectedAddTypeIndexChanged(int value)
        {
            IsOfflineSelected = value == 0;
            IsMicrosoftSelected = value == 1;
            IsYggdrasilSelected = value == 2;
        }

        /// <summary>离线账户用户名输入</summary>
        [ObservableProperty]
        private string _offlineUsername = string.Empty;

        /// <summary>外置登录服务器地址</summary>
        [ObservableProperty]
        private string _yggdrasilServerUrl = "https://littleskin.cn/api/yggdrasil";

        /// <summary>外置登录用户名</summary>
        [ObservableProperty]
        private string _yggdrasilUsername = string.Empty;

        /// <summary>外置登录密码</summary>
        [ObservableProperty]
        private string _yggdrasilPassword = string.Empty;

        /// <summary>是否正在执行添加操作（按钮禁用）</summary>
        [ObservableProperty]
        private bool _isAdding;

        /// <summary>添加操作的提示信息</summary>
        [ObservableProperty]
        private string? _addMessage;

        // ===== 微软登录进度相关 =====

        /// <summary>微软登录面板是否可见</summary>
        [ObservableProperty]
        private bool _isMicrosoftLoginVisible;

        /// <summary>微软登录用户代码（显示在界面上让用户输入）</summary>
        [ObservableProperty]
        private string? _msUserCode;

        /// <summary>微软登录验证网址</summary>
        [ObservableProperty]
        private string? _msVerificationUri;

        /// <summary>微软登录进度文字</summary>
        [ObservableProperty]
        private string _msProgressMessage = string.Empty;

        /// <summary>微软登录是否可取消</summary>
        [ObservableProperty]
        private bool _canCancelMsLogin;

        /// <summary>显示打开浏览器按钮</summary>
        [ObservableProperty]
        private bool _showOpenBrowserButton;

        /// <summary>显示"添加账户"按钮</summary>
        [RelayCommand]
        private void ShowAddPanel()
        {
            IsAddPanelVisible = true;
            IsMicrosoftLoginVisible = false;
            AddMessage = null;
        }

        /// <summary>取消添加账户</summary>
        [RelayCommand]
        private void CancelAdd()
        {
            // 取消可能正在进行的微软登录
            _msLoginCts?.Cancel();
            IsAddPanelVisible = false;
            IsMicrosoftLoginVisible = false;
            IsAdding = false;
            AddMessage = null;
        }

        /// <summary>确认添加账户（根据选中的类型执行对应流程）</summary>
        [RelayCommand]
        private async Task ConfirmAddAsync()
        {
            IsAdding = true;
            AddMessage = null;
            try
            {
                switch (SelectedAddTypeIndex)
                {
                    case 0:
                        await AddOfflineAsync();
                        break;
                    case 1:
                        await AddMicrosoftAsync();
                        break;
                    case 2:
                        await AddYggdrasilAsync();
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                AddMessage = "已取消登录";
            }
            catch (Exception ex)
            {
                Logger.Error("添加账户失败", ex);
                AddMessage = "添加失败：" + ex.Message;
            }
            finally
            {
                IsAdding = false;
            }
        }

        /// <summary>添加离线账户</summary>
        private async Task AddOfflineAsync()
        {
            if (string.IsNullOrWhiteSpace(OfflineUsername))
            {
                AddMessage = "请输入玩家名";
                return;
            }

            await _accountManager.AddOfflineAccountAsync(OfflineUsername);
            IsAddPanelVisible = false;
            OfflineUsername = string.Empty;
        }

        /// <summary>添加微软账户（设备代码流）</summary>
        private async Task AddMicrosoftAsync()
        {
            IsMicrosoftLoginVisible = true;
            ShowOpenBrowserButton = false;
            CanCancelMsLogin = true;
            MsProgressMessage = "正在请求设备代码...";

            _msLoginCts?.Dispose();
            _msLoginCts = new CancellationTokenSource();

            var progress = new Progress<MicrosoftLoginProgress>(OnMsLoginProgress);
            await _accountManager.AddMicrosoftAccountAsync(progress, _msLoginCts.Token);

            // 登录成功
            IsAddPanelVisible = false;
            IsMicrosoftLoginVisible = false;
        }

        /// <summary>微软登录进度回调（切回 UI 线程）</summary>
        private void OnMsLoginProgress(MicrosoftLoginProgress p)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                MsUserCode = p.UserCode;
                MsVerificationUri = p.VerificationUri;
                MsProgressMessage = p.Message;
                ShowOpenBrowserButton = p.Stage == MicrosoftLoginStage.WaitingForUser;
            });
        }

        /// <summary>添加外置账户</summary>
        private async Task AddYggdrasilAsync()
        {
            if (string.IsNullOrWhiteSpace(YggdrasilServerUrl))
            {
                AddMessage = "请输入认证服务器地址";
                return;
            }
            if (string.IsNullOrWhiteSpace(YggdrasilUsername))
            {
                AddMessage = "请输入用户名";
                return;
            }
            if (string.IsNullOrWhiteSpace(YggdrasilPassword))
            {
                AddMessage = "请输入密码";
                return;
            }

            AddMessage = "正在登录...";
            await _accountManager.AddYggdrasilAccountAsync(
                YggdrasilServerUrl, YggdrasilUsername, YggdrasilPassword);
            IsAddPanelVisible = false;
            YggdrasilUsername = string.Empty;
            YggdrasilPassword = string.Empty;
        }

        /// <summary>打开浏览器访问验证网址</summary>
        [RelayCommand]
        private void OpenBrowser()
        {
            if (string.IsNullOrEmpty(MsVerificationUri)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = MsVerificationUri,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Warn("打开浏览器失败：" + ex.Message);
            }
        }

        /// <summary>设置当前账户</summary>
        [RelayCommand]
        private void SetCurrent(Guid accountId)
        {
            _accountManager.SetCurrentAccount(accountId);
        }

        /// <summary>删除账户</summary>
        [RelayCommand]
        private async Task RemoveAsync(Guid accountId)
        {
            var result = MessageBox.Show("确定要删除这个账户吗？", "删除账户",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (result != MessageBoxResult.OK) return;

            _accountManager.RemoveAccount(accountId);
            await Task.CompletedTask;
        }

        /// <summary>刷新账户令牌</summary>
        [RelayCommand]
        private async Task RefreshAsync(Guid accountId)
        {
            var ok = await _accountManager.RefreshAccountAsync(accountId);
            if (!ok)
            {
                MessageBox.Show("令牌刷新失败，请检查网络或重新登录。", "刷新失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>账户列表变化时刷新界面</summary>
        private void OnAccountsChanged(object? sender, EventArgs e)
        {
            Application.Current?.Dispatcher.Invoke(RefreshAccountList);
        }

        /// <summary>刷新账户列表显示（含异步加载头像）</summary>
        private void RefreshAccountList()
        {
            var accounts = _accountManager.GetAccounts();
            CurrentAccountId = _accountManager.CurrentAccount?.AccountId;

            Accounts.Clear();
            foreach (var account in accounts)
            {
                var item = new AccountDisplayItem(account, _skinService)
                {
                    IsCurrent = account.AccountId == CurrentAccountId
                };
                Accounts.Add(item);
                _ = item.LoadAvatarAsync();
            }

            HasNoAccounts = Accounts.Count == 0;
        }
    }

    /// <summary>
    /// 账户显示项：包装 AccountBase，提供界面绑定用的头像、类型文字等属性。
    /// </summary>
    public class AccountDisplayItem : ObservableObject
    {
        private readonly AccountBase _account;
        private readonly SkinService _skinService;

        public AccountDisplayItem(AccountBase account, SkinService skinService)
        {
            _account = account;
            _skinService = skinService;
        }

        /// <summary>原始账户对象</summary>
        public AccountBase Account => _account;

        /// <summary>账户 ID</summary>
        public Guid AccountId => _account.AccountId;

        /// <summary>用户名</summary>
        public string Username => _account.Username;

        /// <summary>账户类型文字</summary>
        public string TypeText => _account.Type switch
        {
            AccountType.Offline => "离线",
            AccountType.Microsoft => "微软",
            AccountType.Yggdrasil => "外置",
            _ => _account.Type.ToString()
        };

        /// <summary>UUID（显示用）</summary>
        public string Uuid => _account.Uuid;

        private bool _isCurrent;
        /// <summary>是否为当前选中账户（用于高亮显示）</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set => SetProperty(ref _isCurrent, value);
        }

        private ImageSource? _avatar;
        /// <summary>头像（可能为 null）</summary>
        public ImageSource? Avatar
        {
            get => _avatar;
            set => SetProperty(ref _avatar, value);
        }

        /// <summary>异步加载头像</summary>
        public async Task LoadAvatarAsync()
        {
            string? skinUrl = _account switch
            {
                MicrosoftAccount ms => ms.SkinUrl,
                YggdrasilAccount yg => yg.SkinUrl,
                _ => null
            };

            var avatar = await _skinService.GetAvatarAsync(_account.Uuid, skinUrl);
            Application.Current?.Dispatcher.Invoke(() => Avatar = avatar);
        }
    }
}
