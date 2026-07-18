using System;
using System.Collections.Generic;
using System.IO;
using YCL.Core.Utils;
using YCL.Core.Versions;
using YCL.Models.Accounts;
using YCL.Models.Versions;

namespace YCL.Core.Launch
{
    /// <summary>
    /// 启动参数生成器实现。
    /// 主要工作：
    /// 1. 拼 JVM 参数：内存 + 版本自带的 jvm 参数 + classpath + 日志配置
    /// 2. 拼游戏参数：从 arguments.game 或 minecraftArguments 取，替换 ${} 占位符
    /// 3. 返回包含 java 路径、JVM 参数、主类、游戏参数的完整对象
    /// </summary>
    public class LaunchArgumentBuilder : ILaunchArgumentBuilder
    {
        /// <summary>启动器名（用于 ${launcher_name} 占位符）</summary>
        private const string LauncherName = "YCL";

        /// <summary>启动器版本（用于 ${launcher_version} 占位符）</summary>
        private const string LauncherVersion = "1.0.0";

        /// <summary>版本类型（用于 ${version_type} 占位符，未指定时用 "release"）</summary>
        private const string DefaultVersionType = "release";

        /// <inheritdoc/>
        public LaunchArguments Build(
            ResolvedVersion resolved,
            AccountBase account,
            string minecraftPath,
            string javaPath,
            int maxMemoryMb,
            int minMemoryMb,
            bool enableVersionIsolation = false,
            List<string>? extraJvmArguments = null,
            int windowWidth = 0,
            int windowHeight = 0,
            bool fullscreenOnLaunch = false,
            string? userExtraJvmArgs = null)
        {
            var info = resolved.Info;
            if (info == null || string.IsNullOrEmpty(info.MainClass))
            {
                throw new InvalidOperationException("版本信息不完整：缺少 mainClass");
            }

            // 版本隔离：启用后游戏目录指向 .minecraft/versions/<id>/，
            // 每个版本拥有独立的 mods/saves/configs 等子目录。
            // assets/libraries 仍共享 .minecraft/ 下的（不隔离）。
            string gameDirectory = minecraftPath;
            if (enableVersionIsolation && !string.IsNullOrEmpty(info.Id))
            {
                gameDirectory = Path.Combine(minecraftPath, "versions", info.Id);
            }

            var result = new LaunchArguments
            {
                JavaPath = javaPath,
                MainClass = info.MainClass!,
                WorkingDirectory = gameDirectory
            };

            // natives 解压目录：.minecraft/versions/<id>/natives-<时间戳>
            // 实际解压由 GameLauncher 负责，这里只生成路径
            var nativesDir = Path.Combine(resolved.VersionDirectory,
                "natives-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            result.NativesDirectory = nativesDir;

            // 准备占位符字典（先准备好，后面替换 ${xxx} 用）
            // 注意：game_directory / gameDir 用隔离后的 gameDirectory，
            //       但 assets_root / library_directory 仍指向 .minecraft/ 下的共享目录
            var placeholders = BuildPlaceholders(resolved, account, minecraftPath, nativesDir, gameDirectory);

            // 1. JVM 参数（含可能的 authlib-injector 注入参数 + 用户自定义参数）
            result.JvmArguments = BuildJvmArguments(resolved, placeholders, maxMemoryMb, minMemoryMb,
                minecraftPath, extraJvmArguments, userExtraJvmArgs);

            // 2. 游戏参数（含窗口大小 / 全屏参数）
            result.GameArguments = BuildGameArguments(info, placeholders, windowWidth, windowHeight, fullscreenOnLaunch);

            Logger.Debug($"启动参数生成完成：JVM 参数 {result.JvmArguments.Count} 项，" +
                         $"游戏参数 {result.GameArguments.Count} 项，" +
                         $"版本隔离={enableVersionIsolation}，gameDir={gameDirectory}，账户类型={account.Type}，" +
                         $"窗口={windowWidth}x{windowHeight}，全屏={fullscreenOnLaunch}");
            Logger.Debug("完整命令行：" + result.ToCommandLine());

            return result;
        }

        /// <summary>
        /// 构建占位符字典。键是 ${xxx} 中的 xxx，值是要替换的内容。
        /// 注意：<paramref name="gameDirectory"/> 是版本隔离后的游戏目录（用于 game_directory 占位符），
        /// <paramref name="minecraftPath"/> 是 .minecraft 根目录（assets/libraries 共享目录）。
        /// </summary>
        private Dictionary<string, string> BuildPlaceholders(
            ResolvedVersion resolved,
            AccountBase account,
            string minecraftPath,
            string nativesDir,
            string gameDirectory)
        {
            var info = resolved.Info;
            var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["auth_player_name"] = account.Username,
                ["auth_uuid"] = account.Uuid,
                ["auth_access_token"] = account.AccessToken,
                ["auth_session"] = $"token:{account.AccessToken}:{account.Uuid}", // 旧版本字段
                ["userProperties"] = "{}",
                ["user_type"] = account.UserType,
                ["version_name"] = info?.Id ?? string.Empty,
                ["version_type"] = info?.Type ?? DefaultVersionType,
                // game_directory 用隔离后的目录（启用隔离时为 .minecraft/versions/<id>/）
                ["game_directory"] = gameDirectory,
                ["gameDir"] = gameDirectory,
                // assets_root 仍指向 .minecraft/assets（共享，不隔离）
                ["assets_root"] = Path.Combine(minecraftPath, "assets"),
                ["assets_index_name"] = info?.AssetIndex?.Id ?? info?.Assets ?? string.Empty,
                ["natives_directory"] = nativesDir,
                ["launcher_name"] = LauncherName,
                ["launcher_version"] = LauncherVersion,
                ["classpath"] = string.Join(Path.PathSeparator.ToString(), resolved.ClasspathFiles),
                ["classpath_separator"] = Path.PathSeparator.ToString(),
                // library_directory 仍指向 .minecraft/libraries（共享，不隔离）
                ["library_directory"] = Path.Combine(minecraftPath, "libraries"),
                ["classpath_libs"] = string.Join(Path.PathSeparator.ToString(), resolved.ClasspathFiles),
                // ${launcher_name} 在某些版本里被用作 log4j 配置文件路径占位符，按官方逻辑应是文件路径
                // 这里专门用 log_path 处理
                ["log_path"] = GetLog4jConfigPath(minecraftPath, info),
                ["log4j_configurationFile"] = GetLog4jConfigPath(minecraftPath, info),
            };

            return placeholders;
        }

