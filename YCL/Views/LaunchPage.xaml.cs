using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using YCL.ViewModels;

namespace YCL.Views
{
    /// <summary>
    /// 启动页。包含版本选择、玩家名、内存设置、启动按钮、日志面板等。
    /// </summary>
    public partial class LaunchPage : UserControl
    {
        public LaunchPage()
        {
            InitializeComponent();
        }

        /// <summary>页面加载完成：订阅日志集合变化，自动滚动到底部</summary>
        private void LaunchPage_Loaded(object sender, RoutedEventArgs e)
        {
            // 订阅日志集合变化，每加一条新日志就滚动到底部
            if (DataContext is LaunchPageViewModel vm)
            {
                vm.LogEntries.CollectionChanged -= OnLogCollectionChanged;
                vm.LogEntries.CollectionChanged += OnLogCollectionChanged;
            }
        }

        /// <summary>日志集合变化时自动滚动到底部</summary>
        private void OnLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogListBox.Items.Count > 0)
            {
                // 用 Dispatcher 异步滚动，确保 UI 已渲染完新项
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (LogListBox.Items.Count > 0)
                    {
                        LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
                    }
                }));
            }
        }
    }
}
