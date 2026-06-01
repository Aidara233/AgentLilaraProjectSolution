using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Client
{
    /// <summary>
    /// 按优先级依次尝试多个 IOcrProvider，首次成功即返回。
    /// 所有提供者都失败时返回空结果。
    /// </summary>
    internal class FallbackOcrProvider : IOcrProvider, IDisposable
    {
        private readonly List<IOcrProvider> _providers;

        public FallbackOcrProvider(List<IOcrProvider> providers)
        {
            _providers = providers ?? throw new ArgumentNullException(nameof(providers));
            if (_providers.Count == 0)
                Signal.Warn(LogGroup.Engine, "FallbackOcrProvider: 无可用OCR提供者");
        }

        public async Task<OcrResult> RecognizeAsync(string imagePath)
        {
            for (int i = 0; i < _providers.Count; i++)
            {
                var provider = _providers[i];
                try
                {
                    var name = (provider as UmiOcrProvider)?.Name ??
                               (provider as SiliconFlowOcrProvider)?.Name ??
                               provider.GetType().Name;
                    Signal.Event(LogGroup.Engine, $"OCR尝试: {name} (优先级{i + 1}/{_providers.Count})");
                    var result = await provider.RecognizeAsync(imagePath);
                    return result;
                }
                catch (Exception ex)
                {
                    var name = (provider as UmiOcrProvider)?.Name ??
                               (provider as SiliconFlowOcrProvider)?.Name ??
                               provider.GetType().Name;
                    Signal.Warn(LogGroup.Engine, $"OCR提供者 {name} 失败，尝试下一个", new { error = ex.Message });
                }
            }

            Signal.Warn(LogGroup.Engine, "所有OCR提供者均失败");
            return new OcrResult { HasText = false, Text = null };
        }

        public void Dispose()
        {
            foreach (var p in _providers)
                (p as IDisposable)?.Dispose();
        }
    }
}
