// AgentCoreProcessor/Component/SubAgentAccessAdapter.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Component;

/// <summary>
/// ISubAgentAccess 实现：桥接 SessionEngine 与父 session 通信。
///
/// 维护三张表：
///   _childMailboxes   childSessionId → MailboxInputPort（父→子指令注入）
///   _parentLoopIds    childSessionId → 父 loopId（结果回传+唤醒）
///   _loopIdToChildSession 反查（report_to_parent 从子 loopId 找到自己的 sessionId）
///
/// 父子通信路径（无 CrossRequest 镜像，纯消息驱动）：
///   - 父→子：send_instruction → mailbox.Writer.TryWrite → 下轮 WaitForActivation 立即激活
///   - 子→父：report_to_parent → RuntimeSession.InjectExternal 直接注入父上下文
///   - 子自动完成：OnCompleted 事件钩子 → 父 session InjectExternal + WaitAsync 唤醒
///
/// 子 agent 默认组件包含 sub-agent-runtime（report_to_parent 工具），不含 system-ops（避免无序套娃）。
/// </summary>
internal class SubAgentAccessAdapter : ISubAgentAccess, AgentLilara.PluginSDK.Services.ISessionResource
{
    private readonly Engine.MasterEngine _master;
    private bool _resourceRegistered;

    // 子 agent 邮箱端口。父 session send_instruction 写入，子 session WaitForActivation 读出。
    private readonly ConcurrentDictionary<string, MailboxInputPort> _childMailboxes = new();
    // 子 session 完成的等待任务。WaitAsync 在此阻塞完成。
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _waitTcs = new();

    public SubAgentAccessAdapter(Engine.MasterEngine master)
    {
        _master = master;
    }

    private void EnsureResourceRegistered()
    {
        if (_resourceRegistered) return;
        var se = _master.GetActiveEnginesSnapshot().OfType<SessionEngine>().FirstOrDefault();
        if (se != null)
        {
            se.RegisterSessionResource(this);
            _resourceRegistered = true;
        }
    }

    private SessionEngine? GetSessionEngine()
    {
        var engines = _master.GetActiveEnginesSnapshot();
        return engines.OfType<SessionEngine>().FirstOrDefault();
    }

    /// <summary>子 agent 默认组件：基础输出+文件操作+记忆读写+子 agent 回报工具。</summary>
    private static readonly string[] DefaultSubAgentComponents =
        { "basic-tools", "file-tools", "memory-tools", "sub-agent-runtime" };

    public SubAgentInfo Create(string instruction, SubAgentOptions? options = null)
    {
        EnsureResourceRegistered();
        var se = GetSessionEngine();
        if (se == null)
        {
            Signal.Warn(LogGroup.Engine, "SubAgentAccessAdapter: SessionEngine 未就绪", new { });
            return new SubAgentInfo { SessionId = "", IsAlive = false };
        }

        options ??= new SubAgentOptions();
        var additionalComps = options.ComponentIds == null || options.ComponentIds.Length == 0
            ? DefaultSubAgentComponents
            : options.ComponentIds;

        var spec = new CreateSessionSpec
        {
            Type = "task",
            InitialState = options.InitialState ?? instruction,
            ParentSessionId = options.ParentLoopId,
            AdditionalComponents = additionalComps,
            InputPorts = new()
        };

        var session = se.CreateSession(spec);
        var sessionId = session.SessionId;

        // 附加 mailbox 输入端口（父→子指令通道）
        var mailbox = new MailboxInputPort();
        if (session is RuntimeSession rsMailbox)
            rsMailbox.AddInputPort("mailbox", mailbox);
        _childMailboxes[sessionId] = mailbox;

        // 订阅完成事件
        if (session is RuntimeSession rsComplete)
            rsComplete.OnCompleted += OnChildSessionCompleted;

        Signal.Event(LogGroup.Engine, "子 agent 已创建",
            new { sessionId, parentLoopId = options.ParentLoopId });

        return new SubAgentInfo
        {
            SessionId = sessionId,
            IsAlive = session.IsAlive,
            ParentLoopId = options.ParentLoopId
        };
    }

    public SubAgentInfo? Get(string sessionId)
    {
        EnsureResourceRegistered();
        var se = GetSessionEngine();
        var session = se?.GetSession(sessionId);
        if (session == null) return null;
        return ToInfo(session, sessionId);
    }

