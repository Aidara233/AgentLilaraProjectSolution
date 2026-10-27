// AgentCoreProcessor/Engine/Session/RuntimeSession.Bootstrap.cs
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
/// <see cref="RuntimeSession"/> 的部件：bootstrap + 工具结果分发（partial 文件，仅物理拆分）。
/// <para>含 InitActions 启动序列、工具结果的解释接线（<see cref="ToolEffectInterpreter"/>）、
/// 全局提示段收集与上下文变更的应用点。</para>
/// </summary>
internal partial class RuntimeSession
{
    /// <summary>工具副作用结果：是否结束本突发 / 整个会话（类型见 <see cref="ToolEffectInterpreter"/>）。</summary>

    /// <summary>
    /// 执行初始化工具调用（bootstrap）。由 CreateSession 在 host 就绪后**同步**调用——
    /// claim/订阅/连接是入向路由前提，必须在 CreateSession 返回前生效，否则 escalate 后
    /// 新消息因 GetFullClaimer 命中不到而落回 ChannelEngine。结果系统消息缓存供 RunAsync 首轮消费。
    /// </summary>
    public async Task BootstrapAsync()
    {
        if (_initActions.Count == 0) return;

        var resolver = (Func<string, ITool?>)(name =>
            _tools.TryGetValue(name, out var t) ? t :
              _componentHost?.TryGetTool(name) ?? ToolRegistry.Get(name));
        var executor = ToolExecutorFactory.Create(resolver);
        executor.PermissionGroup = _componentLoopType;
        executor.SessionId = SessionId;
        executor.ChannelId = _connected.CurrentChannelId;
        var lines = new List<string>();

        foreach (var action in _initActions)
        {
            var results = await executor.ExecuteAsync(new List<ToolCall> { action });
            var result = results.Count > 0 ? results[0]
                : new ToolResult { Status = "failed", Error = "无结果" };
            await ApplyToolEffectAsync(action, result);

            var argStr = action.Inputs.Count > 0 ? string.Join(", ", action.Inputs) : "";
            var summary = result.IsSuccess
                ? DescribeInitResult(action.Tool, result)
                : $"失败：{result.Error ?? result.Status}";
            lines.Add($"- {action.Tool}({argStr}) → {summary}");
        }

        var msg = "[初始化] 你启动时执行了以下工具调用：\n" + string.Join("\n", lines);
        _bootstrapMessages.Add(new Message { Role = "user", Content = msg });
    }

    /// <summary>为初始化工具结果生成人类可读说明（连接类工具据当前连接状态描述）。</summary>
    private string DescribeInitResult(string tool, ToolResult result)
    {
        return tool switch
        {
            "connect_channel" => _connected.CanSpeak
                ? $"已连接到频道「{_connected.CurrentChannelId}」（可发言）"
                : $"连接频道 {result.Data} 未成功（可能已被占用）",
            "listen_channel" => $"已旁听频道「{_connected.CurrentChannelId}」（只看不说）",
            "disconnect_channel" => "已断开频道连接",
            _ => result.Data ?? "成功"
        };
    }

    /// <summary>
    /// 统一工具副作用分发。正常 agent 循环与 InitActions bootstrap 共用单一来源。
    /// speak/send_media 等 OutputOnly 工具自带发送，不在此重发。
    ///
    /// <para>⚠️ **判定已移到纯函数 <see cref="ToolEffectInterpreter"/>**：这里只做两件事——
    /// 把会话事实取成值（<see cref="BuildToolEffectContextAsync"/>），以及按顺序执行意图。
    /// 这样 step 不再直连 <c>_sessionEngine</c> / <c>_componentHost</c> / <c>_mind</c>（H1 之后的第二处解耦），
    /// 而"哪种工具结果会收尾／终结"第一次有了不建会话也能跑的测试。</para>
    /// </summary>
    private async Task<ToolEffect> ApplyToolEffectAsync(ToolCall call, ToolResult result)
    {
        var context = await BuildToolEffectContextAsync();
        var effect = ToolEffectInterpreter.Interpret(call, result, context, DateTime.UtcNow);

        var endSession = effect.EndSession;

        foreach (var action in effect.Actions)
        {
            switch (action)
            {
                case ToolEffectAction.SendMessage send:
                    await SendOutput(send.Text);
                    break;

                case ToolEffectAction.InjectContext inject:
                    InjectExternal(inject.Text);
                    break;

                case ToolEffectAction.SetWaitUntil wait:
                    SetWaitUntil(wait.Until);
                    break;

                case ToolEffectAction.WriteJournal journal:
                    await WriteJournalFromWaitAsync(journal.Raw);
                    break;

                case ToolEffectAction.ConnectChannel connect:
                    await ConnectChannelAsync(connect.ChannelId, connect.Mode);
                    break;

                case ToolEffectAction.DisconnectChannel:
                    DisconnectChannel();
                    break;

                case ToolEffectAction.CountForceExitAttempt:
                    _forceExitAttempts++;
                    break;

                case ToolEffectAction.Note note:
                    Signal.Event(LogGroup.Engine, note.Message, new { sessionId = SessionId });
                    break;

                case ToolEffectAction.Escalate escalate:
                    // 原实现：创建失败时把 EndSession 降回 false（会话继续跑），只记 warn。
                    if (_sessionEngine is null) { endSession = false; break; }
                    try
                    {
                        await _sessionEngine.CreateFromEscalateAsync(this, escalate.Reason);
                    }
                    catch (Exception ex)
                    {
                        Signal.Warn(LogGroup.Engine, "escalate 创建 working 失败",
                            new { sessionId = SessionId, error = ex.Message });
                        endSession = false;
                    }
                    break;
            }
        }

        return endSession == effect.EndSession ? effect : effect with { EndSession = endSession };
    }

