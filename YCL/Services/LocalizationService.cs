using System;
using System.Windows;
using YCL.Core.Utils;
using YCL.Models;
using YCL.Services;

namespace YCL.Services
{
    /// <summary>
    /// 本地化服务：管理界面语言切换。
    /// 通过在 Application.Resources.MergedDictionaries 中替换语言 ResourceDictionary 实现运行时切换。
    /// </summary>
    public class LocalizationService
    {
        /// <summary>当前语言</summary>
        public Language CurrentLanguage { get; private set; } = Language.zhCN;

        /// <summary>语言资源字典的统一来源标记（用于在 MergedDictionaries 中查找并替换）</summary>
        private const string LangDictMarker = "Localization/Strings";

        /// <summary>应用配置服务</summary>
        private readonly IConfigService _configService;

        public LocalizationService(IConfigService configService)
        {
            _configService = configService;
        }

        /// <summary>从配置初始化语言</summary>
        public void InitializeFromConfig()
        {
            ApplyLanguage(_configService.Current.Language);
        }

        /// <summary>切换语言并保存配置</summary>
        public void SetLanguage(Language language)
        {
            ApplyLanguage(language);
            _configService.Current.Language = language;
            _configService.Save();
            Logger.Info($"界面语言已切换为 {language}");
        }

        /// <summary>应用语言：替换 MergedDictionaries 中的语言字典</summary>
        public void ApplyLanguage(Language language)
        {
            CurrentLanguage = language;

            var dictUri = language switch
            {
                Language.zhCN => new Uri("Localization/zh-CN.xaml", UriKind.Relative),
                Language.zhTW => new Uri("Localization/zh-TW.xaml", UriKind.Relative),
                Language.en => new Uri("Localization/en-US.xaml", UriKind.Relative),
                _ => new Uri("Localization/zh-CN.xaml", UriKind.Relative)
            };

            var newDict = new ResourceDictionary { Source = dictUri };

            var appDictionaries = Application.Current.Resources.MergedDictionaries;

            // 查找并移除旧的语言字典（通过检查是否包含已知 key 来识别）
            for (int i = appDictionaries.Count - 1; i >= 0; i--)
            {
                var dict = appDictionaries[i];
                if (dict.Contains("Nav_Launch"))
                {
                    appDictionaries.RemoveAt(i);
                }
            }

            // 插入新的语言字典到最前面（优先级最高）
            appDictionaries.Insert(0, newDict);
        }

        /// <summary>按 key 获取当前语言的字符串（用于 code-behind）</summary>
        public string GetString(string key)
        {
            return Application.Current.TryFindResource(key) as string ?? key;
        }
    }
}
