using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using YCL.Core.Utils;
using YCL.Services;

namespace YCL.Views
{
    /// <summary>
    /// 全屏崩溃报告窗口（v26.1.0.5 规格9.1）。
    /// 当游戏异常退出时弹出，显示：
    /// 1. 大标题 + 退出码/版本/时间
    /// 2. 崩溃分析与解决方案列表（来自 CrashReportService.AnalyzeCrash）
    /// 3. 崩溃报告文件路径
    /// 4. 操作按钮：复制路径 / 打开报告文件夹 / 关闭
    /// </summary>
    public partial class CrashReportWindow : Window
    {
        private readonly string? _reportPath;

        /// <summary>
        /// 创建并显示全屏崩溃报告窗口。
        /// </summary>
        /// <param name="versionId">崩溃的游戏版本 id</param>
        /// <param name="exitCode">游戏进程退出码</param>
        /// <param name="gameLog">完整游戏日志（用于分析）</param>
        /// <param name="reportPath">崩溃报告文件路径；为 null 表示生成失败</param>
        public CrashReportWindow(string versionId, int exitCode, string gameLog, string? reportPath)
        {
            InitializeComponent();
            _reportPath = reportPath;

            // 顶部副标题：版本 + 退出码 + 时间
            SubtitleText.Text = $"版本：{versionId}    退出码：{exitCode}    时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";

            // 分析崩溃原因，填充解决方案列表
            var solutions = CrashReportService.AnalyzeCrash(gameLog ?? string.Empty);
            if (solutions.Count > 0)
            {
                SolutionsList.ItemsSource = solutions;
                NoSolutionHint.Visibility = Visibility.Collapsed;
            }
            else
            {
                SolutionsList.Visibility = Visibility.Collapsed;
                NoSolutionHint.Visibility = Visibility.Visible;
            }

            // 报告路径显示
            if (!string.IsNullOrEmpty(reportPath))
            {
                ReportPathText.Text = reportPath;
                ReportFailText.Visibility = Visibility.Collapsed;
            }
            else
            {
                ReportPathText.Visibility = Visibility.Collapsed;
                ReportFailText.Visibility = Visibility.Visible;
                // 报告生成失败时禁用复制和打开文件夹按钮
                CopyPathButton.IsEnabled = false;
                OpenFolderButton.IsEnabled = false;
            }
        }

        /// <summary>复制报告路径到剪贴板</summary>
        private void CopyPathButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_reportPath))
                {
                    Clipboard.SetText(_reportPath);
                    CopyPathButton.Content = "已复制！";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("复制崩溃报告路径失败", ex);
            }
        }

        /// <summary>在资源管理器中打开报告所在文件夹并选中报告文件</summary>
        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_reportPath) || !System.IO.File.Exists(_reportPath)) return;
                // 用 explorer.exe /select,<path> 打开文件夹并选中文件
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{_reportPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error("打开崩溃报告文件夹失败", ex);
            }
        }

        /// <summary>关闭窗口</summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