    /// <summary>
    /// 把解释工具结果所需的会话事实取成值。⚠️ **保留原实现的短路顺序**：
    /// 只有在"没有在飞资源"时才会去问组件门（<c>QueryCanExitAsync</c>），
    /// 多问一次是行为差异，而这正是这类重构最容易悄悄改掉的地方。
    /// </summary>
    private async Task<ToolEffectContext> BuildToolEffectContextAsync()
    {
        var children = _sessionEngine?.GetActiveChildren(SessionId);
        var childCount = children?.Count ?? 0;
        var inFlightResources = _sessionEngine?.AnyResourceBlocksExit(SessionId) == true;

        var canExit = true;
        if (!inFlightResources)
            canExit = _componentHost is null || await _componentHost.QueryCanExitAsync();

        return new ToolEffectContext(Type, childCount, inFlightResources, canExit, _forceExitAttempts);
    }

    /// <summary>
    /// 把 wait 的 journal 参数写成经历（L2 + 情绪信号）。
    /// 支持 "[情绪:轴:目标值] 正文" 前缀标记，解析成 EmotionSignals。
    /// </summary>
    private async Task WriteJournalFromWaitAsync(string raw)
    {
        try
        {
            var mind = _sessionEngine?.Mind;
            if (mind?.Writer == null) return;

            var content = raw.Trim();
            string? signalsJson = null;

            // 解析 "[情绪:轴:目标值] 正文"（可多个，如 [情绪:joy:0.4] [情绪:excitement:0.3] 正文）
            if (content.StartsWith("[情绪:", StringComparison.Ordinal))
            {
                var signals = new List<string>();
                var start = 0;
                while (start < content.Length && content[start] == '[')
                {
                    var end = content.IndexOf(']', start);
                    if (end < 0) break;
                    var tag = content[(start + 1)..end];
                    var segs = tag.Split(':');
                    if (segs.Length == 3 && segs[0] == "情绪"
                        && float.TryParse(segs[2], System.Globalization.CultureInfo.InvariantCulture, out var target))
                    {
                        signals.Add($"{{\"Axis\":\"{segs[1]}\",\"Target\":{target.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"Reason\":\"wait journal\"}}");
                    }
                    start = end + 1;
                }
                content = content[start..].Trim();
                if (signals.Count > 0)
                    signalsJson = $"[{string.Join(",", signals)}]";
            }

            var req = new Mind.WriteJournalRequest
            {
                Content = content,
                Kind = "Experience",
                Source = "Session",
                EmotionSignalsJson = signalsJson,
                OccurredAt = DateTime.Now,
            };
            await mind.Writer.WriteBatchAsync(new Mind.WriteBatchRequest
            {
                Journals = new List<Mind.WriteJournalRequest> { req },
            });
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "wait journal 写入失败",
                new { sessionId = SessionId, error = ex.Message });
        }
    }

    /// <summary>
    /// 追加 Global 组件的 prompt 段（情绪/经历注入等）。Global 组件是单例，
    /// 每轮注入一次（带 LoopInfo 区分调用方）。注入段与 Loop 段合并进 [组件通知]。
    /// <para>取段与容错的实现在 <see cref="GlobalPromptSections.CollectPerRound"/>（上下文域），
    /// 这里只留一个薄壳保持调用点不变。</para>
    /// </summary>
    private void AppendGlobalPromptSections(List<string> sections) =>
        sections.AddRange(GlobalPromptSections.CollectPerRound(_services, SessionId, _componentLoopType));

    /// <summary>
    /// 追加 Global 组件的会话开头一次性静态参考段（技能清单等）。仅新会话首轮上下文构建时调用，
    /// 恢复会话跳过（参考段已随首轮持久化在存档历史中，不重复注入）。产物为独立用户消息，非 [组件通知]。
    /// </summary>
    private void AppendStartReferenceSections()
    {
        var section = GlobalPromptSections.CollectStartReference(_services, SessionId, _componentLoopType);
        if (section is null) return;

        _history.Add(new Message { Role = "user", Content = section });
        Signal.Debug(LogGroup.Engine, "会话开头参考注入(一次性)",
            new { sessionId = SessionId, sectionLen = section.Length });
    }

    /// <summary>
    /// 应用上下文层产出的变更意图。**这是唯一允许改工具表的地方**（见 <see cref="ContextMutation"/>）。
    ///
    /// <para>⚠️ 原来这几行写在压缩块里，直接 `_tools.Clear()` 并改 `_agentCore.AdditionalTools`。
    /// 那是一条从上下文层到工具层的反向数据流：拆模块后它会变成看不见的回指，
    /// 而且"谁能改工具表"会散在两个地方。现在意图与应用分离，改法只有这一处。</para>
    /// </summary>
    private void ApplyContextMutation(ContextMutation mutation)
    {
        switch (mutation)
        {
            case LockToolTableTo lockTo:
                // 调用方已判过 _compactTool 非空与 _preLockdownTools 为 null；这里保留一次防御性返回。
                if (_compactTool is null) return;

                _preLockdownTools = new Dictionary<string, ITool>(_tools, StringComparer.OrdinalIgnoreCase);
                _tools.Clear();
                _activeTools.Clear();
                _tools[lockTo.ToolName] = _compactTool;
                _activeTools.Add(lockTo.ToolName);
                // ⚠️ H2：不再写 _agentCore.AdditionalTools。每轮调用时 BuildInvokeRequest()
                // 会从 _tools 取当前表，所以锁定效果由"请求参数"承载，而不是"改对象字段"。
                Signal.Event(LogGroup.Engine, "L3 锁定：工具表只剩 " + lockTo.ToolName,
                    new { sessionId = SessionId, preLockdownCount = _preLockdownTools.Count });
                break;
        }
    }
}
