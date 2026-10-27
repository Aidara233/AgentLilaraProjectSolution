// AgentCoreProcessor/Engine/Context/SessionContextHost.cs
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Util;

namespace AgentCoreProcessor.Engine.Context;

/// <summary>
/// 一个会话的**上下文宿主**：session 的认知 = 挂载的频道端口集合（每根贡献
/// 频道信息 + 参与者 + 历史）+ InitialState。working / task 共用，差异只在传入的端口集合。
///
/// <para>⚠️ <b>它是上下文宿主，不是会话宿主</b>——名字里的 "Host" 曾让人以为是会话的生命周期宿主，
/// 而它做的三件事全是上下文：<see cref="BuildStartAsync"/>（起始）、
/// <see cref="BuildRoundAsync"/>（每轮）、<see cref="BuildChannelContextAsync"/>（连接频道时）。
/// 所以它住在 <c>Engine/Context</c>，与 <see cref="ChannelContextService"/> 同域。</para>
///
/// <para>⚠️ <b>它按设计就不该被会话反向持有</b>：会话只通过 <c>SetContextHost</c> 注入一次，
/// 之后只调它的方法、不读它的内部集合。这条约束是 A3 把它从 <c>Engine/Session</c> 搬出来的理由——
/// 搬出来之后，"它属于上下文域"就由命名空间表达了，而不是靠注释。</para>
/// </summary>
internal class SessionContextHost : AgentCoreProcessor.Engine.Session.ISessionContext
{
    private readonly List<(int channelId, string? channelName, bool preload)> _channelPorts;
    private readonly string? _initialState;
    private readonly ChannelContextService _svc;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<int, ChannelContextState> _states = new();

    public SessionContextHost(
        IReadOnlyList<(int channelId, string? channelName, bool preload)> channelPorts,
        string? initialState, ChannelContextService svc, Func<DateTime>? now = null)
    {
        _channelPorts = new List<(int, string?, bool)>(channelPorts);
        _initialState = initialState;
        _svc = svc;
        _now = now ?? (() => DateTime.Now);
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

    /// <summary>连接某频道时构建其上下文块（频道信息 + 参与者 + 历史），用于注入当前轮。</summary>
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
        var now = _now();
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
