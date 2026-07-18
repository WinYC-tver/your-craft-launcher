using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using YCL.Core.Utils;

namespace YCL.Core.Screenshots
{
    /// <summary>
    /// 截图管理器实现。
    /// 扫描 screenshots 目录下的 .png/.jpg/.jpeg 文件，
    /// 用系统默认程序打开图片，用资源管理器打开文件夹。
    /// </summary>
    public class ScreenshotManager : IScreenshotManager
    {
        /// <summary>支持的图片扩展名（小写）</summary>
        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg"
        };

        /// <summary>
        /// 扫描 gameDir/screenshots/ 下的所有图片文件。
        /// 按截图时间倒序（最新的在前）。
        /// </summary>
        public List<ScreenshotInfo> ListScreenshots(string gameDir)
        {
            var result = new List<ScreenshotInfo>();
            if (string.IsNullOrWhiteSpace(gameDir))
                return result;

            var screenshotsDir = Path.Combine(gameDir, "screenshots");
            if (!Directory.Exists(screenshotsDir))
            {
                Logger.Debug($"截图目录不存在：{screenshotsDir}");
                return result;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(screenshotsDir))
                {
                    try
                    {
                        var ext = Path.GetExtension(file);
                        if (!SupportedExtensions.Contains(ext))
                            continue;

                        var info = new FileInfo(file);
                        result.Add(new ScreenshotInfo
                        {
                            FileName = info.Name,
                            FilePath = file,
                            CaptureTime = info.LastWriteTime,
                            SizeBytes = info.Length
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"读取截图文件信息失败：{file} - {ex.Message}");
                    }
                }

                // 按截图时间倒序（最新的在前）
                result = result.OrderByDescending(s => s.CaptureTime).ToList();
                Logger.Info($"在 {screenshotsDir} 下扫描到 {result.Count} 张截图");
            }
            catch (Exception ex)
            {
                Logger.Error("扫描截图目录失败", ex);
            }

            return result;
        }

        /// <summary>
        /// 用系统默认的图片查看器打开截图。
        /// UseShellExecute = true 让系统用关联程序打开文件。
        /// </summary>
        public void OpenScreenshot(ScreenshotInfo screenshot)
        {
            if (screenshot == null || string.IsNullOrEmpty(screenshot.FilePath))
                return;

            if (!File.Exists(screenshot.FilePath))
            {
                Logger.Warn($"要打开的截图文件不存在：{screenshot.FilePath}");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = screenshot.FilePath,
                    UseShellExecute = true  // 用系统关联程序打开
                });
                Logger.Info($"已打开截图：{screenshot.FileName}");
            }
            catch (Exception ex)
            {
                Logger.Error("打开截图失败", ex);
                throw;
            }
        }

        /// <summary>
        /// 删除截图文件。
        /// </summary>
        public void DeleteScreenshot(ScreenshotInfo screenshot)
        {
            if (screenshot == null || string.IsNullOrEmpty(screenshot.FilePath))
                return;

            if (!File.Exists(screenshot.FilePath))
            {
                Logger.Warn($"要删除的截图文件不存在：{screenshot.FilePath}");
                return;
            }

            File.Delete(screenshot.FilePath);
            Logger.Info($"已删除截图：{screenshot.FileName}");
        }

        /// <summary>
        /// 在资源管理器中打开 gameDir/screenshots/ 文件夹。
        /// 如果文件夹不存在，先尝试创建（空文件夹也打开，方便用户放入文件）。
        /// </summary>
        public void OpenScreenshotsFolder(string gameDir)
        {
            if (string.IsNullOrWhiteSpace(gameDir))
                return;

            var screenshotsDir = Path.Combine(gameDir, "screenshots");
            try
            {
                // 文件夹不存在则创建（避免资源管理器打开失败）
                Directory.CreateDirectory(screenshotsDir);
            }
            catch (Exception ex)
            {
                Logger.Warn($"创建截图目录失败：{screenshotsDir} - {ex.Message}");
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = screenshotsDir,
                    UseShellExecute = true  // 用资源管理器打开文件夹
                });
                Logger.Info($"已打开截图文件夹：{screenshotsDir}");
            }
            catch (Exception ex)
            {
                Logger.Error("打开截图文件夹失败", ex);
                throw;
            }
        }
    }
}
