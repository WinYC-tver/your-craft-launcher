using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YCL.Core.Utils;

namespace YCL.Services
{
    /// <summary>
    /// 皮肤显示服务：下载玩家皮肤、缓存到本地、截取头部头像供 UI 显示。
    ///
    /// 皮肤缓存位置：%AppData%\YCL\cache\skins\&lt;uuid&gt;.png
    /// 头像截取：Minecraft 皮肤贴图是 64×64（或旧版 64×32），
    /// 头部正面位于 (8, 8) 起 8×8 像素区域，截取后放大显示。
    ///
    /// 皮肤下载失败不抛异常，返回 null，UI 显示默认占位头像。
    /// </summary>
    public class SkinService
    {
        /// <summary>皮肤缓存目录：%AppData%\YCL\cache\skins\</summary>
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YCL", "cache", "skins");

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        /// <summary>
        /// 获取玩家头像。自动下载皮肤、截取头部、放大到指定尺寸。
        /// </summary>
        /// <param name="uuid">玩家 UUID（用作缓存文件名）</param>
        /// <param name="skinUrl">皮肤 URL（为空或下载失败返回 null）</param>
        /// <param name="size">头像边长（像素，默认 64）</param>
        /// <returns>头像图像（已冻结可跨线程使用），失败返回 null</returns>
        public async Task<ImageSource?> GetAvatarAsync(string uuid, string? skinUrl, int size = 64)
        {
            if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(skinUrl))
                return null;

            try
            {
                var skinPath = await EnsureSkinDownloadedAsync(uuid, skinUrl);
                if (skinPath == null)
                    return null;

                return CropHead(skinPath, size);
            }
            catch (Exception ex)
            {
                Logger.Warn($"获取皮肤头像失败：{uuid} - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 同步获取头像（从缓存读取，不联网下载）。用于已下载过的情况。
        /// </summary>
        public ImageSource? GetAvatarFromCache(string uuid, int size = 64)
        {
            if (string.IsNullOrEmpty(uuid))
                return null;

            var skinPath = Path.Combine(CacheDir, uuid + ".png");
            if (!File.Exists(skinPath))
                return null;

            try
            {
                return CropHead(skinPath, size);
            }
            catch (Exception ex)
            {
                Logger.Warn($"从缓存读取头像失败：{uuid} - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 确保皮肤已下载到缓存。如果缓存存在则直接返回路径。
        /// </summary>
        private async Task<string?> EnsureSkinDownloadedAsync(string uuid, string skinUrl)
        {
            Directory.CreateDirectory(CacheDir);
            var skinPath = Path.Combine(CacheDir, uuid + ".png");

            // 已有缓存则直接用
            if (File.Exists(skinPath))
                return skinPath;

            // 下载皮肤
            try
            {
                using var resp = await Http.GetStreamAsync(skinUrl);
                await using var fs = new FileStream(skinPath, FileMode.Create, FileAccess.Write);
                await resp.CopyToAsync(fs);
                Logger.Info($"皮肤已下载：{uuid}");
                return skinPath;
            }
            catch (Exception ex)
            {
                Logger.Warn($"下载皮肤失败：{uuid} - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从皮肤贴图截取头部正面并放大。
        /// 头部正面区域：起点 (8, 8)，大小 8×8。
        /// </summary>
        private static ImageSource CropHead(string skinPath, int size)
        {
            var fullSkin = new BitmapImage();
            fullSkin.BeginInit();
            fullSkin.UriSource = new Uri(skinPath, UriKind.Absolute);
            fullSkin.CacheOption = BitmapCacheOption.OnLoad;
            fullSkin.EndInit();
            fullSkin.Freeze();

            // 截取头部正面：8×8
            var crop = new CroppedBitmap(fullSkin, new System.Windows.Int32Rect(8, 8, 8, 8));
            crop.Freeze();

            // 放大到指定尺寸
            var scale = Math.Max(1.0, size / 8.0);
            var scaled = new TransformedBitmap(crop, new ScaleTransform(scale, scale));
            scaled.Freeze();

            return scaled;
        }
    }
}
