using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using YCL.Core.Utils;

namespace YCL.Core.Mods
{
    /// <summary>
    /// 本地模组管理服务实现。
    ///
    /// 核心流程：
    /// 1. ListMods：扫描 mods 目录下 .jar / .disabled 文件，用 ZipArchive 打开 jar，
    ///    依次尝试解析 fabric.mod.json → META-INF/mods.toml → META-INF/mcmod.info，
    ///    提取关键元数据（id/name/version/description/authors/icon），并把图标释放到临时目录。
    /// 2. ToggleMod：通过重命名 .jar ↔ .disabled 切换启用状态。
    /// 3. DeleteMod：直接删除文件。
    /// 4. OpenModsFolder：用 explorer.exe 打开 mods 文件夹。
    ///
    /// 注意：单个 jar 解析失败只记日志，不影响其他模组的扫描。
    /// TOML 解析采用简单字符串处理（不引入新 NuGet 包），只提取关键字段。
    /// </summary>
    public class LocalModManager : ILocalModManager
    {
        /// <summary>存放从 jar 中提取出的图标的临时目录（%LocalAppData%\YCL\mod_icons）</summary>
        private static readonly string IconCacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YCL", "mod_icons");

        /// <inheritdoc/>
        public List<ModInfo> ListMods(string gameDir)
        {
            var result = new List<ModInfo>();
            if (string.IsNullOrWhiteSpace(gameDir))
            {
                Logger.Warn("扫描模组失败：gameDir 为空");
                return result;
            }

            var modsDir = GetModsDirectory(gameDir);
            if (!Directory.Exists(modsDir))
            {
                Logger.Info($"mods 目录不存在：{modsDir}（尚未安装任何模组）");
                return result;
            }

            // 收集所有 .jar 和 .disabled 文件
            var files = new List<string>();
            files.AddRange(Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly));
            files.AddRange(Directory.GetFiles(modsDir, "*.disabled", SearchOption.TopDirectoryOnly));

            // 同时也支持 .jar.disabled 这种组合扩展名（部分启动器用这种命名）
            files.AddRange(Directory.GetFiles(modsDir, "*.jar.disabled", SearchOption.TopDirectoryOnly));

            // 去重（.jar.disabled 在 *.jar 和 *.disabled 中都会匹配到）
            var distinctFiles = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

            foreach (var file in distinctFiles)
            {
                try
                {
                    var info = ParseModFile(file);
                    if (info != null)
                        result.Add(info);
                }
                catch (Exception ex)
                {
                    // 单个文件解析失败不影响其他
                    Logger.Warn($"解析模组文件失败：{file} - {ex.Message}");
                    // 仍然加入一个最简信息（仅文件名），让用户能看到这个文件
                    result.Add(CreateFallbackModInfo(file));
                }
            }

            // 按名称排序（启用的排前面）
            result.Sort((a, b) =>
            {
                if (a.Enabled != b.Enabled) return a.Enabled ? -1 : 1;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            Logger.Info($"扫描到 {result.Count} 个模组：{modsDir}");
            return result;
        }

        /// <inheritdoc/>
        public void ToggleMod(string modFilePath, bool enable)
        {
            if (string.IsNullOrWhiteSpace(modFilePath) || !File.Exists(modFilePath))
            {
                Logger.Warn($"切换模组状态失败：文件不存在 {modFilePath}");
                return;
            }

            // 计算目标文件名
            string targetPath;
            var currentName = Path.GetFileName(modFilePath);
            var isCurrentlyDisabled = currentName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);

            if (enable)
            {
                // 启用：去掉 .disabled 后缀
                if (!isCurrentlyDisabled)
                {
                    Logger.Info($"模组已启用，无需操作：{modFilePath}");
                    return;
                }
                // 去掉 .disabled 后缀（注意 .jar.disabled 也要正确处理）
                var newName = currentName.Substring(0, currentName.Length - ".disabled".Length);
                targetPath = Path.Combine(Path.GetDirectoryName(modFilePath)!, newName);
            }
            else
            {
                // 禁用：加 .disabled 后缀
                if (isCurrentlyDisabled)
                {
                    Logger.Info($"模组已禁用，无需操作：{modFilePath}");
                    return;
                }
                targetPath = modFilePath + ".disabled";
            }

            try
            {
                // 目标文件已存在时先删除（避免冲突）
                if (File.Exists(targetPath) && !string.Equals(modFilePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(targetPath);
                }
                File.Move(modFilePath, targetPath);
                Logger.Info($"模组状态切换：{Path.GetFileName(modFilePath)} → {Path.GetFileName(targetPath)}");
            }
            catch (Exception ex)
            {
                Logger.Error($"切换模组状态失败：{modFilePath}", ex);
                throw;
            }
        }

        /// <inheritdoc/>
        public void DeleteMod(string modFilePath)
        {
            if (string.IsNullOrWhiteSpace(modFilePath))
            {
                Logger.Warn("删除模组失败：路径为空");
                return;
            }
            if (!File.Exists(modFilePath))
            {
                Logger.Warn($"删除模组失败：文件不存在 {modFilePath}");
                return;
            }

            try
            {
                File.Delete(modFilePath);
                Logger.Info($"已删除模组文件：{modFilePath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"删除模组文件失败：{modFilePath}", ex);
                throw;
            }
        }

        /// <inheritdoc/>
        public void OpenModsFolder(string gameDir)
        {
            var modsDir = GetModsDirectory(gameDir);
            try
            {
                // 确保目录存在
                Directory.CreateDirectory(modsDir);

                // 用 explorer.exe 打开文件夹
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{modsDir}\"",
                    UseShellExecute = true
                });
                Logger.Info($"已打开 mods 文件夹：{modsDir}");
            }
            catch (Exception ex)
            {
                Logger.Error($"打开 mods 文件夹失败：{modsDir}", ex);
                throw;
            }
        }

