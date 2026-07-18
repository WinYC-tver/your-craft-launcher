namespace YCL.Core.Mods
{
    /// <summary>
    /// 模组加载器类型枚举。
    /// 用于标识一个模组是为哪种加载器编写的（Fabric / Forge / Quilt / NeoForge / LiteLoader）。
    /// 通过解析 jar 内的元数据文件（fabric.mod.json / mods.toml / mcmod.info）判断。
    /// </summary>
    public enum LoaderType
    {
        /// <summary>未知（无法判断加载器类型，或解析失败）</summary>
        Unknown = 0,

        /// <summary>Fabric（轻量加载器，1.14+ 主流）</summary>
        Fabric = 1,

        /// <summary>Forge（最老牌加载器）</summary>
        Forge = 2,

        /// <summary>Quilt（Fabric 的社区分叉，兼容 Fabric 模组）</summary>
        Quilt = 3,

        /// <summary>NeoForge（Forge 的社区分叉，1.20+ 起活跃）</summary>
        NeoForge = 4,

        /// <summary>LiteLoader（古老加载器，仅 1.12 及以下）</summary>
        LiteLoader = 5
    }
}
