using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YCL.Core.Accounts;
using YCL.Core.Download;
using YCL.Core.Java;
using YCL.Core.Launch;
using YCL.Core.ModLoaders;
using YCL.Core.Mods;
using YCL.Core.Resources;
using YCL.Core.Saves;
using YCL.Core.Screenshots;
using YCL.Core.Update;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Services;
using YCL.ViewModels;

namespace YCL
{
    /// <summary>
    /// 应用程序入口：使用 Microsoft.Extensions.Hosting 配置依赖注入容器，
    /// 注册所有 ViewModel、导航服务、主题服务、配置服务与主窗口，
    /// 挂接全局异常捕获，并在启动时应用已保存的主题设置。
    /// </summary>
    public partial class App : Application
    {
        private readonly IHost _host;

        public App()
        {
            // 先挂接全局异常捕获，确保后续启动过程中任何异常都能被记录
            RegisterGlobalExceptionHandlers();

            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // 1. 注册配置服务（单例，整个应用共享同一份配置）
                    services.AddSingleton<IConfigService, ConfigService>();

                    // 2. 注册主题服务（单例）
                    services.AddSingleton<IThemeService, ThemeService>();

                    // 3. 注册导航服务（单例，整个应用共享一个实例）
                    services.AddSingleton<INavigationService, NavigationService>();

                    // 4. 注册版本解析服务（单例）
                    services.AddSingleton<IVersionResolver, VersionResolver>();

                    // 5. 注册启动参数生成器（单例）
                    services.AddSingleton<ILaunchArgumentBuilder, LaunchArgumentBuilder>();

                    // 6. 注册下载源管理器（单例，从配置读取当前下载源）
                    //    用工厂模式：先从 IConfigService 读取 DownloadSource 再构造
                    services.AddSingleton<IDownloadSource>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfigService>();
                        return new DownloadSourceManager(config.Current.DownloadSource);
                    });

                    // 7. 注册版本清单服务（单例，依赖 IDownloadSource）
                    services.AddSingleton<IVersionManifestService, VersionManifestService>();

                    // 8. 注册 Minecraft 文件下载器（单例，依赖 IDownloadSource 与配置）
                    //    用工厂模式：从 IConfigService 读取 DownloadThreads 与 RetryCount
                    services.AddSingleton<IMinecraftFileDownloader>(sp =>
                    {
                        var downloadSource = sp.GetRequiredService<IDownloadSource>();
                        var config = sp.GetRequiredService<IConfigService>();
                        return new MinecraftFileDownloader(
                            downloadSource,
                            config.Current.DownloadThreads,
                            config.Current.RetryCount);
                    });

