// AgentCoreProcessor/Engine/Session/RuntimeSession.ChannelConnect.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Axes;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Engine.Messaging;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Util;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// <see cref="RuntimeSession"/> 的部件：频道连接（partial 文件，仅物理拆分）。
/// <para>连接 = 统一动作，同时管输入（订阅+历史）、输出（speak 去向）、占用（claim→停 express）；
/// 独占单频道，连新的先断旧的。connect_channel / listen_channel / disconnect_channel 与
/// InitActions bootstrap 都经 <see cref="ToolEffectInterpreter"/> 走到这里，逻辑单一来源。</para>
/// <para>⚠️ 两端共享的字段（_pendingInjections / _bootstrapMessages / _spec）仍留在主文件。</para>
/// </summary>
internal partial class RuntimeSession
{
    /// <summary>
    /// 连接到频道。Full：claim 占用+停 express+订阅 normal+mention+可发言；Listen：仅订阅 normal、不占用、不可发言。
    /// 连接前先断开当前连接（独占单频道）。连接后载入该频道历史到下一轮上下文。
    /// </summary>
    public async Task ConnectChannelAsync(int channelId, ConnectionMode mode)
    {
        if (channelId <= 0 || _sessionEngine == null || _messageEngine == null) return;

        // 独占：先断开当前连接
        if (_connected.CurrentChannelId != 0)
            DisconnectChannel();

        var level = mode == ConnectionMode.Full ? ClaimLevel.Full : ClaimLevel.Listen;
        var claimed = _sessionEngine.ClaimChannel(SessionId, channelId, level);
        if (!claimed && mode == ConnectionMode.Full)
        {
            _pendingInjections.Add(new Message { Role = "user",
                Content = $"[系统] 连接频道 {channelId} 失败：该频道已被其他会话占用。" });
            return;
        }

        // 订阅消息：Full 收 normal+mention，Listen 仅收 normal
        var normalPort = _sessionEngine.SubscribeChannel(channelId, "normal", 5);
        _inputs[$"ch{channelId}:normal"] = normalPort;
        _connectedPorts.Add(normalPort);
        if (mode == ConnectionMode.Full)
        {
            var mentionPort = _sessionEngine.SubscribeChannel(channelId, "mention", 100);
            _inputs[$"ch{channelId}:mention"] = mentionPort;
            _connectedPorts.Add(mentionPort);
        }

        _connected.Set(channelId, mode);

        // 载入频道上下文（信息+参与者+历史）到下一轮
        if (_host != null)
        {
            var channel = await _sessionEngine.GetChannelByIdAsync(channelId);
            _host.AttachChannel(channelId, channel?.Name);
            try { _pendingInjections.AddRange(await _host.BuildChannelContextAsync(channelId)); }
            catch (Exception ex) { Signal.Warn(LogGroup.Engine, "连接频道历史载入失败", new { sessionId = SessionId, channelId, error = ex.Message }); }
        }

        Signal.Event(LogGroup.Engine, "Session 连接频道",
            new { sessionId = SessionId, channelId, mode = mode.ToString() });

        // 连接状态即时落地，避免崩溃丢失 claim（§5.9）
        PersistStateSync();
    }

    /// <summary>断开当前连接频道：退订端口、移除 InputPort、释放 claim、清状态、host 卸载。</summary>
    public void DisconnectChannel()
    {
        var channelId = _connected.CurrentChannelId;
        if (channelId == 0) return;

        foreach (var port in _connectedPorts)
            _messageEngine?.Unsubscribe(port);
        _connectedPorts.Clear();
        var toRemove = _inputs.Keys.Where(k => k.StartsWith($"ch{channelId}:")).ToList();
        foreach (var k in toRemove) _inputs.Remove(k);

        _sessionEngine?.ReleaseChannel(SessionId, channelId);
        _host?.DetachChannel(channelId);
        _connected.Clear();

        Signal.Event(LogGroup.Engine, "Session 断开频道", new { sessionId = SessionId, channelId });

        // 断开状态即时落地，避免崩溃后 claim 状态与存档不一致（§5.9）
        PersistStateSync();
    }

    /// <summary>同步持久化 state.json（连接/断开兜底，best-effort 不阻塞）。</summary>
    private void PersistStateSync()
    {
        if (_persister == null) return;
        try { _persister.SaveStateAsync(BuildStateSnapshot()).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "连接状态持久化失败（不阻塞）",
                new { sessionId = SessionId, error = ex.Message });
        }
    }
}
