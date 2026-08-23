// AgentCoreProcessor/Engine/Session/SessionAgentHost.cs
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Util;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// RuntimeSession 的上下文宿主。session 的认知 = 挂载的频道端口集合（每根贡献
/// 频道信息+参与者+历史）+ InitialState。working/task 共用，差异只在传入的端口集合。
/// </summary>
internal class SessionAgentHost
{
    private readonly List<(int channelId, string? channelName, bool preload)> _channelPorts;
    private readonly string? _initialState;
    private readonly ChannelContextService _svc;
    private readonly Dictionary<int, ChannelContextState> _states = new();

    public SessionAgentHost(
        IReadOnlyList<(int channelId, string? channelName, bool preload)> channelPorts,
        string? initialState, ChannelContextService svc)
    {
        _channelPorts = new List<(int, string?, bool)>(channelPorts);
        _initialState = initialState;
        _svc = svc;
        foreach (var (cid, cname, _) in channelPorts)
            _states[cid] = new ChannelContextState { ChannelId = cid, ChannelName = cname, Mode = "session" };
    }

    public ChannelContextState StateFor(int channelId) => _states[channelId];

    /// <summary>当前已挂载的频道 ID 集合。</summary>
    public IEnumerable<int> ChannelIds => _states.Keys;

    /// <summary>
    /// 运行期挂载一个频道（连接动作）。已存在则返回原状态。
    /// preload=false：历史由 ConnectChannelAsync 经 BuildChannelContextAsync 单独注入，
    /// 避免 BuildStartAsync 重复渲染（连接动作与首轮起始上下文构建是两条独立路径）。
    /// </summary>
    public ChannelContextState AttachChannel(int channelId, string? channelName)
    {
        if (!_states.TryGetValue(channelId, out var st))
        {
            st = new ChannelContextState { ChannelId = channelId, ChannelName = channelName, Mode = "session" };
            _states[channelId] = st;
            _channelPorts.Add((channelId, channelName, false));
        }
        return st;
    }

    /// <summary>运行期卸载一个频道（断开动作）。</summary>
    public void DetachChannel(int channelId)
    {
        _states.Remove(channelId);
        _channelPorts.RemoveAll(p => p.channelId == channelId);
    }

    /// <summary>连接某频道时构建其上下文块（频道信息+参与者+历史），用于注入当前轮。</summary>
    public async Task<List<Message>> BuildChannelContextAsync(int channelId)
    {
        var msgs = new List<Message>();
        if (!_states.TryGetValue(channelId, out var st)) return msgs;
        var info = ChannelContextService.RenderChannelInfo(st.ChannelName, channelId);
        if (!string.IsNullOrEmpty(info))
            msgs.Add(new Message { Role = "user", Content = info });
        var parts = ChannelContextService.RenderParticipants(st.ChannelName, channelId, await _svc.GetParticipantsAsync(channelId));
        if (!string.IsNullOrEmpty(parts))
            msgs.Add(new Message { Role = "user", Content = parts });
        msgs.AddRange(await _svc.BuildHistoryBlockAsync(st));
        return msgs;
    }

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
        var now = System.DateTime.Now;
        var msgs = new List<Message>
        {
            new() { Role = "user", Content = $"[系统] 当前时间: {now:yyyy-MM-dd HH:mm:ss dddd} {TimeUtil.TimeZoneAnchorText(now)}" }
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
