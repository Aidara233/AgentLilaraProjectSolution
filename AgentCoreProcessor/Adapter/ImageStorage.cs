using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using SkiaSharp;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// 图片下载与本地存储。按日期分目录：Storage/Images/yyyy/MM/dd/{hash}.{ext}
    /// 通过 ImageRecord 去重：同哈希图片只存一份。
    /// 缩略图存储：Storage/Images/thumbs/{hash}.jpg
    /// </summary>
    internal static class ImageStorage
    {
        private static ImageRepository? _repo;
        private static EventBus? _eventBus;

        /// <summary>缩略图长边最大像素（可配置）</summary>
        public static int ThumbnailMaxEdge { get; set; } = 1568;

        /// <summary>缩略图 JPEG 质量（0-100）</summary>
        public static int ThumbnailQuality { get; set; } = 85;

        /// <summary>单张图片直传大小上限（字节），超出则必须手动查看</summary>
        public static long MaxDirectSendSize { get; set; } = 1024 * 1024; // 1MB

        /// <summary>单轮图片直传总大小上限（字节）</summary>
        public static long MaxTotalDirectSendSize { get; set; } = 3 * 1024 * 1024; // 3MB

        /// <summary>单轮图片直传最大数量</summary>
        public static int MaxDirectSendCount { get; set; } = 5;

        public static void Init(ImageRepository repo, EventBus? eventBus = null)
        {
            _repo = repo;
            _eventBus = eventBus;
        }

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
                    FileSize = bytes.Length,
                    CreatedAt = DateTime.Now
                });
                _eventBus?.PublishSignal("new-image", hash);
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
                _eventBus?.PublishSignal("new-image", hash);
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

        public static async Task SetCategoryAsync(string hash, string category)
        {
            if (_repo == null) return;
            var record = await _repo.GetByHashAsync(hash);
            if (record != null && record.Category == null)
            {
                record.Category = category;
                await _repo.UpdateAsync(record);
            }
        }

        public static async Task<string?> GetDescriptionAsync(string hash)
        {
            if (_repo == null) return null;
            var record = await _repo.GetByHashAsync(hash);
            return record?.Description;
        }

        public static async Task UpdateDescriptionAsync(string hash, string description)
        {
            if (_repo == null) return;
            await _repo.UpdateDescriptionAsync(hash, description);
        }

        public static async Task IncrementSeenCountAsync(string hash)
        {
            if (_repo == null) return;
            await _repo.IncrementSeenCountAsync(hash);
        }

        public static async Task<string?> GetCategoryAsync(string hash)
        {
            if (_repo == null) return null;
            var record = await _repo.GetByHashAsync(hash);
            return record?.Category;
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

        // ── 缩略图 ──

        private static string GetThumbDir()
        {
            var dir = Path.Combine(PathConfig.StoragePath, "Images", "thumbs");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>生成缩略图并存储，返回缩略图路径。已有缩略图时直接返回。</summary>
        public static async Task<string?> EnsureThumbnailAsync(string hash)
        {
            if (_repo == null) return null;
            var record = await _repo.GetByHashAsync(hash);
            if (record == null) return null;

            if (!string.IsNullOrEmpty(record.ThumbnailPath) && File.Exists(record.ThumbnailPath))
                return record.ThumbnailPath;

            if (!File.Exists(record.LocalPath)) return null;

            var thumbPath = GenerateThumbnail(record.LocalPath, hash);
            if (thumbPath != null)
            {
                record.ThumbnailPath = thumbPath;
                await _repo.UpdateAsync(record);
            }
            return thumbPath;
        }

        /// <summary>生成缩略图：缩放到 MaxEdge 以内，输出 JPEG。</summary>
        private static string? GenerateThumbnail(string sourcePath, string hash)
        {
            try
            {
                using var input = File.OpenRead(sourcePath);
                using var codec = SKCodec.Create(input);
                if (codec == null) return null;

                var info = codec.Info;
                var maxEdge = Math.Max(info.Width, info.Height);

                // 不需要缩放的小图直接复制
                if (maxEdge <= ThumbnailMaxEdge && sourcePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                    return null; // 原图即可作为缩略图使用

                var scale = maxEdge > ThumbnailMaxEdge ? (float)ThumbnailMaxEdge / maxEdge : 1f;
                var newWidth = (int)(info.Width * scale);
                var newHeight = (int)(info.Height * scale);

                using var bitmap = SKBitmap.Decode(codec);
                if (bitmap == null) return null;

                using var resized = bitmap.Resize(new SKImageInfo(newWidth, newHeight), SKSamplingOptions.Default);
                if (resized == null) return null;

                using var image = SKImage.FromBitmap(resized);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, ThumbnailQuality);

                var thumbPath = Path.Combine(GetThumbDir(), $"{hash}.jpg");
                using var output = File.OpenWrite(thumbPath);
                data.SaveTo(output);
                return thumbPath;
            }
            catch (Exception ex)
            {
                Engine.FrameworkLogger.Log("ImageStorage", $"缩略图生成失败: {ex.Message}");
                return null;
            }
        }

        // ── 按 ID 查询 ──

        /// <summary>按自增 ID 获取图片记录。</summary>
        public static async Task<ImageRecord?> GetByIdAsync(int id)
        {
            if (_repo == null) return null;
            return await _repo.GetByIdAsync(id);
        }

        /// <summary>按哈希获取图片记录。</summary>
        public static async Task<ImageRecord?> GetByHashAsync(string hash)
        {
            if (_repo == null) return null;
            return await _repo.GetByHashAsync(hash);
        }

        /// <summary>获取图片用于模型输入的路径（优先缩略图，fallback 原图）。</summary>
        public static async Task<string?> GetModelInputPathAsync(string hash)
        {
            if (_repo == null) return null;
            var record = await _repo.GetByHashAsync(hash);
            if (record == null) return null;

            // 有缩略图用缩略图
            if (!string.IsNullOrEmpty(record.ThumbnailPath) && File.Exists(record.ThumbnailPath))
                return record.ThumbnailPath;

            // 原图存在就用原图
            if (File.Exists(record.LocalPath))
                return record.LocalPath;

            return null;
        }

        /// <summary>获取图片 ID（通过哈希查询）。</summary>
        public static async Task<int?> GetIdByHashAsync(string hash)
        {
            if (_repo == null) return null;
            var record = await _repo.GetByHashAsync(hash);
            return record?.Id;
        }

        /// <summary>删除图片（文件 + 缩略图 + DB 记录）。</summary>
        public static async Task<bool> DeleteAsync(string hash)
        {
            if (_repo == null) return false;
            var record = await _repo.GetByHashAsync(hash);
            if (record == null) return false;

            try { if (File.Exists(record.LocalPath)) File.Delete(record.LocalPath); } catch { }
            try { if (!string.IsNullOrEmpty(record.ThumbnailPath) && File.Exists(record.ThumbnailPath)) File.Delete(record.ThumbnailPath); } catch { }
            await _repo.DeleteAsync(record);
            return true;
        }

        /// <summary>清除图片的描述（用于重新生成）。</summary>
        public static async Task ClearDescriptionAsync(string hash)
        {
            if (_repo == null) return;
            var record = await _repo.GetByHashAsync(hash);
            if (record != null)
            {
                record.Description = null;
                await _repo.UpdateAsync(record);
            }
        }

        /// <summary>清除图片的 OCR 结果（用于重新处理）。</summary>
        public static async Task ClearOcrAsync(string hash)
        {
            if (_repo == null) return;
            var record = await _repo.GetByHashAsync(hash);
            if (record != null)
            {
                record.HasText = null;
                record.OcrText = null;
                await _repo.UpdateAsync(record);
            }
        }

        /// <summary>获取待索引图片（无描述或未OCR），按时间倒序。</summary>
        public static async Task<List<ImageRecord>> GetPendingIndexAsync(int limit = 50)
        {
            if (_repo == null) return new List<ImageRecord>();
            return await _repo.GetPendingIndexAsync(limit);
        }

        /// <summary>更新 OCR 结果。</summary>
        public static async Task UpdateOcrAsync(string hash, bool hasText, string? ocrText)
        {
            if (_repo == null) return;
            await _repo.UpdateOcrAsync(hash, hasText, ocrText);
        }

        /// <summary>分页查询图片（WebUI 用）。</summary>
        public static async Task<List<ImageRecord>> GetPagedAsync(int offset, int limit,
            string? statusFilter = null, string? categoryFilter = null, string? keyword = null)
        {
            if (_repo == null) return new List<ImageRecord>();
            return await _repo.GetPagedAsync(offset, limit, statusFilter, categoryFilter, keyword);
        }

        /// <summary>获取筛选后的图片总数。</summary>
        public static async Task<int> GetFilteredCountAsync(
            string? statusFilter = null, string? categoryFilter = null, string? keyword = null)
        {
            if (_repo == null) return 0;
            return await _repo.GetFilteredCountAsync(statusFilter, categoryFilter, keyword);
        }
    }
}
