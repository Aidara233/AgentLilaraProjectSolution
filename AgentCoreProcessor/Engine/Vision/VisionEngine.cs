using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;

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
        private volatile bool _visionSuspended;
        private string? _suspendReason;

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

            _visionAvailable = ctx.Vision != null;
            if (!_visionAvailable && config.VisionEnabled)
                FrameworkLogger.Log("VisionEngine", "警告: IVisionProvider 不可用，视觉描述生成已禁用。请检查 Storage/Core/VisionProvider.json 配置");

            _ocrAvailable = ctx.Ocr != null;
            if (!_ocrAvailable && config.OcrEnabled)
                FrameworkLogger.Log("VisionEngine", "警告: IOcrProvider 不可用，OCR 已禁用。请检查 Storage/Core/OcrProvider.json 配置");

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

            visionSemaphore?.Dispose();
            ocrSemaphore?.Dispose();
            FrameworkLogger.Log("VisionEngine", "视觉引擎已停止");
        }

        // PLACEHOLDER_PROCESS_METHODS

        private async Task ProcessPendingImagesAsync()
        {
            _visionSuspended = false;
            _suspendReason = null;

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
            if (!config.VisionEnabled || !_visionAvailable || _visionSuspended) return;
            if (!string.IsNullOrEmpty(record.Description)) return;

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
                        var hint = record.Category == "sticker"
                            ? "这是一张聊天表情包，用10字以内描述其表达的情绪或动作"
                            : null;
                        desc = await ctx.Vision!.DescribeImageAsync(path, hint);
                        break;
                    }
                    catch (System.Net.Http.HttpRequestException ex) when (
                        ex.Message.Contains("401") || ex.Message.Contains("403"))
                    {
                        _visionSuspended = true;
                        _suspendReason = $"认证失败 ({ex.Message})，本轮 Vision 处理已暂停";
                        FrameworkLogger.Log("VisionEngine", _suspendReason);
                        return;
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
                {
                    await ImageStorage.UpdateDescriptionAsync(record.Hash, desc);
                    Interlocked.Increment(ref _totalProcessed);
                }
                else
                {
                    Interlocked.Increment(ref _visionErrors);
                    FrameworkLogger.Log("VisionEngine", $"Vision 最终失败，跳过: hash={record.Hash}");
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
                FrameworkLogger.Log("VisionEngine", $"OCR 认证失败，跳过本轮: {ex.Message}");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _ocrErrors);
                FrameworkLogger.LogError("VisionEngine", ex, $"OCR 失败: hash={record.Hash}");
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
            FrameworkLogger.Log("VisionEngine", "配置已更新");
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
