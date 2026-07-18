using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using YCL.Core.Saves;
using YCL.Core.Utils;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 存档页 ViewModel：负责扫描、备份、恢复、导入、导出、删除存档。
    /// 用户先选择版本（确定 gameDir），再操作存档。
    /// 备份文件保存到 %AppData%\YCL\saves_backup\。
    /// </summary>
    public partial class SavesPageViewModel : ViewModelBase
    {
        private readonly IConfigService _configService;
        private readonly ISaveManager _saveManager;

        /// <summary>备份目录：%AppData%\YCL\saves_backup\</summary>
        private static readonly string BackupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YCL", "saves_backup");

        public SavesPageViewModel(IConfigService configService, ISaveManager saveManager)
        {
            _configService = configService;
            _saveManager = saveManager;

            // 立即扫描版本列表（后台线程，避免阻塞 UI）
            _ = RefreshVersionsAsync();
        }

        /// <summary>已安装的版本列表</summary>
        public ObservableCollection<string> Versions { get; } = new();

        /// <summary>存档列表</summary>
        public ObservableCollection<SaveInfo> Saves { get; } = new();

        /// <summary>备份列表</summary>
        public ObservableCollection<BackupInfo> Backups { get; } = new();

        /// <summary>当前选中的版本</summary>
        [ObservableProperty]
        private string? _selectedVersion;

        /// <summary>版本变化时自动刷新存档和备份列表</summary>
        partial void OnSelectedVersionChanged(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                _ = RefreshSavesAsync();
                _ = RefreshBackupsAsync();
            }
        }

        /// <summary>当前选中的存档</summary>
        [ObservableProperty]
        private SaveInfo? _selectedSave;

        /// <summary>当前选中的备份</summary>
        [ObservableProperty]
        private BackupInfo? _selectedBackup;

        /// <summary>是否正在执行操作（备份/恢复/导入/导出等）</summary>
        [ObservableProperty]
        private bool _isBusy;

        /// <summary>进度百分比（0~100，-1 表示不确定）</summary>
        [ObservableProperty]
        private int _progressPercent = -1;

        /// <summary>状态提示信息</summary>
        [ObservableProperty]
        private string? _statusMessage;

        /// <summary>提示信息（如目录不存在时显示）</summary>
        [ObservableProperty]
        private string? _hintMessage;

        /// <summary>当前游戏目录（显示用）</summary>
        [ObservableProperty]
        private string _gameDirDisplay = string.Empty;

        // ================================================================
        // 命令
        // ================================================================

        /// <summary>刷新版本列表</summary>
        [RelayCommand]
        private async Task RefreshVersionsAsync()
        {
            try
            {
                var minecraftPath = GetMinecraftPath();
                var versions = ScanVersions(minecraftPath);

                Versions.Clear();
                foreach (var v in versions)
                    Versions.Add(v);

                // 默认选中第一个版本或配置中保存的版本
                if (Versions.Count > 0 && string.IsNullOrEmpty(SelectedVersion))
                {
                    var last = _configService.Current.LastSelectedVersion;
                    if (!string.IsNullOrEmpty(last) && Versions.Contains(last))
                        SelectedVersion = last;
                    else
                        SelectedVersion = Versions[0];
                }

                if (Versions.Count == 0)
                {
                    HintMessage = $"在 {minecraftPath}\\versions 下没有找到任何版本。\n请先下载版本。";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("刷新版本列表失败", ex);
                HintMessage = "刷新版本列表失败：" + ex.Message;
            }
            await Task.CompletedTask;
        }

        /// <summary>刷新存档列表</summary>
        [RelayCommand]
        private async Task RefreshSavesAsync()
        {
            var gameDir = GetCurrentGameDir();
            if (string.IsNullOrEmpty(gameDir))
            {
                HintMessage = "请先选择一个版本";
                return;
            }

            GameDirDisplay = "游戏目录：" + Path.Combine(gameDir, "saves");

            try
            {
                var saves = await Task.Run(() => _saveManager.ListSaves(gameDir));
                Saves.Clear();
                foreach (var s in saves)
                    Saves.Add(s);

                HintMessage = Saves.Count == 0
                    ? $"在 {gameDir}\\saves 下没有找到存档。"
                    : null;
            }
            catch (Exception ex)
            {
                Logger.Error("刷新存档列表失败", ex);
                HintMessage = "刷新存档列表失败：" + ex.Message;
            }
        }

        /// <summary>刷新备份列表</summary>
        [RelayCommand]
        private async Task RefreshBackupsAsync()
        {
            try
            {
                var backups = await Task.Run(() => _saveManager.ListBackups(BackupDirectory));
                Backups.Clear();
                foreach (var b in backups)
                    Backups.Add(b);
            }
            catch (Exception ex)
            {
                Logger.Error("刷新备份列表失败", ex);
            }
        }

        /// <summary>备份选中存档</summary>
        [RelayCommand]
        private async Task BackupSaveAsync()
        {
            if (SelectedSave == null)
            {
                MessageBox.Show("请先选择一个要备份的存档。", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IsBusy = true;
            ProgressPercent = -1;
            StatusMessage = $"正在备份存档 {SelectedSave.Name}...";

            try
            {
                var progress = new Progress<BackupProgress>(p =>
                {
                    ProgressPercent = p.Percent;
                    StatusMessage = p.Message;
                });

                await _saveManager.BackupSaveAsync(SelectedSave, BackupDirectory, progress, CancellationToken.None);
                StatusMessage = $"备份完成：{SelectedSave.Name}";

                // 刷新备份列表
                await RefreshBackupsAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("备份存档失败", ex);
                StatusMessage = "备份失败：" + ex.Message;
                MessageBox.Show("备份存档失败：" + ex.Message, "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsBusy = false;
                ProgressPercent = -1;
            }
        }

        /// <summary>恢复选中备份</summary>
        [RelayCommand]
        private async Task RestoreBackupAsync()
        {
            if (SelectedBackup == null)
            {
                MessageBox.Show("请先选择一个要恢复的备份。", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var gameDir = GetCurrentGameDir();
            if (string.IsNullOrEmpty(gameDir))
            {
                MessageBox.Show("请先选择一个版本。", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 确认恢复
            var result = MessageBox.Show(
                $"确定要从备份 {SelectedBackup.FileName} 恢复存档吗？\n" +
                "如已存在同名存档，恢复后会自动加 _restored 后缀。",
                "确认恢复", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            IsBusy = true;
            StatusMessage = $"正在恢复备份 {SelectedBackup.FileName}...";

            try
            {
                await _saveManager.RestoreSaveAsync(SelectedBackup.FilePath, gameDir, CancellationToken.None);
                StatusMessage = "恢复完成";
                await RefreshSavesAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("恢复存档失败", ex);
                StatusMessage = "恢复失败：" + ex.Message;
                MessageBox.Show("恢复存档失败：" + ex.Message, "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>导入外部存档 zip</summary>
        [RelayCommand]
        private async Task ImportSaveAsync()
        {
            var gameDir = GetCurrentGameDir();
            if (string.IsNullOrEmpty(gameDir))
            {
                MessageBox.Show("请先选择一个版本。", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "选择要导入的存档 zip 文件",
                Filter = "存档压缩包|*.zip|所有文件|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true) return;

            IsBusy = true;
            StatusMessage = "正在导入存档...";

            try
            {
                await _saveManager.ImportSaveAsync(dialog.FileName, gameDir);
                StatusMessage = "导入完成";
                await RefreshSavesAsync();
                MessageBox.Show("存档导入成功！", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("导入存档失败", ex);
                StatusMessage = "导入失败：" + ex.Message;
                MessageBox.Show("导入存档失败：" + ex.Message, "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>导出选中存档到指定路径</summary>
        [RelayCommand]
        private async Task ExportSaveAsync()
        {
            if (SelectedSave == null)
            {
                MessageBox.Show("请先选择一个要导出的存档。", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "导出存档为 zip",
                Filter = "存档压缩包|*.zip",
                FileName = SelectedSave.Name + ".zip"
            };

            if (dialog.ShowDialog() != true) return;

            IsBusy = true;
            StatusMessage = $"正在导出存档 {SelectedSave.Name}...";

            try
            {
                await _saveManager.ExportSaveAsync(SelectedSave, dialog.FileName);
                StatusMessage = "导出完成";
                MessageBox.Show("存档导出成功！", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("导出存档失败", ex);
                StatusMessage = "导出失败：" + ex.Message;
                MessageBox.Show("导出存档失败：" + ex.Message, "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>删除选中存档</summary>
        [RelayCommand]
        private async Task DeleteSaveAsync()
        {
            if (SelectedSave == null)
            {
                MessageBox.Show("请先选择一个要删除的存档。", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 确认删除
            var result = MessageBox.Show(
                $"确定要删除存档 \"{SelectedSave.Name}\" 吗？\n" +
                "此操作不可恢复！建议先备份。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                _saveManager.DeleteSave(SelectedSave);
                StatusMessage = $"已删除存档：{SelectedSave.Name}";
                await RefreshSavesAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("删除存档失败", ex);
                MessageBox.Show("删除存档失败：" + ex.Message, "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>删除选中备份</summary>
        [RelayCommand]
        private async Task DeleteBackupAsync()
        {
            if (SelectedBackup == null)
            {
                MessageBox.Show("请先选择一个要删除的备份。", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"确定要删除备份 {SelectedBackup.FileName} 吗？\n此操作不可恢复！",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                _saveManager.DeleteBackup(SelectedBackup.FilePath);
                StatusMessage = $"已删除备份：{SelectedBackup.FileName}";
                await RefreshBackupsAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("删除备份失败", ex);
                MessageBox.Show("删除备份失败：" + ex.Message, "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>打开存档文件夹</summary>
        [RelayCommand]
        private void OpenSavesFolder()
        {
            var gameDir = GetCurrentGameDir();
            if (string.IsNullOrEmpty(gameDir))
            {
                MessageBox.Show("请先选择一个版本。", "YCL 启动器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var savesDir = Path.Combine(gameDir, "saves");
            try
            {
                Directory.CreateDirectory(savesDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = savesDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error("打开存档文件夹失败", ex);
            }
        }

        // ================================================================
        // 私有辅助方法
        // ================================================================

        /// <summary>获取 .minecraft 根目录路径（从配置读取，为空时用默认值）</summary>
        private string GetMinecraftPath()
        {
            var path = _configService.Current.MinecraftPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    ".minecraft");
            }
            return path;
        }

        /// <summary>
        /// 获取当前版本对应的游戏目录。
        /// 版本隔离启用时：.minecraft/versions/<versionId>/
        /// 否则：.minecraft/
        /// </summary>
        private string? GetCurrentGameDir()
        {
            if (string.IsNullOrEmpty(SelectedVersion))
                return null;

            var minecraftPath = GetMinecraftPath();
            if (_configService.Current.EnableVersionIsolation)
            {
                return Path.Combine(minecraftPath, "versions", SelectedVersion);
            }
            return minecraftPath;
        }

        /// <summary>扫描 .minecraft/versions 目录下的所有版本</summary>
        private static List<string> ScanVersions(string minecraftPath)
        {
            var result = new List<string>();
            try
            {
                var versionsDir = Path.Combine(minecraftPath, "versions");
                if (!Directory.Exists(versionsDir))
                    return result;

                foreach (var dir in Directory.GetDirectories(versionsDir))
                {
                    var id = Path.GetFileName(dir);
                    var jsonPath = Path.Combine(dir, id + ".json");
                    if (File.Exists(jsonPath))
                        result.Add(id);
                }

                result.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Logger.Error("扫描 versions 目录出错", ex);
            }
            return result;
        }
    }
}
