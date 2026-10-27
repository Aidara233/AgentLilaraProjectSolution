// AgentCoreProcessor/Engine/Session/ToolEffectInterpreter.cs
using System;
using System.Collections.Generic;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// 一个工具结果对**控制流**的影响：是否收尾本突发、是否终结会话，以及要执行的副作用动作。
/// </summary>
internal readonly record struct ToolEffect(
    bool EndBurst,
    bool EndSession,
    IReadOnlyList<ToolEffectAction> Actions)
{
    public static readonly ToolEffect None = new(false, false, Array.Empty<ToolEffectAction>());
}

/// <summary>
/// 工具结果引发的**副作用意图**——只描述"要做什么"，不在解释器里做。
/// <para>⚠️ 顺序就是执行顺序：原实现里 <c>InjectExternal</c> / <c>SendOutput</c> 发生在返回之前，
/// 这里保持同样的先后（见 <see cref="ToolEffectInterpreter"/> 的说明）。</para>
/// </summary>
internal abstract record ToolEffectAction
{
    /// <summary>往上下文注入一条系统提示（原 <c>InjectExternal</c>）。</summary>
    public sealed record InjectContext(string Text) : ToolEffectAction;

    /// <summary>向当前频道发言（原 <c>SendOutput</c>）。</summary>
    public sealed record SendMessage(string Text) : ToolEffectAction;

    /// <summary>把本会话挂起到指定时刻（原 <c>SetWaitUntil</c>）。</summary>
    public sealed record SetWaitUntil(DateTime Until) : ToolEffectAction;

    /// <summary>写一条经历（原 <c>WriteJournalFromWaitAsync</c>）。</summary>
    public sealed record WriteJournal(string Raw) : ToolEffectAction;

    /// <summary>连接/监听一个频道（原 <c>ConnectChannelAsync</c>）。</summary>
    public sealed record ConnectChannel(int ChannelId, ConnectionMode Mode) : ToolEffectAction;

    /// <summary>断开当前频道（原 <c>DisconnectChannel</c>）。</summary>
    public sealed record DisconnectChannel : ToolEffectAction;

    /// <summary>升级为 working 会话（原 <c>_sessionEngine.CreateFromEscalateAsync</c>）。</summary>
    public sealed record Escalate(string Reason) : ToolEffectAction;

    /// <summary>强制退出计数 +1（原 <c>_forceExitAttempts++</c>）。</summary>
    public sealed record CountForceExitAttempt : ToolEffectAction;

    /// <summary>
    /// 一条**仅供记录**的说明（原实现里的 <c>Signal.Event</c>）。
    /// <para>⚠️ 它单独成一种意图，是因为"何时记一条日志"属于控制流的一部分（顺序敏感），
    /// 而让解释器直接调 Signal 会破坏纯度；交给应用侧记，顺序与原实现一致。</para>
    /// </summary>
    public sealed record Note(string Message) : ToolEffectAction;
}

/// <summary>
/// 解释工具结果时**需要知道的会话事实**——全部是值，不含任何会话对象引用。
///
/// <para>⚠️ 这是把"解释"做成纯函数的关键：原实现直接问
/// <c>_sessionEngine.GetActiveChildren()</c> / <c>AnyResourceBlocksExit()</c> /
/// <c>_componentHost.QueryCanExitAsync()</c>，于是 step 与 4 个部件硬连。
/// 把这些答案**先取成值**再交给解释器，耦合就从"调用"降级成"入参"。</para>
/// </summary>
internal sealed record ToolEffectContext(
    string SessionType,
    int ActiveChildCount,
    bool InFlightResources,
    bool CanExit,
    int ForceExitAttempts)
{
    /// <summary>原实现里 hasInFlightResources 的完整判据（含组件门）。</summary>
    public bool BlocksExit => InFlightResources || !CanExit;
}

/// <summary>
/// **工具结果 → 控制流**的纯函数解释器。
///
/// <para>⚠️ 为什么必须是纯函数：原来这 90 行以实例方法形式直接调 <c>_sessionEngine</c>、
/// <c>_componentHost</c>、<c>_mind</c>、本会话频道连接，一共四条跨部件边。
/// 抽成"值进 → 意图出"之后，那四条边全部消失，而且这段逻辑第一次**可以单测**
/// （不需要建会话、不需要存储、不需要模型）。</para>
///
/// <para>⚠️ **只允许写日志，不允许改状态**：下面的 <c>Signal.Warn</c> 是观察，不是状态变更，
/// 所以不破坏纯度声明。任何"顺手改一下字段"的改动都会让这个类型退回实例方法，
/// 而那正是它要消灭的东西。</para>
///
/// <para>⚠️ **失败的工具不产生任何效果**（<c>!result.IsSuccess</c> ⇒ 空效果）：
/// 否则 stale 的 <c>end_session</c> / <c>wait</c> 会在失败后仍然生效。</para>
/// </summary>
internal static class ToolEffectInterpreter
{
    /// <summary>wait 工具的默认挂起分钟数（与 <c>wait</c> 的参数缺省一致）。</summary>
    private const int DefaultWaitMinutes = 5;