    public Task<bool> SendInstructionAsync(string sessionId, string instruction)
    {
        EnsureResourceRegistered();
        if (!_childMailboxes.TryGetValue(sessionId, out var mailbox))
            return Task.FromResult(false);

        var ok = mailbox.TryWrite(instruction);
        if (ok)
        {
            Signal.Event(LogGroup.Engine, "子 agent 指令注入",
                new { sessionId, contentLen = instruction.Length });

            // 顺带主动唤醒子 session
            GetSessionEngine()?.WakeUpSession(sessionId);
        }
        return Task.FromResult(ok);
    }

    public SubAgentStopResult RequestStop(string sessionId)
    {
        EnsureResourceRegistered();
        var se = GetSessionEngine();
        if (se == null) return new SubAgentStopResult { Success = false, BlockReason = "SessionEngine 不可用" };

        var stopResult = se.StopSession(sessionId, "requested by parent");
        return new SubAgentStopResult
        {
            Success = stopResult.Success,
            BlockReason = stopResult.BlockReason,
            BlockingChildren = stopResult.BlockingChildren
        };
    }

    public List<SubAgentInfo> List(string? parentLoopId = null)
    {
        EnsureResourceRegistered();
        var se = GetSessionEngine();
        if (se == null) return new List<SubAgentInfo>();

        // 解析 parentLoopId → 纯 sessionId
        var parentId = parentLoopId?.StartsWith("session:") == true
            ? parentLoopId[8..]
            : parentLoopId;

        var children = parentId != null ? se.GetActiveChildren(parentId) : null;
        // 如果 parentLoopId 是 "channel:N"，需要 SessionEngine 反查 Full claimer
        if (parentId == null && parentLoopId != null && LoopId.IsChannel(parentLoopId, out var parsedCid))
        {
            var claimer = se.GetFullClaimer(parsedCid);
            if (!string.IsNullOrEmpty(claimer)) children = se.GetActiveChildren(claimer);
        }

        var list = new List<SubAgentInfo>();
        if (children == null) return list;
        foreach (var childId in children)
        {
            var s = se.GetSession(childId);
            if (s == null) continue;
            list.Add(ToInfo(s, childId));
        }
        return list;
    }

    public SubAgentDetail? GetDetail(string sessionId)
    {
        EnsureResourceRegistered();
        var se = GetSessionEngine();
        var session = se?.GetSession(sessionId);
        if (session == null) return null;

        var detail = new SubAgentDetail
        {
            SessionId = sessionId,
            IsAlive = session.IsAlive,
            Status = session.Status.ToString(),
            ParentLoopId = session.ParentSessionId,
            RoundCount = 0,
            CreatedAt = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow
        };

        if (session is RuntimeSession rs)
        {
            detail.RoundCount = rs.RoundCountInternal;
            detail.CreatedAt = rs.CreatedAtInternal;
            detail.LastActivity = rs.LastActivityInternal;
            if (!rs.IsAlive)
                detail.FinalOutput = rs.ExtractFinalOutput();
        }
        return detail;
    }

    public bool ReportToParent(string callerLoopId, string content, ReportState state)
    {
        EnsureResourceRegistered();
        // callerLoopId = "session:xxx"，取冒号后段为 childId
        var childId = callerLoopId.StartsWith("session:") ? callerLoopId[8..] : callerLoopId;

        var effectiveParent = GetSessionEngine()?.ResolveEffectiveParent(childId);
        if (string.IsNullOrEmpty(effectiveParent))
        {
            Signal.Warn(LogGroup.Engine, "ReportToParent: 无有效父 session", new { childId });
            return false;
        }

        var parent = GetSessionEngine()?.GetSession(effectiveParent) as RuntimeSession;
        if (parent != null)
        {
            parent.InjectExternal($"[子会话 {childId} 回报] ({state}) {content}");
            Signal.Event(LogGroup.Engine, "子 agent 回报父会话",
                new { childId, parentId = effectiveParent, state = state.ToString(), contentLen = content.Length });
            return true;
        }

        Signal.Warn(LogGroup.Engine, "ReportToParent: 父 session 不存在或非 RuntimeSession",
            new { childId, effectiveParent });
        return false;
    }

