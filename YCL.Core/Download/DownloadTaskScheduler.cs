using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;

namespace YCL.Core.Download
{
    /// <summary>
    /// 下载任务调度器：维护下载队列，控制并发数，汇总整体进度，
    /// 支持暂停 / 继续 / 取消。
    ///
    /// 设计要点：
    /// - 用 <see cref="SemaphoreSlim"/> 控制并发下载数（来自 AppConfig.DownloadThreads）
    /// - 用 <see cref="ManualResetEventSlim"/> 实现暂停 / 继续
    /// - 用 <see cref="ConcurrentQueue{T}"/> 作为任务队列（assets 可能几万个）
    /// - 每个任务用 <see cref="MultiThreadDownloader"/> 下载（小文件自动回退单线程）
    /// - 下载完成后用 <see cref="FileValidator"/> 做 SHA1 校验
    /// - 校验失败则删除文件并重试（最多 RetryCount 次）
    /// </summary>
    public class DownloadTaskScheduler
    {
        /// <summary>最大并发下载数</summary>
        private readonly int _maxConcurrency;

        /// <summary>每个任务失败重试次数</summary>
        private readonly int _retryCount;

        /// <summary>下载源管理器（URL 转换）</summary>
        private readonly IDownloadSource _downloadSource;

        /// <summary>任务队列</summary>
        private readonly ConcurrentQueue<DownloadTask> _queue = new();

        /// <summary>并发控制信号量</summary>
        private SemaphoreSlim? _semaphore;

        /// <summary>暂停 / 继续控制（signaled = 运行中，non-signaled = 已暂停）</summary>
        private ManualResetEventSlim? _pauseEvent;

        /// <summary>用于线程安全地汇总进度</summary>
        private readonly object _progressLock = new();

        /// <summary>已完成的任务数</summary>
        private int _completedFiles;

        /// <summary>失败的任务数</summary>
        private int _failedFiles;

        /// <summary>总任务数（开始执行时确定）</summary>
        private int _totalFiles;

        /// <summary>总字节数（所有任务 Size 之和）</summary>
        private long _totalBytes;

        /// <summary>已下载字节数</summary>
        private long _downloadedBytes;

        /// <summary>进度上报用：上次上报时间</summary>
        private DateTime _lastReportTime = DateTime.UtcNow;

        /// <summary>进度上报用：上次上报时的已下载字节</summary>
        private long _lastReportBytes;

        /// <summary>当前正在下载的文件名集合（用于 UI 显示）</summary>
        private readonly HashSet<string> _currentFiles = new();

        /// <summary>整体下载进度变化事件</summary>
        public event EventHandler<BatchDownloadProgressEventArgs>? ProgressChanged;

        /// <summary>单个任务完成事件（成功或失败都会触发）</summary>
        public event EventHandler<DownloadTaskCompletedEventArgs>? TaskCompleted;

        /// <summary>
        /// 单个任务的实时下载进度事件。
        /// 由内部下载器（<see cref="MultiThreadDownloader"/>）的 ProgressChanged 转发而来，
        /// 供 UI 显示每个文件独立的进度条与速度。
        /// </summary>
        public event EventHandler<DownloadTaskProgressEventArgs>? TaskProgressChanged;

        /// <summary>是否正在运行</summary>
        public bool IsRunning { get; private set; }

        /// <summary>是否已暂停</summary>
        public bool IsPaused { get; private set; }

        /// <summary>
        /// 构造下载任务调度器。
        /// </summary>
        /// <param name="downloadSource">下载源管理器</param>
        /// <param name="maxConcurrency">最大并发下载数</param>
        /// <param name="retryCount">每个任务失败重试次数</param>
        public DownloadTaskScheduler(IDownloadSource downloadSource, int maxConcurrency = 8, int retryCount = 3)
        {
            _downloadSource = downloadSource ?? throw new ArgumentNullException(nameof(downloadSource));
            _maxConcurrency = Math.Max(1, maxConcurrency);
            _retryCount = Math.Max(0, retryCount);
        }

        /// <summary>加入单个下载任务到队列</summary>
        public void Enqueue(DownloadTask task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            _queue.Enqueue(task);
        }

        /// <summary>批量加入下载任务到队列</summary>
        public void EnqueueRange(IEnumerable<DownloadTask> tasks)
        {
            if (tasks == null) throw new ArgumentNullException(nameof(tasks));
            foreach (var t in tasks)
            {
                if (t != null)
                    _queue.Enqueue(t);
            }
        }

        /// <summary>当前队列中剩余的任务数</summary>
        public int PendingCount => _queue.Count;

        /// <summary>
        /// 开始执行队列中所有下载任务。
        /// 此方法会阻塞（异步）直到所有任务完成或被取消。
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            if (IsRunning)
                throw new InvalidOperationException("调度器已在运行中");

            // 初始化状态
            IsRunning = true;
            IsPaused = false;
            _semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
            _pauseEvent = new ManualResetEventSlim(true); // 初始为 signaled（运行中）