    /// <summary>
    /// 解释一条工具调用 + 结果。
    /// </summary>
    /// <param name="call">工具调用（名称与输入）。</param>
    /// <param name="result">执行结果。</param>
    /// <param name="context">会话事实快照。</param>
    /// <param name="now">"现在"由调用方给，解释器不读时钟。</param>
    public static ToolEffect Interpret(
        ToolCall call, ToolResult result, ToolEffectContext context, DateTime now)
    {
        // 权限组拒绝 / 执行失败的工具不触发副作用，避免 stale end_session / wait 误生效。
        if (!result.IsSuccess) return ToolEffect.None;

        switch (call.Tool)
        {
            case "end_session":
                return InterpretEndSession(result, context);

            case "compact":
                // compact 执行成功 = 新 session 已接管频道，旧 session 退出循环。
                // tool_result 已在主路径追加进历史，"善始善终"。
                return new ToolEffect(EndBurst: false, EndSession: true, Array.Empty<ToolEffectAction>());

            case "wait":
                return InterpretWait(call, now);

            case "escalate":
                return InterpretEscalate(result, context);

            case "connect_channel":
                return int.TryParse(result.Data, out var connId)
                    ? new ToolEffect(false, false, [new ToolEffectAction.ConnectChannel(connId, ConnectionMode.Full)])
                    : ToolEffect.None;

            case "listen_channel":
                return int.TryParse(result.Data, out var listenId)
                    ? new ToolEffect(false, false, [new ToolEffectAction.ConnectChannel(listenId, ConnectionMode.Listen)])
                    : ToolEffect.None;

            case "disconnect_channel":
                return new ToolEffect(false, false, [new ToolEffectAction.DisconnectChannel()]);

            default:
                return ToolEffect.None;
        }
    }

    private static ToolEffect InterpretEndSession(ToolResult result, ToolEffectContext context)
    {
        // ① 还有活跃子 agent → 拒绝，让模型先收尾
        if (context.ActiveChildCount > 0)
        {
            var warn = $"[系统警告] 你还有 {context.ActiveChildCount} 个活跃子 agent，请先 stop_sub_agent 后再 end_session。";
            return new ToolEffect(false, false, [new ToolEffectAction.InjectContext(warn)]);
        }

        // ② 有在飞资源（下载 / SSH）或组件门不放行 → 第一次警告，第二次强制
        if (context.BlocksExit)
        {
            if (context.ForceExitAttempts > 0)
            {
                var text = result.Data ?? "工作完成（强制退出，仍有未完成资源）。";
                return new ToolEffect(false, true,
                    [new ToolEffectAction.Note("end_session force override"), new ToolEffectAction.SendMessage(text)]);
            }

            const string warn =
                "[系统警告] 仍有未完成的下载或 SSH 任务。请先 cancel_download/cancel ssh 任务后再 end_session；" +
                "或再次 end_session 强制退出（不推荐）。";
            return new ToolEffect(false, false,
                [new ToolEffectAction.InjectContext(warn), new ToolEffectAction.CountForceExitAttempt()]);
        }

        // ③ 干净退出
        return new ToolEffect(false, true,
            [new ToolEffectAction.SendMessage(result.Data ?? "工作完成。")]);
    }

    private static ToolEffect InterpretWait(ToolCall call, DateTime now)
    {
        var minutes = DefaultWaitMinutes;
        if (call.Inputs.Count > 1 && int.TryParse(call.Inputs[1], out var parsed))
            minutes = parsed;

        var actions = new List<ToolEffectAction>
        {
            new ToolEffectAction.SetWaitUntil(now.AddMinutes(minutes)),
        };

        // 可选 journal：随手记当下经历（心智引擎 Writer）
        if (call.Inputs.Count > 2 && !string.IsNullOrWhiteSpace(call.Inputs[2]))
            actions.Add(new ToolEffectAction.WriteJournal(call.Inputs[2]));

        return new ToolEffect(EndBurst: true, EndSession: false, actions);
    }

    private static ToolEffect InterpretEscalate(ToolResult result, ToolEffectContext context)
    {
        // 仅 express 会话：同池换活跃 session → working，结束 express 会话。
        // 非 express 会话调用 escalate 视为 no-op（权限组已过滤，此处兜底）。
        if (!string.Equals(context.SessionType, "express", StringComparison.Ordinal))
            return ToolEffect.None;

        return new ToolEffect(false, true,
            [new ToolEffectAction.Escalate(result.Data ?? "")]);
    }
}
