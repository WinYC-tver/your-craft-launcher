using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YCL.Core.Saves
{
    /// <summary>
    /// 存档管理器接口。负责扫描、备份、恢复、导入、导出、删除存档。
    /// 所有路径操作都是安全的：目录不存在时返回空列表，不抛异常。
    /// </summary>
    public interface ISaveManager
    {
        /// <summary>
        /// 扫描 gameDir/saves/ 下的所有存档（按最后修改时间倒序）。
        /// 每个子目录是一个存档。
        /// </summary>
        /// <param name="gameDir">游戏目录（.minecraft 或版本隔离目录）</param>
        /// <returns>存档列表，目录不存在时返回空列表</returns>
        List<SaveInfo> ListSaves(string gameDir);

        /// <summary>
        /// 备份存档为 zip。
        /// 命名规则：<saveName>_<yyyyMMdd_HHmmss>.zip
        /// 保存到 backupDir（默认 %AppData%\YCL\saves_backup\，由调用方传入）。
        /// </summary>
        /// <param name="save">要备份的存档</param>
        /// <param name="backupDir">备份目录</param>
        /// <param name="progress">进度回调（可为 null）</param>
        /// <param name="ct">取消令牌</param>
        Task BackupSaveAsync(SaveInfo save, string backupDir, IProgress<BackupProgress>? progress, CancellationToken ct);

        /// <summary>
        /// 从备份 zip 恢复存档到 gameDir/saves/。
        /// 如已存在同名存档，恢复后的存档名加 _restored 后缀。
        /// </summary>
        /// <param name="backupZipPath">备份 zip 的完整路径</param>
        /// <param name="gameDir">游戏目录</param>
        /// <param name="ct">取消令牌</param>
        Task RestoreSaveAsync(string backupZipPath, string gameDir, CancellationToken ct);

        /// <summary>
        /// 导入外部存档 zip 到 gameDir/saves/。
        /// 逻辑同 <see cref="RestoreSaveAsync"/>。
        /// </summary>
        /// <param name="sourceZipPath">要导入的 zip 路径</param>
        /// <param name="gameDir">游戏目录</param>
        Task ImportSaveAsync(string sourceZipPath, string gameDir);

        /// <summary>
        /// 导出存档到指定 zip 路径。
        /// </summary>
        /// <param name="save">要导出的存档</param>
        /// <param name="targetZipPath">目标 zip 完整路径</param>
        Task ExportSaveAsync(SaveInfo save, string targetZipPath);

        /// <summary>
        /// 删除存档目录（含全部内容）。
        /// 注意：删除确认由 UI 层处理，此方法直接删除。
        /// </summary>
        /// <param name="save">要删除的存档</param>
        void DeleteSave(SaveInfo save);

        /// <summary>
        /// 列出 backupDir 下的所有备份 zip（按创建时间倒序）。
        /// </summary>
        /// <param name="backupDir">备份目录</param>
        /// <returns>备份列表，目录不存在时返回空列表</returns>
        List<BackupInfo> ListBackups(string backupDir);

        /// <summary>
        /// 删除指定备份 zip 文件。
        /// </summary>
        /// <param name="backupZipPath">备份 zip 完整路径</param>
        void DeleteBackup(string backupZipPath);
    }
}