        /// <inheritdoc/>
        public string GetModsDirectory(string gameDir)
        {
            if (string.IsNullOrWhiteSpace(gameDir))
                return string.Empty;
            return Path.Combine(gameDir, "mods");
        }

        /// <summary>
        /// 解析单个模组文件，提取元数据。
        /// 失败时返回 null（调用方会用 CreateFallbackModInfo 兜底）。
        /// </summary>
        private ModInfo? ParseModFile(string filePath)
        {
            var info = new ModInfo
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                // 根据扩展名判断启用状态
                Enabled = !filePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            };

            // .disabled 文件可能是 .jar.disabled，需要先打开看是不是 zip
            // 如果文件本身不是 zip（解析会抛异常），就只用文件名信息
            try
            {
                using var stream = File.OpenRead(filePath);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

                // 依次尝试三种元数据格式
                if (TryParseFabricModJson(zip, info))
                {
                    info.LoaderType = LoaderType.Fabric;
                }
                else if (TryParseModsToml(zip, info))
                {
                    info.LoaderType = LoaderType.Forge;
                }
                else if (TryParseMcmodInfo(zip, info))
                {
                    info.LoaderType = LoaderType.Forge;
                }
                else
                {
                    // 没有识别到任何元数据，但仍视为一个有效模组（可能是 NeoForge 等其他格式）
                    Logger.Debug($"未识别到模组元数据：{filePath}");
                }
            }
            catch (InvalidDataException)
            {
                // 不是 zip 文件（可能是损坏的或非 jar 文件）
                Logger.Debug($"文件不是有效的 zip/jar：{filePath}");
            }

            return info;
        }

