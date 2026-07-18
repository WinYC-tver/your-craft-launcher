using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Accounts;
using YCL.Core.Download;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Models.Accounts;
using YCL.Models.Versions;

namespace YCL.Core.Launch
{
    /// <summary>
    /// 游戏启动器实现。
    /// 流程：
    /// 1. 调用 IVersionResolver 解析版本（含 inheritsFrom 递归 + 库路径解析）
    /// 2. 调用 ILaunchArgumentBuilder 生成启动参数
    /// 3. 校验必要文件的 SHA1 完整性，缺失/损坏文件自动下载（阶段 3 新增）
    /// 4. 解压 natives 文件到临时目录
    /// 5. 启动 java 进程，重定向 stdout/stderr
    /// 6. 异步读取日志，触发事件通知 UI
    /// 7. 监听进程退出，触发事件
    /// </summary>
    public class GameLauncher : IGameLauncher
    {
        private readonly IVersionResolver _versionResolver;
        private readonly ILaunchArgumentBuilder _argumentBuilder;
        private readonly IMinecraftFileDownloader? _fileDownloader;

        // 当前游戏进程（启动后赋值，退出后置 null）
        private Process? _process;
        // 用于取消日志读取任务的令牌
        private CancellationTokenSource? _logCts;

        /// <summary>当前启动状态</summary>
        public LaunchState State { get; private set; } = LaunchState.Idle;

        /// <inheritdoc/>
        /// <remarks>默认 false（不隔离）。调用方在启动前从配置读取并设置。</remarks>
        public bool EnableVersionIsolation { get; set; }

        /// <inheritdoc/>
        /// <remarks>默认 0（用游戏默认窗口大小）。调用方在启动前从配置读取并设置。</remarks>
        public int WindowWidth { get; set; }

        /// <inheritdoc/>
        public int WindowHeight { get; set; }

        /// <inheritdoc/>
        public bool FullscreenOnLaunch { get; set; }

        /// <inheritdoc/>
        public string ExtraJvmArgs { get; set; } = string.Empty;

        /// <inheritdoc/>
        public bool CleanBeforeLaunch { get; set; }

        /// <inheritdoc/>
        public event EventHandler<LaunchProgressEventArgs>? ProgressChanged;

        /// <inheritdoc/>
        public event EventHandler<GameLogEventArgs>? LogReceived;

        /// <inheritdoc/>
        public event EventHandler<GameExitedEventArgs>? Exited;

        /// <inheritdoc/>
        public event EventHandler<LaunchState>? StateChanged;

        /// <summary>
        /// 构造游戏启动器。
        /// </summary>
        /// <param name="versionResolver">版本解析服务</param>
        /// <param name="argumentBuilder">启动参数生成器</param>
        /// <param name="fileDownloader">Minecraft 文件下载器（可选，用于启动前自动下载缺失文件）</param>
        public GameLauncher(
            IVersionResolver versionResolver,
            ILaunchArgumentBuilder argumentBuilder,
            IMinecraftFileDownloader? fileDownloader = null)
        {
            _versionResolver = versionResolver;
            _argumentBuilder = argumentBuilder;
            _fileDownloader = fileDownloader;
        }