    public async Task<bool> WaitAsync(string sessionId, int timeoutMs)
    {
        EnsureResourceRegistered();
        var se = GetSessionEngine();
        var session = se?.GetSession(sessionId);
        if (session == null) return false;
        if (!session.IsAlive) return true;

        var tcs = _waitTcs.GetOrAdd(sessionId,
            _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        if (session is RuntimeSession rs)
        {
            Action<IRuntimeSession>? handler = null;
            handler = _ =>
            {
                rs.OnCompleted -= handler;
                tcs.TrySetResult(true);
            };
            rs.OnCompleted += handler;
        }

        var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        _waitTcs.TryRemove(sessionId, out _);
        return winner == tcs.Task && tcs.Task.IsCompleted;
    }

    // ── 子 session 完成钩子 ──

    private void OnChildSessionCompleted(IRuntimeSession session)
    {
        var sessionId = session.SessionId;

        try
        {
            // 关闭邮箱写入端
            if (_childMailboxes.TryRemove(sessionId, out var mailbox))
                mailbox.Complete();

            // 触发 wait_sub 等待
            if (_waitTcs.TryRemove(sessionId, out var tcs))
                tcs.TrySetResult(true);

            // 由 SessionEngine 统一管理子链清理
            var se = GetSessionEngine();
            se?.OnChildCompleted(sessionId);

            // 通知父 session
            var effectiveParent = se?.ResolveEffectiveParent(sessionId);
            if (!string.IsNullOrEmpty(effectiveParent))
            {
                var parent = se?.GetSession(effectiveParent) as RuntimeSession;
                var finalOutput = (session as RuntimeSession)?.ExtractFinalOutput() ?? "";
                var status = session.Status.ToString();
                var preview = string.IsNullOrEmpty(finalOutput)
                    ? ""
                    : "\n内容预览：" + (finalOutput.Length > 300 ? finalOutput[..300] + "..." : finalOutput);
                parent?.InjectExternal($"[子会话 {sessionId} 已完成] 状态：{status}{preview}");
            }
        }
        catch (Exception ex)
        {
            Signal.Error(LogGroup.Engine, "OnChildSessionCompleted 异常",
                new { sessionId, error = ex.Message });
        }
    }

    /// <summary>
    /// 唤醒父 session 并向其上下文注入系统消息。
    /// 三条路径：
    ///   ① 父 loopId = "session:xxx" → 找到 RuntimeSession 直接 InjectExternal
    ///   ② 父 loopId = "channel:N" 且频道被 WorkingSession Full 占用 → 注入到该 WorkingSession
    ///   ③ 父 loopId = "channel:N" 且无 Full 占用 → 仅 WakeLoop 加速唤醒 ExpressEngine
    /// </summary>
    private void InjectToParent(string parentLoopId, string content)
    {
        RuntimeSession? target = null;

        if (parentLoopId.StartsWith("session:", StringComparison.OrdinalIgnoreCase))
        {
            var sid = parentLoopId[8..];
            target = GetSessionEngine()?.GetSession(sid) as RuntimeSession;
        }
        else if (LoopId.IsChannel(parentLoopId, out var cid))
        {
            var se = GetSessionEngine();
            var claimer = se?.GetFullClaimer(cid);
            if (!string.IsNullOrEmpty(claimer))
                target = se?.GetSession(claimer) as RuntimeSession;
        }

        if (target != null)
        {
            target.InjectExternal($"[系统通知]\n{content}");
            return;
        }

        // 父不是 RuntimeSession（或频道未被 session 占用）：走 ExpressEngine 接力路径
        _master.WakeLoop(parentLoopId);
    }

    private SubAgentInfo ToInfo(IRuntimeSession session, string sessionId)
    {
        var info = new SubAgentInfo
        {
            SessionId = sessionId,
            IsAlive = session.IsAlive,
            ParentLoopId = session.ParentSessionId
        };
        return info;
    }

    // ── ISessionResource 实现 ──

    public Task OnSessionCompactingAsync(string oldSessionId, string newSessionId)
    {
        // Sub-agent parent chain is already handled by SessionEngine's resource pool tree
        // No-op: child mailboxes keyed by childId, not parentId
        return Task.CompletedTask;
    }

    public Task OnSessionFreedAsync(string sessionId)
    {
        // Find children whose effective parent is this session and clean up
        var toClean = _childMailboxes.Keys
            .Where(cid =>
            {
                var se = GetSessionEngine();
                return se?.ResolveEffectiveParent(cid) == sessionId;
            })
            .ToList();
        foreach (var cid in toClean)
        {
            _childMailboxes.TryRemove(cid, out _);
            _waitTcs.TryRemove(cid, out _);
        }
        return Task.CompletedTask;
    }

    public bool HasActiveResources(string sessionId)
    {
        var se = GetSessionEngine();
        if (se == null) return false;
        var children = se.GetActiveChildren(sessionId);
        return children != null && children.Count > 0;
    }
}