        /// <summary>
        /// 解析 Fabric 模组的 fabric.mod.json。
        /// 字段：schemaVersion / id / name / version / description / authors / icon / depends
        /// </summary>
        private bool TryParseFabricModJson(ZipArchive zip, ModInfo info)
        {
            var entry = zip.GetEntry("fabric.mod.json");
            if (entry == null) return false;

            try
            {
                using var entryStream = entry.Open();
                using var doc = JsonDocument.Parse(entryStream);
                var root = doc.RootElement;

                info.ModId = GetString(root, "id") ?? info.ModId;
                info.Name = GetString(root, "name") ?? info.Name;
                info.Version = GetString(root, "version") ?? info.Version;
                info.Description = GetString(root, "description") ?? info.Description;

                // authors：可能是字符串数组，也可能是对象数组（含 name 字段）
                if (root.TryGetProperty("authors", out var authorsEl) && authorsEl.ValueKind == JsonValueKind.Array)
                {
                    info.Authors.Clear();
                    foreach (var a in authorsEl.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.String)
                        {
                            var s = a.GetString();
                            if (!string.IsNullOrEmpty(s)) info.Authors.Add(s);
                        }
                        else if (a.ValueKind == JsonValueKind.Object)
                        {
                            var name = GetString(a, "name");
                            if (!string.IsNullOrEmpty(name)) info.Authors.Add(name);
                        }
                    }
                }

                // depends.minecraft：可能为字符串或字符串数组
                if (root.TryGetProperty("depends", out var dependsEl) &&
                    dependsEl.TryGetProperty("minecraft", out var mcEl))
                {
                    info.MinecraftVersion = ExtractMinecraftVersion(mcEl);
                }

                // icon：可能是字符串路径，也可能是对象（含 64x64 等字段）
                string? iconEntryPath = null;
                if (root.TryGetProperty("icon", out var iconEl))
                {
                    if (iconEl.ValueKind == JsonValueKind.String)
                    {
                        iconEntryPath = iconEl.GetString();
                    }
                    else if (iconEl.ValueKind == JsonValueKind.Object)
                    {
                        // 取第一个字符串字段作为图标
                        foreach (var prop in iconEl.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.String)
                            {
                                iconEntryPath = prop.Value.GetString();
                                break;
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(iconEntryPath))
                {
                    info.LogoPath = ExtractIcon(zip, iconEntryPath, info.ModId);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"解析 fabric.mod.json 失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 解析 Forge 1.13+ 的 META-INF/mods.toml（TOML 格式）。
        /// 用简单字符串处理，只提取 mods[0] 段的关键字段。
        /// </summary>
        private bool TryParseModsToml(ZipArchive zip, ModInfo info)
        {
            var entry = zip.GetEntry("META-INF/mods.toml");
            if (entry == null) return false;

            try
            {
                using var entryStream = entry.Open();
                using var reader = new StreamReader(entryStream);
                var content = reader.ReadToEnd();

                // 解析 [[mods]] 段中的字段
                // 简单处理：用正则匹配 modId / displayName / version / description / logoFile
                info.ModId = TryGetTomlValue(content, "modId") ?? info.ModId;
                info.Name = TryGetTomlValue(content, "displayName") ?? info.Name;
                info.Version = TryGetTomlValue(content, "version") ?? info.Version;
                info.Description = TryGetTomlValue(content, "description") ?? info.Description;

                var logoFile = TryGetTomlValue(content, "logoFile");
                if (!string.IsNullOrEmpty(logoFile))
                {
                    info.LogoPath = ExtractIcon(zip, logoFile, info.ModId);
                }

                // modLoader 字段标识加载器类型（如 "javafml" 表示 Forge，"neofml" 表示 NeoForge）
                var modLoader = TryGetTomlValue(content, "modLoader");
                if (!string.IsNullOrEmpty(modLoader))
                {
                    if (modLoader.Contains("neo", StringComparison.OrdinalIgnoreCase))
                        info.LoaderType = LoaderType.NeoForge;
                    else
                        info.LoaderType = LoaderType.Forge;
                }

                return !string.IsNullOrEmpty(info.ModId) || !string.IsNullOrEmpty(info.Name);
            }
            catch (Exception ex)
            {
                Logger.Debug($"解析 mods.toml 失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 解析 Forge 1.12- 的 META-INF/mcmod.info（JSON 数组格式）。
        /// 结构：{"modList": [{"modid": "...", "name": "...", "version": "...", "description": "...", "authorList": [...]}]}
        /// </summary>
        private bool TryParseMcmodInfo(ZipArchive zip, ModInfo info)
        {
            var entry = zip.GetEntry("META-INF/mcmod.info");
            if (entry == null) return false;

            try
            {
                using var entryStream = entry.Open();
                using var doc = JsonDocument.Parse(entryStream);
                var root = doc.RootElement;

                // mcmod.info 顶层可能是数组也可能是对象含 modList 数组
                JsonElement modListEl;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    modListEl = root;
                }
                else if (root.TryGetProperty("modList", out var mlEl) && mlEl.ValueKind == JsonValueKind.Array)
                {
                    modListEl = mlEl;
                }
                else
                {
                    return false;
                }

                // 取第一个对象
                foreach (var item in modListEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    info.ModId = GetString(item, "modid") ?? info.ModId;
                    info.Name = GetString(item, "name") ?? info.Name;
                    info.Version = GetString(item, "version") ?? info.Version;
                    info.Description = GetString(item, "description") ?? info.Description;

                    // authorList 是字符串数组
                    if (item.TryGetProperty("authorList", out var authorsEl) && authorsEl.ValueKind == JsonValueKind.Array)
                    {
                        info.Authors.Clear();
                        foreach (var a in authorsEl.EnumerateArray())
                        {
                            if (a.ValueKind == JsonValueKind.String)
                            {
                                var s = a.GetString();
                                if (!string.IsNullOrEmpty(s)) info.Authors.Add(s);
                            }
                        }
                    }

                    // mcversion 字段
                    info.MinecraftVersion = GetString(item, "mcversion") ?? info.MinecraftVersion;

                    // logoFile 字段
                    var logoFile = GetString(item, "logoFile");
                    if (!string.IsNullOrEmpty(logoFile))
                    {
                        info.LogoPath = ExtractIcon(zip, logoFile, info.ModId);
                    }

                    break; // 只取第一个
                }

                return !string.IsNullOrEmpty(info.ModId) || !string.IsNullOrEmpty(info.Name);
            }
            catch (Exception ex)
            {
                Logger.Debug($"解析 mcmod.info 失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从 jar 中提取图标文件到临时缓存目录。
        /// 返回释放后的本地文件路径，失败返回 null。
        /// </summary>
        private string? ExtractIcon(ZipArchive zip, string iconEntryPath, string modId)
        {
            if (string.IsNullOrWhiteSpace(iconEntryPath)) return null;

            // 路径可能是相对于 jar 根目录，也可能以 / 开头
            var normalizedPath = iconEntryPath.TrimStart('/');
            var entry = zip.GetEntry(normalizedPath) ?? zip.GetEntry(iconEntryPath);
            if (entry == null) return null;

            try
            {
                Directory.CreateDirectory(IconCacheDirectory);

                // 用 modId + 原文件名作为缓存文件名，避免冲突
                var safeModId = string.IsNullOrWhiteSpace(modId) ? "unknown" : SanitizeFileName(modId);
                var ext = Path.GetExtension(iconEntryPath);
                if (string.IsNullOrEmpty(ext)) ext = ".png";
                var cachePath = Path.Combine(IconCacheDirectory, $"{safeModId}{ext}");

                using var entryStream = entry.Open();
                using var fileStream = File.Create(cachePath);
                entryStream.CopyTo(fileStream);

                return cachePath;
            }
            catch (Exception ex)
            {
                Logger.Debug($"提取图标失败：{iconEntryPath} - {ex.Message}");
                return null;
            }
        }

        /// <summary>当 jar 解析完全失败时，仅用文件名构造最简模组信息</summary>
        private static ModInfo CreateFallbackModInfo(string filePath)
        {
            return new ModInfo
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                Name = Path.GetFileNameWithoutExtension(filePath),
                Enabled = !filePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
                LoaderType = LoaderType.Unknown,
                Description = "（无法解析模组元数据）"
            };
        }

        /// <summary>从 JsonElement 安全读取字符串字段</summary>
        private static string? GetString(JsonElement parent, string name)
        {
            if (parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
            return null;
        }

        /// <summary>
        /// 从 depends.minecraft 字段提取 Minecraft 版本。
        /// 该字段可能是字符串（如 "1.20.4"）、版本范围（如 ">=1.20"）或字符串数组。
        /// </summary>
        private static string ExtractMinecraftVersion(JsonElement mcEl)
        {
            if (mcEl.ValueKind == JsonValueKind.String)
            {
                var s = mcEl.GetString() ?? string.Empty;
                // 去除版本范围符号（>=、<=、>、<、=）
                return Regex.Replace(s, @"[<>=]", "").Trim();
            }
            if (mcEl.ValueKind == JsonValueKind.Array)
            {
                // 取数组中第一个元素
                foreach (var item in mcEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrEmpty(s))
                            return Regex.Replace(s, @"[<>=]", "").Trim();
                    }
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// 从 TOML 文本中提取简单键值对的值。
        /// 仅匹配 `key = "value"` 形式（忽略 [[mods]] / [deps] 等表头）。
        /// 对于 [[mods]] 段内的字段足够用。
        /// </summary>
        private static string? TryGetTomlValue(string content, string key)
        {
            // 匹配 key = "value"（值用双引号包围）
            var pattern = $@"^\s*{Regex.Escape(key)}\s*=\s*""([^""]*)""\s*$";
            var match = Regex.Match(content, pattern, RegexOptions.Multiline);
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }
            return null;
        }

        /// <summary>清理文件名中的非法字符</summary>
        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var result = name;
            foreach (var c in invalid)
                result = result.Replace(c, '_');
            return result;
        }
    }
}
