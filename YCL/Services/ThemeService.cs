using System;
using System.Windows;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern;
using YCL.Core.Utils;
using YCL.Models;
// 解决 YCL.Models.ThemeMode 与 System.Windows.ThemeMode 的命名冲突，
// 明确告诉编译器此处用 YCL 自己定义的 ThemeMode 枚举
using ThemeMode = YCL.Models.ThemeMode;
// iNKORE 的 WindowHelper 与 BackdropType 枚举的真实命名空间：
// WindowHelper → iNKORE.UI.WPF.Modern.Controls.Helpers
// BackdropType 枚举 → iNKORE.UI.WPF.Modern.Helpers.Styles
// 用别名避免与 YCL.Models.BackdropType 冲突
using InkoreWindowHelper = iNKORE.UI.WPF.Modern.Controls.Helpers.WindowHelper;
using InkoreBackdropType = iNKORE.UI.WPF.Modern.Helpers.Styles.BackdropType;

namespace YCL.Services
{
    /// <summary>
    /// 主题服务实现：基于 iNKORE.UI.WPF.Modern 的 <see cref="ThemeManager"/> 切换主题。
    ///
    /// iNKORE 主题 API 用法（经反射确认）：
    /// - <see cref="ThemeManager.Current"/> 是全局单例。
    /// - <see cref="ThemeManager.ApplicationTheme"/> 是 Nullable&lt;ApplicationTheme&gt;，
    ///   设为 Light/Dark 强制亮色/暗色；设为 null 则跟随系统主题。
    /// - <see cref="ThemeManager.AccentColor"/> 是 Nullable&lt;Color&gt;，
    ///   设为某个颜色即自定义强调色；设为 null 则使用系统强调色。
    ///
    /// 背景效果 API（iNKORE 0.10.2.1 实际命名空间）：
    /// - 静态方法：<c>InkoreWindowHelper.SetSystemBackdropType(Window, InkoreBackdropType)</c>
    /// - 枚举值：None / Mica / Acrylic / Tabbed / Acrylic10 / Acrylic11
    /// </summary>
    public class ThemeService : IThemeService
    {
        /// <summary>
        /// 壁纸变更事件。当 <see cref="ApplyWallpaper"/> 被调用后触发，
        /// MainWindow 订阅此事件以更新自身壁纸 Image 的绑定属性。
        /// 参数：(path, opacity)。
        /// </summary>
        public static event Action<string?, double>? WallpaperChanged;

        /// <inheritdoc/>
        public void ApplyTheme(ThemeMode mode)
        {
            try
            {
                // 把我们自己的 ThemeMode 枚举映射到 iNKORE 的 ApplicationTheme?
                ApplicationTheme? inkoreTheme = mode switch
                {
                    ThemeMode.Light => ApplicationTheme.Light,
                    ThemeMode.Dark => ApplicationTheme.Dark,
                    // Default=跟随系统，设为 null
                    _ => null
                };

                // 设置后 iNKORE 会立即更新整个应用的资源字典，无需重启
                ThemeManager.Current.ApplicationTheme = inkoreTheme;
                Logger.Info($"已切换主题模式：{mode}");
            }
            catch (Exception ex)
            {
                Logger.Error("切换主题模式失败", ex);
            }
        }

        /// <inheritdoc/>
        public void ApplyAccentColor(Color? color)
        {
            try
            {
                // 设置强调色；传 null 表示跟随系统强调色
                ThemeManager.Current.AccentColor = color;
                if (color.HasValue)
                {
                    Logger.Info($"已切换强调色：{color.Value}");
                }
                else
                {
                    Logger.Info("已切换为系统强调色");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("切换强调色失败", ex);
            }
        }

        /// <inheritdoc/>
        public void ApplyFromConfig(IConfigService configService)
        {
            var config = configService.Current;
            ApplyTheme(config.ThemeMode);

            // 把配置里存的十六进制字符串转成 Color；为空则用系统色
            ApplyAccentColor(ParseColor(config.AccentColorHex));

            // 应用背景效果与自定义壁纸（v26 个性化）
            ApplyBackdrop(config.Backdrop);
            ApplyWallpaper(config.WallpaperPath, config.WallpaperOpacity);
        }

        /// <inheritdoc/>
        public void ApplyBackdrop(BackdropType backdrop)
        {
            try
            {
                // 映射 YCL.Models.BackdropType → iNKORE.UI.WPF.Modern.Helpers.Styles.BackdropType
                // Default→None（不应用特殊效果）, Acrylic→Acrylic, Mica→Mica, MicaAlt→Tabbed
                InkoreBackdropType targetValue = backdrop switch
                {
                    BackdropType.Acrylic => InkoreBackdropType.Acrylic,
                    BackdropType.Mica => InkoreBackdropType.Mica,
                    BackdropType.MicaAlt => InkoreBackdropType.Tabbed,
                    _ => InkoreBackdropType.None
                };

                // 遍历当前应用所有窗口逐个应用背景效果
                foreach (Window window in Application.Current.Windows)
                {
                    try
                    {
                        InkoreWindowHelper.SetSystemBackdropType(window, targetValue);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"为窗口 {window?.GetType().Name} 设置背景效果失败：{ex.Message}");
                    }
                }

                Logger.Info($"已应用背景效果：{backdrop}（映射到 {targetValue}）");
            }
            catch (Exception ex)
            {
                Logger.Error("应用背景效果失败", ex);
            }
        }

        /// <inheritdoc/>
        public void ApplyWallpaper(string? path, double opacity)
        {
            try
            {
                // 限制不透明度在 0~1 之间
                if (opacity < 0) opacity = 0;
                if (opacity > 1) opacity = 1;

                var resources = Application.Current.Resources;

                // 把壁纸路径与不透明度存入应用资源字典
                // MainWindow 中的壁纸 Image 通过 DynamicResource 绑定这两个键
                string? effectivePath = null;
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                {
                    effectivePath = path;
                    Logger.Info($"已设置自定义壁纸：{path}（不透明度 {opacity}）");
                }
                else
                {
                    Logger.Info("已清除自定义壁纸");
                }
                resources["AppWallpaperPath"] = effectivePath;
                resources["AppWallpaperOpacity"] = opacity;

                // 通知所有订阅者（如 MainWindow）壁纸已变更
                WallpaperChanged?.Invoke(effectivePath, opacity);
            }
            catch (Exception ex)
            {
                Logger.Error("应用自定义壁纸失败", ex);
            }
        }

        /// <summary>
        /// 把十六进制颜色字符串（如 "#FF0078D7" 或 "#0078D7"）转换成 Color 对象。
        /// 解析失败或传入空值时返回 null（表示用系统强调色）。
        /// </summary>
        public static Color? ParseColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return null;

            try
            {
                // ColorConverter 是 WPF 内置工具，支持 #RGB / #ARGB / #RRGGBB / #AARRGGBB
                var converted = ColorConverter.ConvertFromString(hex);
                if (converted is Color c)
                    return c;
            }
            catch (Exception ex)
            {
                Logger.Warn($"无法解析颜色字符串：{hex}，将使用系统强调色。原因：{ex.Message}");
            }

            return null;
        }
    }
}
