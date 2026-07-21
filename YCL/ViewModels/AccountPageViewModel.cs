using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Win32;
using YCL.Core.Accounts;
using YCL.Core.Utils;
using YCL.Models;
using YCL.Models.Accounts;
using YCL.Services;
using MessageBox = System.Windows.MessageBox;

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
        private readonly IConfigService _configService;

        /// <summary>微软登录的取消令牌源（登录过程中可取消）</summary>
        private CancellationTokenSource? _msLoginCts;

        /// <summary>
        /// 皮肤预览刷新请求事件：参数为本地皮肤 PNG 文件的绝对路径。
        /// AccountPage.xaml.cs 订阅此事件，收到后刷新 Webview2 导航到新皮肤。
        /// </summary>
        public event Action<string>? SkinPreviewRequested;

        public AccountPageViewModel(IAccountManager accountManager, SkinService skinService, IConfigService configService)
        {
            _accountManager = accountManager;
            _skinService = skinService;
            _configService = configService;

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

        /// <summary>
        /// 更换皮肤命令：弹 OpenFileDialog 选择本地 .png 皮肤文件，
        /// 复制到 %AppData%\YCL\skins\{accountId}.png 与 %AppData%\YCL\cache\skins\{uuid}.png，
        /// 然后刷新头像显示。
        /// 对于离线账户：皮肤仅本地存储；
        /// 对于微软/外置账户：SkinService 未提供上传 API，目前也仅本地存储（提示用户）。
        /// </summary>
        [RelayCommand]
        private void UploadSkin(AccountDisplayItem? account)
        {
            if (account == null) return;

            var dialog = new OpenFileDialog
            {
                Title = "选择皮肤文件",
                Filter = "皮肤文件 (*.png)|*.png|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true) return;

            var skinFile = dialog.FileName;
            if (!File.Exists(skinFile))
            {
                MessageBox.Show("所选文件不存在。", "更换皮肤",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // 复制到 %AppData%\YCL\skins\{accountId}.png（按账户 ID 命名）
                var skinsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "YCL", "skins");
                Directory.CreateDirectory(skinsDir);
                var destPath = Path.Combine(skinsDir, account.AccountId + ".png");
                File.Copy(skinFile, destPath, true);

                // 同时复制到 SkinService 缓存目录（%AppData%\YCL\cache\skins\{uuid}.png），
                // 让 SkinService.GetAvatarFromCache 能读到新皮肤并截取头部头像
                var skinCacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "YCL", "cache", "skins");
                Directory.CreateDirectory(skinCacheDir);
                var cachePath = Path.Combine(skinCacheDir, account.Uuid + ".png");
                File.Copy(skinFile, cachePath, true);

                // 重新加载头像（从缓存读取新皮肤并截取头部）
                account.ReloadSkinFromCache();

                Logger.Info($"已更换皮肤：账户 {account.Username} -> {destPath}");

                // 触发 3D 皮肤预览刷新事件，通知 AccountPage 的 Webview2 加载新皮肤
                // 使用 cachePath（%AppData%\YCL\cache\skins\{uuid}.png）作为预览源
                SkinPreviewRequested?.Invoke(cachePath);

                // 对于微软/外置账户，提示用户皮肤仅本地存储（SkinService 未提供上传 API）
                var message = $"已更换皮肤：{account.Username}";
                if (account.TypeText == "微软" || account.TypeText == "外置")
                {
                    message += $"\n\n提示：此{account.TypeText}账户的皮肤仅保存在本地，未上传到服务器。";
                }
                MessageBox.Show(message, "更换皮肤",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("更换皮肤失败", ex);
                MessageBox.Show("更换皮肤失败：" + ex.Message, "更换皮肤",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>账户列表变化时刷新界面</summary>
        private void OnAccountsChanged(object? sender, EventArgs e)
        {
            Application.Current?.Dispatcher.Invoke(RefreshAccountList);
        }

        /// <summary>
        /// 修改 UUID 命令：弹 ContentDialog 选择 UUID 模式（行业规范/PCL/自定义），
        /// Custom 模式时显示 TextBox 输入自定义 UUID，
        /// 确认后根据玩家名重新生成 UUID 并更新账户。
        /// 仅离线账户支持此操作。
        /// </summary>
        [RelayCommand]
        private async Task ChangeUuidAsync(AccountDisplayItem? account)
        {
            if (account == null) return;

            // 仅离线账户支持修改 UUID
            if (account.TypeText != "离线")
            {
                MessageBox.Show("仅离线账户支持修改 UUID。", "修改 UUID",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 创建 UUID 模式选择 ComboBox，默认选中配置中的模式
            var modeCombo = new ComboBox { MinWidth = 240 };
            modeCombo.Items.Add(new ComboBoxItem { Content = "行业规范 UUID（推荐）" });
            modeCombo.Items.Add(new ComboBoxItem { Content = "PCL UUID" });
            modeCombo.Items.Add(new ComboBoxItem { Content = "自定义" });
            modeCombo.SelectedIndex = (int)_configService.Current.UuidMode;

            // 自定义 UUID 输入框（仅 Custom 模式显示）
            var customLabel = new TextBlock
            {
                Text = "自定义 UUID（带连字符的标准格式）：",
                Margin = new Thickness(0, 12, 0, 4),
                Visibility = Visibility.Collapsed
            };
            var customInput = new TextBox
            {
                MinWidth = 300,
                Visibility = Visibility.Collapsed
            };

            // 模式切换时同步显示/隐藏自定义输入区
            modeCombo.SelectionChanged += (s, e) =>
            {
                var isCustom = modeCombo.SelectedIndex == 2;
                customLabel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
                customInput.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            };

            var contentPanel = new StackPanel();
            contentPanel.Children.Add(new TextBlock
            {
                Text = $"为离线账户 {account.Username} 选择 UUID 生成模式：",
                TextWrapping = TextWrapping.Wrap
            });
            contentPanel.Children.Add(new TextBlock { Text = "UUID 模式：", Margin = new Thickness(0, 12, 0, 4) });
            contentPanel.Children.Add(modeCombo);
            contentPanel.Children.Add(customLabel);
            contentPanel.Children.Add(customInput);

            var dialog = new ContentDialog
            {
                Title = "修改 UUID",
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = contentPanel
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var mode = modeCombo.SelectedIndex;
            var customUuid = customInput.Text?.Trim() ?? "";

            // 根据模式生成新 UUID
            var newUuid = GenerateUuidByMode(account.Username, mode, customUuid, out string? error);
            if (newUuid == null)
            {
                MessageBox.Show(error ?? "UUID 生成失败", "修改 UUID",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 更新账户 UUID
            account.Account.Uuid = newUuid;

            // 保存 UUID 模式到配置
            _configService.Current.UuidMode = (UuidMode)mode;
            _configService.Save();

            // 调用 RefreshAccountAsync 触发 SaveInternal 保存账户（离线账户 RefreshAsync 返回 true）
            await _accountManager.RefreshAccountAsync(account.AccountId);

            Logger.Info($"已修改 UUID：账户 {account.Username} -> {newUuid}（模式 {mode}）");
            MessageBox.Show($"已修改 UUID：\n{newUuid}", "修改 UUID",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 根据模式生成离线 UUID（用于"修改 UUID"和"添加离线账户"流程）。
        /// - 0=行业规范：MD5 哈希 "OfflinePlayer:" + 玩家名，构造 UUID v3
        /// - 1=PCL：SHA256 哈希玩家名取前 16 字节构造 UUID
        /// - 2=自定义：用户输入的 UUID 字符串
        /// </summary>
        /// <param name="username">玩家名</param>
        /// <param name="mode">0=行业规范 1=PCL 2=自定义</param>
        /// <param name="customUuid">自定义模式下的 UUID 输入</param>
        /// <param name="error">校验失败时的错误信息</param>
        /// <returns>生成的 UUID 字符串；失败返回 null 并通过 error 输出错误信息</returns>
        private static string? GenerateUuidByMode(string username, int mode, string customUuid, out string? error)
        {
            error = null;
            switch (mode)
            {
                case 0: // 行业规范（MD5 + version=3，与 Java 版离线算法一致）
                    return OfflineAccount.GenerateOfflineUuid(username);
                case 1: // PCL：SHA256 哈希玩家名取前 16 字节构造 UUID
                    return GeneratePclUuid(username);
                case 2: // 自定义
                    if (string.IsNullOrWhiteSpace(customUuid))
                    {
                        error = "请输入自定义 UUID";
                        return null;
                    }
                    if (!Guid.TryParse(customUuid, out _))
                    {
                        error = "自定义 UUID 格式无效，请输入标准格式（如 xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx）";
                        return null;
                    }
                    return customUuid;
                default:
                    return OfflineAccount.GenerateOfflineUuid(username);
            }
        }

        /// <summary>
        /// 生成 PCL 风格的 UUID：对玩家名求 SHA256 哈希，取前 16 字节构造 UUID。
        /// 与 PCL 启动器使用 SHA256 而非 MD5 的算法一致。
        /// </summary>
        private static string GeneratePclUuid(string username)
        {
            try
            {
                var input = Encoding.UTF8.GetBytes(username);
                byte[] hash;
                using (var sha256 = SHA256.Create())
                {
                    hash = sha256.ComputeHash(input); // 32 字节
                }

                // 取前 16 字节作为 UUID
                var bytes = new byte[16];
                Array.Copy(hash, 0, bytes, 0, 16);

                // 设置 version=4（随机/哈希类）和 variant=IETF，保证 UUID 格式合法
                bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40); // version 4
                bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant IETF

                return new Guid(bytes).ToString();
            }
            catch
            {
                return Guid.NewGuid().ToString();
            }
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

        /// <summary>
        /// 从本地皮肤缓存重新加载头像（用于更换皮肤后刷新头像显示）。
        /// 直接调用 SkinService.GetAvatarFromCache，不走网络下载。
        /// </summary>
        public void ReloadSkinFromCache()
        {
            try
            {
                var avatar = _skinService.GetAvatarFromCache(_account.Uuid);
                Avatar = avatar;
            }
            catch (Exception ex)
            {
                Logger.Warn($"更换皮肤后刷新头像失败：{_account.Uuid} - {ex.Message}");
            }
        }
    }
}
