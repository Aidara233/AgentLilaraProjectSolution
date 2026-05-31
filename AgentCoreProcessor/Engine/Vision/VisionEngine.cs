using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json.Linq;

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
        public int Phase1Count { get; set; }
        public int Phase2Count { get; set; }
        public int Phase3Count { get; set; }
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
        private SemaphoreSlim? ocrSemaphore;
        private SemaphoreSlim? phase1Semaphore;
        private int _activeTasks;
        private bool _visionAvailable;
        private bool _ocrAvailable;
        private int _totalProcessed;
        private int _visionErrors;
        private int _ocrErrors;
        private int _totalCycles;
        private int _phase1Count;
        private int _phase2Count;
        private int _phase3Count;
        private volatile bool _visionSuspended;
        private string? _suspendReason;

        private SiliconFlowVisionProvider? phase1Provider;
        private SiliconFlowVisionProvider? phase2Provider;
        private PhaseConfig? phase1Config;
        private PhaseConfig? phase2Config;
        private PhaseConfig? phase3Config;
        private string? visionApiKey;
        private string? visionEndpoint;

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
            ocrSemaphore = new SemaphoreSlim(config.OcrConcurrency);
            phase1Semaphore = new SemaphoreSlim(config.Phase1Concurrency);

            LoadProviders();

            _visionAvailable = phase1Provider != null && phase2Provider != null;
            if (!_visionAvailable && config.VisionEnabled)
                Signal.Warn(LogGroup.Engine, "Vision已启用但提供者未配置或配置不完整");

            _ocrAvailable = ctx.Ocr != null;
            if (!_ocrAvailable && config.OcrEnabled)
                Signal.Warn(LogGroup.Engine, "OCR已启用但提供者未配置");

            // 订阅 VisionRefineSignal 在 OnEvent 中处理

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
                        phase1 = _phase1Count,
                        phase2 = _phase2Count,
                        phase3 = _phase3Count,
                        suspended = _visionSuspended,
                        suspendReason = _suspendReason
                    });
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Engine, $"Vision处理异常 #{_totalCycles}", new { error = ex.GetType().Name, message = ex.Message });
                }
            }

            ocrSemaphore?.Dispose();
            phase1Semaphore?.Dispose();
            phase1Provider?.Dispose();
            phase2Provider?.Dispose();

            lifeCtx.Close(new { engineType = EngineType, reason = "shutdown" });
        }

        private void LoadProviders()
        {
            var vpPath = System.IO.Path.Combine(Config.PathConfig.StoragePath, "Core", "VisionProvider.json");
            if (!System.IO.File.Exists(vpPath))
            {
                Signal.Warn(LogGroup.Engine, "VisionProvider.json 不存在，视觉处理不可用");
                return;
            }
            try
            {
                var vpJson = System.IO.File.ReadAllText(vpPath);
                var vpObj = JObject.Parse(vpJson);
                visionApiKey = vpObj["apiKey"]?.ToString();
                visionEndpoint = vpObj["endpoint"]?.ToString() ?? "https://api.siliconflow.cn/v1/chat/completions";

                if (string.IsNullOrEmpty(visionApiKey))
                {
                    Signal.Warn(LogGroup.Engine, "VisionProvider.json 中 apiKey 为空");
                    return;
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "读取 VisionProvider.json 失败", new { error = ex.Message });
                return;
            }

            var configDir = System.IO.Path.Combine(Config.PathConfig.StoragePath, "Vision");
            var phase1Path = System.IO.Path.Combine(configDir, "Phase1Coarse.json");
            var phase2Path = System.IO.Path.Combine(configDir, "Phase2Refine.json");
            var phase3Path = System.IO.Path.Combine(configDir, "Phase3ToolForce.json");

            phase1Config = PhaseConfig.Load(phase1Path, PhaseConfig.Phase1Default());
            phase2Config = PhaseConfig.Load(phase2Path, PhaseConfig.Phase2Default());
            phase3Config = PhaseConfig.Load(phase3Path, PhaseConfig.Phase3Default());

            // 使用 VisionProvider.json 的 endpoint（用户可能走中转站）
            phase1Config.Endpoint = visionEndpoint;
            phase2Config.Endpoint = visionEndpoint;
            phase3Config.Endpoint = visionEndpoint;

            phase1Provider = new SiliconFlowVisionProvider(visionApiKey, phase1Config);
            phase2Provider = new SiliconFlowVisionProvider(visionApiKey, phase2Config);
        }

        private async Task ProcessPendingImagesAsync()
        {
            _visionSuspended = false;
            _suspendReason = null;

            // Phase 0: OCR
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

            // Phase 1: 粗扫（Phase=0 且 OCR 完成）
            if (config.VisionEnabled && _visionAvailable)
            {
                while (IsAlive)
                {
                    var pending = await ImageStorage.GetVisionPendingAsync(config.BatchSize);
                    if (pending.Count == 0) break;
                    var tasks = pending.Select(r => ProcessPhase1WrapperAsync(r));
                    await Task.WhenAll(tasks);
                    if (_visionSuspended) break;
                }
            }

            // Phase 1.5: 下文达标检查（Phase=1 图片）
            if (config.VisionEnabled && _visionAvailable)
            {
                var phase1Images = await ImageStorage.GetByPhaseAsync(1, config.BatchSize);
                foreach (var img in phase1Images)
                {
                    await CheckAndTriggerPhase2Async(img);
                }
            }
        }

        private async Task CheckAndTriggerPhase2Async(Database.ImageRecord img)
        {
            if (img.FirstSeenMessageId == null) return;
            var msg = await ctx.Session.GetMessageByIdAsync(img.FirstSeenMessageId.Value);
            if (msg == null) return;

            var followUps = await ctx.Session.GetMessagesAfterIdAsync(msg.ChannelId, img.FirstSeenMessageId.Value, config.RefineTriggerCount + 1);
            if (followUps.Count < config.RefineTriggerCount) return;

            // 仅信息型图片触发下文精炼（分享/表情不精炼）
            if (img.Classification is "share-photo" or "share-art" or "meme" or "sticker") return;

            // 构建上下文
            var channel = await ctx.Session.GetChannelByIdAsync(msg.ChannelId);
            var type = channel?.Name?.StartsWith("group") == true ? "群聊" : "私聊";
            var followUpText = string.Join("\n", followUps.Select(m => $"[{m.SenderName}]: {m.Content}"));
            var contextText = $"频道类型：{type}，发图者：{msg.SenderName}\n后续消息：\n{followUpText}";

            // 填充 Phase 2 prompt
            var prompt = phase2Config!.PromptTemplate
                .Replace("{ContextText}", contextText)
                .Replace("{Classification}", img.Classification ?? "unknown")
                .Replace("{Focus}", "");

            var path = await ImageStorage.GetModelInputPathAsync(img.Hash);
            if (path == null) return;

            string? output = null;
            try
            {
                output = await phase2Provider!.DescribeImageAsync(path, prompt);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _visionErrors);
                Signal.Warn(LogGroup.Engine, "Phase2(下文)调用失败", new { hash = img.Hash, error = ex.Message });
                return;
            }

            if (string.IsNullOrEmpty(output))
            {
                Interlocked.Increment(ref _visionErrors);
                Signal.Warn(LogGroup.Engine, "Phase2(下文)空输出", new { hash = img.Hash });
                return;
            }

            var (classification, description) = ParseVisionOutput(output);
            await ImageStorage.UpdateDescriptionAsync(img.Hash, description);
            await ImageStorage.UpdateClassificationAsync(img.Hash, classification);
            await ImageStorage.UpdatePhaseAsync(img.Hash, 2);
            Interlocked.Increment(ref _totalProcessed);
            Interlocked.Increment(ref _phase2Count);
            Signal.Event(LogGroup.Engine, "Phase2完成(下文)", new { hash = img.Hash, classification });

            Signal.Event(LogGroup.Engine, "Phase2下文达标触发", new { hash = img.Hash, channelId = msg.ChannelId });
        }

        // ── OCR ──

        private async Task ProcessOcrWrapperAsync(Database.ImageRecord record)
        {
            Interlocked.Increment(ref _activeTasks);
            try { await ProcessOcrAsync(record); }
            finally { Interlocked.Decrement(ref _activeTasks); }
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

        // ── Phase 1 ──

        private async Task ProcessPhase1WrapperAsync(Database.ImageRecord record)
        {
            Interlocked.Increment(ref _activeTasks);
            try { await ProcessPhase1Async(record); }
            finally { Interlocked.Decrement(ref _activeTasks); }
        }

        private async Task ProcessPhase1Async(Database.ImageRecord record)
        {
            if (!config.VisionEnabled || !_visionAvailable || _visionSuspended) return;
            if (record.Phase != 0) return;

            await phase1Semaphore!.WaitAsync();
            try
            {
                if (_visionSuspended) return;

                var path = await ImageStorage.GetModelInputPathAsync(record.Hash);
                if (path == null) return;

                string? output = null;
                for (int attempt = 0; attempt <= config.VisionRetryCount; attempt++)
                {
                    try
                    {
                        output = await phase1Provider!.DescribeImageAsync(path);
                        break;
                    }
                    catch (System.Net.Http.HttpRequestException ex) when (
                        ex.Message.Contains("401") || ex.Message.Contains("403"))
                    {
                        _visionSuspended = true;
                        _suspendReason = $"Phase1 认证失败 ({ex.Message})";
                        Signal.Warn(LogGroup.Engine, "Phase1认证失败暂停", new { hash = record.Hash, error = ex.Message });
                        return;
                    }
                    catch (Exception ex)
                    {
                        Signal.Warn(LogGroup.Engine, "Phase1调用失败", new { hash = record.Hash, attempt = attempt + 1, error = ex.Message });
                        if (attempt < config.VisionRetryCount)
                            await Task.Delay(config.VisionRetryDelayMs);
                    }
                }

                if (string.IsNullOrEmpty(output))
                {
                    Interlocked.Increment(ref _visionErrors);
                    Signal.Warn(LogGroup.Engine, "Phase1空输出", new { hash = record.Hash });
                    return;
                }

                var (classification, description) = ParseVisionOutput(output);

                // 所有类型都保留粗描（模型需要知道图片内容）。仅门控 Phase 2 精炼
                await ImageStorage.UpdateDescriptionAsync(record.Hash, description);
                await ImageStorage.UpdateClassificationAsync(record.Hash, classification);
                await ImageStorage.UpdatePhaseAsync(record.Hash, 1);

                Interlocked.Increment(ref _totalProcessed);
                Interlocked.Increment(ref _phase1Count);
                Signal.Event(LogGroup.Engine, "Phase1完成", new { hash = record.Hash, classification });
            }
            finally
            {
                phase1Semaphore!.Release();
            }
        }

        // ── EventBus: Phase 2 / Phase 3 ──

        private async Task HandleRefineSignalAsync(VisionRefineSignal signal)
        {
            try
            {
                var record = await ImageStorage.GetByHashAsync(signal.Hash);
                if (record == null) return;

                if (signal.TargetPhase == 2 && record.Phase >= 2) return; // 已精炼过
                if (signal.TargetPhase == 3 && record.Phase < 1) return;  // 至少需要 Phase 1

                var provider = phase2Provider!;
                var configForPhase = signal.TargetPhase == 3 ? phase3Config! : phase2Config!;

                // 如果 Phase 3 但 provider 模型不同，用 Phase3Config 重建
                if (signal.TargetPhase == 3 && phase3Config!.Model != phase2Config!.Model)
                    provider = new SiliconFlowVisionProvider(visionApiKey!, phase3Config);

                var prompt = BuildPhasePrompt(signal, record, configForPhase);
                var path = await ImageStorage.GetModelInputPathAsync(signal.Hash);
                if (path == null) return;

                string? output = null;
                try
                {
                    output = await provider.DescribeImageAsync(path, prompt);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _visionErrors);
                    Signal.Warn(LogGroup.Engine, $"Phase{signal.TargetPhase}调用失败", new { hash = signal.Hash, error = ex.Message });
                    return;
                }

                if (string.IsNullOrEmpty(output))
                {
                    Interlocked.Increment(ref _visionErrors);
                    Signal.Warn(LogGroup.Engine, $"Phase{signal.TargetPhase}空输出", new { hash = signal.Hash });
                    return;
                }

                var (classification, description) = ParseVisionOutput(output);

                if (signal.TargetPhase == 2)
                {
                    await ImageStorage.UpdateDescriptionAsync(record.Hash, description);
                    await ImageStorage.UpdateClassificationAsync(record.Hash, classification);
                    await ImageStorage.UpdatePhaseAsync(record.Hash, 2);
                    Interlocked.Increment(ref _phase2Count);
                    Signal.Event(LogGroup.Engine, "Phase2完成(信号)", new { hash = signal.Hash, classification });
                }
                else if (signal.TargetPhase == 3)
                {
                    var newDesc = (record.Description ?? "") + "\n[精炼] " + description;
                    await ImageStorage.UpdateDescriptionAsync(record.Hash, newDesc);
                    await ImageStorage.UpdateClassificationAsync(record.Hash, classification);
                    await ImageStorage.UpdateRefineFocusAsync(record.Hash, signal.Focus ?? "");
                    await ImageStorage.UpdatePhaseAsync(record.Hash, 3);
                    Interlocked.Increment(ref _phase3Count);
                    Signal.Event(LogGroup.Engine, "Phase3完成(工具)", new { hash = signal.Hash, classification });
                }

                Interlocked.Increment(ref _totalProcessed);
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, $"Phase{signal.TargetPhase}处理失败",
                    new { hash = signal.Hash, error = ex.Message });
            }
        }

        private static string BuildPhasePrompt(VisionRefineSignal signal, Database.ImageRecord record, PhaseConfig config)
        {
            return config.PromptTemplate
                .Replace("{ContextText}", signal.ContextText ?? "")
                .Replace("{Classification}", record.Classification ?? "unknown")
                .Replace("{Focus}", signal.Focus ?? "");
        }

        // ── JSON 解析 ──

        private static (string Classification, string Description) ParseVisionOutput(string output)
        {
            output = output.Trim();

            // 1. 尝试直接解析 JSON
            var result = TryParseJson(output);
            if (result != null) return result.Value;

            // 2. 提取 ```json ... ``` 代码块
            var jsonBlock = Regex.Match(output, @"```json\s*\n?(.*?)\n?```", RegexOptions.Singleline);
            if (jsonBlock.Success)
            {
                result = TryParseJson(jsonBlock.Groups[1].Value.Trim());
                if (result != null) return result.Value;
            }

            // 3. 提取 ``` ... ``` 代码块（无语言标注）
            var codeBlock = Regex.Match(output, @"```\s*\n?(.*?)\n?```", RegexOptions.Singleline);
            if (codeBlock.Success)
            {
                result = TryParseJson(codeBlock.Groups[1].Value.Trim());
                if (result != null) return result.Value;
            }

            // 4. 兜底
            return ("unknown", output);
        }

        private static (string, string)? TryParseJson(string text)
        {
            try
            {
                var obj = JObject.Parse(text);
                var classification = obj["classification"]?.ToString()?.Trim() ?? "";
                var description = obj["description"]?.ToString()?.Trim() ?? "";
                classification = NormalizeClassification(classification);
                if (!string.IsNullOrEmpty(description))
                    return (classification, description);
            }
            catch { }
            return null;
        }

        private static string NormalizeClassification(string raw)
        {
            return raw switch
            {
                "info-screenshot" => "info-screenshot",
                "info-photo" => "info-photo",
                "share-photo" => "share-photo",
                "share-art" => "share-art",
                "meme" => "meme",
                "sticker" => "sticker",
                "unknown" => "unknown",
                _ => "unknown"
            };
        }

        // ── Snapshot / Config / Events ──

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
                Config = config,
                Phase1Count = _phase1Count,
                Phase2Count = _phase2Count,
                Phase3Count = _phase3Count
            };
        }

        public void UpdateConfig(VisionEngineConfig newConfig)
        {
            config = newConfig;
            config.Save();
            ocrSemaphore?.Dispose();
            phase1Semaphore?.Dispose();
            ocrSemaphore = new SemaphoreSlim(config.OcrConcurrency);
            phase1Semaphore = new SemaphoreSlim(config.Phase1Concurrency);
        }

        public void OnEvent(EngineEvent e)
        {
            if (e is SignalEvent signal)
            {
                if (signal.SignalName == "new-image")
                    gate.Signal();
                else if (signal.SignalName == "refine-image")
                    OnRefineSignalEvent(signal.Payload);
            }
        }

        private void OnRefineSignalEvent(object? payload)
        {
            try
            {
                var json = payload?.ToString();
                if (string.IsNullOrEmpty(json)) return;
                var obj = JObject.Parse(json);
                var hash = obj["hash"]?.ToString();
                var targetPhase = obj["targetPhase"]?.ToObject<int>() ?? 2;
                var focus = obj["focus"]?.ToString();
                var contextText = obj["contextText"]?.ToString();

                if (string.IsNullOrEmpty(hash)) return;
                _ = HandleRefineSignalAsync(new VisionRefineSignal(hash, targetPhase, focus, contextText));
            }
            catch { }
        }

        public void RequestStop()
        {
            IsAlive = false;
            gate.Signal();
        }
    }
}
