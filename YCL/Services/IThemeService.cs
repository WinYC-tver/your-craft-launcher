using System.Windows.Media;
using YCL.Models;

namespace YCL.Services
{
    /// <summary>
    /// 主题服务接口：负责切换应用的亮色/暗色/跟随系统主题，以及自定义强调色。
    /// 通过这个接口，ViewModel 不需要直接依赖 iNKORE 库，方便测试和解耦。
    /// </summary>
    public interface IThemeService
    {
        /// <summary>应用指定主题模式</summary>
        void ApplyTheme(ThemeMode mode);

        /// <summary>应用自定义强调色（传 null 表示使用系统强调色）</summary>
        void ApplyAccentColor(Color? color);

        /// <summary>从配置对象中读取主题设置并应用（启动时调用）</summary>
        void ApplyFromConfig(IConfigService configService);
    }
}
