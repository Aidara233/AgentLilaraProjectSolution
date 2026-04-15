using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// 图片下载与本地存储。按日期分目录：Storage/Images/yyyy/MM/dd/{guid}.{ext}
    /// </summary>
    internal static class ImageStorage
    {
        /// <summary>
        /// 从 URL 下载图片并保存到本地，返回本地绝对路径。
        /// </summary>
        public static async Task<string> DownloadAndSaveAsync(string url, HttpClient http)
        {
            var now = DateTime.Now;
            var dateDir = Path.Combine(PathConfig.StoragePath, "Images",
                now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
            Directory.CreateDirectory(dateDir);

            var bytes = await http.GetByteArrayAsync(url);
            var ext = GuessExtension(url, bytes);
            var fileName = $"{Guid.NewGuid():N}.{ext}";
            var fullPath = Path.Combine(dateDir, fileName);
            await File.WriteAllBytesAsync(fullPath, bytes);
            return fullPath;
        }

        /// <summary>
        /// 从本地路径复制图片到存储目录（FileAdapter 测试用），返回存储路径。
        /// </summary>
        public static async Task<string> CopyToStorageAsync(string sourcePath)
        {
            var now = DateTime.Now;
            var dateDir = Path.Combine(PathConfig.StoragePath, "Images",
                now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
            Directory.CreateDirectory(dateDir);

            var ext = Path.GetExtension(sourcePath).TrimStart('.');
            if (string.IsNullOrEmpty(ext)) ext = "bin";
            var fileName = $"{Guid.NewGuid():N}.{ext}";
            var fullPath = Path.Combine(dateDir, fileName);
            var bytes = await File.ReadAllBytesAsync(sourcePath);
            await File.WriteAllBytesAsync(fullPath, bytes);
            return fullPath;
        }

        /// <summary>
        /// 推断图片扩展名：先看 URL 路径扩展名，再看文件头魔数。
        /// </summary>
        private static string GuessExtension(string url, byte[] data)
        {
            // 尝试从 URL 路径提取扩展名
            try
            {
                var uri = new Uri(url);
                var pathExt = Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
                if (pathExt is "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp")
                    return pathExt;
            }
            catch { /* URL 解析失败，继续用魔数 */ }

            // 文件头魔数
            if (data.Length >= 2)
            {
                if (data[0] == 0xFF && data[1] == 0xD8) return "jpg";
                if (data[0] == 0x89 && data[1] == 0x50) return "png";
                if (data[0] == 0x47 && data[1] == 0x49) return "gif";
                if (data[0] == 0x42 && data[1] == 0x4D) return "bmp";
            }
            if (data.Length >= 4)
            {
                // RIFF....WEBP
                if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46)
                    return "webp";
            }

            return "bin";
        }
    }
}
