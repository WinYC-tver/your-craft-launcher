using System;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern;
using YCL.Core.Utils;
using YCL.Models;

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
    /// </summary>
    public class ThemeService : IThemeService
    {
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
