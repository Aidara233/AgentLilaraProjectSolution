using System;
using System.IO;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Component
{
    /// <summary>
    /// IImageAccess 实现，桥接插件层到内部 ImageStorage / OCR 基础设施。
    /// </summary>
    internal class ImageAccessImpl : IImageAccess
    {
        private readonly string _workspaceDir;
        private readonly IOcrProvider? _ocr;

        public ImageAccessImpl(string workspaceDir, IOcrProvider? ocr)
        {
            _workspaceDir = Path.GetFullPath(workspaceDir);
            _ocr = ocr;
            Directory.CreateDirectory(_workspaceDir);
        }

        public async Task<ImageResolveResult> ResolveImageAsync(string identifier, string source = "workspace")
        {
            try
            {
#pragma warning disable CS8622
                var (record, displayName) = await ResolveInternalAsync(identifier, source);
#pragma warning restore CS8622
                if (record == null)
                    return new ImageResolveResult { Success = false, Error = BuildNotFoundError(identifier, source) };

                return new ImageResolveResult
                {
                    Success = true,
                    LocalPath = record.LocalPath,
                    ImageId = record.Id,
                    ImageHash = record.Hash,
                    Category = record.Category,
                    DisplayName = displayName
                };
            }
            catch (Exception ex)
            {
                return new ImageResolveResult { Success = false, Error = $"解析图片失败: {ex.Message}" };
            }
        }

        public async Task<ImageOcrResult> OcrImageAsync(string identifier, string source = "workspace")
        {
            try
            {
                var (record, _) = await ResolveInternalAsync(identifier, source);
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
                var (record, _) = await ResolveInternalAsync(identifier, source);
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

        private async Task<(Database.ImageRecord? record, string displayName)> ResolveInternalAsync(
            string identifier, string source)
        {
            if (source == "received")
            {
                if (!int.TryParse(identifier, out var id))
                    return (null, "");

                var record = await ImageStorage.GetByIdAsync(id);
                return (record, record != null ? $"Id={record.Id}" : "");
            }

            // workspace 模式：沙箱检查 → 入库（如需要）→ 按 hash 查询
            var fullPath = Path.GetFullPath(Path.Combine(_workspaceDir, identifier));
            var workspaceRoot = _workspaceDir.EndsWith(Path.DirectorySeparatorChar)
                ? _workspaceDir
                : _workspaceDir + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase)
                && !fullPath.Equals(_workspaceDir, StringComparison.OrdinalIgnoreCase))
                return (null, "");

            if (!File.Exists(fullPath))
                return (null, "");

            var fileName = Path.GetFileName(identifier);
            var (_, hash) = await ImageStorage.CopyToStorageAsync(fullPath);
            var resolved = await ImageStorage.GetByHashAsync(hash);
            return (resolved, fileName);
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
