using System;
using System.Collections.Generic;

namespace YCL.Core.Screenshots
{
    /// <summary>
    /// 截图管理器接口。负责扫描、打开、删除截图，以及打开截图所在文件夹。
    /// 缩略图生成由 UI 层负责（用 WPF 的 BitmapImage 解码），此接口只管文件操作。
    /// </summary>
    public interface IScreenshotManager
    {
        /// <summary>
        /// 扫描 gameDir/screenshots/ 下的所有图片文件（.png/.jpg/.jpeg）。
        /// 按截图时间倒序（最新的在前）。
        /// </summary>
        /// <param name="gameDir">游戏目录（.minecraft 或版本隔离目录）</param>
        /// <returns>截图列表，目录不存在时返回空列表</returns>
        List<ScreenshotInfo> ListScreenshots(string gameDir);

        /// <summary>
        /// 用系统默认的图片查看器打开截图。
        /// </summary>
        /// <param name="screenshot">要打开的截图</param>
        void OpenScreenshot(ScreenshotInfo screenshot);

        /// <summary>
        /// 删除截图文件。
        /// 注意：删除确认由 UI 层处理，此方法直接删除。
        /// </summary>
        /// <param name="screenshot">要删除的截图</param>
        void DeleteScreenshot(ScreenshotInfo screenshot);

        /// <summary>
        /// 在资源管理器中打开 gameDir/screenshots/ 文件夹。
        /// </summary>
        /// <param name="gameDir">游戏目录</param>
        void OpenScreenshotsFolder(string gameDir);
    }
}
