using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using YCL.Core.Utils;

namespace YCL.Core.Java
{
    /// <summary>
    /// Java 检测器实现：扫描系统已安装的 Java 运行时。
    ///
    /// 扫描来源：
    /// 1. 注册表 HKLM\SOFTWARE\JavaSoft\Java Development Kit / Java Runtime Environment（含 Wow6432Node 32 位视图）
    /// 2. 常见安装路径（Program Files\Java、Eclipse Adoptium、Microsoft\jdk-*、Zulu、%USERPROFILE%\.jdks）
    /// 3. .minecraft/runtime（Mojang 自带的运行时）
    /// 4. PATH 环境变量中的 java/javaw
    ///
    /// 找到 javaw.exe 后，运行 `javaw.exe -version` 解析版本号、是否 JDK、架构。
    /// 旧版 Java（1.8 及以下）版本信息输出到 stderr，新版（9+）输出到 stdout，这里两者都读。
    /// </summary>
    public class JavaDetector : IJavaDetector
    {
        /// <summary>.minecraft 路径提供者（用于扫描 runtime 目录，为空则跳过）</summary>
        private readonly Func<string?> _minecraftPathProvider;

        /// <summary>
        /// 构造 Java 检测器。
        /// </summary>
        /// <param name="minecraftPathProvider">返回当前 .minecraft 路径的委托（用于扫描 Mojang 自带运行时；可为空）</param>
        public JavaDetector(Func<string?>? minecraftPathProvider = null)
        {
            _minecraftPathProvider = minecraftPathProvider ?? (() => null);
        }

        /// <inheritdoc/>
        public async Task<List<JavaInfo>> DetectAsync(CancellationToken cancellationToken = default)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 扫描注册表
            foreach (var p in ScanRegistry())
                candidates.Add(p);

            // 2. 扫描常见安装路径
            foreach (var p in ScanCommonPaths())
                candidates.Add(p);

            // 3. 扫描 .minecraft/runtime
            try
            {
                var mcPath = _minecraftPathProvider();
                if (!string.IsNullOrEmpty(mcPath))
                {
                    foreach (var p in ScanMinecraftRuntime(mcPath))
                        candidates.Add(p);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("扫描 .minecraft/runtime 失败：" + ex.Message);
            }

            // 4. 扫描 PATH
            foreach (var p in ScanPath())
                candidates.Add(p);

            // 5. 对每个 javaw.exe 解析版本信息
            var results = new List<JavaInfo>();
            foreach (var javaPath in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = await ProbeAsync(javaPath, cancellationToken);
                    if (info != null)
                        results.Add(info);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"探测 Java 失败：{javaPath} - {ex.Message}");
                }
            }

            // 按主版本号降序排序（Java 21 在前，8 在后）
            results.Sort((a, b) => b.Version.CompareTo(a.Version));