        /// <inheritdoc/>
        public async Task<bool> LaunchAsync(
            string minecraftPath,
            string versionId,
            AccountBase account,
            string javaPath,
            int maxMemoryMb,
            int minMemoryMb)
        {
            // 防止重复启动
            if (State == LaunchState.Running || State == LaunchState.Preparing
                || State == LaunchState.ExtractingNatives || State == LaunchState.Starting)
            {
                Logger.Warn("已有游戏正在启动或运行中，忽略重复启动请求");
                return false;
            }

            try
            {
                // 阶段 1：解析版本
                SetState(LaunchState.Preparing);
                RaiseProgress("解析版本", 10, $"正在解析版本 {versionId}...");
                Logger.Info($"开始启动游戏：版本={versionId}, 玩家={account.Username}, " +
                            $"账户类型={account.Type}, java={javaPath}, 最大内存={maxMemoryMb}MB");

                // 检查 java 路径
                if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
                {
                    throw new FileNotFoundException($"找不到 Java 运行时：{javaPath}。请在设置中配置 JavaPath。");
                }

                // 检查 .minecraft 目录
                if (string.IsNullOrEmpty(minecraftPath) || !Directory.Exists(minecraftPath))
                {
                    throw new DirectoryNotFoundException($"找不到 .minecraft 目录：{minecraftPath}");
                }

                // 启动前检查令牌是否过期，过期则自动刷新
                await EnsureTokenValidAsync(account);

                // 解析版本（含 inheritsFrom 递归合并 + 库路径解析）
                var resolved = await Task.Run(() => _versionResolver.Resolve(minecraftPath, versionId));
                RaiseProgress("解析版本", 30, "版本解析完成，正在生成启动参数...");

                // 阶段 2：生成启动参数（传入 EnableVersionIsolation 决定 gameDir）
                // 对外置登录账户，生成 authlib-injector 注入参数
                List<string>? extraJvmArgs = await BuildExtraJvmArgsAsync(account);

                var launchArgs = _argumentBuilder.Build(
                    resolved, account, minecraftPath, javaPath, maxMemoryMb, minMemoryMb,
                    EnableVersionIsolation, extraJvmArgs,
                    WindowWidth, WindowHeight, FullscreenOnLaunch,
                    string.IsNullOrEmpty(ExtraJvmArgs) ? null : ExtraJvmArgs);

                // 版本隔离：启用时确保隔离目录的 mods/saves 等子目录存在
                if (EnableVersionIsolation)
                {
                    EnsureIsolationDirectories(launchArgs.WorkingDirectory);
                }

                // 启动前清理：用户在设置页开启了 CleanBeforeLaunch 时，
                // 清理版本目录下的旧 natives-* 临时目录（释放磁盘空间）
                if (CleanBeforeLaunch)
                {
                    CleanOldNatives(resolved.VersionDirectory);
                }

                // 阶段 3：校验文件完整性，缺失/损坏文件自动下载
                RaiseProgress("检查文件", 40, "正在校验游戏文件完整性...");
                await EnsureRequiredFilesAsync(resolved, minecraftPath);
                // 下载完成后再做一次存在性检查（确保关键文件就绪）
                CheckRequiredFiles(resolved, javaPath);

                // 阶段 4：解压 natives
                SetState(LaunchState.ExtractingNatives);
                RaiseProgress("解压 natives", 60, "正在解压本地库文件...");
                ExtractNatives(resolved, launchArgs.NativesDirectory);

                // 阶段 5：启动进程
                SetState(LaunchState.Starting);
                RaiseProgress("启动游戏", 80, "正在启动 Java 进程...");

                StartProcess(launchArgs);

                SetState(LaunchState.Running);
                RaiseProgress("启动完成", 100, "游戏已启动，等待运行...");
                Logger.Info("游戏进程已启动，PID = " + (_process?.Id.ToString() ?? "unknown"));

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("启动游戏失败", ex);
                SetState(LaunchState.Failed);
                RaiseProgress("启动失败", -1, "启动失败：" + ex.Message);
                // 把错误也作为日志推送给 UI
                LogReceived?.Invoke(this, new GameLogEventArgs
                {
                    Line = "[YCL 启动器] 启动失败：" + ex.Message,
                    IsError = true
                });
                return false;
            }
        }

