using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;

namespace AgentCoreProcessor.Engine.Vision
{
    internal class VisionEngine : ISubEngine
    {
        public string EngineType => "Vision";
        public bool IsAlive { get; private set; } = true;
        public bool IsInfrastructure => true;
        public bool IsBusy => _activeTasks > 0;

        private readonly ISystemContext ctx;
        private readonly LoopGate gate = new();
        private VisionEngineConfig config = new();
        private SemaphoreSlim? visionSemaphore;
        private SemaphoreSlim? ocrSemaphore;
        private PaddleOCRSharp.PaddleOCREngine? ocrEngine;
        private int _activeTasks;
        private bool _visionAvailable;

        public VisionEngine(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public void SignalGate() => gate.Signal();

        public async Task RunAsync()
        {
            config = VisionEngineConfig.Load();
            visionSemaphore = new SemaphoreSlim(config.VisionConcurrency);
            ocrSemaphore = new SemaphoreSlim(config.OcrConcurrency);

            // 检查视觉模型可用性
            _visionAvailable = ctx.Vision != null;
            if (!_visionAvailable && config.VisionEnabled)
                FrameworkLogger.Log("VisionEngine", "警告: IVisionProvider 不可用，视觉描述生成已禁用。请检查 Storage/Core/VisionProvider.json 配置");

            InitOcr();
            FrameworkLogger.Log("VisionEngine", "视觉引擎已启动");

            while (IsAlive)
            {
                await gate.WaitAsync(TimeSpan.FromSeconds(60));
                if (!IsAlive) break;

                try
                {
                    await ProcessPendingImagesAsync();
                }
                catch (Exception ex)
                {
                    FrameworkLogger.LogError("VisionEngine", ex, "处理循环异常");
                }
            }

            DisposeOcr();
            visionSemaphore?.Dispose();
            ocrSemaphore?.Dispose();
            FrameworkLogger.Log("VisionEngine", "视觉引擎已停止");
        }

        // PLACEHOLDER_PROCESS_METHODS

        private async Task ProcessPendingImagesAsync()
        {
            var pending = await ImageStorage.GetPendingIndexAsync(config.BatchSize);
            if (pending.Count == 0) return;

            FrameworkLogger.Log("VisionEngine", $"开始处理 {pending.Count} 张待索引图片");
            var tasks = pending.Select(ProcessSingleImageAsync);
            await Task.WhenAll(tasks);
            FrameworkLogger.Log("VisionEngine", $"本轮处理完成");
        }

        private async Task ProcessSingleImageAsync(Database.ImageRecord record)
        {
            Interlocked.Increment(ref _activeTasks);
            try
            {
                var visionTask = ProcessVisionAsync(record);
                var ocrTask = ProcessOcrAsync(record);
                await Task.WhenAll(visionTask, ocrTask);
            }
            finally
            {
                Interlocked.Decrement(ref _activeTasks);
            }
        }

        private async Task ProcessVisionAsync(Database.ImageRecord record)
        {
            if (!config.VisionEnabled || !_visionAvailable) return;
            if (!string.IsNullOrEmpty(record.Description)) return;

            await visionSemaphore!.WaitAsync();
            try
            {
                var path = await ImageStorage.GetModelInputPathAsync(record.Hash);
                if (path == null) return;

                string? desc = null;
                for (int attempt = 0; attempt <= config.VisionRetryCount; attempt++)
                {
                    try
                    {
                        var hint = record.Category == "sticker"
                            ? "这是一张聊天表情包，用10字以内描述其表达的情绪或动作"
                            : null;
                        desc = await ctx.Vision!.DescribeImageAsync(path, hint);
                        break;
                    }
                    catch (Exception ex)
                    {
                        FrameworkLogger.Log("VisionEngine",
                            $"Vision 失败 (attempt {attempt + 1}/{config.VisionRetryCount + 1}): hash={record.Hash} err={ex.Message}");
                        if (attempt < config.VisionRetryCount)
                            await Task.Delay(config.VisionRetryDelayMs);
                    }
                }

                if (!string.IsNullOrEmpty(desc))
                    await ImageStorage.UpdateDescriptionAsync(record.Hash, desc);
                else
                    FrameworkLogger.Log("VisionEngine", $"Vision 最终失败，跳过: hash={record.Hash}");
            }
            finally
            {
                visionSemaphore!.Release();
            }
        }

        private async Task ProcessOcrAsync(Database.ImageRecord record)
        {
            if (!config.OcrEnabled || ocrEngine == null) return;
            if (record.HasText != null) return;

            await ocrSemaphore!.WaitAsync();
            try
            {
                var result = ocrEngine.DetectText(record.LocalPath);
                var text = result?.Text?.Trim();
                var hasText = !string.IsNullOrEmpty(text);
                await ImageStorage.UpdateOcrAsync(record.Hash, hasText, hasText ? text : null);
            }
            catch (Exception ex)
            {
                FrameworkLogger.LogError("VisionEngine", ex, $"OCR 失败: hash={record.Hash}");
                await ImageStorage.UpdateOcrAsync(record.Hash, false, null);
            }
            finally
            {
                ocrSemaphore!.Release();
            }
        }

        private void InitOcr()
        {
            if (!config.OcrEnabled) return;
            try
            {
                ocrEngine = new PaddleOCRSharp.PaddleOCREngine(null, new PaddleOCRSharp.OCRParameter());
                FrameworkLogger.Log("VisionEngine", "PaddleOCR 引擎已加载");
            }
            catch (Exception ex)
            {
                FrameworkLogger.LogError("VisionEngine", ex, "PaddleOCR 初始化失败，OCR 功能不可用");
            }
        }

        private void DisposeOcr()
        {
            ocrEngine?.Dispose();
            ocrEngine = null;
        }

        public void OnEvent(EngineEvent e)
        {
            if (e is SignalEvent signal && signal.SignalName == "new-image")
                gate.Signal();
        }

        public void RequestStop()
        {
            IsAlive = false;
            gate.Signal();
        }
    }
}