            Logger.Info($"Java 检测完成，共发现 {results.Count} 个 Java 运行时");
            return results;
        }

        /// <summary>
        /// 扫描注册表 HKLM\SOFTWARE\JavaSoft 节点。
        /// 同时读取 64 位视图与 32 位 Wow6432Node 视图。
        /// </summary>
        private static IEnumerable<string> ScanRegistry()
        {
            if (!OperatingSystem.IsWindows()) yield break;

            // Java 注册表根节点路径
            var roots = new[]
            {
                @"SOFTWARE\JavaSoft\Java Development Kit",
                @"SOFTWARE\JavaSoft\Java Runtime Environment",
                @"SOFTWARE\JavaSoft\JDK",          // 新版 Adoptium/Temurin 用这个节点
                @"SOFTWARE\JavaSoft\JRE"
            };

            // 两个视图：64 位与 32 位（Wow6432Node）
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

            foreach (var view in views)
            {
                foreach (var root in roots)
                {
                    IEnumerable<string> paths;
                    try
                    {
                        paths = EnumerateRegistryJava(root, view);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"读取注册表失败：{root} ({view}) - {ex.Message}");
                        continue;
                    }

                    foreach (var p in paths)
                        yield return p;
                }
            }
        }

        /// <summary>枚举注册表中某个 JavaSoft 子节点下的所有 JavaHome</summary>
        private static IEnumerable<string> EnumerateRegistryJava(string subKey, RegistryView view)
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(subKey);
            if (key == null) yield break;

            // 直接子节点可能是版本号（如 "17.0.1"），其下有 JavaHome 值
            foreach (var versionName in key.GetSubKeyNames())
            {
                using var versionKey = key.OpenSubKey(versionName);
                if (versionKey == null) continue;

                var home = versionKey.GetValue("JavaHome") as string;
                if (string.IsNullOrEmpty(home)) continue;

                var javaPath = Path.Combine(home, "bin", "javaw.exe");
                if (File.Exists(javaPath))
                    yield return javaPath;
            }
        }

        /// <summary>
        /// 扫描常见 Java 安装路径。
        /// 包括：Program Files\Java、Eclipse Adoptium、Microsoft\jdk-*、Zulu、IntelliJ .jdks。
        /// </summary>
        private static IEnumerable<string> ScanCommonPaths()
        {
            var searchRoots = new List<string>();

            // Program Files
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf))
            {
                searchRoots.Add(Path.Combine(pf, "Java"));
                searchRoots.Add(Path.Combine(pf, "Eclipse Adoptium"));
                searchRoots.Add(Path.Combine(pf, "Eclipse Foundation"));
                searchRoots.Add(Path.Combine(pf, "Microsoft"));
                searchRoots.Add(Path.Combine(pf, "Zulu"));
                searchRoots.Add(Path.Combine(pf, "Amazon Corretto"));
                searchRoots.Add(Path.Combine(pf, "BellSoft"));
                searchRoots.Add(Path.Combine(pf, "Java"));
            }
            if (!string.IsNullOrEmpty(pf86))
            {
                searchRoots.Add(Path.Combine(pf86, "Java"));
                searchRoots.Add(Path.Combine(pf86, "Eclipse Adoptium"));
            }

            // IntelliJ IDEA 装的 JDK：~/.jdks
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                searchRoots.Add(Path.Combine(userProfile, ".jdks"));
            }

            // YCL 自己装的 Java：%AppData%\YCL\java
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                searchRoots.Add(Path.Combine(appData, "YCL", "java"));
            }

            foreach (var root in searchRoots)
            {
                if (!Directory.Exists(root)) continue;

                string[] subDirs;
                try { subDirs = Directory.GetDirectories(root); }
                catch { continue; }

                foreach (var sub in subDirs)
                {
                    // 直接 bin/javaw.exe
                    var javaPath = Path.Combine(sub, "bin", "javaw.exe");
                    if (File.Exists(javaPath))
                        yield return javaPath;

                    // Microsoft 的目录形如 jdk-17.0.1+5，bin 直接在下面
                    // 已被上面覆盖，这里无需重复

                    // 某些打包方式可能是子目录下还有一层
                    string[] innerDirs;
                    try { innerDirs = Directory.GetDirectories(sub); }
                    catch { innerDirs = Array.Empty<string>(); }

                    foreach (var inner in innerDirs)
                    {
                        var innerJava = Path.Combine(inner, "bin", "javaw.exe");
                        if (File.Exists(innerJava))
                            yield return innerJava;
                    }
                }
            }
        }

        /// <summary>
        /// 扫描 .minecraft/runtime 目录下的 Mojang 自带运行时。
        /// 目录结构通常为：
        ///   .minecraft/runtime/jre-legacy/windows-x64/jre-legacy/bin/javaw.exe
        ///   .minecraft/runtime/java-runtime-gamma/windows-x64/java-runtime-gamma/bin/javaw.exe
        ///   .minecraft/runtime/java-runtime-beta/windows-x64/java-runtime-beta/bin/javaw.exe
        /// 为兼容各种变体，这里在 runtime 目录下递归查找所有 javaw.exe。
        /// </summary>
        private static IEnumerable<string> ScanMinecraftRuntime(string minecraftPath)
        {
            var runtimeDir = Path.Combine(minecraftPath, "runtime");
            if (!Directory.Exists(runtimeDir)) yield break;

            // 递归查找 javaw.exe（深度限制在 5 层）
            foreach (var p in EnumerateFilesSafe(runtimeDir, "javaw.exe", maxDepth: 5))
                yield return p;

            // 也找一下 java.exe（有些打包没有 javaw.exe，但启动器需要 javaw）
            // 这里只收集，后续 ProbeAsync 会校验
        }

        /// <summary>安全递归枚举文件，遇到无权限目录跳过</summary>
        private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern, int maxDepth)
        {
            if (maxDepth < 0) yield break;

            string[] files;
            try { files = Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly); }
            catch { files = Array.Empty<string>(); }

            foreach (var f in files) yield return f;

            if (maxDepth == 0) yield break;

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly); }
            catch { subDirs = Array.Empty<string>(); }

            foreach (var sub in subDirs)
            {
                foreach (var f in EnumerateFilesSafe(sub, pattern, maxDepth - 1))
                    yield return f;
            }
        }

        /// <summary>
        /// 扫描 PATH 环境变量中的 java/javaw。
        /// 用 Where/where 命令查找，最简单的方法是遍历 PATH 目录。
        /// </summary>
        private static IEnumerable<string> ScanPath()
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) yield break;

            var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in dirs)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;

                string trimmed;
                try { trimmed = dir.Trim().Trim('"'); }
                catch { continue; }

                if (!Directory.Exists(trimmed)) continue;

                var javaPath = Path.Combine(trimmed, "javaw.exe");
                if (File.Exists(javaPath))
                    yield return javaPath;
            }
        }

        /// <summary>
        /// 对一个 javaw.exe 运行 -version 解析版本信息。
        /// 旧版 Java（8 及以下）输出去 stderr，新版（9+）去 stdout，这里两者都读。
        /// 输出示例（Java 8，stderr）：
        ///   java version "1.8.0_321"
        ///   Java(TM) SE Runtime Environment (build 1.8.0_321-b07)
        ///   Java HotSpot(TM) 64-Bit Server VM (build 25.321-b07, mixed mode)
        /// 输出示例（Java 17，stdout）：
        ///   openjdk version "17.0.1" 2021-10-19
        ///   OpenJDK Runtime Environment (build 17.0.1+12-39)
        ///   OpenJDK 64-Bit Server VM (build 17.0.1+12-39, mixed mode, sharing)
        /// </summary>
        private static async Task<JavaInfo?> ProbeAsync(string javaPath, CancellationToken ct)
        {
            if (!File.Exists(javaPath)) return null;

            // 用 java -version 而不是 javaw -version（javaw 是无窗口版本，理论上输出相同，但 java 更通用）
            // 实际上 javaw.exe -version 也能用，这里用 java.exe 如果在同一目录存在的话，否则用 javaw
            var exePath = javaPath;
            var javaExe = Path.Combine(Path.GetDirectoryName(javaPath)!, "java.exe");
            if (File.Exists(javaExe))
                exePath = javaExe;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = new Process { StartInfo = psi };

            if (!proc.Start()) return null;

            // 同时读 stdout 和 stderr，避免缓冲区满导致死锁
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            // 等待进程退出（带超时保护，避免某些卡死的 java）
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                Logger.Warn($"Java -version 超时：{javaPath}");
                return null;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var output = (stderr ?? "") + "\n" + (stdout ?? "");

            var info = ParseVersionOutput(javaPath, output);
            if (info == null)
            {
                // 解析失败，至少记录路径（架构未知）
                Logger.Warn($"无法解析 Java 版本输出：{javaPath}");
                return new JavaInfo
                {
                    Path = javaPath,
                    Version = 0,
                    VersionString = "未知",
                    IsJdk = true, // 假设是 JDK
                    Architecture = DetectArchitectureByPath(javaPath)
                };
            }

            return info;
        }

        /// <summary>
        /// 解析 `java -version` 的输出，提取版本号、是否 JDK、架构。
        /// </summary>
        private static JavaInfo? ParseVersionOutput(string javaPath, string output)
        {
            if (string.IsNullOrEmpty(output)) return null;

            // 匹配版本号行：
            //   java version "1.8.0_321"
            //   openjdk version "17.0.1" 2021-10-19
            //   openjdk version "21" 2023-09-19
            var versionMatch = Regex.Match(
                output,
                @"(?:java|openjdk)\s+version\s+""([^""]+)""",
                RegexOptions.IgnoreCase);

            if (!versionMatch.Success)
                return null;

            var versionString = versionMatch.Groups[1].Value;
            var majorVersion = ParseMajorVersion(versionString);

            // 是否 JDK：输出含 "Server VM" 或 "HotSpot" 通常都是 JDK/JRE，
            // 但要区分 JDK 与 JRE 比较困难。简单规则：
            // - 输出含 "Development" 或 "JDK" 视为 JDK
            // - 输出含 "Runtime Environment" 但不含 "Server VM" 视为 JRE
            // - 默认视为 JDK（多数现代 Java 都是 JDK）
            bool isJdk = true;
            if (output.Contains("Runtime Environment", StringComparison.OrdinalIgnoreCase) &&
                !output.Contains("Server VM", StringComparison.OrdinalIgnoreCase) &&
                !output.Contains("Client VM", StringComparison.OrdinalIgnoreCase))
            {
                isJdk = false;
            }
            if (output.Contains("Java(TM) SE Runtime Environment", StringComparison.OrdinalIgnoreCase))
            {
                // Oracle 旧版 JRE 的特征
                isJdk = !output.Contains("Java HotSpot(TM) Server VM", StringComparison.OrdinalIgnoreCase)
                        || output.Contains("Development", StringComparison.OrdinalIgnoreCase);
            }

            return new JavaInfo
            {
                Path = javaPath,
                Version = majorVersion,
                VersionString = versionString,
                IsJdk = isJdk,
                Architecture = DetectArchitecture(output, javaPath)
            };
        }

        /// <summary>
        /// 从完整版本字符串解析主版本号。
        /// 1.8.0_321 → 8
        /// 17.0.1 → 17
        /// 21 → 21
        /// </summary>
        private static int ParseMajorVersion(string versionString)
        {
            if (string.IsNullOrEmpty(versionString)) return 0;

            // 1.8.0_321 这种旧版格式
            if (versionString.StartsWith("1."))
            {
                var dotIndex = versionString.IndexOf('.', 2);
                var minorStr = dotIndex > 0
                    ? versionString.Substring(2, dotIndex - 2)
                    : versionString.Substring(2);
                return int.TryParse(minorStr, out var minor) ? minor : 0;
            }

            // 17.0.1 / 21 / 21.0.1+12 等
            var firstDot = versionString.IndexOf('.');
            var firstPlus = versionString.IndexOf('+');
            var end = versionString.Length;
            if (firstDot > 0 && firstDot < end) end = firstDot;
            if (firstPlus > 0 && firstPlus < end) end = firstPlus;

            var majorStr = versionString.Substring(0, end);
            return int.TryParse(majorStr, out var major) ? major : 0;
        }

        /// <summary>
        /// 检测 Java 架构：从输出文本或路径推断。
        /// 输出含 "64-Bit" → x64
        /// 输出含 "32-Bit" 或 "Client VM" 通常是 x86
        /// 路径含 "Program Files (x86)" 或 "(x86)" → x86
        /// </summary>
        private static string DetectArchitecture(string output, string javaPath)
        {
            if (output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("64-bit", StringComparison.OrdinalIgnoreCase))
            {
                // 区分 arm64 与 x64
                if (output.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("arm64", StringComparison.OrdinalIgnoreCase))
                    return "arm64";
                return "x64";
            }

            if (output.Contains("32-Bit", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("32-bit", StringComparison.OrdinalIgnoreCase))
                return "x86";

            // 从路径推断
            return DetectArchitectureByPath(javaPath);
        }

        /// <summary>仅从路径推断架构</summary>
        private static string DetectArchitectureByPath(string javaPath)
        {
            if (javaPath.Contains("(x86)", StringComparison.OrdinalIgnoreCase) ||
                javaPath.Contains("\\x86\\", StringComparison.OrdinalIgnoreCase) ||
                javaPath.Contains("/x86/", StringComparison.OrdinalIgnoreCase))
                return "x86";

            if (javaPath.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ||
                javaPath.Contains("arm64", StringComparison.OrdinalIgnoreCase))
                return "arm64";

            // 默认假设 x64（现代 Java 大多是 64 位）
            return "x64";
        }
    }
}
