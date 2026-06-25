// AgentCoreProcessor/Engine/Extraction/ExtractionEngine.cs
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine.Extraction;

/// <summary>
/// 统一记忆提取 + 监听检测。每频道独立 Worker，引擎无关地从入向咽喉
/// （HandleMessageAsync 入库后）统一计数触发，覆盖 ChannelEngine 与 session 占用两条路。
/// </summary>
internal class ExtractionEngine : ISubEngine
{
    public string EngineType => "Extraction";
    public bool IsAlive { get; private set; } = true;
    public bool IsInfrastructure => true;

    /// <summary>每多少条新消息探一次提取（Worker 内部再按活跃/潜水阈值决定是否真跑）。</summary>
    private const int CheckInterval = 4;

    private readonly ISystemContext _ctx;
    private readonly ConcurrentDictionary<int, ChannelExtractionWorker> _workers = new();
    private readonly ConcurrentDictionary<int, int> _channelCounters = new();
    private readonly List<EavesdropTask> _eavesdropTasks = new();

    public ExtractionEngine(ISystemContext ctx)
    {
        _ctx = ctx;
    }

    private ChannelExtractionWorker GetWorker(int channelId) =>
        _workers.GetOrAdd(channelId, id =>
            new ChannelExtractionWorker(_ctx, id, ChannelStateManager.LoadConfig(id)));

    /// <summary>入向咽喉每条已入库消息调一次。达探测间隔触发对应频道 Worker。</summary>
    public void OnMessage(int channelId)
    {
        var count = _channelCounters.AddOrUpdate(channelId, 1, (_, c) => c + 1);
        if (count >= CheckInterval)
        {
            _channelCounters[channelId] = 0;
            GetWorker(channelId).Trigger(null);
        }

        // 监听检测：关键词粗筛（待实现，见 CheckKeywordMatch）
        // var hits = CheckEavesdrop(channelId);
    }

    /// <summary>WebUI 强制提取入口。</summary>
    public void ForceExtraction(int channelId) => GetWorker(channelId).ForceTrigger(null);

    /// <summary>WebUI 状态查询：提取进度快照（无 Worker 则全 0）。</summary>
    public (bool running, int extractedCount, int lastExtractedId) GetExtractionStatus(int channelId)
    {
        if (_workers.TryGetValue(channelId, out var w))
            return (w.IsRunning, w.ExtractedMessageCount, w.LastExtractedMessageId);
        return (false, 0, 0);
    }

    public void SetAutoExtraction(int channelId, bool enabled) =>
        GetWorker(channelId).SetAutoExtraction(enabled);

    // ── 监听任务（关键词检测尚未实现，留壳）──

    public void AddEavesdropTask(EavesdropTask task)
    {
        lock (_eavesdropTasks) _eavesdropTasks.Add(task);
    }

    public Task RunAsync() => Task.CompletedTask; // 被动引擎，无常驻循环（由 OnMessage 驱动）

    public void OnEvent(EngineEvent e) { }
    public void RequestStop() => IsAlive = false;
}
