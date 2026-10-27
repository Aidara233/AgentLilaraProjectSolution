// AgentCoreProcessor/Engine/Session/RuntimeSession.Activation.cs
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

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// <see cref="RuntimeSession"/> 的部件：Activation（partial 文件，仅物理拆分，类型仍是一个）。
/// <para>⚠️ 拆文件的目的是让一个部件一个文件可读、可对照 <c>session-decomposition.md</c> 的 20 部件表；
/// 真正的类型边界（各自独立类）要到 C 阶段拆 csproj 时才动。</para>
/// </summary>
internal partial class RuntimeSession
{
    private string _activationReason = "message";

    private async Task<bool> WaitForActivationAsync(CancellationToken ct)
    {
        _activationReason = "message";
        var activated = await WaitForActivationCoreAsync(ct);
        _activationShadow?.Compare(activated, activated ? _activationReason : "cancelled");
        _activationShadow?.Synchronize(_inputs);
        return activated;
    }

    /// <summary>原判定逻辑（影子对比期间保持不变）。</summary>
    private async Task<bool> WaitForActivationCoreAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _activationShadow?.Observe(_inputs);
            // ① 非消息端口（系统注入/timeout/signal）有数据 → 立即激活
            foreach (var (name, port) in _inputs)
            {
                if (port is not MessageInputPort && port.TryPeek(out _))
                {
                    _activationReason = "system-input";
                    Signal.Debug(LogGroup.Engine, "WaitForActivation 唤醒：系统端口有输入", new { sessionId = SessionId, port = name });
                    return true;
                }
            }

            // ② @提及端口 (Weight >= 100) 有数据 → 立即激活
            foreach (var (name, port) in _inputs)
            {
                if (port is MessageInputPort mp && mp.Weight >= 100 && mp.TryPeek(out _))
                {
                    ResetAllMpQueueCounts();
                    Signal.Debug(LogGroup.Engine, "WaitForActivation 唤醒：@提及端口有输入",
                        new { sessionId = SessionId, port = name, weight = mp.Weight });
                    return true;
                }
            }

            // ③ 普通消息端口 (Weight < 100)：累积权重 >= 100 才激活（Weight=5 需 20 条）
            int totalWeight = 0;
            foreach (var (name, port) in _inputs)
            {
                if (port is MessageInputPort mp && mp.Weight < 100)
                    totalWeight += mp.QueueCount * mp.Weight;
            }
            if (totalWeight >= 100)
            {
                ResetAllMpQueueCounts();
                Signal.Debug(LogGroup.Engine, "WaitForActivation 唤醒：累积权重达标",
                    new { sessionId = SessionId, totalWeight });
                return true;
            }

            // ④ 检查 WaitUntil 是否到期
            if (WaitUntil != null && DateTime.UtcNow >= WaitUntil.Value)
            {
                _activationReason = "timeout";
                Signal.Debug(LogGroup.Engine, "WaitForActivation 唤醒：WaitUntil 到期", new { sessionId = SessionId });
                InjectMessage(new { type = "timeout", wait_reason = "wait timeout expired" });
                return true;
            }

            // ⑤ 外部唤醒信号（异步任务完成/跨循环通知）
            if (_wakeSignaled)
            {
                _activationReason = "wake-signal";
                _wakeSignaled = false;
                Signal.Debug(LogGroup.Engine, "WaitForActivation 唤醒：外部信号", new { sessionId = SessionId });
                return true;
            }

            await Task.Delay(200, ct);
        }
        Signal.Debug(LogGroup.Engine, "WaitForActivation 退出：ct 取消", new { sessionId = SessionId });
        return false;
    }

    private void ResetAllMpQueueCounts()
    {
        foreach (var (_, port) in _inputs)
        {
            if (port is MessageInputPort mp)
                mp.ResetQueueCount();
        }
    }
}