                    // 9. 注册版本管理服务（单例，依赖清单服务、文件下载器、版本解析器与配置）
                    //    用工厂模式：通过两个 Func 委托从 IConfigService 读取 MinecraftPath 与 EnableVersionIsolation，
                    //    这样用户在设置页改了配置后能被 VersionManager 即时感知
                    services.AddSingleton<IVersionManager>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfigService>();
                        var manifestService = sp.GetRequiredService<IVersionManifestService>();
                        var fileDownloader = sp.GetRequiredService<IMinecraftFileDownloader>();
                        var versionResolver = sp.GetRequiredService<IVersionResolver>();
                        return new VersionManager(
                            () => config.Current.MinecraftPath,
                            () => config.Current.EnableVersionIsolation,
                            manifestService,
                            fileDownloader,
                            versionResolver);
                    });

                    // 10. 注册游戏启动器（单例，整个应用共享一个游戏进程）
                    services.AddSingleton<IGameLauncher, GameLauncher>();

                    // 11. 注册微软账户认证器（单例）
                    services.AddSingleton<IMicrosoftAuthenticator, MicrosoftAuthenticator>();

                    // 12. 注册外置登录认证器（单例）
                    services.AddSingleton<IYggdrasilAuthenticator, YggdrasilAuthenticator>();

                    // 13. 注册账户管理服务（单例，依赖两个认证器）
                    //     AccountManager 构造时会注入令牌刷新处理器到账户类，并加载已保存账户
                    services.AddSingleton<IAccountManager, AccountManager>();

                    // 14. 注册皮肤显示服务（单例）
                    services.AddSingleton<SkinService>();

                    // 14.1 注册本地化服务（单例，v26.1.0.5 多语言支持）
                    services.AddSingleton<LocalizationService>();

                    // 15. 注册 Java 检测器（单例，依赖 .minecraft 路径以扫描 runtime 目录）
                    //     用工厂模式：通过 Func 委托从 IConfigService 读取 MinecraftPath
                    services.AddSingleton<IJavaDetector>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfigService>();
                        return new JavaDetector(() => config.Current.MinecraftPath);
                    });

                    // 16. 注册 Java 安装器（单例，从 Adoptium 下载 JDK）
                    services.AddSingleton<IJavaInstaller, JavaInstaller>();

                    // 17. 注册模组加载器安装器（5 个，每个都依赖 .minecraft 路径与下载源）
                    //     Forge 与 NeoForge 还需要 JavaPath（运行 installer.jar）
                    //     所有加载器注册为 IModLoaderInstaller，ModLoaderManager 通过 IEnumerable<IModLoaderInstaller> 收集
                    services.AddSingleton<IModLoaderInstaller>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfigService>();
                        var downloadSource = sp.GetRequiredService<IDownloadSource>();
                        return new FabricInstaller(
                            () => config.Current.MinecraftPath,
                            downloadSource);
                    });
                    services.AddSingleton<IModLoaderInstaller>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfigService>();
                        var downloadSource = sp.GetRequiredService<IDownloadSource>();
                        return new ForgeInstaller(
                            () => config.Current.MinecraftPath,
                            downloadSource,
                            () => config.Current.JavaPath);
                    });
                    services.AddSingleton<IModLoaderInstaller>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfigService>();
                        var downloadSource = sp.GetRequiredService<IDownloadSource>();
                        return new QuiltInstaller(
                            () => config.Current.MinecraftPath,
                            downloadSource);
                    });
                    services.AddSingleton<IModLoaderInstaller>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfigService>();
                        var downloadSource = sp.GetRequiredService<IDownloadSource>();
                        return new NeoForgeInstaller(
                            () => config.Current.MinecraftPath,
                            downloadSource,
                            () => config.Current.JavaPath);
                    });
                    services.AddSingleton<IModLoaderInstaller>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfigService>();
                        var downloadSource = sp.GetRequiredService<IDownloadSource>();
                        return new LiteLoaderInstaller(
                            () => config.Current.MinecraftPath,
                            downloadSource);
                    });

                    // 18. 注册模组加载器管理服务（单例，收集所有 IModLoaderInstaller）
                    services.AddSingleton<IModLoaderManager, ModLoaderManager>();

                    // 18.5 注册多线程下载器（单例，供模组/资源下载复用）
                    //     从 IConfigService 读取 DownloadThreads 与 RetryCount
                    services.AddSingleton<MultiThreadDownloader>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfigService>();
                        return new MultiThreadDownloader(config.Current.DownloadThreads, config.Current.RetryCount);
                    });

                    // 18.6 注册本地模组管理服务（单例，无状态，调用时传入 gameDir）
                    services.AddSingleton<ILocalModManager, LocalModManager>();

                    // 18.7 注册 CurseForge 客户端（单例，从配置读取 API Key）
                    //      Key 为空时 IsConfigured 返回 false，调用方会自动降级到 Modrinth
                    services.AddSingleton<ICurseForgeClient>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfigService>();
                        var downloader = sp.GetRequiredService<MultiThreadDownloader>();
                        return new CurseForgeClient(config.Current.CurseForgeApiKey, downloader);
                    });

                    // 18.8 注册 Modrinth 客户端（单例，公开 API 无需 Key）
                    services.AddSingleton<IModrinthClient>(sp =>
                    {
                        var downloader = sp.GetRequiredService<MultiThreadDownloader>();
                        return new ModrinthClient(downloader);
                    });

                    // 18.9 注册模组下载服务（单例，统一封装 CurseForge 与 Modrinth）
                    services.AddSingleton<IModDownloadService, ModDownloadService>();

                    // 18.10 注册整合包安装服务（单例，依赖版本管理、加载器、两个平台客户端）
                    services.AddSingleton<IModpackService, ModpackService>();

                    // 18.11 注册资源下载服务（单例，搜索并下载资源包/光影包/地图）
                    services.AddSingleton<IResourceService, ResourceService>();

                    // 18.12 注册存档管理服务（单例，无状态，调用时传入 gameDir）
                    services.AddSingleton<ISaveManager, SaveManager>();

                    // 18.13 注册截图管理服务（单例，无状态）
                    services.AddSingleton<IScreenshotManager, ScreenshotManager>();

                    // 18.14 注册启动器更新检查器（单例，从配置读取 GitHub 仓库地址）
                    //     用工厂模式：通过 Func 委托从 IConfigService 读取 UpdateRepo
                    services.AddSingleton<IUpdateChecker>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfigService>();
                        return new UpdateChecker(() => config.Current.UpdateRepo);
                    });

                    // 18.15 注册 Terracotta 联机服务（单例，提供基于 TCP 转发的房间管理）
                    services.AddSingleton<TerracottaService>();

                    // 19. 注册主窗口 ViewModel（单例）
                    services.AddSingleton<MainWindowViewModel>();

                    // 20. 注册各页面 ViewModel
                    //     启动页与下载页 ViewModel 设为单例：
                    //     - 启动页：IGameLauncher 事件订阅需要在 ViewModel 生命周期内一致
                    //     - 下载页：IMinecraftFileDownloader 事件订阅需要避免页面切换时累积
                    services.AddSingleton<LaunchPageViewModel>();
                    services.AddSingleton<DownloadPageViewModel>();
                    // 资源详情页 ViewModel（单例，由 DownloadPage 内嵌显示，避免每次进入重建状态）
                    services.AddSingleton<ResourceDetailPageViewModel>();
                    services.AddSingleton<HomePageViewModel>();
                    services.AddSingleton<JavaPageViewModel>();
                    services.AddSingleton<AccountPageViewModel>();
                    services.AddSingleton<ModPageViewModel>();
                    services.AddSingleton<ResourcePageViewModel>();
                    services.AddTransient<SettingsPageViewModel>();
                    services.AddTransient<AboutPageViewModel>();
                    // 存档页与截图页：每次导航都重新创建，确保数据为最新
                    services.AddTransient<SavesPageViewModel>();
                    services.AddTransient<ScreenshotsPageViewModel>();

                    // 20.5 v26 新增板块 ViewModel：新闻 / 功能 / 联机 / 实例面板
                    services.AddSingleton<NewsPageViewModel>();
                    services.AddSingleton<FunctionsHubViewModel>();
                    services.AddSingleton<MultiplayerPageViewModel>();
                    services.AddSingleton<InstancePanelViewModel>();

                    // 21. 注册主窗口（单例）
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            Logger.Info("启动器主机已构建完成");
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            Logger.Info("启动器正在启动...");

            // 启动主机
            await _host.StartAsync();

            // 从容器获取主题服务与配置服务，应用上次保存的主题设置
            var themeService = _host.Services.GetRequiredService<IThemeService>();
            var configService = _host.Services.GetRequiredService<IConfigService>();
            themeService.ApplyFromConfig(configService);

            // 应用上次保存的界面语言（v26.1.0.5 多语言支持）
            _host.Services.GetRequiredService<LocalizationService>().InitializeFromConfig();

            // 从依赖注入容器获取主窗口并显示
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            Logger.Info("主窗口已显示，启动完成");

            // 启动时检查更新（异步，不阻塞主窗口显示）
            // 仅在用户启用了 "启动时检查更新" 时执行
            if (configService.Current.CheckUpdateOnStartup)
            {
                _ = CheckForUpdatesAsync(_host.Services.GetRequiredService<IUpdateChecker>());
            }

            base.OnStartup(e);
        }

        /// <summary>
        /// 异步检查启动器更新。发现新版本时弹窗提示用户。
        /// 任何异常都记录日志，不影响启动器正常使用。
        /// </summary>
        private async Task CheckForUpdatesAsync(IUpdateChecker updateChecker)
        {
            try
            {
                Logger.Info("开始启动时更新检查");
                var release = await updateChecker.CheckForUpdatesAsync(System.Threading.CancellationToken.None);
                if (release != null)
                {
                    Logger.Info($"发现新版本：{release.Version}");
                    // 切回 UI 线程弹窗提示
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var msg = $"发现新版本 {release.Version}！\n\n" +
                                  $"发布时间：{release.PublishedAtDisplay}\n\n" +
                                  $"更新内容：\n{release.ReleaseNotes}\n\n" +
                                  $"是否前往下载页面？";
                        var result = MessageBox.Show(msg, "发现新版本",
                            MessageBoxButton.YesNo, MessageBoxImage.Information);
                        if (result == MessageBoxResult.Yes)
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = release.ReleaseUrl,
                                UseShellExecute = true
                            });
                        }
                    }));
                }
                else
                {
                    Logger.Info("启动时更新检查：已是最新版本");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("启动时更新检查失败", ex);
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            Logger.Info("启动器正在退出...");
            // 退出时停止并释放主机
            await _host.StopAsync();
            _host.Dispose();

            base.OnExit(e);
        }

        /// <summary>
        /// 挂接三个全局异常入口，确保任何线程抛出的异常都能被捕获并记录。
        /// 捕获后：写日志 + 弹出友好的错误对话框，尽量避免应用崩溃。
        /// </summary>
        private void RegisterGlobalExceptionHandlers()
        {
            // 1. UI 线程异常：Dispatcher 上未处理的异常
            //    设置 e.Handled = true 可阻止应用崩溃
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            // 2. 非 UI 线程异常：所有 .NET 线程中未处理的异常
            //    注意：这个入口无法阻止 CLR 终止进程，只能记录
            AppDomain.CurrentDomain.UnhandledException += AppDomain_UnhandledException;

            // 3. Task 中未观察的异常：Task 抛出但未被 await / ContinueWith 观察的异常
            //    设置 e.SetObserved() 可阻止应用崩溃
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        /// <summary>UI 线程异常处理</summary>
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Logger.Error("UI 线程发生未处理异常", e.Exception);
            ShowErrorDialog($"出错了：{e.Exception.Message}", "程序遇到了一个问题，但已恢复，你可以继续使用。");
            // 标记已处理，阻止应用崩溃
            e.Handled = true;
        }

        /// <summary>非 UI 线程异常处理</summary>
        private void AppDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            Logger.Error("非 UI 线程发生未处理异常（进程可能即将终止）", ex);
            // 这里不能阻止进程终止，但尽量弹窗提示
            // 如果是 UI 线程触发的，可以直接弹窗；否则用 Dispatcher 切回 UI 线程
            if (ex != null)
            {
                Dispatcher.Invoke(() =>
                    ShowErrorDialog($"严重错误：{ex.Message}", "程序遇到了一个严重问题，可能需要重启。"));
            }
        }

        /// <summary>Task 未观察异常处理</summary>
        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Logger.Error("后台任务发生未观察异常", e.Exception);
            // 标记为已观察，阻止应用崩溃
            e.SetObserved();
            // 在 UI 线程弹窗提示
            Dispatcher.BeginInvoke(new Action(() =>
                ShowErrorDialog($"后台任务出错：{e.Exception.Message}", "一个后台任务出错了，但已处理，你可以继续使用。")));
        }

        /// <summary>
        /// 弹出友好的错误对话框。用 WPF 内置的 MessageBox，简单可靠。
        /// </summary>
        private void ShowErrorDialog(string mainMessage, string detail)
        {
            // 用 MessageBox 简单弹窗。这里不加 try-catch，因为如果弹窗都失败，
            // 说明情况已经很糟，再 try-catch 意义不大。
            MessageBox.Show($"{detail}\n\n{mainMessage}",
                "YCL 启动器", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
