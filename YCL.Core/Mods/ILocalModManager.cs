using System.Collections.Generic;

namespace YCL.Core.Mods
{
    /// <summary>
    /// 本地模组管理服务接口。
    /// 负责扫描指定游戏目录下 mods 文件夹的模组、解析元数据、
    /// 启用/禁用模组、删除模组与打开 mods 文件夹。
    ///
    /// 调用方（如 ModPageViewModel）只依赖此接口，不直接依赖具体实现。
    /// </summary>
    public interface ILocalModManager
    {
        /// <summary>
        /// 扫描指定游戏目录下 mods 文件夹的所有模组。
        /// 会扫描 .jar（启用）和 .disabled（禁用）文件，
        /// 并从 jar 内解析元数据（fabric.mod.json / mods.toml / mcmod.info）。
        /// 单个文件解析失败不影响其他文件。
        /// </summary>
        /// <param name="gameDir">游戏目录路径（.minecraft 或 .minecraft/versions/&lt;id&gt;）</param>
        /// <returns>解析出的模组信息列表</returns>
        List<ModInfo> ListMods(string gameDir);

        /// <summary>
        /// 切换模组的启用状态（通过重命名 .jar ↔ .disabled 实现）。
        /// - enable=true：xxx.disabled → xxx.jar
        /// - enable=false：xxx.jar → xxx.disabled
        /// </summary>
        /// <param name="modFilePath">模组文件当前路径</param>
        /// <param name="enable">是否启用</param>
        void ToggleMod(string modFilePath, bool enable);

        /// <summary>
        /// 删除模组文件。
        /// 注意：此操作不可逆，调用方应在 UI 弹确认对话框。
        /// </summary>
        /// <param name="modFilePath">模组文件路径</param>
        void DeleteMod(string modFilePath);

        /// <summary>
        /// 用系统资源管理器打开 mods 文件夹。
        /// 如果 mods 文件夹不存在会先创建。
        /// </summary>
        /// <param name="gameDir">游戏目录路径</param>
        void OpenModsFolder(string gameDir);

        /// <summary>
        /// 获取指定游戏目录下的 mods 文件夹路径。
        /// 不存在时会创建。
        /// </summary>
        /// <param name="gameDir">游戏目录路径</param>
        /// <returns>mods 文件夹完整路径</returns>
        string GetModsDirectory(string gameDir);
    }
}
