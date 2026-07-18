using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using YCL.Core.Utils;
using YCL.Models.Versions;

namespace YCL.Core.Versions
{
    /// <summary>
    /// 版本解析服务实现。
    /// 主要职责：
    /// 1. 读取版本 JSON 反序列化为 <see cref="VersionInfo"/>
    /// 2. 递归处理 inheritsFrom：把父版本的 libraries 与 arguments 合并进来
    /// 3. 解析库文件路径：根据 name 推路径、根据 rules 过滤、根据 natives 取 classifier
    /// </summary>
    public class VersionResolver : IVersionResolver
    {
        // JSON 反序列化选项：注册 ArgumentItemConverter 处理混合类型数组
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            // 容忍 JSON 中包含模型没有定义的字段
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        static VersionResolver()
        {
            JsonOptions.Converters.Add(new ArgumentItemConverter());
        }

        /// <inheritdoc/>
        public ResolvedVersion Resolve(string minecraftPath, string versionId)
        {
            Logger.Info($"开始解析版本：{versionId}（.minecraft = {minecraftPath}）");

            var resolved = new ResolvedVersion();

            // 递归读取并合并版本（处理 inheritsFrom 链）
            var mergedInfo = ReadAndMerge(minecraftPath, versionId);
            resolved.Info = mergedInfo;

            // 版本目录与客户端 jar 路径
            resolved.VersionDirectory = Path.Combine(minecraftPath, "versions", versionId);
            resolved.ClientJarPath = Path.Combine(resolved.VersionDirectory, versionId + ".jar");

            // 解析库文件路径
            ResolveLibraries(minecraftPath, mergedInfo.Libraries, resolved);

            Logger.Info($"版本解析完成：classpath 库 {resolved.ClasspathFiles.Count} 个，" +
                        $"natives 库 {resolved.NativeFiles.Count} 个");
            return resolved;
        }

        /// <inheritdoc/>
        public List<string> ListVersions(string minecraftPath)
        {
            var result = new List<string>();
            try
            {
                var versionsDir = Path.Combine(minecraftPath, "versions");
                if (!Directory.Exists(versionsDir))
                {
                    Logger.Warn($"versions 目录不存在：{versionsDir}");
                    return result;
                }

                foreach (var dir in Directory.GetDirectories(versionsDir))
                {
                    var id = Path.GetFileName(dir);
                    // 检查目录下是否有 <id>.json
                    var jsonPath = Path.Combine(dir, id + ".json");
                    if (File.Exists(jsonPath))
                    {
                        result.Add(id);
                    }
                }

                result.Sort(StringComparer.OrdinalIgnoreCase);
                Logger.Info($"扫描到 {result.Count} 个版本：{versionsDir}");
            }
            catch (Exception ex)
            {
                Logger.Error("扫描版本列表失败", ex);
            }

            return result;
        }