        /// <summary>
        /// 获取 log4j 配置文件的绝对路径（如果版本含 logging 配置）。
        /// </summary>
        private string GetLog4jConfigPath(string minecraftPath, VersionInfo? info)
        {
            if (info?.Logging?.Client?.File == null)
                return string.Empty;

            var fileId = info.Logging.Client.File.Id;
            if (string.IsNullOrEmpty(fileId))
                return string.Empty;

            // 配置文件存放在 .minecraft/assets/log_configs/<id>
            return Path.Combine(minecraftPath, "assets", "log_configs", fileId);
        }

        /// <summary>
        /// 构建 JVM 参数列表。
        /// 顺序：内存参数 → 额外注入参数（authlib-injector）→ 用户自定义参数 → 版本自带 jvm 参数 → 日志配置 → natives 路径 → classpath
        /// </summary>
        private List<string> BuildJvmArguments(
            ResolvedVersion resolved,
            Dictionary<string, string> placeholders,
            int maxMemoryMb,
            int minMemoryMb,
            string minecraftPath,
            List<string>? extraJvmArguments,
            string? userExtraJvmArgs)
        {
            var jvmArgs = new List<string>();

            // 1. 内存参数
            jvmArgs.Add($"-Xmx{maxMemoryMb}M");
            jvmArgs.Add($"-Xms{minMemoryMb}M");

            // 1.5 额外 JVM 参数（authlib-injector 注入参数，外置登录时使用）
            if (extraJvmArguments != null)
            {
                jvmArgs.AddRange(extraJvmArguments);
            }

            // 1.6 用户自定义 JVM 参数（从设置页读取，空格分隔，如 "-XX:+UseG1GC -XX:MaxGCPauseMillis=50"）
            if (!string.IsNullOrWhiteSpace(userExtraJvmArgs))
            {
                var userArgs = userExtraJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                jvmArgs.AddRange(userArgs);
            }

            // 2. 版本自带的 jvm 参数（应用 rules 过滤 + 替换占位符）
            if (resolved.Info?.Arguments?.Jvm != null)
            {
                foreach (var item in resolved.Info.Arguments.Jvm)
                {
                    // 应用 rules 过滤
                    if (!IsArgumentAllowed(item))
                        continue;

                    foreach (var value in item.GetValueAsList())
                    {
                        jvmArgs.Add(ReplacePlaceholders(value, placeholders));
                    }
                }
            }

            // 3. 日志配置（如果版本含 logging.client）
            var logging = resolved.Info?.Logging?.Client;
            if (logging?.File != null && !string.IsNullOrEmpty(logging.Argument))
            {
                // logging.Argument 形如 "-Dlog4j.configurationFile=${path}"
                // 替换 ${path} 为实际文件路径
                var logArg = logging.Argument.Replace("${path}",
                    GetLog4jConfigPath(minecraftPath, resolved.Info));
                jvmArgs.Add(logArg);
            }

            // 4. 确保有 -Djava.library.path（指向 natives 目录）
            //    版本自带的 jvm 参数里通常已有，这里再确保一下
            var nativesDir = placeholders["natives_directory"];
            if (!jvmArgs.Any(a => a.Contains("java.library.path")))
            {
                jvmArgs.Add($"-Djava.library.path={nativesDir}");
            }

            // 5. classpath：-cp <所有 jar 路径用分号连接>
            //    版本自带的 jvm 参数里通常已有 -cp ${classpath}，这里再确保一下
            if (!jvmArgs.Any(a => a == "-cp"))
            {
                jvmArgs.Add("-cp");
                jvmArgs.Add(placeholders["classpath"]);
            }

            return jvmArgs;
        }

