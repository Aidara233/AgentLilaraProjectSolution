using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// 图片下载与本地存储。按日期分目录：Storage/Images/yyyy/MM/dd/{hash}.{ext}
    /// 通过 ImageRecord 去重：同哈希图片只存一份。
    /// </summary>
    internal static class ImageStorage
    {
        private static ImageRepository? _repo;

        public static void Init(ImageRepository repo) => _repo = repo;

        /// <summary>
        /// 从 URL 下载图片，去重存储，返回 (本地路径, 哈希)。
        /// </summary>
        public static async Task<(string LocalPath, string Hash)> DownloadAndSaveAsync(string url, HttpClient http)
        {
            var bytes = await http.GetByteArrayAsync(url);
            var hash = ComputeHash(bytes);

            if (_repo != null)
            {
                var existing = await _repo.GetByHashAsync(hash);
                if (existing != null && File.Exists(existing.LocalPath))
                    return (existing.LocalPath, hash);

                // 文件丢失，重新存储并更新记录
                if (existing != null)
                {
                    var newPath = SaveToDateDir(bytes, hash, GuessExtension(url, bytes));
                    existing.LocalPath = newPath;
                    existing.SourceUrl = url;
                    await _repo.UpdateAsync(existing);
                    return (newPath, hash);
                }
            }

            var ext = GuessExtension(url, bytes);
            var localPath = SaveToDateDir(bytes, hash, ext);

            if (_repo != null)
            {
                await _repo.SaveAsync(new ImageRecord
                {
                    Hash = hash,
                    LocalPath = localPath,
                    SourceUrl = url,
                    CreatedAt = DateTime.Now
                });
            }

            return (localPath, hash);
        }

        /// <summary>
        /// 从本地路径复制图片到存储目录（FileAdapter 测试用），返回 (存储路径, 哈希)。
        /// </summary>
        public static async Task<(string LocalPath, string Hash)> CopyToStorageAsync(string sourcePath)
        {
            var bytes = await File.ReadAllBytesAsync(sourcePath);
            var hash = ComputeHash(bytes);

            if (_repo != null)
            {
                var existing = await _repo.GetByHashAsync(hash);
                if (existing != null && File.Exists(existing.LocalPath))
                    return (existing.LocalPath, hash);
            }

            var ext = Path.GetExtension(sourcePath).TrimStart('.');
            if (string.IsNullOrEmpty(ext)) ext = "bin";
            var localPath = SaveToDateDir(bytes, hash, ext);

            if (_repo != null)
            {
                await _repo.SaveAsync(new ImageRecord
                {
                    Hash = hash,
                    LocalPath = localPath,
                    SourceUrl = null,
                    CreatedAt = DateTime.Now
                });
            }

            return (localPath, hash);
        }

        /// <summary>按哈希列表获取本地路径，文件丢失时尝试从 SourceUrl 重新下载。</summary>
        public static async Task<List<string>> ResolvePathsAsync(string? imageHashes, HttpClient? http = null)
        {
            var paths = new List<string>();
            if (string.IsNullOrEmpty(imageHashes) || _repo == null) return paths;

            foreach (var hash in imageHashes.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var record = await _repo.GetByHashAsync(hash.Trim());
                if (record == null) continue;

                if (File.Exists(record.LocalPath))
                {
                    paths.Add(record.LocalPath);
                }
                else if (!string.IsNullOrEmpty(record.SourceUrl) && http != null)
                {
                    try
                    {
                        var bytes = await http.GetByteArrayAsync(record.SourceUrl);
                        var ext = GuessExtension(record.SourceUrl, bytes);
                        var newPath = SaveToDateDir(bytes, hash.Trim(), ext);
                        record.LocalPath = newPath;
                        await _repo.UpdateAsync(record);
                        paths.Add(newPath);
                    }
                    catch { }
                }
            }
            return paths;
        }

        private static string ComputeHash(byte[] data)
        {
            var hashBytes = MD5.HashData(data);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        private static string SaveToDateDir(byte[] data, string hash, string ext)
        {
            var now = DateTime.Now;
            var dateDir = Path.Combine(PathConfig.StoragePath, "Images",
                now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
            Directory.CreateDirectory(dateDir);
            var fullPath = Path.Combine(dateDir, $"{hash}.{ext}");
            File.WriteAllBytes(fullPath, data);
            return fullPath;
        }

        private static string GuessExtension(string url, byte[] data)
        {
            try
            {
                var uri = new Uri(url);
                var pathExt = Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
                if (pathExt is "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp")
                    return pathExt;
            }
            catch { }

            if (data.Length >= 2)
            {
                if (data[0] == 0xFF && data[1] == 0xD8) return "jpg";
                if (data[0] == 0x89 && data[1] == 0x50) return "png";
                if (data[0] == 0x47 && data[1] == 0x49) return "gif";
                if (data[0] == 0x42 && data[1] == 0x4D) return "bmp";
            }
            if (data.Length >= 4)
            {
                if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46)
                    return "webp";
            }

            return "bin";
        }
    }
}
