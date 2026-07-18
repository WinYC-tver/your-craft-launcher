using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using YCL.Core.Utils;

namespace YCL.Core.Download
{
    /// <summary>
    /// 文件完整性校验器：计算文件的 SHA1 哈希并比对期望值。
    /// Minecraft 几乎所有文件（库、客户端 jar、assets、版本 JSON 等）
    /// 都在版本清单中提供了 SHA1 校验值，下载完成后必须校验以确保文件没损坏。
    /// </summary>
    public static class FileValidator
    {
        /// <summary>校验用的缓冲区大小（64KB，平衡内存与性能）</summary>
        private const int BufferSize = 64 * 1024;

        /// <summary>
        /// 计算指定文件的 SHA1 哈希值（返回小写十六进制字符串）。
        /// 文件不存在时抛出 <see cref="FileNotFoundException"/>。
        /// </summary>
        /// <param name="filePath">文件完整路径</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>40 字符的小写十六进制 SHA1 字符串</returns>
        public static async Task<string> ComputeSha1Async(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"待校验的文件不存在：{filePath}", filePath);

            // SHA1 是 IDisposable，用 using 确保释放
            using var sha1 = SHA1.Create();

            await using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, useAsync: true);

            // 异步计算整个流的哈希（内部会分块读取，不会一次性把大文件加载到内存）
            var hashBytes = await sha1.ComputeHashAsync(stream, cancellationToken);

            // 转换为小写十六进制字符串
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// 校验文件的 SHA1 是否匹配期望值。
        /// 如果 expectedSha1 为空或 null，视为不需要校验，直接返回 true（校验通过）。
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="expectedSha1">期望的 SHA1（小写十六进制，40 字符）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>true = 校验通过或无需校验；false = 校验失败</returns>
        public static async Task<bool> ValidateAsync(string filePath, string? expectedSha1, CancellationToken cancellationToken = default)
        {
            // 没有提供期望值，跳过校验
            if (string.IsNullOrWhiteSpace(expectedSha1))
                return true;

            // 文件不存在视为校验失败
            if (!File.Exists(filePath))
            {
                Logger.Warn($"校验失败：文件不存在 - {filePath}");
                return false;
            }

            var actualSha1 = await ComputeSha1Async(filePath, cancellationToken);
            var expected = expectedSha1!.Trim().ToLowerInvariant();

            if (string.Equals(actualSha1, expected, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Debug($"SHA1 校验通过：{Path.GetFileName(filePath)}");
                return true;
            }
            else
            {
                Logger.Warn($"SHA1 校验失败：{Path.GetFileName(filePath)}\n  期望：{expected}\n  实际：{actualSha1}");
                return false;
            }
        }

        /// <summary>
        /// 简单检查文件是否存在且大小匹配（不计算 SHA1，速度快）。
        /// 适用于快速预检查：如果文件都不存在或大小不对，肯定要重新下载。
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="expectedSize">期望大小（字节），&lt;=0 表示不检查大小</param>
        /// <returns>true = 存在且大小匹配；false = 不存在或大小不符</returns>
        public static bool QuickCheck(string filePath, long expectedSize = -1)
        {
            if (!File.Exists(filePath)) return false;
            if (expectedSize <= 0) return true;
            try
            {
                return new FileInfo(filePath).Length == expectedSize;
            }
            catch
            {
                return false;
            }
        }
    }
}