        /// <inheritdoc/>
        public void Stop()
        {
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    Logger.Info("用户请求关闭游戏进程，PID = " + _process.Id);
                    _process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Logger.Error("关闭游戏进程失败", ex);
                }
            }
        }

        /// <summary>更新状态并触发 StateChanged 事件</summary>
        private void SetState(LaunchState newState)
        {
            if (State == newState) return;
            Logger.Debug($"启动状态变化：{State} → {newState}");
            State = newState;
            StateChanged?.Invoke(this, newState);
        }

        /// <summary>
        /// 启动前检查令牌是否过期，过期则自动刷新。
        /// - 微软账户：令牌即将过期时调用刷新流程
        /// - 外置账户：可选刷新（这里不做主动刷新，由用户手动触发）
        /// - 离线账户：无需刷新
        /// </summary>
        private async Task EnsureTokenValidAsync(AccountBase account)
        {
            try
            {
                if (account is MicrosoftAccount msAccount && msAccount.IsTokenExpired())
                {
                    RaiseProgress("刷新令牌", 15, "账户令牌已过期，正在自动刷新...");
                    Logger.Info($"微软账户令牌已过期，自动刷新：{msAccount.Username}");
                    var ok = await msAccount.RefreshAsync();
                    if (!ok)
                    {
                        Logger.Warn("微软账户令牌刷新失败，将尝试用旧令牌启动");
                    }
                }
            }
            catch (Exception ex)
            {
                // 刷新失败不中断启动，尝试用旧令牌
                Logger.Warn("令牌刷新异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 为外置登录账户生成 authlib-injector 注入参数。
        /// 非外置账户返回 null（不加额外参数）。
        /// </summary>
        private async Task<List<string>?> BuildExtraJvmArgsAsync(AccountBase account)
        {
            if (account is not YggdrasilAccount yggAccount)
                return null;

            try
            {
                RaiseProgress("准备 authlib-injector", 25, "正在准备外置登录注入...");
                // 确保 authlib-injector.jar 已下载
                var jarPath = await AuthlibInjectorHelper.EnsureJarAsync();
                // 生成注入参数（含服务器元数据 Base64）
                return await AuthlibInjectorHelper.GetLaunchArgumentsAsync(yggAccount, jarPath);
            }
            catch (Exception ex)
            {
                Logger.Error("准备 authlib-injector 注入参数失败", ex);
                throw new InvalidOperationException(
                    "外置登录注入准备失败：" + ex.Message + "\n请检查网络连接后重试。", ex);
            }
        }

        /// <summary>触发进度事件</summary>
        private void RaiseProgress(string stage, int percent, string message)
        {
            Logger.Info($"[启动进度] {stage} ({percent}%): {message}");
            ProgressChanged?.Invoke(this, new LaunchProgressEventArgs
            {
                Stage = stage,
                Percent = percent,
                Message = message
            });
        }

        /// <summary>
        /// 校验必要文件的完整性（SHA1），缺失或损坏的文件自动下载。
        /// 阶段 3 新增：用 <see cref="IMinecraftFileDownloader"/> 下载缺失文件。
        ///
        /// 流程：
        /// 1. 用 BuildDownloadTasks 构造所有需要的文件清单
        /// 2. 对启动必需的文件（client.jar、libraries、natives、logging）做 SHA1 校验
        /// 3. 如果有缺失/损坏文件，调用 DownloadVersionAsync 下载
        /// 4. 下载进度通过 ProgressChanged 事件通知 UI
        ///
        /// 注意：assets 不做启动前校验（缺失不影响启动，只影响游戏内贴图/音效）。
        /// 如果 _fileDownloader 为 null（未注入），只记录警告并返回，由 CheckRequiredFiles 兜底。
        /// </summary>
        private async Task EnsureRequiredFilesAsync(ResolvedVersion resolved, string minecraftPath)
        {
            // 没有注入下载器，无法自动下载，直接返回（由后续的 CheckRequiredFiles 抛异常）
            if (_fileDownloader == null)
            {
                Logger.Warn("未注入 IMinecraftFileDownloader，跳过自动下载缺失文件");
                return;
            }

            Logger.Info("开始校验文件完整性...");
            var tasks = _fileDownloader.BuildDownloadTasks(resolved.Info, minecraftPath);
            if (tasks.Count == 0)
            {
                Logger.Info("无需校验的文件（BuildDownloadTasks 返回空）");
                return;
            }

            // 检查启动必需的文件（排除 assets / assetIndex）是否存在且 SHA1 校验通过
            var missingCount = 0;
            foreach (var task in tasks)
            {
                // assets 不做启动前校验（数量多、不影响启动）
                if (task.Category == "assets" || task.Category == "assetIndex")
                    continue;

                bool ok = File.Exists(task.TargetPath);
                if (ok && !string.IsNullOrEmpty(task.Sha1))
                {
                    // 文件存在，做 SHA1 校验
                    ok = await FileValidator.ValidateAsync(task.TargetPath, task.Sha1);
                }

                if (!ok)
                {
                    missingCount++;
                    Logger.Debug($"文件缺失或损坏：{task.DisplayName}（{task.Category}）");
                }
            }

            if (missingCount == 0)
            {
                Logger.Info($"所有启动必需文件校验通过（共检查 {tasks.Count} 个文件）");
                return;
            }

            // 有缺失/损坏文件，开始下载
            Logger.Info($"发现 {missingCount} 个缺失/损坏文件，开始自动下载...");
            RaiseProgress("下载文件", 42, $"发现 {missingCount} 个文件缺失/损坏，正在下载...");

            // 临时订阅下载进度，转发到启动进度事件
            _fileDownloader.ProgressChanged += OnDownloadProgressForLaunch;
            try
            {
                // DownloadVersionAsync 会下载所有缺失文件（已存在且校验通过的会跳过）
                // 注意：assets objects 可能不会被完整下载（需要 assetIndex 先下载），
                //       但启动必需的 client.jar / libraries / natives 会被下载
                var result = await _fileDownloader.DownloadVersionAsync(resolved.Info, minecraftPath);

                if (result.IsCanceled)
                {
                    Logger.Warn("文件下载被取消");
                }
                else if (result.FailedFiles > 0)
                {
                    Logger.Warn($"文件下载部分失败：成功 {result.SuccessFiles}，失败 {result.FailedFiles}");
                }
                else
                {
                    Logger.Info($"文件下载完成：成功 {result.SuccessFiles} 个文件");
                }

                RaiseProgress("下载文件", 48, $"下载完成（成功 {result.SuccessFiles}，失败 {result.FailedFiles}）");
            }
            finally
            {
                _fileDownloader.ProgressChanged -= OnDownloadProgressForLaunch;
            }
        }

        /// <summary>
        /// 下载进度转发：把 IMinecraftFileDownloader 的整体进度转为启动进度事件。
        /// 用于在启动前下载缺失文件时通知 UI。
        /// </summary>
        private void OnDownloadProgressForLaunch(object? sender, BatchDownloadProgressEventArgs e)
        {
            // 进度映射到 42~48 之间（启动进度的小段）
            var percent = 42 + (e.Percent < 0 ? 0 : e.Percent * 0.06);
            var msg = $"下载中：{e.CompletedFiles}/{e.TotalFiles} 文件" +
                      (e.FailedFiles > 0 ? $"（失败 {e.FailedFiles}）" : "") +
                      $"，速度 {FormatBytesPerSecond(e.BytesPerSecond)}";
            RaiseProgress("下载文件", (int)percent, msg);
        }

        /// <summary>把字节/秒格式化为人类可读字符串</summary>
        private static string FormatBytesPerSecond(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "-";
            if (bytesPerSecond < 1024) return bytesPerSecond.ToString("F0") + " B/s";
            if (bytesPerSecond < 1024 * 1024) return (bytesPerSecond / 1024).ToString("F1") + " KB/s";
            if (bytesPerSecond < 1024L * 1024 * 1024) return (bytesPerSecond / (1024 * 1024)).ToString("F1") + " MB/s";
            return (bytesPerSecond / (1024.0 * 1024 * 1024)).ToString("F2") + " GB/s";
        }

        /// <summary>
        /// 检查关键文件是否存在（启动前最后兜底检查）：
        /// - java 可执行文件
        /// - 客户端 jar
        /// - classpath 中的所有库 jar
        /// - natives zip 文件
        /// 注意：SHA1 完整性校验已在 <see cref="EnsureRequiredFilesAsync"/> 中完成，
        ///       这里只做存在性检查，确保启动所需文件就绪。
        /// </summary>
        private void CheckRequiredFiles(ResolvedVersion resolved, string javaPath)
        {
            // 客户端 jar
            if (!File.Exists(resolved.ClientJarPath))
            {
                throw new FileNotFoundException(
                    $"找不到客户端 jar：{resolved.ClientJarPath}。可能需要先下载此版本。");
            }

            // classpath 中的库文件
            var missingLibs = new List<string>();
            foreach (var libPath in resolved.ClasspathFiles)
            {
                if (!File.Exists(libPath))
                    missingLibs.Add(libPath);
            }

            if (missingLibs.Count > 0)
            {
                Logger.Warn($"有 {missingLibs.Count} 个 classpath 库文件缺失：");
                foreach (var p in missingLibs.Take(5))
                    Logger.Warn("  缺失：" + p);
                if (missingLibs.Count > 5)
                    Logger.Warn($"  ... 等 {missingLibs.Count} 个");
                throw new FileNotFoundException(
                    $"有 {missingLibs.Count} 个依赖库文件缺失（第一个：{missingLibs[0]}）。" +
                    "可能需要先下载此版本。");
            }

            // natives zip 文件
            var missingNatives = new List<string>();
            foreach (var nativePath in resolved.NativeFiles)
            {
                if (!File.Exists(nativePath))
                    missingNatives.Add(nativePath);
            }

            if (missingNatives.Count > 0)
            {
                Logger.Warn($"有 {missingNatives.Count} 个 natives 文件缺失：");
                foreach (var p in missingNatives.Take(5))
                    Logger.Warn("  缺失：" + p);
                throw new FileNotFoundException(
                    $"有 {missingNatives.Count} 个本地库文件缺失（第一个：{missingNatives[0]}）。" +
                    "可能需要先下载此版本。");
            }
        }

        /// <summary>
        /// 解压 natives 文件到指定目录。
        /// 遍历所有 natives zip 文件，逐个解压到 nativesDir，
        /// 跳过 exclude 列表中的路径（如 META-INF/）。
        /// </summary>
        private void ExtractNatives(ResolvedVersion resolved, string nativesDir)
        {
            // 创建 natives 目录（已存在则先清空，避免旧文件干扰）
            if (Directory.Exists(nativesDir))
            {
                try
                {
                    Directory.Delete(nativesDir, recursive: true);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"清空旧的 natives 目录失败：{nativesDir}（{ex.Message}）");
                }
            }
            Directory.CreateDirectory(nativesDir);

            if (resolved.NativeFiles.Count == 0)
            {
                Logger.Warn("没有需要解压的 natives 文件（可能是纯 Java 版本，或 natives 缺失）");
                return;
            }

            // 收集所有库的 exclude 列表（全局有效：任一库声明 exclude 都跳过）
            // 实际上 exclude 是按库生效的，但简化处理：统一收集所有 exclude 模式
            var globalExcludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (resolved.Info?.Libraries != null)
            {
                foreach (var lib in resolved.Info.Libraries)
                {
                    if (lib.Extract?.Exclude != null)
                    {
                        foreach (var ex in lib.Extract.Exclude)
                            globalExcludes.Add(ex);
                    }
                }
            }
            Logger.Debug($"natives 解压排除列表：{string.Join(", ", globalExcludes)}");

            // 逐个解压 natives zip
            for (int i = 0; i < resolved.NativeFiles.Count; i++)
            {
                var zipPath = resolved.NativeFiles[i];
                if (!File.Exists(zipPath))
                {
                    Logger.Warn($"natives zip 不存在，跳过：{zipPath}");
                    continue;
                }

                Logger.Debug($"解压 natives ({i + 1}/{resolved.NativeFiles.Count})：{zipPath}");
                try
                {
                    using var archive = ZipFile.OpenRead(zipPath);
                    foreach (var entry in archive.Entries)
                    {
                        // 检查是否在 exclude 列表中
                        if (ShouldExclude(entry.FullName, globalExcludes))
                            continue;

                        // 防止 zip slip 攻击：确保解压路径在目标目录内
                        var targetPath = Path.GetFullPath(Path.Combine(nativesDir, entry.FullName));
                        var nativesDirFull = Path.GetFullPath(nativesDir);
                        if (!targetPath.StartsWith(nativesDirFull, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Warn($"跳过可疑的 zip 条目：{entry.FullName}");
                            continue;
                        }

                        // 创建目录（条目是目录或子目录中的文件）
                        var targetDir = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(targetDir))
                            Directory.CreateDirectory(targetDir);

                        // 条目是目录（以 / 结尾或长度为 0）则跳过写入
                        if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                            continue;
                        if (entry.Length == 0 && string.IsNullOrEmpty(Path.GetFileName(entry.FullName)))
                            continue;

                        entry.ExtractToFile(targetPath, overwrite: true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"解压 natives 失败：{zipPath}", ex);
                    throw;
                }
            }

            Logger.Info($"natives 解压完成，目录：{nativesDir}");
        }

        /// <summary>判断 zip 条目是否应该被排除（按前缀匹配）</summary>
        private bool ShouldExclude(string entryName, HashSet<string> excludes)
        {
            foreach (var ex in excludes)
            {
                if (string.IsNullOrEmpty(ex)) continue;
                // 支持 "META-INF/" 这样的前缀匹配，也支持 "META-INF/MANIFEST.MF" 这样的精确前缀
                if (entryName.StartsWith(ex, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 启动 java 进程。
        /// 设置工作目录、重定向 stdout/stderr、CreateNoWindow 等。
        /// </summary>
        private void StartProcess(LaunchArguments launchArgs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = launchArgs.JavaPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = launchArgs.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 防止子进程继承控制台（Windows 专用）
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            // JVM 参数
            foreach (var arg in launchArgs.JvmArguments)
                psi.ArgumentList.Add(arg);

            // 主类
            psi.ArgumentList.Add(launchArgs.MainClass);

            // 游戏参数
            foreach (var arg in launchArgs.GameArguments)
                psi.ArgumentList.Add(arg);

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            // 订阅退出事件
            _process.Exited += OnProcessExited;

            if (!_process.Start())
            {
                throw new InvalidOperationException("Java 进程启动失败（Process.Start 返回 false）");
            }

            // 异步读取日志（避免 stdout/stderr 缓冲区满导致进程阻塞）
            _logCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadStreamAsync(_process.StandardOutput, false, _logCts.Token));
            _ = Task.Run(() => ReadStreamAsync(_process.StandardError, true, _logCts.Token));
        }

        /// <summary>异步读取进程输出流，每行触发 LogReceived 事件</summary>
        private async Task ReadStreamAsync(StreamReader reader, bool isError, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // ReadLineAsync 在流结束时返回 null，循环退出
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;

                    Logger.Debug($"[游戏{(isError ? "错误" : "输出")}] {line}");
                    LogReceived?.Invoke(this, new GameLogEventArgs
                    {
                        Line = line,
                        IsError = isError
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，忽略
            }
            catch (Exception ex)
            {
                Logger.Error("读取游戏进程输出流失败", ex);
            }
        }

        /// <summary>进程退出事件处理</summary>
        private void OnProcessExited(object? sender, EventArgs e)
        {
            try
            {
                var exitCode = _process?.ExitCode ?? -1;
                Logger.Info($"游戏进程已退出，退出码 = {exitCode}");

                // 取消日志读取任务
                _logCts?.Cancel();
                _logCts?.Dispose();
                _logCts = null;

                SetState(LaunchState.Exited);
                Exited?.Invoke(this, new GameExitedEventArgs { ExitCode = exitCode });
            }
            catch (Exception ex)
            {
                Logger.Error("处理进程退出事件时出错", ex);
            }
            finally
            {
                _process = null;
            }
        }

        /// <summary>
        /// 确保版本隔离目录及其常用子目录存在。
        /// 启用版本隔离时，游戏目录是 .minecraft/versions/&lt;id&gt;/，
        /// 启动前需要创建 mods/saves/configs 等子目录，否则游戏首次启动可能报错。
        /// </summary>
        private void EnsureIsolationDirectories(string gameDirectory)
        {
            try
            {
                Directory.CreateDirectory(gameDirectory);

                // 创建常用的游戏子目录（不存在则创建，存在则无害）
                string[] subDirs = { "mods", "saves", "configs", "resourcepacks", "shaderpacks" };
                foreach (var sub in subDirs)
                {
                    Directory.CreateDirectory(Path.Combine(gameDirectory, sub));
                }

                Logger.Info($"已确保版本隔离目录存在：{gameDirectory}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"创建版本隔离子目录失败：{gameDirectory} - {ex.Message}");
            }
        }

        /// <summary>
        /// 清理版本目录下的旧 natives-* 临时目录。
        /// 每次启动会生成新的 natives-<时间戳> 目录，旧的需要手动清理。
        /// 此方法在 CleanBeforeLaunch 为 true 时调用，删除所有匹配 natives-* 的子目录。
        /// </summary>
        private void CleanOldNatives(string versionDirectory)
        {
            try
            {
                if (!Directory.Exists(versionDirectory))
                    return;

                var dirInfo = new DirectoryInfo(versionDirectory);
                // 查找所有 natives-* 子目录
                var nativesDirs = dirInfo.GetDirectories("natives-*", SearchOption.TopDirectoryOnly);
                if (nativesDirs.Length == 0)
                {
                    Logger.Debug("启动前清理：没有旧 natives 目录需要清理");
                    return;
                }

                foreach (var dir in nativesDirs)
                {
                    try
                    {
                        dir.Delete(recursive: true);
                        Logger.Debug($"启动前清理：已删除旧 natives 目录 {dir.Name}");
                    }
                    catch (Exception ex)
                    {
                        // 单个目录删除失败不影响启动
                        Logger.Warn($"启动前清理：删除 {dir.Name} 失败 - {ex.Message}");
                    }
                }

                Logger.Info($"启动前清理：共清理 {nativesDirs.Length} 个旧 natives 目录");
            }
            catch (Exception ex)
            {
                Logger.Warn("启动前清理失败：" + ex.Message);
            }
        }
    }
}
