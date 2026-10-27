// AgentCoreProcessor/Engine/Session/SessionEngine.Escalate.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Engine.Messaging;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// <see cref="SessionEngine"/> 的部件：express 会话升级与空闲回收（partial 文件，仅物理拆分，类型仍是一个）。
/// <para>含频道 express 会话创建、escalate 同池换活跃 session、池占用原地移交与 express 空闲回收。</para>
/// </summary>
internal partial class SessionEngine
{
    /// <summary>
    /// 为频道创建 express 会话（频道层调度：无占用消息到达 / 冷启动）。
    /// express 会话 AutoClaim Full → 顶层池持有 claim。频道已被会话占用则返回 null。
    /// 直接 claim + 订阅 normal/mention 端口（不走 connect_channel init-action——
    /// 该工具需 permission 白名单且与 AutoClaim 重复；express 是轻量被动响应，端口即连接）。
    /// 会话身份/模式提示词由 SessionTypeProfile.PromptFiles（express_workflow.txt）加载，InitialState 留空。
    /// </summary>
    public IRuntimeSession? CreateExpressSessionForChannel(int channelId)
    {
        if (channelId <= 0) return null;
        lock (_poolLock)
        {
            if (_pools.TryGetValue(channelId, out var pool) && pool.HasFullClaim)
                return null;
        }
        return CreateSession(new CreateSessionSpec
        {
            Type = "express",
            AutoClaimChannel = true,
            InputPorts = new Dictionary<string, PortSpec>(StringComparer.OrdinalIgnoreCase)
            {
                // express 全量占用：normal 攒权重 + mention 即时。端口即连接（AutoClaim 用首个 channelId）。
                ["ch0:normal"] = new PortSpec { ChannelId = channelId, SubStream = "normal", Weight = 5, PreloadHistory = false },
                ["ch0:mention"] = new PortSpec { ChannelId = channelId, SubStream = "mention", Weight = 100, PreloadHistory = false },
            },
            OutputPorts = new Dictionary<string, string>(),
            InitialState = null,
        });
    }

    /// <summary>
    /// express 会话 escalate → 同池换活跃 session 为 working 会话。claim 原地不动（不释放、不重取）。
    /// 镜像 CreateFromCompact：先 TakeOver 预定新 working id，CreateSession(fixedSessionId) 幂等承接。
    /// </summary>
    public async Task<IRuntimeSession> CreateFromEscalateAsync(RuntimeSession expressSession, string reason)
    {
        var newId = Guid.NewGuid().ToString("N")[..12];
        var channelId = expressSession.ConnectedChannel.CurrentChannelId;
        if (channelId <= 0)
            throw new InvalidOperationException($"express 会话 {expressSession.SessionId} escalate 时未连接频道");

        if (!TakeOverChannelPool(channelId, newId, expressSession.SessionId))
            throw new InvalidOperationException($"express 会话 {expressSession.SessionId} 无法接管频道 {channelId}：占用已丢失或接管冲突");

        var spec = new CreateSessionSpec
        {
            Type = "channel",
            AutoClaimChannel = true,
            InputPorts = new Dictionary<string, PortSpec>(StringComparer.OrdinalIgnoreCase)
            {
                // working 全量占用：escalate 后同池已 TakeOver 到本 id，端口即连接（AutoClaim 幂等命中）
                ["ch0:normal"] = new PortSpec { ChannelId = channelId, SubStream = "normal", Weight = 5, PreloadHistory = false },
                ["ch0:mention"] = new PortSpec { ChannelId = channelId, SubStream = "mention", Weight = 100, PreloadHistory = false },
            },
            OutputPorts = new Dictionary<string, string>(),
            // 模式身份提示词由 channel.json PromptFiles 加载；此处只放实例特有声明的 escalate 原因
            InitialState = $"用户请求切换到工作模式，原因：{reason}",
            ParentSessionId = expressSession.SessionId,
        };

        var working = (RuntimeSession)CreateSession(spec, fixedSessionId: newId);

        Signal.Event(LogGroup.Engine, "express escalate → working（同池换活跃 session）",
            new { channelId, expressId = expressSession.SessionId, workingId = working.SessionId });
        return working;
    }

    /// <summary>
    /// 同池换活跃 session（escalate / compact 专用）：占用状态原地不动，不释放、不重取。
    /// express escalate 到 working 时两者共享同一池，claim 从 express 平滑移交 working。
    /// </summary>
    public bool TakeOverChannelPool(int channelId, string newSessionId, string? expectedSessionId = null)
    {
        lock (_poolLock)
        {
            if (!_pools.TryGetValue(channelId, out var pool) || !pool.HasFullClaim)
                return false;
            if (expectedSessionId is not null && pool.ActiveSessionId != expectedSessionId)
                return false;
            if (_poolByMember.TryGetValue(newSessionId, out var existing) && !ReferenceEquals(existing, pool))
                return false;
            pool.TakeOver(newSessionId);
            _poolByMember[newSessionId] = pool;
            return true;
        }
    }

    /// <summary>
    /// express 会话空闲回收（30s 监控循环调用）。空闲超时 = ImpulseConfig.ColdTimeoutSeconds（默认 600s）。
    /// 只杀 Type="express" 会话，绝不误伤 working/task。回收后池释放 claim，频道回无占用。
    /// </summary>
    private void ReclaimIdleExpressSessions()
    {
        var idleThreshold = TimeSpan.FromSeconds(_ctx.ImpulseConfig.ColdTimeoutSeconds);
        var now = DateTime.UtcNow;
        List<RuntimeSession> idle;
        lock (_sessions)
        {
            idle = _sessions.Values
                .Where(s => s.Type == "express" && s.IsAlive)
                .Where(s => (now - s.LastActivityInternal) > idleThreshold)
                .ToList();
        }
        foreach (var rs in idle)
        {
            Signal.Event(LogGroup.Engine, "express 会话空闲回收",
                new { sessionId = rs.SessionId, idleSeconds = (int)(now - rs.LastActivityInternal).TotalSeconds });
            rs.RequestStop("express idle reclaim");
        }
    }
}
