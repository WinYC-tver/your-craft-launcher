using System;
using System.IO;
using System.Text.Json;
using YCL.Core.Utils;
using YCL.Models;

namespace YCL.Services
{
    /// <summary>
    /// 配置服务实现：用 System.Text.Json 把 <see cref="AppConfig"/> 序列化成 JSON，
    /// 保存到 %AppData%\YCL\config.json。启动时自动加载，修改后调用 <see cref="Save"/> 保存。
    /// </summary>
    public class ConfigService : IConfigService
    {
        // 配置文件所在目录：%AppData%\YCL\
        private static readonly string ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YCL");

        // 配置文件完整路径：%AppData%\YCL\config.json
        private static readonly string ConfigPath = Path.Combine(ConfigDirectory, "config.json");

        // JSON 序列化选项：缩进排版，方便用户直接打开 config.json 查看
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        /// <summary>当前配置对象</summary>
        public AppConfig Current { get; private set; }

        public ConfigService()
        {
            // 构造时立刻加载一次配置，这样别的服务拿到 ConfigService 时配置已就绪
            Current = LoadInternal();
        }

        /// <inheritdoc/>
        public void Save()
        {
            try
            {
                // 目录不存在则创建
                Directory.CreateDirectory(ConfigDirectory);
                // 序列化成 JSON 字符串后写入文件
                var json = JsonSerializer.Serialize(Current, JsonOptions);
                File.WriteAllText(ConfigPath, json);
                Logger.Info($"配置已保存到 {ConfigPath}");
            }
            catch (Exception ex)
            {
                // 保存失败不能让程序崩溃，记录日志即可
                Logger.Error("保存配置文件失败", ex);
            }
        }

        /// <inheritdoc/>
        public void Load()
        {
            Current = LoadInternal();
        }

        /// <summary>内部加载方法：读取配置文件并反序列化。文件不存在或格式错误时返回默认配置。</summary>
        private AppConfig LoadInternal()
        {
            try
            {
                // 配置文件不存在，说明是第一次运行，返回默认配置
                if (!File.Exists(ConfigPath))
                {
                    Logger.Info("配置文件不存在，使用默认配置");
                    return new AppConfig();
                }

                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config != null)
                {
                    Logger.Info($"已从 {ConfigPath} 加载配置");
                    return config;
                }
            }
            catch (Exception ex)
            {
                // 配置文件损坏等情况，记录错误并返回默认配置
                Logger.Error("加载配置文件失败，使用默认配置", ex);
            }

            return new AppConfig();
        }
    }
}
