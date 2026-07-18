using YCL.Models;

namespace YCL.Services
{
    /// <summary>
    /// 配置服务接口：负责读写启动器配置文件（config.json）。
    /// 通过这个接口，其他模块不需要关心配置文件存放在哪里、怎么序列化，
    /// 只要拿到 <see cref="AppConfig"/> 对象就能读取或修改配置。
    /// </summary>
    public interface IConfigService
    {
        /// <summary>当前配置对象（修改后调用 <see cref="Save"/> 持久化）</summary>
        AppConfig Current { get; }

        /// <summary>把当前配置保存到配置文件</summary>
        void Save();

        /// <summary>从配置文件重新加载配置（覆盖内存中的当前配置）</summary>
        void Load();
    }
}
