using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YCL.Core.Accounts;
using YCL.Core.Download;
using YCL.Core.Launch;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Models.Versions;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 联机页 ViewModel：集成 Terracotta（陶瓦联机）客户端。
    ///
    /// 主要功能：
    /// 1. 提供项目主页跳转
    /// 2. 从 GitHub Release 下载 Terracotta jar 客户端
    /// 3. 创建房间（占位：弹 MessageBox 提示）
    /// 4. 加入房间 / 联机启动：将 Terracotta jar 复制到 mods 目录，调用 IGameLauncher 启动游戏
    /// 5. 服务器列表：保存常用服务器地址到 %AppData%\YCL\servers.txt
    /// 6. 联机帮助：在 UI 上展示 Terracotta 联机步骤说明
    /// </summary>
    public partial class MultiplayerPageViewModel : ViewModelBase
    {
        private readonly IVersionManager _versionManager;
        private readonly IConfigService _configService;
        private readonly MultiThreadDownloader _downloader;
        private readonly IGameLauncher _gameLauncher;
        private readonly IAccountManager _accountManager;
        private readonly TerracottaService _terracottaService;

        /// <summary>房间列表定时刷新定时器（每 5 秒调用 GetRooms 刷新 Rooms 集合）</summary>
        private readonly DispatcherTimer _roomsRefreshTimer;

        /// <summary>Terracotta jar 存放路径：%AppData%\YCL\terracotta\terracotta.jar</summary>
        private static readonly string TerracottaJarPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YCL", "terracotta", "terracotta.jar");

        /// <summary>保存的常用服务器列表文件：%AppData%\YCL\servers.txt</summary>
        private static readonly string SavedServersPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YCL", "servers.txt");

        /// <summary>
        /// 共享 HttpClient（用于 GitHub API + jar 下载）。
        /// 使用静态实例避免 socket 耗尽。User-Agent 设为 "YCL"（GitHub API 强制要求）。
        /// </summary>
        private static readonly HttpClient Http = new HttpClient
        {
            DefaultRequestHeaders = { { "User-Agent", "YCL" } },
            Timeout = TimeSpan.FromMinutes(5)
        };

        public MultiplayerPageViewModel(
            IVersionManager versionManager,
            IConfigService configService,
            MultiThreadDownloader downloader,
            IGameLauncher gameLauncher,
            IAccountManager accountManager,
            TerracottaService terracottaService)
        {
            _versionManager = versionManager;
            _configService = configService;
            _downloader = downloader;
            _gameLauncher = gameLauncher;
            _accountManager = accountManager;
            _terracottaService = terracottaService;

            // 构造函数里检查 Terracotta 是否已下载
            if (File.Exists(TerracottaJarPath))
            {
                IsTerracottaReady = true;
                DownloadStatus = "Terracotta 已就绪";
            }

            // 加载已保存的服务器列表
            LoadSavedServers();

            // 立即异步加载已安装版本列表
            _ = LoadVersionsAsync();

            // 初始化房间列表定时刷新定时器：每 5 秒调用一次 GetRooms 刷新 Rooms 集合
            // 用 DispatcherTimer 保证 Tick 在 UI 线程触发，避免跨线程修改 ObservableCollection
            _roomsRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _roomsRefreshTimer.Tick += OnRoomsRefreshTimerTick;
            _roomsRefreshTimer.Start();

            // 立即刷新一次房间列表，避免首次显示要等 5 秒
            _ = RefreshRoomsAsync();
        }

        // ===== 绑定属性 =====

        /// <summary>已安装版本列表（用于下拉框选择）</summary>
        [ObservableProperty]
        private ObservableCollection<InstalledVersionInfo> _versions = new();

        /// <summary>当前选中的版本</summary>
        [ObservableProperty]
        private InstalledVersionInfo? _selectedVersion;

        /// <summary>是否正在下载 Terracotta</summary>
        [ObservableProperty]
        private bool _isDownloading;

        /// <summary>下载状态文字</summary>
        [ObservableProperty]
        private string _downloadStatus = "Terracotta 未下载";

        /// <summary>下载进度（0~100）</summary>
        [ObservableProperty]
        private double _downloadProgress;

        /// <summary>Terracotta 是否已就绪（即 jar 文件已存在）</summary>
        [ObservableProperty]
        private bool _isTerracottaReady;

        /// <summary>联机服务器地址（加入房间用）</summary>
        [ObservableProperty]
        private string _serverAddress = "";

        /// <summary>房间名称（创建房间用）</summary>
        [ObservableProperty]
        private string _roomName = "";

        /// <summary>
        /// 已保存的常用服务器地址列表（点击可快速填入 ServerAddress）。
        /// 持久化到 %AppData%\YCL\servers.txt，每行一个地址。
        /// </summary>
        public ObservableCollection<string> SavedServers { get; } = new();

        /// <summary>是否正在联机启动中（用于禁用按钮 + 显示状态）</summary>
        [ObservableProperty]
        private bool _isLaunching;

        /// <summary>联机启动状态文字（显示在加入房间卡片下方）</summary>
        [ObservableProperty]
        private string _launchStatus = "";

        /// <summary>当前房间列表（绑定到 UI，每 5 秒自动刷新一次）</summary>
        public ObservableCollection<TerracottaRoom> Rooms { get; } = new();

        /// <summary>联机码（加入房间用，8 位字符）</summary>
        [ObservableProperty]
        private string _joinCode = "";

        /// <summary>最大人数（创建房间用，默认 4 人）</summary>
        [ObservableProperty]
        private int _maxPlayers = 4;

        // ===== 命令 =====

        /// <summary>用系统默认浏览器打开 Terracotta 项目主页</summary>
        [RelayCommand]
        private void OpenTerracotta()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/burningtnt/Terracotta",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error("打开 Terracotta 项目主页失败", ex);
            }
        }

        /// <summary>加载已安装版本列表命令</summary>
        [RelayCommand]
        private async Task LoadVersionsAsync()
        {
            try
            {
                var versions = await _versionManager.ListInstalledVersionsAsync();
                Versions.Clear();
                foreach (var v in versions)
                    Versions.Add(v);

                // 默认选中第一个
                if (Versions.Count > 0 && SelectedVersion == null)
                {
                    SelectedVersion = Versions[0];
                }
            }
            catch (Exception ex)
            {
                Logger.Error("加载版本列表失败", ex);
            }
        }

        /// <summary>下载 Terracotta jar 命令</summary>
        [RelayCommand]
        private async Task DownloadTerracottaAsync()
        {
            if (IsDownloading) return;
            IsDownloading = true;
            DownloadProgress = 0;
            DownloadStatus = "正在获取最新版本信息...";

            try
            {
                // 1. 请求 GitHub API 获取最新 release
                var apiUrl = "https://api.github.com/repos/burningtnt/Terracotta/releases/latest";
                string jsonResponse;
                try
                {
                    jsonResponse = await Http.GetStringAsync(apiUrl);
                }
                catch (Exception ex)
                {
                    DownloadStatus = "获取最新版本失败：" + ex.Message;
                    Logger.Error("请求 GitHub API 失败", ex);
                    return;
                }

                // 2. 解析 JSON，找第一个 .jar 资源的下载地址
                string? downloadUrl = null;
                string? releaseName = null;
                try
                {
                    using var doc = JsonDocument.Parse(jsonResponse);
                    if (doc.RootElement.TryGetProperty("name", out var nameProp))
                        releaseName = nameProp.GetString();

                    if (doc.RootElement.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (!asset.TryGetProperty("name", out var nameEl)) continue;
                            var name = nameEl.GetString();
                            if (string.IsNullOrEmpty(name)) continue;
                            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

                            if (asset.TryGetProperty("browser_download_url", out var urlEl))
                            {
                                downloadUrl = urlEl.GetString();
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DownloadStatus = "解析版本信息失败：" + ex.Message;
                    Logger.Error("解析 GitHub release JSON 失败", ex);
                    return;
                }

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    DownloadStatus = "未找到可下载的 jar 文件";
                    Logger.Warn("Terracotta release 中没有 .jar 资源");
                    return;
                }

                // 3. 确保目录存在
                var dir = Path.GetDirectoryName(TerracottaJarPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // 4. 用 HttpClient 流式下载，带进度报告
                DownloadStatus = $"正在下载 Terracotta{(!string.IsNullOrEmpty(releaseName) ? "（" + releaseName + "）" : "")}...";
                await DownloadFileWithProgressAsync(downloadUrl, TerracottaJarPath);

                // 5. 完成
                IsTerracottaReady = true;
                DownloadStatus = "Terracotta 已就绪";
                DownloadProgress = 100;
                Logger.Info($"Terracotta 下载完成：{TerracottaJarPath}");
            }
            catch (Exception ex)
            {
                Logger.Error("下载 Terracotta 失败", ex);
                DownloadStatus = "下载失败：" + ex.Message;
            }
            finally
            {
                IsDownloading = false;
            }
        }

        /// <summary>
        /// 用 HttpClient 流式下载文件，带进度报告。
        /// 下载到 .part 临时文件，成功后再重命名为目标文件，避免半成品。
        /// </summary>
        private async Task DownloadFileWithProgressAsync(string url, string targetPath)
        {
            var tempPath = targetPath + ".part";

            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            long downloadedBytes = 0;
            var buffer = new byte[81920];
            var lastReportPercent = 0.0;

            await using var networkStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                buffer.Length, useAsync: true);

            int read;
            while ((read = await networkStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                downloadedBytes += read;

                // 更新进度（间隔 1% 以上才更新，减少 UI 抖动）
                if (totalBytes > 0)
                {
                    var percent = (double)downloadedBytes / totalBytes * 100.0;
                    if (percent - lastReportPercent >= 1.0 || percent >= 100.0)
                    {
                        lastReportPercent = percent;
                        DownloadProgress = percent;
                    }
                }
            }

            await fileStream.FlushAsync();

            // 替换文件
            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tempPath, targetPath);

            DownloadProgress = 100;
        }

        /// <summary>
        /// 创建房间命令：调用 TerracottaService.CreateRoom 启动 TCP 转发，生成联机码。
        /// 把返回的房间加到 Rooms 集合，并弹窗显示联机码和房主地址。
        /// </summary>
        [RelayCommand]
        private void CreateRoom()
        {
            if (string.IsNullOrWhiteSpace(RoomName))
            {
                MessageBox.Show("请先填写房间名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // 调用 TerracottaService 创建房间，启动 TCP 转发到 Minecraft 默认端口 25565
                var room = _terracottaService.CreateRoom(RoomName, MaxPlayers, 25565);

                // 把新房间加到本地集合（定时器 5 秒后也会刷新覆盖，但先加上让 UI 立即可见）
                Rooms.Add(room);

                Logger.Info($"房间已创建：{room.RoomName}（{room.RoomCode}），房主地址：{room.HostAddress}");

                MessageBox.Show(
                    "房间已创建，把联机码分享给朋友即可加入。\n\n" +
                    "房间名称：" + room.RoomName + "\n" +
                    "联机码：" + room.RoomCode + "\n" +
                    "房主地址：" + room.HostAddress + "\n\n" +
                    "请在 Minecraft 多人游戏里直接连接到：" + room.HostAddress,
                    "房间创建成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("创建房间失败", ex);
                MessageBox.Show("创建房间失败：" + ex.Message, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 加入房间命令：根据联机码调用 TerracottaService.GetRoomByCode 查询房主地址。
        /// 找不到房间时提示错误，找到时显示房主地址，让用户在 Minecraft 多人游戏里直连。
        /// </summary>
        [RelayCommand]
        private async Task JoinRoomAsync()
        {
            if (string.IsNullOrWhiteSpace(JoinCode))
            {
                MessageBox.Show("请先填写联机码。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // 在后台线程调用 GetRoomByCode，避免阻塞 UI（虽然内部只是查列表，但保持异步习惯）
                var code = JoinCode.Trim();
                var room = await Task.Run(() => _terracottaService.GetRoomByCode(code));

                if (room == null)
                {
                    MessageBox.Show(
                        "找不到该联机码的房间。\n\n" +
                        "请检查联机码是否正确，或房主是否已关闭房间。",
                        "加入房间失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                Logger.Info($"已查询到房间：{room.RoomName}（{room.RoomCode}），房主地址：{room.HostAddress}");

                MessageBox.Show(
                    "已找到房间！\n\n" +
                    "房间名称：" + room.RoomName + "\n" +
                    "联机码：" + room.RoomCode + "\n" +
                    "房主地址：" + room.HostAddress + "\n\n" +
                    "请在 Minecraft 多人游戏里直接连接到：" + room.HostAddress,
                    "加入房间",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("加入房间失败", ex);
                MessageBox.Show("加入房间失败：" + ex.Message, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 真正的联机启动命令：
        /// 1. 检查 Terracotta 是否已就绪
        /// 2. 检查是否选中版本
        /// 3. 检查服务器地址非空
        /// 4. 把 Terracotta jar 复制到选中版本的 mods 目录（Forge/Fabric 自动加载）
        /// 5. 同步配置到 IGameLauncher 并启动游戏
        /// 启动后显示"联机启动中..."状态，失败时显示错误信息。
        /// </summary>
        [RelayCommand]
        private async Task LaunchMultiplayerAsync()
        {
            // 防止重复点击
            if (IsLaunching) return;

            // 检查 Terracotta jar 是否已下载
            if (!IsTerracottaReady)
            {
                LaunchStatus = "请先下载 Terracotta 客户端。";
                MessageBox.Show(LaunchStatus, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 检查是否选择了游戏版本
            if (SelectedVersion == null)
            {
                LaunchStatus = "请先选择一个 Minecraft 版本。";
                MessageBox.Show(LaunchStatus, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 检查是否填写了服务器地址
            if (string.IsNullOrWhiteSpace(ServerAddress))
            {
                LaunchStatus = "请填写服务器地址。";
                MessageBox.Show(LaunchStatus, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 获取当前账户（启动游戏需要）
            var account = _accountManager.GetCurrentAccount();
            if (account == null)
            {
                LaunchStatus = "请先到账户页面添加并选择一个账户，再启动联机。";
                MessageBox.Show(LaunchStatus, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsLaunching = true;
            LaunchStatus = "联机启动中...";

            try
            {
                // 获取游戏目录（考虑版本隔离），把 Terracotta jar 复制到 mods 目录
                var gameDir = _versionManager.GetGameDirectory(SelectedVersion.Id);
                var modsDir = Path.Combine(gameDir, "mods");

                Directory.CreateDirectory(modsDir);
                var targetJarPath = Path.Combine(modsDir, "terracotta.jar");
                File.Copy(TerracottaJarPath, targetJarPath, overwrite: true);
                Logger.Info($"Terracotta jar 已复制到：{targetJarPath}");

                // 读取配置（Java 路径等）
                var config = _configService.Current;
                var javaPath = config.JavaPath;
                if (string.IsNullOrWhiteSpace(javaPath))
                {
                    LaunchStatus = "未配置 Java 路径，请到设置页填写 JavaPath。";
                    MessageBox.Show(
                        LaunchStatus + "\n\n如果你还没装 Java，可以从 https://adoptium.net 下载。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 同步配置到启动器（IGameLauncher 不支持额外 mod 参数，所以采用 mods 目录复制方案）
                _gameLauncher.EnableVersionIsolation = config.EnableVersionIsolation;
                _gameLauncher.WindowWidth = config.WindowWidth;
                _gameLauncher.WindowHeight = config.WindowHeight;
                _gameLauncher.FullscreenOnLaunch = config.FullscreenOnLaunch;
                _gameLauncher.ExtraJvmArgs = config.ExtraJvmArgs ?? string.Empty;
                _gameLauncher.CleanBeforeLaunch = config.CleanBeforeLaunch;

                // 获取 .minecraft 路径
                var minecraftPath = config.MinecraftPath;
                if (string.IsNullOrWhiteSpace(minecraftPath))
                {
                    minecraftPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        ".minecraft");
                }

                // 调用 IGameLauncher 启动游戏
                LaunchStatus = "正在调用启动器...";
                var success = await _gameLauncher.LaunchAsync(
                    minecraftPath,
                    SelectedVersion.Id,
                    account,
                    javaPath,
                    config.MaxMemory,
                    config.MinMemory);

                if (success)
                {
                    Logger.Info($"联机启动成功：版本 {SelectedVersion.Id}，服务器 {ServerAddress}");
                    LaunchStatus = $"联机已启动（版本 {SelectedVersion.Id}，服务器 {ServerAddress}）。\n进入游戏后请用多人游戏界面连接服务器。";
                    MessageBox.Show(
                        "联机游戏已启动！\n\n" +
                        "Terracotta jar 已复制到 mods 目录，Minecraft 启动后会自动加载。\n" +
                        "进入游戏后，使用 Terracotta 提供的多人游戏界面输入服务器地址连接。\n\n" +
                        "服务器地址：" + ServerAddress,
                        "联机启动成功",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    LaunchStatus = "联机启动失败，请查看启动器日志。";
                    MessageBox.Show(
                        LaunchStatus + "\n\n你可以手动启动 Minecraft，Terracotta jar 已在 mods 目录中。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("联机启动异常", ex);
                LaunchStatus = "联机启动异常：" + ex.Message;
                MessageBox.Show(
                    LaunchStatus + "\n\n你可以手动启动 Minecraft，Terracotta jar 已在 mods 目录中。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsLaunching = false;
            }
        }

        /// <summary>
        /// 保存当前 ServerAddress 到常用服务器列表。
        /// 同时写入 %AppData%\YCL\servers.txt（每行一个地址）和 SavedServers 集合。
        /// </summary>
        [RelayCommand]
        private void SaveServer()
        {
            var addr = ServerAddress?.Trim();
            if (string.IsNullOrEmpty(addr))
            {
                MessageBox.Show("请先填写服务器地址。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 去重：如果列表里已有该地址，先移除旧条目
            if (SavedServers.Contains(addr))
                SavedServers.Remove(addr);

            // 新地址加到最前面（最近使用的优先）
            SavedServers.Insert(0, addr);

            // 持久化到文件
            try
            {
                var dir = Path.GetDirectoryName(SavedServersPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllLines(SavedServersPath, SavedServers);
                Logger.Info($"已保存服务器地址：{addr}");
            }
            catch (Exception ex)
            {
                Logger.Error("保存服务器列表失败", ex);
                MessageBox.Show("保存服务器列表失败：" + ex.Message,
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 点击已保存的服务器条目：将其填入 ServerAddress 输入框。
        /// </summary>
        [RelayCommand]
        private void SelectServer(string? server)
        {
            if (string.IsNullOrEmpty(server)) return;
            ServerAddress = server;
        }

        /// <summary>
        /// 删除一个已保存的服务器条目（右键删除 / 删除按钮）。
        /// </summary>
        [RelayCommand]
        private void RemoveServer(string? server)
        {
            if (string.IsNullOrEmpty(server)) return;
            if (!SavedServers.Contains(server)) return;
            SavedServers.Remove(server);
            try
            {
                File.WriteAllLines(SavedServersPath, SavedServers);
            }
            catch (Exception ex)
            {
                Logger.Error("更新服务器列表文件失败", ex);
            }
        }

        /// <summary>
        /// 从 %AppData%\YCL\servers.txt 加载已保存的服务器列表。
        /// 文件不存在时静默跳过（首次使用）。
        /// </summary>
        private void LoadSavedServers()
        {
            try
            {
                if (!File.Exists(SavedServersPath)) return;
                SavedServers.Clear();
                foreach (var line in File.ReadAllLines(SavedServersPath))
                {
                    var s = line.Trim();
                    if (!string.IsNullOrEmpty(s))
                        SavedServers.Add(s);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("加载服务器列表失败：" + ex.Message);
            }
        }

        // ===== TerracottaService 房间列表刷新 & 房间管理 =====

        /// <summary>
        /// 定时器 Tick 事件处理：异步刷新房间列表。
        /// DispatcherTimer 默认在 UI 线程触发，无需手动切换。
        /// </summary>
        private void OnRoomsRefreshTimerTick(object? sender, EventArgs e)
        {
            _ = RefreshRoomsAsync();
        }

        /// <summary>
        /// 刷新房间列表命令：调用 TerracottaService.GetRooms 重新加载 Rooms 集合。
        /// 也可在 XAML 中绑定到页面 Loaded 事件或刷新按钮。
        /// </summary>
        [RelayCommand]
        private async Task RefreshRoomsAsync()
        {
            try
            {
                // 在后台线程调用 GetRooms（虽然内部加锁很快，但保持异步习惯避免阻塞 UI）
                var rooms = await Task.Run(() => _terracottaService.GetRooms());

                Rooms.Clear();
                foreach (var r in rooms)
                    Rooms.Add(r);
            }
            catch (Exception ex)
            {
                Logger.Error("刷新房间列表失败", ex);
            }
        }

        /// <summary>
        /// 关闭房间命令：房主退出时调用 TerracottaService.RemoveRoom 关闭 TCP 监听，
        /// 并从 Rooms 集合移除该房间。
        /// </summary>
        /// <param name="room">要关闭的房间（XAML 中可绑定 CommandParameter="{Binding SelectedRoom}"）</param>
        [RelayCommand]
        private void CloseRoom(TerracottaRoom? room)
        {
            if (room == null)
            {
                MessageBox.Show("请先选择要关闭的房间。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var success = _terracottaService.RemoveRoom(room.RoomCode);
                if (success)
                {
                    // 从本地集合移除（定时器也会同步，但先移除让 UI 立即反应）
                    Rooms.Remove(room);
                    Logger.Info($"房间已关闭：{room.RoomName}（{room.RoomCode}）");
                    MessageBox.Show(
                        "房间已关闭，TCP 监听已停止。\n\n" +
                        "房间名称：" + room.RoomName + "\n" +
                        "联机码：" + room.RoomCode,
                        "关闭房间",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        "关闭房间失败：找不到该房间（可能已被关闭）。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("关闭房间失败", ex);
                MessageBox.Show("关闭房间失败：" + ex.Message, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