            // 把队列里的任务转成列表，确定总数
            var tasks = new List<DownloadTask>();
            while (_queue.TryDequeue(out var t))
            {
                tasks.Add(t);
            }

            _totalFiles = tasks.Count;
            _completedFiles = 0;
            _failedFiles = 0;
            _downloadedBytes = 0;
            _totalBytes = 0;
            foreach (var t in tasks)
            {
                if (t.Size > 0)
                    _totalBytes += t.Size;
            }
            if (_totalBytes == 0)
                _totalBytes = -1; // 未知

            _lastReportTime = DateTime.UtcNow;
            _lastReportBytes = 0;

            Logger.Info($"下载调度器启动：共 {_totalFiles} 个任务，并发 {_maxConcurrency}，总字节 {_totalBytes}");

            // 触发初始进度
            RaiseProgress();

            // 并发执行所有任务
            var runningTasks = new List<Task>();
            foreach (var task in tasks)
            {
                // 等待信号量（控制并发）
                await _semaphore.WaitAsync(cancellationToken);
                // 等待暂停解除（如果已暂停）
                WaitIfPaused(cancellationToken);

                var runningTask = Task.Run(() => ProcessTaskAsync(task, cancellationToken), cancellationToken);
                runningTasks.Add(runningTask);
            }

            // 等待所有任务完成
            try
            {
                await Task.WhenAll(runningTasks);
            }
            catch (OperationCanceledException)
            {
                Logger.Info("下载调度器被取消");
                throw;
            }

            Logger.Info($"下载调度器完成：成功 {_completedFiles - _failedFiles}，失败 {_failedFiles}，总 {_totalFiles}");

            // 触发最终进度
            RaiseProgress();

            // 释放资源
            _semaphore.Dispose();
            _semaphore = null;
            _pauseEvent.Dispose();
            _pauseEvent = null;
            IsRunning = false;
        }

        /// <summary>暂停下载（正在执行的任务会完成，新任务不开始）</summary>
        public void Pause()
        {
            if (!IsRunning || IsPaused) return;
            IsPaused = true;
            _pauseEvent?.Reset();
            Logger.Info("下载调度器已暂停");
        }

        /// <summary>继续下载</summary>
        public void Resume()
        {
            if (!IsRunning || !IsPaused) return;
            IsPaused = false;
            _pauseEvent?.Set();
            Logger.Info("下载调度器已继续");
        }

        /// <summary>等待暂停解除（同步阻塞，但只在 Task.Run 的线程上调用，不阻塞 UI）</summary>
        private void WaitIfPaused(CancellationToken ct)
        {
            // ManualResetEventSlim 的 Wait 支持 CancellationToken
            _pauseEvent?.Wait(ct);
        }

