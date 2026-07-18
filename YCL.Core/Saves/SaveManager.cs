using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;

namespace YCL.Core.Saves
{
    /// <summary>
    /// 存档管理器实现。
    /// 使用 <see cref="ZipFile"/> 打包/解压存档，所有耗时操作在后台线程执行，
    /// 通过 <see cref="IProgress{BackupProgress}"/> 反馈进度。
    /// </summary>
    public class SaveManager : ISaveManager
    {
        /// <summary>
        /// 扫描 gameDir/saves/ 下的所有存档。
        /// 每个子目录视为一个存档，统计其总大小、最后修改时间、是否含 icon.png。
        /// </summary>
        public List<SaveInfo> ListSaves(string gameDir)
        {
            var result = new List<SaveInfo>();
            if (string.IsNullOrWhiteSpace(gameDir))
                return result;

            var savesDir = Path.Combine(gameDir, "saves");
            if (!Directory.Exists(savesDir))
            {
                Logger.Debug($"存档目录不存在：{savesDir}");
                return result;
            }

            try
            {
                foreach (var dir in Directory.GetDirectories(savesDir))
                {
                    try
                    {
                        var info = BuildSaveInfo(dir);
                        if (info != null)
                            result.Add(info);
                    }
                    catch (Exception ex)
                    {
                        // 单个存档扫描失败不影响其他存档
                        Logger.Warn($"扫描存档失败：{dir} - {ex.Message}");
                    }
                }

                // 按最后修改时间倒序（最新的在前）
                result = result.OrderByDescending(s => s.LastModified).ToList();
                Logger.Info($"在 {savesDir} 下扫描到 {result.Count} 个存档");
            }
            catch (Exception ex)
            {
                Logger.Error("扫描存档目录失败", ex);
            }

            return result;
        }

        /// <summary>
        /// 备份存档为 zip。
        /// 文件名：<saveName>_<yyyyMMdd_HHmmss>.zip
        /// </summary>
        public async Task BackupSaveAsync(SaveInfo save, string backupDir,
            IProgress<BackupProgress>? progress, CancellationToken ct)
        {
            if (save == null || string.IsNullOrEmpty(save.Path) || !Directory.Exists(save.Path))
                throw new DirectoryNotFoundException($"存档目录不存在：{save?.Path}");

            // 确保备份目录存在
            Directory.CreateDirectory(backupDir);

            // 生成备份文件名：<saveName>_<yyyyMMdd_HHmmss>.zip
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeName = SanitizeFileName(save.Name);
            var zipFileName = $"{safeName}_{timestamp}.zip";
            var zipPath = Path.Combine(backupDir, zipFileName);

            progress?.Report(new BackupProgress(0, $"正在备份存档 {save.Name}..."));

            // 在后台线程执行压缩（避免阻塞调用线程）
            await Task.Run(() =>
            {
                // 用 ZipFile.CreateFromDirectory 打包整个存档目录
                // CompressionLevel.Optimal：平衡速度与压缩率
                ct.ThrowIfCancellationRequested();
                ZipFile.CreateFromDirectory(save.Path, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                ct.ThrowIfCancellationRequested();
            }, ct).ConfigureAwait(false);

            progress?.Report(new BackupProgress(100, $"备份完成：{zipFileName}"));
            Logger.Info($"存档 {save.Name} 已备份到 {zipPath}");
        }

        /// <summary>
        /// 从备份 zip 恢复存档到 gameDir/saves/。
        /// 如已存在同名存档，恢复后的目录名加 _restored 后缀。
        /// </summary>
        public async Task RestoreSaveAsync(string backupZipPath, string gameDir, CancellationToken ct)
        {
            if (!File.Exists(backupZipPath))
                throw new FileNotFoundException($"备份文件不存在：{backupZipPath}");

            var savesDir = Path.Combine(gameDir, "saves");
            Directory.CreateDirectory(savesDir);

            // 从 zip 文件名推断存档名（去掉 _yyyyMMdd_HHmmss.zip 后缀）
            var fileName = Path.GetFileNameWithoutExtension(backupZipPath);
            var saveName = TryParseSaveNameFromBackup(fileName);

            // 如果推断不出，用文件名作为存档名
            if (string.IsNullOrEmpty(saveName))
                saveName = fileName;

            // 如已存在同名存档，加 _restored 后缀避免覆盖
            var targetDir = Path.Combine(savesDir, saveName);
            if (Directory.Exists(targetDir))
            {
                targetDir = Path.Combine(savesDir, saveName + "_restored");
                // 如果 _restored 也存在，再加数字后缀
                int suffix = 1;
                while (Directory.Exists(targetDir))
                {
                    targetDir = Path.Combine(savesDir, $"{saveName}_restored_{suffix}");
                    suffix++;
                }
            }

            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                // 解压到目标目录
                ZipFile.ExtractToDirectory(backupZipPath, targetDir);
                ct.ThrowIfCancellationRequested();
            }, ct).ConfigureAwait(false);

            Logger.Info($"已从 {backupZipPath} 恢复存档到 {targetDir}");
        }

        /// <summary>导入外部存档 zip（逻辑同 Restore）</summary>
        public Task ImportSaveAsync(string sourceZipPath, string gameDir)
            => RestoreSaveAsync(sourceZipPath, gameDir, CancellationToken.None);

