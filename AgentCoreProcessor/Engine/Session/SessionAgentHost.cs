// AgentCoreProcessor/Engine/Session/SessionAgentHost.cs
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// RuntimeSession 的上下文宿主。session 的认知 = 挂载的频道端口集合（每根贡献
/// 频道信息+参与者+历史）+ InitialState。working/task 共用，差异只在传入的端口集合。
/// </summary>
internal class SessionAgentHost
{
    private readonly IReadOnlyList<(int channelId, string? channelName, bool preload)> _channelPorts;
    private readonly string? _initialState;
    private readonly ChannelContextService _svc;
    private readonly Dictionary<int, ChannelContextState> _states = new();

    public SessionAgentHost(
        IReadOnlyList<(int channelId, string? channelName, bool preload)> channelPorts,
        string? initialState, ChannelContextService svc)
    {
        _channelPorts = channelPorts;
        _initialState = initialState;
        _svc = svc;
        foreach (var (cid, cname, _) in channelPorts)
            _states[cid] = new ChannelContextState { ChannelId = cid, ChannelName = cname, Mode = "session" };
    }

    public ChannelContextState StateFor(int channelId) => _states[channelId];

    /// <summary>起始上下文：每个 preload 频道端口的上下文块 + InitialState 当前指令。</summary>
    public async Task<List<Message>> BuildStartAsync()
    {
        var msgs = new List<Message>();
        foreach (var (cid, cname, preload) in _channelPorts)
        {
            if (!preload) continue;
            var st = _states[cid];
            var info = ChannelContextService.RenderChannelInfo(cname, cid);
            if (!string.IsNullOrEmpty(info))
                msgs.Add(new Message { Role = "user", Content = info });
            var parts = ChannelContextService.RenderParticipants(cname, cid, await _svc.GetParticipantsAsync(cid));
            if (!string.IsNullOrEmpty(parts))
                msgs.Add(new Message { Role = "user", Content = parts });
            msgs.AddRange(await _svc.BuildHistoryBlockAsync(st));
        }
        if (!string.IsNullOrEmpty(_initialState))
            msgs.Add(new Message { Role = "user", Content = $"[当前任务]\n{_initialState}" });
        return msgs;
    }

    /// <summary>每轮：当前时间 + drained 新消息格式化 + 滞后图片补注。</summary>
    public async Task<List<Message>> BuildRoundAsync(IEnumerable<(int channelId, UserMessage msg, bool isMention)> drained)
    {
        var msgs = new List<Message>
        {
            new() { Role = "user", Content = $"[系统] 当前时间: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss dddd} UTC+8 (Asia/Shanghai)" }
        };
        foreach (var (cid, m, isMention) in drained)
        {
            var formatted = await _svc.FormatIncomingAsync(_states[cid], m, isMention);
            if (formatted != null) msgs.Add(formatted);
        }
        foreach (var (cid, _, _) in _channelPorts)
            msgs.AddRange(await _svc.BuildLaggingImageInjectionsAsync(_states[cid]));
        return msgs;
    }
}
