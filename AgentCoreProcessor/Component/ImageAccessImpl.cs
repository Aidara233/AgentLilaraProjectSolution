using System;
using System.IO;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Component
{
    /// <summary>
    /// IImageAccess 实现，桥接插件层到内部 ImageStorage / Vision / OCR 基础设施。
    /// </summary>
    internal class ImageAccessImpl : IImageAccess
    {
        private readonly string _workspaceDir;
        private readonly IVisionProvider? _vision;
        private readonly IOcrProvider? _ocr;

        public ImageAccessImpl(string workspaceDir, IVisionProvider? vision, IOcrProvider? ocr)
        {
            _workspaceDir = Path.GetFullPath(workspaceDir);
            _vision = vision;
            _ocr = ocr;
            Directory.CreateDirectory(_workspaceDir);
        }

        public async Task<ImageReadResult> ReadImageAsync(
            string identifier, string source = "workspace",
            bool forceRefresh = false, string? contextHint = null)
        {
            try
            {
                var record = await ResolveImageAsync(identifier, source);
                if (record == null)
                    return new ImageReadResult { Success = false, Error = BuildNotFoundError(identifier, source) };

                // 有缓存且不强制刷新 → 直接返回
                if (!forceRefresh && !string.IsNullOrEmpty(record.Description))
                {
                    return new ImageReadResult
                    {
                        Success = true,
                        Description = record.Description,
                        ImageId = record.Id,
                        ImageHash = record.Hash,
                        Category = record.Category,
                        WasCached = true
                    };
                }

                // 调视觉模型生成描述
                if (_vision == null)
                    return new ImageReadResult { Success = false, Error = "视觉模型未配置，无法生成图片描述" };

                var description = await _vision.DescribeImageAsync(record.LocalPath, contextHint);
                if (string.IsNullOrEmpty(description))
                    return new ImageReadResult { Success = false, Error = "视觉模型返回了空描述" };

                await ImageStorage.UpdateDescriptionAsync(record.Hash, description);
                // 重新读取记录以获取最新的 Id 等字段
                var updated = await ImageStorage.GetByHashAsync(record.Hash);

                return new ImageReadResult
                {
                    Success = true,
                    Description = description,
                    ImageId = updated?.Id ?? record.Id,
                    ImageHash = record.Hash,
                    Category = updated?.Category ?? record.Category,
                    WasCached = false
                };
            }
            catch (Exception ex)
            {
                return new ImageReadResult { Success = false, Error = $"读取图片失败: {ex.Message}" };
            }
        }

        public async Task<ImageOcrResult> OcrImageAsync(string identifier, string source = "workspace")
        {
            try
            {
                var record = await ResolveImageAsync(identifier, source);
                if (record == null)
                    return new ImageOcrResult { Success = false, Error = BuildNotFoundError(identifier, source) };

                if (_ocr == null)
                    return new ImageOcrResult { Success = false, Error = "OCR模型未配置，无法执行文字识别" };

                var result = await _ocr.RecognizeAsync(record.LocalPath);
                await ImageStorage.UpdateOcrAsync(record.Hash, result.HasText, result.Text);

                return new ImageOcrResult
                {
                    Success = true,
                    HasText = result.HasText,
                    Text = result.Text
                };
            }
            catch (Exception ex)
            {
                return new ImageOcrResult { Success = false, Error = $"OCR处理失败: {ex.Message}" };
            }
        }

        public async Task<ImageOcrResult> GetOcrAsync(string identifier, string source = "workspace")
        {
            try
            {
                var record = await ResolveImageAsync(identifier, source);
                if (record == null)
                    return new ImageOcrResult { Success = false, Error = BuildNotFoundError(identifier, source) };

                if (record.HasText == null)
                    return new ImageOcrResult
                    {
                        Success = true,
                        HasText = false,
                        Text = "OCR尚未处理此图片，请先使用 ocr_image"
                    };

                return new ImageOcrResult
                {
                    Success = true,
                    HasText = record.HasText.Value,
                    Text = record.OcrText
                };
            }
            catch (Exception ex)
            {
                return new ImageOcrResult { Success = false, Error = $"获取OCR失败: {ex.Message}" };
            }
        }

        // ── 标识符解析 ──

        private async Task<Database.ImageRecord?> ResolveImageAsync(string identifier, string source)
        {
            if (source == "received")
            {
                if (!int.TryParse(identifier, out var id))
                    return null;

                // 标记为 null 调用方处理
                return await ImageStorage.GetByIdAsync(id);
            }

            // workspace 模式：沙箱检查 → 入库（如需要）→ 按 hash 查询
            var fullPath = Path.GetFullPath(Path.Combine(_workspaceDir, identifier));
            var workspaceRoot = _workspaceDir.EndsWith(Path.DirectorySeparatorChar)
                ? _workspaceDir
                : _workspaceDir + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase)
                && !fullPath.Equals(_workspaceDir, StringComparison.OrdinalIgnoreCase))
                return null;

            if (!File.Exists(fullPath))
                return null;

            var (_, hash) = await ImageStorage.CopyToStorageAsync(fullPath);
            return await ImageStorage.GetByHashAsync(hash);
        }

        private static string BuildNotFoundError(string identifier, string source)
        {
            if (source == "received")
            {
                return int.TryParse(identifier, out _)
                    ? $"图片ID不存在: {identifier}"
                    : "received 模式下 image 参数必须是有效的整数 ID";
            }
            return $"文件不存在或路径不合法: {identifier}";
        }
    }
}
