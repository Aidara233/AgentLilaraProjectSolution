// AgentCoreProcessor/Engine/Extraction/ExtractionEngine.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine.Extraction;

/// <summary>
/// 统一记忆提取 + 监听检测，共用 LLM 调用。
/// 每个频道独立计数，10 条新消息一批处理。
/// </summary>
internal class ExtractionEngine : ISubEngine
{
    public string EngineType => "Extraction";
    public bool IsAlive { get; private set; } = true;
    public bool IsInfrastructure => true;

    private readonly ISystemContext _ctx;
    private Messaging.MessageEngine? _messageEngine;
    private SessionEngine? _sessionEngine;
    private readonly ConcurrentDictionary<int, int> _channelCounters = new();
    private readonly List<EavesdropTask> _eavesdropTasks = new();
    private const int BatchSize = 10;

    public ExtractionEngine(ISystemContext ctx)
    {
        _ctx = ctx;
    }

    public void SetMessageEngine(Messaging.MessageEngine me) => _messageEngine = me;
    public void SetSessionEngine(SessionEngine se) => _sessionEngine = se;

    /// <summary>消息到来时递增计数，达阈值触发批量处理。</summary>
    public void OnMessage(int channelId)
    {
        var count = _channelCounters.AddOrUpdate(channelId, 1, (_, c) => c + 1);
        if (count >= BatchSize)
        {
            _channelCounters[channelId] = 0;
            _ = ProcessBatchAsync(channelId);
        }
    }

    private async Task ProcessBatchAsync(int channelId)
    {
        try
        {
            // 1. 记忆提取（复用现有 MemoryExtractionCore / MemoryService 流程）
            // 2. 监听检测：检查该频道的监听规则
            var channelTasks = _eavesdropTasks
                .Where(t => t.Status == "active")
                .Where(t => t.ChannelIds.Length == 0 || t.ChannelIds.Contains(channelId))
                .ToList();

            foreach (var task in channelTasks)
            {
                // 关键词粗筛
                var hit = CheckKeywordMatch(task.Rules);
                if (hit != null)
                {
                    _sessionEngine?.CreateSession(new CreateSessionSpec
                    {
                        ComponentIds = new[] { "basic-tools" },
                        InitialState = new { task.NotificationTargetId, hit.Hint },
                        OverrideLimits = new SessionLimits { MaxLifetime = TimeSpan.FromMinutes(15), IdleTimeout = TimeSpan.FromMinutes(5) }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ExtractionEngine] Batch error ch={channelId}: {ex.Message}");
        }
    }

    private static EavesdropRule? CheckKeywordMatch(EavesdropRule[] rules)
    {
        foreach (var rule in rules)
        {
            // 实际实现需查询最近消息，此处为占位
            // if (recent.Any(m => rule.Keywords.Any(k => m.Content.Contains(k))))
            //     return rule;
        }
        return null;
    }

    public void AddEavesdropTask(EavesdropTask task)
    {
        lock (_eavesdropTasks) _eavesdropTasks.Add(task);
    }

    public Task RunAsync() => Task.CompletedTask;
    public void OnEvent(EngineEvent e) { }
    public void RequestStop() => IsAlive = false;
}