        /// <summary>
        /// 递归读取版本 JSON 并合并 inheritsFrom 父版本。
        /// 合并规则：
        /// - 子版本的 libraries 追加在父版本之后（保持顺序）
        /// - 子版本的 arguments.game / arguments.jvm 追加在父版本之后
        /// - 子版本未提供的字段（mainClass、assetIndex 等）取父版本的值
        /// </summary>
        private VersionInfo ReadAndMerge(string minecraftPath, string versionId)
        {
            var info = ReadVersionJson(minecraftPath, versionId);
            if (info == null)
            {
                throw new FileNotFoundException(
                    $"找不到版本 JSON：{Path.Combine(minecraftPath, "versions", versionId, versionId + ".json")}");
            }

            // 处理 inheritsFrom：递归合并父版本
            if (!string.IsNullOrEmpty(info.InheritsFrom) &&
                !string.Equals(info.InheritsFrom, versionId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info($"版本 {versionId} 继承自 {info.InheritsFrom}，开始递归合并");
                var parentInfo = ReadAndMerge(minecraftPath, info.InheritsFrom!);

                // 合并 libraries：父版本在前，子版本在后
                var mergedLibs = new List<Library>();
                if (parentInfo.Libraries != null)
                    mergedLibs.AddRange(parentInfo.Libraries);
                if (info.Libraries != null)
                    mergedLibs.AddRange(info.Libraries);
                info.Libraries = mergedLibs;

                // 合并 arguments：父版本在前，子版本在后
                if (parentInfo.Arguments != null)
                {
                    info.Arguments ??= new VersionArguments();
                    info.Arguments.Jvm = MergeArgumentLists(parentInfo.Arguments.Jvm, info.Arguments.Jvm);
                    info.Arguments.Game = MergeArgumentLists(parentInfo.Arguments.Game, info.Arguments.Game);
                }

                // 子版本未提供的字段从父版本继承
                info.MainClass ??= parentInfo.MainClass;
                info.AssetIndex ??= parentInfo.AssetIndex;
                info.Assets ??= parentInfo.Assets;
                info.Logging ??= parentInfo.Logging;
                info.Downloads ??= parentInfo.Downloads;
                info.MinecraftArguments ??= parentInfo.MinecraftArguments;
                info.Type ??= parentInfo.Type;
            }

            return info;
        }

        /// <summary>合并两个 ArgumentItem 列表：父版本在前，子版本在后</summary>
        private static List<ArgumentItem>? MergeArgumentLists(List<ArgumentItem>? parent, List<ArgumentItem>? child)
        {
            if (parent == null || parent.Count == 0) return child;
            if (child == null || child.Count == 0) return parent;
            var merged = new List<ArgumentItem>(parent);
            merged.AddRange(child);
            return merged;
        }

        /// <summary>读取版本 JSON 文件并反序列化为 VersionInfo</summary>
        private VersionInfo? ReadVersionJson(string minecraftPath, string versionId)
        {
            var jsonPath = Path.Combine(minecraftPath, "versions", versionId, versionId + ".json");
            if (!File.Exists(jsonPath))
            {
                Logger.Warn($"版本 JSON 不存在：{jsonPath}");
                return null;
            }

            try
            {
                var json = File.ReadAllText(jsonPath);
                var info = JsonSerializer.Deserialize<VersionInfo>(json, JsonOptions);
                if (info == null)
                {
                    Logger.Warn($"版本 JSON 反序列化结果为 null：{jsonPath}");
                }
                return info;
            }
            catch (Exception ex)
            {
                Logger.Error($"读取版本 JSON 失败：{jsonPath}", ex);
                throw;
            }
        }

        /// <summary>
        /// 解析所有库文件路径。
        /// 遍历 libraries 列表：
        /// - 用 rules 过滤掉不适用当前平台的库
        /// - 对纯 Java 库：从 name 推路径，加入 classpath
        /// - 对 natives 库：取 windows 对应 classifier 的路径，加入 natives 列表
        /// </summary>
        private void ResolveLibraries(string minecraftPath, List<Library>? libraries, ResolvedVersion resolved)
        {
            if (libraries == null) return;

            var librariesDir = Path.Combine(minecraftPath, "libraries");

            foreach (var lib in libraries)
            {
                // 1. 检查 rules：当前平台是否适用
                if (!IsLibraryAllowed(lib))
                {
                    Logger.Debug($"库 {lib.Name} 被规则过滤（不适用当前平台）");
                    continue;
                }

                // 2. 处理 natives 库
                if (lib.Natives != null && lib.Natives.Count > 0)
                {
                    var nativePath = GetNativeFilePath(minecraftPath, lib);
                    if (!string.IsNullOrEmpty(nativePath))
                    {
                        resolved.NativeFiles.Add(nativePath);
                    }
                    else
                    {
                        Logger.Warn($"natives 库 {lib.Name} 找不到对应的 classifier 文件路径");
                    }
                    // natives 库本身不再加入 classpath（解压后只放 .dll 等本地库）
                    // 但有的实现会把 natives 库 jar 也加入 classpath，这里按官方启动器逻辑不加
                    continue;
                }

                // 3. 处理普通 Java 库：从 name 推路径，或从 downloads.artifact.path 取
                var jarPath = GetLibraryJarPath(librariesDir, lib);
                if (!string.IsNullOrEmpty(jarPath))
                {
                    resolved.ClasspathFiles.Add(jarPath);
                }
                else
                {
                    Logger.Warn($"库 {lib.Name} 无法解析 jar 路径");
                }
            }
        }

        /// <summary>
        /// 判断库是否适用于当前平台（Windows / x64）。
        /// 规则评估（与 Minecraft 官方启动器一致）：
        /// - 无 rules → 允许
        /// - 有 allow 规则：默认不允许，只有匹配 allow 的 OS 才允许
        /// - 有 disallow 规则：匹配 disallow 的 OS 不允许，其他保持当前状态
        /// - 综合判断：如果存在 allow 规则，初始默认为不允许；否则默认允许
        ///   然后逐条应用规则（后匹配的覆盖先匹配的）
        /// 例子：
        ///   [{allow windows}] → 仅 Windows 允许
        ///   [{disallow linux}] → 除 Linux 外都允许（Windows 也允许）
        ///   [{allow windows}, {disallow windows 10}] → Windows 但非 Win10 允许
        /// </summary>
        private bool IsLibraryAllowed(Library lib)
        {
            if (lib.Rules == null || lib.Rules.Count == 0)
                return true;

            // 检查是否存在 allow 规则
            bool hasAllowRule = lib.Rules.Any(r => (r.Action ?? "allow") == "allow");

            // 如果有 allow 规则，默认不允许（只有匹配 allow 才允许）
            // 如果只有 disallow 规则，默认允许（除非匹配 disallow）
            bool allowed = !hasAllowRule;

            foreach (var rule in lib.Rules)
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

        /// <summary>
        /// 判断当前系统是否匹配规则中的 OS 限定条件。
        /// 我们只关心 Windows，且简化处理：
        /// - name == "windows" → 匹配
        /// - name == "linux" / "osx" → 不匹配
        /// - name 为空 → 视为匹配（不限定 OS）
        /// arch 字段：x86 / x64，我们假定运行在 x64 上
        /// </summary>
        private bool IsOsMatch(RuleOs? os)
        {
            if (os == null || string.IsNullOrEmpty(os.Name))
                return true; // 不限定 OS 视为匹配

            // 仅当 name 是 windows 时匹配
            if (!string.Equals(os.Name, "windows", StringComparison.OrdinalIgnoreCase))
                return false;

            // 检查架构：x64 → 匹配；x86 → 不匹配（我们的目标平台是 x64）
            if (!string.IsNullOrEmpty(os.Arch))
            {
                if (string.Equals(os.Arch, "x86", StringComparison.OrdinalIgnoreCase))
                    return false; // 不在 x86 上加载
                // 其他值（x64 等）视为匹配
            }

            // version 字段是正则表达式，匹配 OS 版本字符串。
            // 这里简化处理，不做精确匹配（实际场景里基本不影响）。
            return true;
        }

        /// <summary>
        /// 获取 natives 库对应的本地库 zip 文件路径。
        /// 从 lib.Natives["windows"] 取 classifier 后缀（可能含 ${arch} 占位符），
        /// 再从 lib.Downloads.Classifiers[classifier] 取文件路径。
        /// </summary>
        private string GetNativeFilePath(string minecraftPath, Library lib)
        {
            if (lib.Natives == null || !lib.Natives.TryGetValue("windows", out var classifier))
            {
                Logger.Debug($"natives 库 {lib.Name} 不含 windows 映射");
                return string.Empty;
            }

            // classifier 可能含 ${arch} 占位符（旧版本如 1.12-），替换为 64
            classifier = classifier.Replace("${arch}", "64");

            // 优先从 downloads.classifiers 取 path
            if (lib.Downloads?.Classifiers != null &&
                lib.Downloads.Classifiers.TryGetValue(classifier, out var artifact) &&
                !string.IsNullOrEmpty(artifact.Path))
            {
                return Path.Combine(minecraftPath, "libraries", artifact.Path.Replace('/', Path.DirectorySeparatorChar));
            }

            // fallback：用 name 推路径
            var pathFromName = GetPathFromName(lib.Name, classifier);
            if (!string.IsNullOrEmpty(pathFromName))
            {
                return Path.Combine(minecraftPath, "libraries", pathFromName);
            }

            return string.Empty;
        }

        /// <summary>
        /// 获取普通 Java 库的 jar 文件路径。
        /// 优先从 downloads.artifact.path 取；没有则用 name 推路径。
        /// </summary>
        private string GetLibraryJarPath(string librariesDir, Library lib)
        {
            // 优先从 downloads.artifact.path 取
            if (lib.Downloads?.Artifact != null && !string.IsNullOrEmpty(lib.Downloads.Artifact.Path))
            {
                var p = lib.Downloads.Artifact.Path.Replace('/', Path.DirectorySeparatorChar);
                return Path.Combine(librariesDir, p);
            }

            // fallback：用 name 推路径
            var pathFromName = GetPathFromName(lib.Name, null);
            if (!string.IsNullOrEmpty(pathFromName))
            {
                return Path.Combine(librariesDir, pathFromName);
            }

            return string.Empty;
        }

        /// <summary>
        /// 根据 Maven 坐标名（group:artifact:version）推出文件相对路径：
        /// group/替换斜杠 / artifact / version / artifact-version[-classifier].jar
        /// </summary>
        /// <param name="name">Maven 坐标，如 "com.mojang:minecraft:1.20.4"</param>
        /// <param name="classifier">可选 classifier，如 "natives-windows"</param>
        /// <returns>相对路径，如 "com/mojang/minecraft/1.20.4/minecraft-1.20.4.jar"</returns>
        private string? GetPathFromName(string? name, string? classifier)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var parts = name.Split(':');
            if (parts.Length < 3) return null;

            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var version = parts[2];

            var fileName = $"{artifact}-{version}";
            if (!string.IsNullOrEmpty(classifier))
                fileName += "-" + classifier;
            fileName += ".jar";

            return $"{group}/{artifact}/{version}/{fileName}";
        }
    }
}
