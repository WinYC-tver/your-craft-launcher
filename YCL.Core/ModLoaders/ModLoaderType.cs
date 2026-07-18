namespace YCL.Core.ModLoaders
{
    /// <summary>
    /// 模组加载器类型枚举。
    /// </summary>
    public enum ModLoaderType
    {
        /// <summary>Minecraft Forge（最老牌的加载器，支持最广）</summary>
        Forge = 0,

        /// <summary>Fabric（轻量级加载器，1.14+ 速度快）</summary>
        Fabric = 1,

        /// <summary>Quilt（Fabric 的社区分叉，兼容 Fabric 模组）</summary>
        Quilt = 2,

        /// <summary>NeoForge（Forge 的社区分叉，1.20+ 起活跃）</summary>
        NeoForge = 3,

        /// <summary>LiteLoader（古老加载器，仅支持 1.12 及以下）</summary>
        LiteLoader = 4
    }
}