        /// <summary>
        /// 处理单个下载任务：检查已存在 → 下载 → 校验 → 失败重试。
        /// </summary>
        private async Task ProcessTaskAsync(DownloadTask task, CancellationToken ct)
        {
            // 标记当前正在下载的文件（用于 UI 显示）
            lock (_progressLock)
            {
                if (!string.IsNullOrEmpty(task.DisplayName))
                    _currentFiles.Add(task.DisplayName);
            }

            try
            {
                bool success = false;
                Exception? lastError = null;

                // 重试循环
                for (int attempt = 0; attempt <= _retryCount && !success; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        // 1. 快速预检查：文件已存在且 SHA1 校验通过 → 跳过下载
                        if (File.Exists(task.TargetPath) && !string.IsNullOrEmpty(task.Sha1))
                        {
                            if (await FileValidator.ValidateAsync(task.TargetPath, task.Sha1, ct))
                            {
                                Logger.Debug($"文件已存在且校验通过，跳过：{task.DisplayName}");
                                // 把文件大小计入已下载字节
                                AddDownloadedBytes(task.Size > 0 ? task.Size : 0);
                                success = true;
                                break;
                            }
                            // 校验失败：删除旧文件重新下载
                            try { File.Delete(task.TargetPath); } catch { /* 忽略 */ }
                        }
                        else if (File.Exists(task.TargetPath) && task.Size > 0)
                        {
                            // 没有 SHA1 但大小匹配 → 跳过
                            if (FileValidator.QuickCheck(task.TargetPath, task.Size))
                            {
                                AddDownloadedBytes(task.Size);
                                success = true;
                                break;
                            }
                        }

                        // 2. 执行下载
                        // 每个任务创建独立的下载器实例，避免事件订阅跨任务串扰
                        var downloader = new MultiThreadDownloader(_maxConcurrency, _retryCount);
                        // 订阅下载器进度，转发为任务级进度事件（供 UI 显示每个文件的实时进度）
                        downloader.ProgressChanged += (s, e) => RaiseTaskProgress(task, e);
                        var url = _downloadSource.TransformUrl(task.Url);
                        await downloader.DownloadAsync(url, task.TargetPath, ct);

                        // 3. 校验 SHA1（如果提供）
                        if (!string.IsNullOrEmpty(task.Sha1))
                        {
                            if (!await FileValidator.ValidateAsync(task.TargetPath, task.Sha1, ct))
                            {
                                // 校验失败：删除文件，重试
                                try { File.Delete(task.TargetPath); } catch { /* 忽略 */ }
                                throw new InvalidDataException($"SHA1 校验失败：{task.DisplayName}");
                            }
                        }

                        // 4. 下载并校验成功
                        AddDownloadedBytes(task.Size > 0 ? task.Size : 0);
                        success = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (attempt < _retryCount)
                        {
                            Logger.Warn($"任务失败（第 {attempt + 1} 次，将重试）：{task.DisplayName} - {ex.Message}");
                            await Task.Delay((attempt + 1) * 1000, ct);
                        }
                    }
                }

                // 更新计数
                Interlocked.Increment(ref _completedFiles);
                if (!success)
                {
                    Interlocked.Increment(ref _failedFiles);
                    Logger.Error($"任务最终失败：{task.DisplayName}", lastError);
                }

                // 触发单个任务完成事件
                TaskCompleted?.Invoke(this, new DownloadTaskCompletedEventArgs
                {
                    Task = task,
                    Success = success,
                    Error = lastError
                });

                // 触发整体进度
                RaiseProgress();
            }
            finally
            {
                // 释放信号量，让下一个任务可以开始
                _semaphore?.Release();

                // 从当前文件列表移除
                lock (_progressLock)
                {
                    _currentFiles.Remove(task.DisplayName);
                }
            }
        }

        /// <summary>累加已下载字节数</summary>
        private void AddDownloadedBytes(long bytes)
        {
            if (bytes <= 0) return;
            Interlocked.Add(ref _downloadedBytes, bytes);
        }

        /// <summary>触发单个任务的实时进度事件（转发自内部下载器）</summary>
        private void RaiseTaskProgress(DownloadTask task, DownloadProgressEventArgs e)
        {
            TaskProgressChanged?.Invoke(this, new DownloadTaskProgressEventArgs
            {
                Task = task,
                DownloadedBytes = e.DownloadedBytes,
                TotalBytes = e.TotalBytes,
                BytesPerSecond = e.BytesPerSecond
            });
        }

        /// <summary>触发整体进度事件</summary>
        private void RaiseProgress(double bytesPerSecond = 0)
        {
            var now = DateTime.UtcNow;
            if (bytesPerSecond <= 0)
            {
                var elapsed = (now - _lastReportTime).TotalSeconds;
                var currentBytes = Interlocked.Read(ref _downloadedBytes);
                var diff = currentBytes - _lastReportBytes;
                bytesPerSecond = elapsed > 0 ? diff / elapsed : 0;
                _lastReportBytes = currentBytes;
            }
            _lastReportTime = now;

            string currentFile;
            lock (_progressLock)
            {
                // 取第一个当前文件名作为代表
                foreach (var f in _currentFiles)
                {
                    currentFile = f;
                    break;
                }
                currentFile = _currentFiles.Count > 0 ? string.Join(", ", _currentFiles) : string.Empty;
            }

            ProgressChanged?.Invoke(this, new BatchDownloadProgressEventArgs
            {
                TotalFiles = _totalFiles,
                CompletedFiles = _completedFiles,
                FailedFiles = _failedFiles,
                TotalBytes = _totalBytes,
                DownloadedBytes = Interlocked.Read(ref _downloadedBytes),
                CurrentFileName = currentFile ?? string.Empty,
                BytesPerSecond = bytesPerSecond
            });
        }
    }

    /// <summary>单个下载任务完成事件参数</summary>
    public class DownloadTaskCompletedEventArgs : EventArgs
    {
        /// <summary>完成的任务</summary>
        public DownloadTask Task { get; set; } = new();

        /// <summary>是否成功</summary>
        public bool Success { get; set; }

        /// <summary>失败时的异常（成功时为 null）</summary>
        public Exception? Error { get; set; }
    }

    /// <summary>
    /// 单个下载任务的实时进度事件参数。
    /// 由 <see cref="DownloadTaskScheduler.TaskProgressChanged"/> 事件触发，
    /// 供 UI 显示每个文件独立的进度条与速度。
    /// </summary>
    public class DownloadTaskProgressEventArgs : EventArgs
    {
        /// <summary>正在下载的任务</summary>
        public DownloadTask Task { get; set; } = new();

        /// <summary>已下载的字节数</summary>
        public long DownloadedBytes { get; set; }

        /// <summary>文件总字节数（-1 表示未知）</summary>
        public long TotalBytes { get; set; } = -1;

        /// <summary>当前下载速度（字节/秒）</summary>
        public double BytesPerSecond { get; set; }

        /// <summary>进度百分比（0~100，-1 表示不确定）</summary>
        public double Percent => TotalBytes > 0 ? DownloadedBytes * 100.0 / TotalBytes : -1;
    }
}
