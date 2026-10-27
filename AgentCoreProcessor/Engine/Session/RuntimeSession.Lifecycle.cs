// AgentCoreProcessor/Engine/Session/RuntimeSession.Lifecycle.cs
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
/// <see cref="RuntimeSession"/> 的部件：lifecycle（停止/唤醒/超时/外部注入）（partial 文件，仅物理拆分，类型仍是一个）。
/// <para>⚠️ 拆文件是为了让一个部件一个文件可读、可对照 <c>session-decomposition.md</c> 的 20 部件表；
/// 真正的类型边界（各自独立类）要到 C 阶段拆 csproj 时才动。</para>
/// </summary>
internal partial class RuntimeSession
{
    private void RestoreLifecycleSnapshot(SessionStateSnapshot state)
    {
        Status = state.Status == SessionStatus.Crashed ? SessionStatus.Restored : state.Status;
        _isRestored = true;
        _createdAt = state.CreatedAt;
        WaitUntil = state.WaitUntil;
        _lastActivity = state.LastActivity;


    }

    private void CaptureLifecycleSnapshot(SnapshotWriter writer)
    {
        writer.Set(s => s.SessionId, SessionId);
        writer.Set(s => s.Status, Status);
        writer.Set(s => s.CreatedAt, _createdAt);
        writer.Set(s => s.WaitUntil, WaitUntil);
        writer.Set(s => s.LastActivity, _lastActivity);
    }

    public void RequestStop(string reason)
    {
        Status = SessionStatus.Completed;
        _cts?.Cancel();
    }

    /// <summary>测试退出清理用：静默置 Completed（不取消循环、不触发 OnCompleted/Resurrector），仅改状态供落盘。</summary>
    internal void MarkCompletedForCleanup() => Status = SessionStatus.Completed;

    public void SetWaitUntil(DateTime? until) => WaitUntil = until;
    public void TouchActivity() => _lastActivity = DateTime.UtcNow;
    /// <summary>外部信号唤醒（由 wakeLoop 回调触发，如异步任务完成）。</summary>
    public void WakeUp() => _wakeSignaled = true;

    /// <summary>
    /// 恢复后的 wait 到期重调度专用：注入 wait timeout 提示并唤醒。
    /// 与 WaitForActivationAsync 的到期检查（InjectMessage timeout）产出一致，
    /// 但不碰 WaitForActivationAsync 内部超长 Task.Delay（Task.Delay 数值溢出会抛
    /// TimeOutException，把会话打 Crashed——历史超长 wait 恢复的已知坑）。
    /// </summary>
    internal void InjectWaitTimeoutExpired()
    {
        InjectMessage(new { type = "timeout", wait_reason = "wait timeout expired" });
        _wakeSignaled = true;
    }

    /// <summary>
    /// 外部直接注入系统消息（如子 session 回报结果、wait timeout 提示等）。
    /// 写入 _pendingInjections，由下一轮 agent 推理前消费；同时触发 WakeUp 唤醒等待中的 session。
    /// </summary>
    internal void InjectExternal(string content)
    {
        _pendingInjections.Add(new Message { Role = "user", Content = content });
        _wakeSignaled = true;
        Signal.Debug(LogGroup.Engine, "外部消息注入",
            new { sessionId = SessionId, contentLen = content.Length });
    }

    /// <summary>当前累计轮次（供 SubAgentAccessAdapter 查询子 session 状态）。</summary>
    internal int RoundCountInternal => _roundCount;
    /// <summary>创建时间（供 SubAgentAccessAdapter 查询存活时长）。</summary>
    internal DateTime CreatedAtInternal => _createdAt;
    /// <summary>最后活动时间（供 SubAgentAccessAdapter 查询）。</summary>
    internal DateTime LastActivityInternal => _lastActivity;

    /// <summary>提取 session 最终输出（最后一条 assistant 消息文本）。供子 session 完成通知使用。</summary>
    internal string? ExtractFinalOutput()
    {
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            var m = _history[i];
            if (m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
                return m.Content;
        }
        return null;
    }

    // ── Agent 循环 ──
}