        /// <summary>
        /// 导出存档到指定 zip 路径。
        /// 与 Backup 不同，Export 由调用方指定目标路径（如用户选择的保存位置）。
        /// </summary>
        public async Task ExportSaveAsync(SaveInfo save, string targetZipPath)
        {
            if (save == null || string.IsNullOrEmpty(save.Path) || !Directory.Exists(save.Path))
                throw new DirectoryNotFoundException($"存档目录不存在：{save?.Path}");

            // 确保目标目录存在
            var targetDir = Path.GetDirectoryName(targetZipPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            await Task.Run(() =>
            {
                ZipFile.CreateFromDirectory(save.Path, targetZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            }).ConfigureAwait(false);

            Logger.Info($"存档 {save.Name} 已导出到 {targetZipPath}");
        }

        /// <summary>
        /// 删除存档目录（含全部内容）。
        /// 注意：删除确认由 UI 层处理，此方法直接删除。
        /// </summary>
        public void DeleteSave(SaveInfo save)
        {
            if (save == null || string.IsNullOrEmpty(save.Path))
                return;

            if (!Directory.Exists(save.Path))
            {
                Logger.Warn($"要删除的存档目录不存在：{save.Path}");
                return;
            }

            Directory.Delete(save.Path, recursive: true);
            Logger.Info($"已删除存档：{save.Name}（{save.Path}）");
        }

        /// <summary>列出 backupDir 下的所有备份 zip（按创建时间倒序）</summary>
        public List<BackupInfo> ListBackups(string backupDir)
        {
            var result = new List<BackupInfo>();
            if (string.IsNullOrWhiteSpace(backupDir) || !Directory.Exists(backupDir))
                return result;

            try
            {
                foreach (var file in Directory.GetFiles(backupDir, "*.zip"))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        var backup = new BackupInfo
                        {
                            FileName = info.Name,
                            FilePath = file,
                            SizeBytes = info.Length,
                            CreatedTime = info.CreationTime,
                            SaveName = TryParseSaveNameFromBackup(Path.GetFileNameWithoutExtension(file))
                        };
                        result.Add(backup);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"读取备份文件信息失败：{file} - {ex.Message}");
                    }
                }

                result = result.OrderByDescending(b => b.CreatedTime).ToList();
                Logger.Info($"在 {backupDir} 下找到 {result.Count} 个备份");
            }
            catch (Exception ex)
            {
                Logger.Error("列出备份文件失败", ex);
            }

            return result;
        }

        /// <summary>删除指定备份 zip 文件</summary>
        public void DeleteBackup(string backupZipPath)
        {
            if (string.IsNullOrEmpty(backupZipPath) || !File.Exists(backupZipPath))
            {
                Logger.Warn($"要删除的备份文件不存在：{backupZipPath}");
                return;
            }

            File.Delete(backupZipPath);
            Logger.Info($"已删除备份：{backupZipPath}");
        }

        // ====== 私有辅助方法 ======

        /// <summary>
        /// 构建单个存档的 SaveInfo：统计大小、修改时间、icon.png。
        /// </summary>
        private static SaveInfo? BuildSaveInfo(string dirPath)
        {
            if (!Directory.Exists(dirPath))
                return null;

            var dirInfo = new DirectoryInfo(dirPath);
            var save = new SaveInfo
            {
                Name = dirInfo.Name,
                Path = dirPath,
                LastModified = dirInfo.LastWriteTime
            };

            // 检查 icon.png
            var iconPath = Path.Combine(dirPath, "icon.png");
            if (File.Exists(iconPath))
            {
                save.HasIcon = true;
                save.IconPath = iconPath;
            }

            // 统计目录总大小（递归所有文件）
            save.SizeBytes = GetDirectorySize(dirPath);

            return save;
        }

        /// <summary>递归计算目录下所有文件的总大小（字节）</summary>
        private static long GetDirectorySize(string dirPath)
        {
            long size = 0;
            try
            {
                // EnumerateFiles 比 GetFiles 更省内存（流式枚举）
                foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        size += info.Length;
                    }
                    catch
                    {
                        // 单个文件无法访问则跳过
                    }
                }
            }
            catch
            {
                // 目录无访问权限等情况，返回已统计的大小
            }
            return size;
        }

        /// <summary>
        /// 从备份文件名推断存档名。
        /// 备份文件名格式：<saveName>_<yyyyMMdd_HHmmss>
        /// 去掉最后的 _yyyyMMdd_HHmmss 部分即为存档名。
        /// 如果格式不匹配，返回完整文件名。
        /// </summary>
        private static string TryParseSaveNameFromBackup(string fileNameWithoutExtension)
        {
            if (string.IsNullOrEmpty(fileNameWithoutExtension))
                return string.Empty;

            // 找最后一个下划线，检查后面是否是 yyyyMMdd_HHmmss 格式
            var lastUnderscore = fileNameWithoutExtension.LastIndexOf('_');
            if (lastUnderscore <= 0 || lastUnderscore >= fileNameWithoutExtension.Length - 1)
                return fileNameWithoutExtension;

            // 取最后一个 _ 后面的部分，应该是 yyyyMMdd_HHmmss
            // 但yyyyMMdd 和 HHmmss 之间也有下划线，所以要找倒数第二个下划线
            var beforeLast = fileNameWithoutExtension.Substring(0, lastUnderscore);
            var secondLastUnderscore = beforeLast.LastIndexOf('_');
            if (secondLastUnderscore <= 0)
                return fileNameWithoutExtension;

            var timePart = fileNameWithoutExtension.Substring(secondLastUnderscore + 1);
            // 简单校验：yyyyMMdd_HHmmss 共 15 个字符
            if (timePart.Length == 15 && timePart[8] == '_')
            {
                return fileNameWithoutExtension.Substring(0, secondLastUnderscore);
            }

            return fileNameWithoutExtension;
        }

        /// <summary>清理文件名中的非法字符（防止备份文件名出错）</summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "save";

            var invalid = Path.GetInvalidFileNameChars();
            var result = name;
            foreach (var c in invalid)
                result = result.Replace(c, '_');
            return result;
        }
    }
}
