// AgentCoreProcessor/Engine/AdapterEng/AdapterEngine.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Messaging;

namespace AgentCoreProcessor.Engine.AdapterEng;

/// <summary>
/// 适配器托管引擎。统一管理适配器生命周期、频道融合、消息分段、发送优先级。
/// </summary>
internal class AdapterEngine : ISubEngine
{
    public string EngineType => "Adapter";
    public bool IsAlive { get; private set; } = true;
    public bool IsInfrastructure => true;

    private readonly ConcurrentDictionary<int, List<AdapterHandle>> _channelAdapters = new();
    private readonly ConcurrentDictionary<string, AdapterHandle> _allAdapters = new();

    public AdapterEngine() { }

    public void RegisterAdapter(Adapter.IAdapter adapter, IReadOnlyList<int> channelIds, int priority = 0)
    {
        var handle = new AdapterHandle { AdapterId = adapter.Id, Priority = priority, Adapter = adapter };
        _allAdapters[adapter.Id] = handle;
        foreach (var chId in channelIds)
            _channelAdapters.GetOrAdd(chId, _ => new()).Add(handle);
    }

    public AdapterHandle? GetActiveAdapter(int channelId)
    {
        if (!_channelAdapters.TryGetValue(channelId, out var handles))
            return null;
        return handles
            .Where(h => h.IsAlive)
            .OrderBy(h => h.Priority)
            .ThenBy(h => h.AdapterId)
            .FirstOrDefault();
    }

    /// <summary>
    /// 按频道标识解析登记过的适配器。控制台等维护入口用它模拟真实出向，
    /// 不经过入向 RouteMessageAsync（那会重新入库并触发模型会话）。
    /// </summary>
    public Adapter.IAdapter? ResolveAdapter(string channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return null;

        if (channelId.Equals("FileAdapter", StringComparison.OrdinalIgnoreCase))
            return _allAdapters.Values.FirstOrDefault(h => h.Adapter.Platform.Equals("File", StringComparison.OrdinalIgnoreCase))?.Adapter;

        foreach (var handle in _allAdapters.Values.Where(h => h.IsAlive))
        {
            if (handle.Adapter is Adapter.IChannelAwareAdapter aware && aware.OwnsChannel(channelId))
                return handle.Adapter;
        }

        // 数字频道 id → 已登记的频道映射；找不到时回退到唯一适配器（与 AdapterManager.ResolveByChannelId 一致）。
        if (int.TryParse(channelId, out var numeric))
        {
            var active = GetActiveAdapter(numeric);
            if (active != null) return active.Adapter;
        }

        var adapters = _allAdapters.Values.Where(h => h.IsAlive).Select(h => h.Adapter).Distinct().ToList();
        return adapters.Count == 1 ? adapters[0] : null;
    }

    public async Task SendOutgoing(EngineOutgoingMessage engineMsg)
    {
        var handle = GetActiveAdapter(engineMsg.ChannelId);
        if (handle == null) return;

        var adapterMsg = new Adapter.OutgoingMessage
        {
            ChannelId = engineMsg.ChannelId.ToString(),
            Content = engineMsg.Content
        };
        await handle.Adapter.SendMessageAsync(adapterMsg);
    }

    public Task RunAsync() => Task.CompletedTask;
    public void OnEvent(EngineEvent e) { }
    public void RequestStop() => IsAlive = false;
}
