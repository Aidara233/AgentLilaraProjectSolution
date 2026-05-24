using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Engine.Vision
{
    internal class VisionEngineSnapshot
    {
        public bool IsAlive { get; set; }
        public bool IsBusy { get; set; }
        public int ActiveTasks { get; set; }
        public int PendingCount { get; set; }
        public int TotalProcessed { get; set; }
        public int VisionErrors { get; set; }
        public int OcrErrors { get; set; }
        public bool VisionAvailable { get; set; }
        public bool VisionSuspended { get; set; }
        public string? SuspendReason { get; set; }
        public bool OcrAvailable { get; set; }
        public VisionEngineConfig Config { get; set; } = new();
    }

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
        private int _activeTasks;
        private bool _visionAvailable;
        private bool _ocrAvailable;
        private int _totalProcessed;
        private int _visionErrors;
        private int _ocrErrors;
        private int _totalCycles;
        private volatile bool _visionSuspended;
        private string? _suspendReason;

        public VisionEngine(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public void SignalGate() => gate.Signal();

        public async Task RunAsync()
        {
            var parentCtx = AgentCoreProcessor.Logging.SignalContext.Current;
            var lifeCtx = Logging.Signal.Continue(
                parentCtx?.SignalId ?? Logging.Signal.NewId(), parentCtx?.CurrentSpanId,
                "vision:main", Logging.LogGroup.Engine, "Vision引擎",
                new { engineType = EngineType });

            config = VisionEngineConfig.Load();
            visionSemaphore = new SemaphoreSlim(config.VisionConcurrency);
            ocrSemaphore = new SemaphoreSlim(config.OcrConcurrency);

            _visionAvailable = ctx.Vision != null;
            if (!_visionAvailable && config.VisionEnabled)
                Signal.Warn(LogGroup.Engine, "Vision已启用但提供者未配置，视觉处理将不可用");

            _ocrAvailable = ctx.Ocr != null;
            if (!_ocrAvailable && config.OcrEnabled)
                Signal.Warn(LogGroup.Engine, "OCR已启用但提供者未配置，OCR处理将不可用");

            while (IsAlive)
            {
                await gate.WaitAsync(TimeSpan.FromSeconds(60));
                if (!IsAlive) break;

                _totalCycles++;
                var processedBefore = _totalProcessed;
                var visionErrBefore = _visionErrors;
                var ocrErrBefore = _ocrErrors;

                using var cycleSpan = Signal.Open(LogGroup.Engine, $"Vision处理 #{_totalCycles}",
                    new { cycle = _totalCycles, visionEnabled = config.VisionEnabled, ocrEnabled = config.OcrEnabled });
                try
                {
                    await ProcessPendingImagesAsync();
                    cycleSpan.SetCloseDetail(new
                    {
                        processed = _totalProcessed - processedBefore,
                        visionErrors = _visionErrors - visionErrBefore,
                        ocrErrors = _ocrErrors - ocrErrBefore,
                        suspended = _visionSuspended,
                        suspendReason = _suspendReason
                    });
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Engine, $"Vision处理异常 #{_totalCycles}", new { error = ex.GetType().Name, message = ex.Message });
                }
            }

            visionSemaphore?.Dispose();
            ocrSemaphore?.Dispose();

            lifeCtx.Close(new { engineType = EngineType, reason = "shutdown" });
        }

        private async Task ProcessPendingImagesAsync()
        {
            _visionSuspended = false;
            _suspendReason = null;

            // Phase 1: OCR（处理所有 HasText IS NULL 的图片）
            if (config.OcrEnabled && _ocrAvailable)
            {
                while (IsAlive)
                {
                    var ocrPending = await ImageStorage.GetOcrPendingAsync(config.BatchSize);
                    if (ocrPending.Count == 0) break;

                    var tasks = ocrPending.Select(r => ProcessOcrWrapperAsync(r));
                    await Task.WhenAll(tasks);
                }
            }

            // Phase 2: Vision（处理 OCR 已完成但 Description IS NULL 的图片）
            if (config.VisionEnabled && _visionAvailable)
            {
                while (IsAlive)
                {
                    var visionPending = await ImageStorage.GetVisionPendingAsync(config.BatchSize);
                    if (visionPending.Count == 0) break;

                    var tasks = visionPending.Select(r => ProcessVisionWrapperAsync(r));
                    await Task.WhenAll(tasks);

                    if (_visionSuspended) break;
                }
            }
        }

        private async Task ProcessOcrWrapperAsync(Database.ImageRecord record)
        {
            Interlocked.Increment(ref _activeTasks);
            try { await ProcessOcrAsync(record); }
            finally { Interlocked.Decrement(ref _activeTasks); }
        }

        private async Task ProcessVisionWrapperAsync(Database.ImageRecord record)
        {
            Interlocked.Increment(ref _activeTasks);
            try { await ProcessVisionAsync(record); }
            finally { Interlocked.Decrement(ref _activeTasks); }
        }

        private async Task ProcessVisionAsync(Database.ImageRecord record)
        {
            if (!config.VisionEnabled || !_visionAvailable || _visionSuspended) return;
            if (record.Description != null) return; // 包括空字符串（已跳过）

            // 跳过条件：表情包 或 OCR 文本足够丰富
            if (record.Category == "sticker" ||
                (record.OcrText != null && record.OcrText.Length >= config.OcrRichTextThreshold))
            {
                await ImageStorage.UpdateDescriptionAsync(record.Hash, "");
                Interlocked.Increment(ref _totalProcessed);
                return;
            }

            await visionSemaphore!.WaitAsync();
            try
            {
                if (_visionSuspended) return;

                var path = await ImageStorage.GetModelInputPathAsync(record.Hash);
                if (path == null) return;

                string? desc = null;
                for (int attempt = 0; attempt <= config.VisionRetryCount; attempt++)
                {
                    try
                    {
                        desc = await ctx.Vision!.DescribeImageAsync(path, null);
                        break;
                    }
                    catch (System.Net.Http.HttpRequestException ex) when (
                        ex.Message.Contains("401") || ex.Message.Contains("403"))
                    {
                        _visionSuspended = true;
                        _suspendReason = $"认证失败 ({ex.Message})，本轮 Vision 处理已暂停";
                        Signal.Warn(LogGroup.Engine, "Vision认证失败暂停", new { hash = record.Hash, error = ex.Message });
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (attempt < config.VisionRetryCount)
                            await Task.Delay(config.VisionRetryDelayMs);
                        else
                            Signal.Warn(LogGroup.Engine, "Vision描述失败", new { hash = record.Hash, attempts = attempt + 1, error = ex.Message });
                    }
                }

                if (!string.IsNullOrEmpty(desc))
                {
                    await ImageStorage.UpdateDescriptionAsync(record.Hash, desc);
                    Interlocked.Increment(ref _totalProcessed);
                }
                else
                {
                    Interlocked.Increment(ref _visionErrors);
                }
            }
            finally
            {
                visionSemaphore!.Release();
            }
        }

        private async Task ProcessOcrAsync(Database.ImageRecord record)
        {
            if (!config.OcrEnabled || !_ocrAvailable) return;
            if (record.HasText != null) return;

            await ocrSemaphore!.WaitAsync();
            try
            {
                var result = await ctx.Ocr!.RecognizeAsync(record.LocalPath);
                await ImageStorage.UpdateOcrAsync(record.Hash, result.HasText, result.Text);
            }
            catch (System.Net.Http.HttpRequestException ex) when (
                ex.Message.Contains("401") || ex.Message.Contains("403"))
            {
                Interlocked.Increment(ref _ocrErrors);
                Signal.Warn(LogGroup.Engine, "OCR认证失败", new { hash = record.Hash, error = ex.Message });
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _ocrErrors);
                Signal.Warn(LogGroup.Engine, "OCR处理失败", new { hash = record.Hash, error = ex.Message });
                await ImageStorage.UpdateOcrAsync(record.Hash, false, null);
            }
            finally
            {
                ocrSemaphore!.Release();
            }
        }

        public VisionEngineSnapshot GetSnapshot()
        {
            return new VisionEngineSnapshot
            {
                IsAlive = IsAlive,
                IsBusy = IsBusy,
                ActiveTasks = _activeTasks,
                PendingCount = -1,
                TotalProcessed = _totalProcessed,
                VisionErrors = _visionErrors,
                OcrErrors = _ocrErrors,
                VisionAvailable = _visionAvailable,
                VisionSuspended = _visionSuspended,
                SuspendReason = _suspendReason,
                OcrAvailable = _ocrAvailable,
                Config = config
            };
        }

        public void UpdateConfig(VisionEngineConfig newConfig)
        {
            config = newConfig;
            config.Save();
            // 重建信号量（下次处理生效）
            visionSemaphore?.Dispose();
            ocrSemaphore?.Dispose();
            visionSemaphore = new SemaphoreSlim(config.VisionConcurrency);
            ocrSemaphore = new SemaphoreSlim(config.OcrConcurrency);
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
