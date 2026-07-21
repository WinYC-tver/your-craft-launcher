using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace YCL.Converters
{
    /// <summary>
    /// 设置搜索可见性转换器（v26.1.0.5 设置搜索）。
    /// value = 当前搜索关键字（string），ConverterParameter = 该分类/设置项的关键词集合（空格分隔）。
    /// 关键字为空 → 显示所有；否则仅当关键词集合包含关键字时显示（不区分大小写）。
    /// </summary>
    public class KeywordMatchToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var keyword = value as string ?? string.Empty;
            var keywords = parameter as string ?? string.Empty;

            if (string.IsNullOrWhiteSpace(keyword))
                return Visibility.Visible;

            // 不区分大小写包含匹配（同时兼容中英文）
            return keywords.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    /// <summary>
    /// 字符串相等 → 可见性转换器。
    /// value = 当前选中分类 Key，ConverterParameter = 该面板对应的分类 Key。
    /// 相等 → Visible，否则 Collapsed。
    /// </summary>
    public class StringEqualsToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var current = value as string ?? string.Empty;
            var expected = parameter as string ?? string.Empty;
            return string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    /// <summary>
    /// 字符串相等 → Boolean 转换器（用于左侧分类按钮的选中态）。
    /// </summary>
    public class StringEqualsToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var current = value as string ?? string.Empty;
            var expected = parameter as string ?? string.Empty;
            return string.Equals(current, expected, StringComparison.OrdinalIgnoreCase);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