        /// <summary>
        /// 构建游戏参数列表。
        /// - 新版本（1.13+）：从 arguments.game 数组提取，应用 rules 过滤，替换占位符
        /// - 旧版本（1.12-）：解析 minecraftArguments 字符串，按空格分词，替换占位符
        /// 最后追加窗口大小（--width/--height）和全屏（--fullscreen）参数
        /// </summary>
        private List<string> BuildGameArguments(VersionInfo info, Dictionary<string, string> placeholders,
            int windowWidth, int windowHeight, bool fullscreenOnLaunch)
        {
            var gameArgs = new List<string>();

            if (info.Arguments?.Game != null && info.Arguments.Game.Count > 0)
            {
                // 新版本：遍历 arguments.game 数组
                foreach (var item in info.Arguments.Game)
                {
                    if (!IsArgumentAllowed(item))
                        continue;

                    foreach (var value in item.GetValueAsList())
                    {
                        gameArgs.Add(ReplacePlaceholders(value, placeholders));
                    }
                }
            }
            else if (!string.IsNullOrEmpty(info.MinecraftArguments))
            {
                // 旧版本：按空格分词 minecraftArguments 字符串
                var tokens = info.MinecraftArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var token in tokens)
                {
                    gameArgs.Add(ReplacePlaceholders(token, placeholders));
                }
            }

            // 追加窗口大小参数（仅当宽度/高度 > 0 时）
            // Minecraft 支持 --width 和 --height 参数控制窗口大小
            if (windowWidth > 0)
            {
                gameArgs.Add("--width");
                gameArgs.Add(windowWidth.ToString());
            }
            if (windowHeight > 0)
            {
                gameArgs.Add("--height");
                gameArgs.Add(windowHeight.ToString());
            }

            // 追加全屏参数
            if (fullscreenOnLaunch)
            {
                gameArgs.Add("--fullscreen");
            }

            return gameArgs;
        }

        /// <summary>
        /// 判断参数（ArgumentItem）的 rules 是否允许其在当前平台生效。
        /// 规则评估逻辑与库的 rules 相同：
        /// - 无 rules → 允许
        /// - 有 allow 规则：默认不允许，只有匹配 allow 的 OS 才允许
        /// - 只有 disallow 规则：默认允许，匹配 disallow 的 OS 不允许
        /// </summary>
        private bool IsArgumentAllowed(ArgumentItem item)
        {
            if (item.Rules == null || item.Rules.Count == 0)
                return true;

            // 检查是否存在 allow 规则
            bool hasAllowRule = item.Rules.Any(r => (r.Action ?? "allow") == "allow");

            // 如果有 allow 规则，默认不允许；否则默认允许
            bool allowed = !hasAllowRule;

            foreach (var rule in item.Rules)
            {
                var action = rule.Action ?? "allow";
                var osMatch = IsOsMatch(rule.Os);

                if (action == "allow" && osMatch)
                    allowed = true;
                else if (action == "disallow" && osMatch)
                    allowed = false;
            }

            return allowed;
        }

        /// <summary>判断当前系统是否匹配规则中的 OS 限定条件（仅 Windows）</summary>
        private bool IsOsMatch(RuleOs? os)
        {
            if (os == null || string.IsNullOrEmpty(os.Name))
                return true;

            if (!string.Equals(os.Name, "windows", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(os.Arch) &&
                string.Equals(os.Arch, "x86", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        /// <summary>
        /// 替换字符串中的 ${xxx} 占位符。
        /// 如果某个占位符不在字典里，保留原样（便于排查问题）。
        /// </summary>
        private string ReplacePlaceholders(string input, Dictionary<string, string> placeholders)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            var result = input;
            foreach (var kv in placeholders)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    continue;

                var key = "${" + kv.Key + "}";
                result = result.Replace(key, kv.Value ?? string.Empty);
            }

            return result;
        }
    }
}
