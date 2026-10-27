// AgentCoreProcessor/Engine/Session/SessionEngine.Assembly.cs
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
/// <see cref="SessionEngine"/> 的部件：建会话与冷启动恢复（partial 文件，仅物理拆分，类型仍是一个）。
/// <para>含 CreateSession 两个重载、组件清单计算、compact 承接与 RestoreAll 存档恢复。</para>
/// </summary>
internal partial class SessionEngine
{
    // ── Session CRUD ──

    public IRuntimeSession CreateSession(CreateSessionSpec spec)
        => CreateSession(spec, fixedSessionId: null);

    /// <summary>
    /// 创建会话。fixedSessionId 仅供 compact 承接用（新 session 在创建前已由池 TakeOver 预定 id，
    /// 必须与池中一致，否则 connect_channel 的幂等 claim 不命中）。其余路径传 null（自动生成）。
    /// </summary>
    internal IRuntimeSession CreateSession(CreateSessionSpec spec, string? fixedSessionId)
    {
        var profile = SessionTypeProfiles.Get(spec.Type);
        // 计算 finalComponents
        var finalComponents = ComputeFinalComponents(spec, profile);

        var session = fixedSessionId != null
            ? new RuntimeSession(spec, fixedSessionId, finalComponents)
            : new RuntimeSession(spec, finalComponents);
        session.SetCurrency(_currency!);
        session.SetSessionEngine(this);
        session.OnCompleted += OnRuntimeSessionCompleted;
        InitializeGraphMount(session);

        // 复活善后：订阅 session 完成事件（心智引擎整理写入）
        if (Mind?.Resurrector != null)
            Mind.Resurrector.Attach(session);

        // 会话身份统一为裸 sessionId（LoopId 概念已移除，channel:N 前缀不再产生）。
        var sessionId = session.SessionId;

        // 加载组件
        try
        {
            session.InitComponents(
                sessionId,
                _ctx.ModuleBus,
                _ctx.ComponentServices,
                () => session.WakeUp());
        }
        catch (Exception ex)
        {
            // 组件加载失败不阻塞 Session 创建
            Signal.Error(LogGroup.Engine, "Session 组件初始化失败", new { sessionId = session.SessionId, error = ex.Message });
        }

        _sessions[session.SessionId] = session;

        // 注入多核配置（白名单 + 默认核）和 MessageEngine
        session.ConfigureCores(profile.AllowedCoreConfigs, profile.DefaultCoreConfig);
        var messageEngine = _ctx.GetActiveEnginesSnapshot()
            .OfType<Messaging.MessageEngine>().FirstOrDefault();
        if (messageEngine != null) session.SetMessageEngine(messageEngine);

        // 根据 PortSpec 创建输入端口并订阅 MessageEngine
        foreach (var (portName, portSpec) in spec.InputPorts)
        {
            if (messageEngine != null)
            {
                var port = messageEngine.Subscribe(portSpec.ChannelId, portSpec.SubStream, portSpec.Weight);
                session.AddInputPort(portName, port);
            }
        }

        // 自动 ClaimChannel
        if (spec.AutoClaimChannel && spec.InputPorts.Count > 0)
        {
            var firstChannel = spec.InputPorts.First().Value.ChannelId;
            var claimed = ClaimChannel(session.SessionId, firstChannel, ClaimLevel.Full);
            // 同步连接状态：speak 目标频道 + escalate 读 CurrentChannelId 都依赖 _connected。
            // 无 connect_channel init-action 时（express 直连端口），此处是唯一设置点。
            if (claimed)
                session.ConnectedChannel.Set(firstChannel, ConnectionMode.Full);
            Signal.Event(LogGroup.Engine, "Session AutoClaim 频道",
                new { sessionId = session.SessionId, channelId = firstChannel, claimed, claimer = GetFullClaimer(firstChannel) });
        }

        // 构建 SessionContextHost（同步）：InitActions 的 connect_channel 需要 host 已就绪才能挂载频道上下文
        // ⚠️ 它是**上下文宿主**（Engine/Context），不是会话宿主；改名的理由见该类型的注释。
        try
        {
            var channelPorts = new List<(int channelId, string? channelName, bool preload)>();
            foreach (var (_, portSpec) in spec.InputPorts)
            {
                var ch = _ctx.Session.GetChannelByIdAsync(portSpec.ChannelId).GetAwaiter().GetResult();
                channelPorts.Add((portSpec.ChannelId, ch?.Name, portSpec.PreloadHistory));
            }

            var dataSource = new SystemContextDataSource(_ctx);
            var svc = new ChannelContextService(dataSource);
            var initialStateText = spec.InitialState is string s ? s
                : spec.InitialState != null ? Newtonsoft.Json.JsonConvert.SerializeObject(spec.InitialState)
                : null;
            var host = new SessionContextHost(channelPorts, initialStateText, svc);
            session.SetContextHost(host);
            Signal.Event(LogGroup.Engine, "Session 上下文宿主就绪",
                new { sessionId = session.SessionId, channelPorts = channelPorts.Count });
        }
        catch (Exception ex)
        {
            Signal.Error(LogGroup.Engine, "Session AgentHost 初始化失败", new { sessionId = session.SessionId, error = ex.Message });
        }

        // 执行 InitActions bootstrap（同步）：claim/订阅/连接是入向路由前提，必须在 CreateSession
        // 返回前生效，否则触发者（escalate）返回后新消息因 claim 未建立而落回 ChannelEngine。
        try
        {
            session.BootstrapAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Signal.Error(LogGroup.Engine, "Session Bootstrap 失败", new { sessionId = session.SessionId, error = ex.Message });
        }

        // 写 spec.json + 注入 persister（持久化三件套就绪）
        // spec.json 是不可变存档，创建时一次写入；state.json 由 RunAsync 内每轮原子重写
        try
        {
            var persister = new SessionPersistence(session.SessionId);
            persister.SaveSpecAsync(spec, DateTime.UtcNow).GetAwaiter().GetResult();
            session.SetPersister(persister);
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "Session spec.json 写入失败（持久化降级）",
                new { sessionId = session.SessionId, error = ex.Message });
        }

        // 启用压缩（L1/L2 文字注入；L3 锁定等 Phase 5 注入 CompactTool 后自动激活）
        session.InitCompression();

        // 启动 Session
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource();
                await session.RunAsync(cts.Token);
                Signal.Event(LogGroup.Engine, "Session RunAsync 返回，即将释放 claim",
                    new { sessionId = session.SessionId, status = session.Status.ToString() });
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "Session 崩溃",
                    new { sessionId = session.SessionId, error = ex.Message, stack = ex.StackTrace });
            }
            finally
            {
                // Session 结束时释放占用
                ReleaseSessionClaims(session.SessionId);
                Signal.Event(LogGroup.Engine, "Session claims 已释放", new { sessionId = session.SessionId });
            }
        });

        // 池树登记（仅当 ParentSessionId 非空）：子 agent → 新建子池挂到父会话所在池。
        // compact 场景（fixedSessionId 已在创建前 TakeOver 进同池）_poolByMember 已含新 id → 跳过。
        if (!string.IsNullOrEmpty(spec.ParentSessionId)
            && !_poolByMember.ContainsKey(session.SessionId))
            CreateSubPool(session.SessionId, spec.ParentSessionId);

        return session;
    }

    /// <summary>
    /// 计算会话的最终组件清单（CreateSession 与 RestoreAll 共用，保证恢复装配与新建一致）。
    /// ResolvedComponentIds（compact 承接）优先；否则类型文件 Components ± Additional/Excluded。
    /// </summary>
    private static string[] ComputeFinalComponents(CreateSessionSpec spec, SessionTypeProfile profile)
    {
        if (spec.ResolvedComponentIds != null)
            return spec.ResolvedComponentIds;
        // 组件清单即会话类型的唯一装配源（Storage/SessionTypes/{type}.json Components）
        var set = new HashSet<string>(profile.Components, StringComparer.OrdinalIgnoreCase);
        if (spec.AdditionalComponents != null) set.UnionWith(spec.AdditionalComponents);
        if (spec.ExcludedComponents != null) set.ExceptWith(spec.ExcludedComponents);
        return set.ToArray();
    }

    /// <summary>
    /// compact 工具专属：旧 session 调 compact(summary) 触发新会话承接。
    /// 新 SessionId（新 GUID），ParentSessionId = 旧 sessionId，
    /// InitialState = "[上一会话摘要]\n{summary}\n\n请继续工作"，轮次接续旧 session。
    /// 同步生效：ClaimChannel 在 CreateSession 内同步完成，新 session 立马接管频道。
    /// </summary>
    public async Task<IRuntimeSession> CreateFromCompact(RuntimeSession oldSession, string summary)
    {
        // 同池换活跃 session：找到旧 session 所在的池，把活跃 session 预定为新 session id。
        // 占用状态原地不动（不释放、不重取）——这是"占用状态在池里"的核心语义。
        // 新 session 创建后 connect_channel 会幂等命中（SessionPool.Claim 同 id 重入成功），
        // 不再有 claim 短暂释放的中间态（原 ReleaseSessionClaims → 重新 claim）。
        var newId = Guid.NewGuid().ToString("N")[..12];
        var oldPool = FindPoolBySession(oldSession.SessionId);
        oldPool?.TakeOver(newId);

        // spec.InputPorts 留空：build port spec 全靠 InitActions.connect_channel 在 BootstrapAsync
        // 阶段一次性完成 claim + 订阅 + 频道上下文载入 —— 与 escalate 路径行为对齐，
        // 避免重复订阅（spec.InputPorts 订阅一次 + connect_channel 又订阅一次 = 4 个端口/2 冗余）。
        // AutoClaimChannel=true 仅在 spec.InputPorts 非空时才生效，这里保持 true 但不触发。
        // RestoreAll 读 spec.InputPorts 时仍能从 state.json 的 ConnectedChannelId 回退重建端口
        // （上一轮已修复）。
        var oldSpec = new CreateSessionSpec
        {
            Type = oldSession.Type,
            ResolvedComponentIds = oldSession.GetComponentIdsForCompact(),
            AutoClaimChannel = true,
            InputPorts = new Dictionary<string, PortSpec>(StringComparer.OrdinalIgnoreCase),
            OutputPorts = new Dictionary<string, string>(),
            InitialState = $"[上一会话摘要]\n以下是上一会话的总结，仅供背景参考（压缩动作已完成，你是新 compact 接管的新会话；不要再调用 compact 总结同一会话，也不要用 escalate，你已经在工作模式里）：\n{summary}\n\n请基于上述总结继续未完成的工作。",
            InitActions = new List<ToolCall>(oldSession.GetInitActionsForCompact()),
            ParentSessionId = oldSession.SessionId,
        };

        var newSession = (RuntimeSession)CreateSession(oldSpec, fixedSessionId: newId);

        // 承接旧 session 的活跃核（同类型白名单必然包含）
        newSession.SetActiveCore(oldSession.ActiveCoreConfig);

        // 接续旧 session 的轮次计数，避免新 session 从 0 重新跑 free round
        newSession.InheritRoundCount(oldSession.GetRoundCountForCompact());

        // 子链自动跟随：子 agent 池的 Parent 指向池对象（不指向 session id），compact 换活跃 session
        // 后子 agent 找父自动拿到新 session，无需迁移。仅持久化子 session 的 effectiveParentId 到 state.json
        // （供重启后子链重建参考）。
        foreach (var childId in (GetActiveChildren(oldSession.SessionId) ?? Array.Empty<string>()))
        {
            try
            {
                var childPersister = new SessionPersistence(childId);
                childPersister.UpdateEffectiveParentAsync(childId, newSession.SessionId).GetAwaiter().GetResult();
            }
            catch { }
        }

        // 通知所有 ISessionResource：loopId 从旧 session rebind 到新 session
        await _sessionResources.NotifyCompactingAsync(oldSession.SessionId, newSession.SessionId);

        Signal.Event(LogGroup.Engine, "compact 创建新 session",
            new { oldId = oldSession.SessionId, newId = newSession.SessionId, summaryLen = summary.Length });

        return newSession;
    }

    // ── 持久化 ──

    private void RestoreAll()
    {
        var baseDir = Path.Combine(PathConfig.StoragePath, "Sessions");
        if (!Directory.Exists(baseDir)) return;

        // ── 阶段 1：扫描所有 Running/Crashed 存档，按 (channelId) 分组取 createdAt 最新者为"幸存者"。
        //    防风暴 / 防 compact 链残留：同 channelId 多个 Running 存档时，旧的 compact 链条已被
        //    最新存档取代，老存档启动也没意义且会争抢 Full claim。无 ConnectedChannelId 的孤儿
        //    归入 key=0 一组。
        var loaded = new List<(string dir, SessionPersistence persister, CreateSessionSpec spec, SessionStateSnapshot state, List<Models.Message> history)>();
        var survivalWinners = new Dictionary<int, (DateTime createdAt, int index)>();

        foreach (var dir in Directory.GetDirectories(baseDir))
        {
            SessionPersistence persister;
            CreateSessionSpec spec;
            SessionStateSnapshot state;
            List<Models.Message> history;

            try
            {
                persister = new SessionPersistence(Path.GetFileName(dir));
                (spec, state, history) = persister.Load();
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "Session 存档读取失败（跳过）",
                    new { sessionId = Path.GetFileName(dir), error = ex.Message });
                continue;
            }

            // 冷启动过滤：只恢复 Running/Crashed，Completed/Creating 保留存档但不加载
            if (state.Status is not (SessionStatus.Running or SessionStatus.Crashed))
            {
                Signal.Debug(LogGroup.Engine, "Session 存档跳过（已结束）",
                    new { sessionId = state.SessionId, status = state.Status.ToString() });
                continue;
            }

            int key = 0;
            if (!string.IsNullOrEmpty(state.ConnectedChannelId)
                && int.TryParse(state.ConnectedChannelId, out var cid) && cid > 0)
                key = cid;

            int idx = loaded.Count;
            loaded.Add((dir, persister, spec, state, history));

            if (!survivalWinners.TryGetValue(key, out var cur))
                survivalWinners[key] = (state.CreatedAt, idx);
            else if (state.CreatedAt > cur.createdAt)
                survivalWinners[key] = (state.CreatedAt, idx);
        }

        var survivorIndexes = new HashSet<int>(
            survivalWinners.Values.Select(v => v.index));

        // 被淘汰者先标 Completed（避免下次重启又尝试），不启动不订阅
        for (int i = 0; i < loaded.Count; i++)
        {
            if (survivorIndexes.Contains(i)) continue;
            var (dir, persister, spec, state, history) = loaded[i];
            try
            {
                persister.UpdateStatusAsync(SessionStatus.Completed).GetAwaiter().GetResult();
                Signal.Event(LogGroup.Engine, "Session 存档被幸存者取代，标记为 Completed",
                    new { sessionId = state.SessionId, channelId = state.ConnectedChannelId, createdAt = state.CreatedAt });
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "Session 存档标 Completed 失败",
                    new { sessionId = state.SessionId, error = ex.Message });
            }
        }

        // ── 阶段 2：仅对幸存者走恢复流程 ──
        // 单个会话恢复失败（坏存档等）只跳过自己、不拖垮整个引擎：RestoreAll 完不成
        // 会连带 SessionEngine 启动失败，频道却已被占用 → 冷启动假占用吞消息（已知坑）。
        // 分两阶段：
        //   A. 先重建会话对象（组件/订阅/历史/登记/启动 RunAsync）——中途失败不产生任何 claim；
        //   B. 全部重建成功后，再统一 ClaimChannel。claim 只对"已完整重建"的会话生效，
        //      从根上杜绝"claim 已落地但会话对象不存在"的幽灵占用中间态。
        var restoredWait = new List<(string sessionId, DateTime waitUntil)>();
        // 需要恢复 Full claim 的会话：(sessionId, channelId)。与旧实现一致：claim 用
        // restorePorts 首个端口频道（spec.InputPorts 非空 或 从 state.ConnectedChannelId 推断 两条路都进）。
        var restoreClaims = new List<(string sessionId, int channelId)>();
        foreach (var idx in survivorIndexes)
        {
            var (dir, persister, spec, state, history) = loaded[idx];

            // ① 收集需要恢复的频道端口。
            //    spec.InputPorts 非空 → 直接用（理论路径）。
            //    spec.InputPorts 空但 state.ConnectedChannelId 非空 → escalate 路径，端口由旧 session
            //    执行 InitActions.connect_channel 动态创建，spec 持久化时未保留端口信息，这里从 state 推断。
            //    ConnectionMode.Full → normal + mention 两端口；Listen → 仅 normal。
            var restorePorts = new List<(string name, PortSpec spec)>();
            var restoreMode = ConnectionMode.None;
            if (spec.InputPorts.Count > 0)
            {
                foreach (var (name, ps) in spec.InputPorts)
                    restorePorts.Add((name, ps));
                if (!string.IsNullOrEmpty(state.ConnectedMode)
                    && Enum.TryParse(state.ConnectedMode, true, out restoreMode) && restoreMode == ConnectionMode.None)
                    restoreMode = ConnectionMode.Full;
            }
            else if (!string.IsNullOrEmpty(state.ConnectedChannelId)
                     && int.TryParse(state.ConnectedChannelId, out var restoredCid) && restoredCid > 0)
            {
                restoreMode = Enum.TryParse(state.ConnectedMode, true, out restoreMode)
                    ? restoreMode : ConnectionMode.Full;
                restorePorts.Add(("default", new PortSpec { ChannelId = restoredCid, SubStream = "normal", Weight = 5, PreloadHistory = false }));
                if (restoreMode == ConnectionMode.Full)
                    restorePorts.Add(("mention", new PortSpec { ChannelId = restoredCid, SubStream = "mention", Weight = 100, PreloadHistory = false }));
                Signal.Event(LogGroup.Engine, "从 state 推断恢复端口",
                    new { sessionId = state.SessionId, channelId = restoredCid, mode = restoreMode.ToString() });
            }

            RuntimeSession session;
            try
            {
                // ② 重建 RuntimeSession（复用旧 SessionId）。finalComponents 必填——组件装配
                //    与 CreateSession 用同一 ComputeFinalComponents，保证恢复=新建。
                var profile = SessionTypeProfiles.Get(spec.Type);
                session = new RuntimeSession(spec, state.SessionId, ComputeFinalComponents(spec, profile));
                session.SetCurrency(_currency!);
                session.SetSessionEngine(this);
                session.OnCompleted += OnRuntimeSessionCompleted;

                // 复活善后：订阅重建 session 的完成事件
                if (Mind?.Resurrector != null)
                    Mind.Resurrector.Attach(session);

                // ③ 走标准初始化路径（组件 / AgentCore / MessageEngine / AgentHost）
                //    跳过 BootstrapAsync：InitActions 已在旧 session 执行过，不重复 claim/订阅。
                //    会话身份统一为裸 sessionId（LoopId 概念已移除）。
                try
                {
                    session.InitComponents(session.SessionId, _ctx.ModuleBus, _ctx.ComponentServices, () => session.WakeUp());
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Engine, "恢复 Session 组件初始化失败",
                        new { sessionId = session.SessionId, error = ex.Message });
                }

                _sessions[session.SessionId] = session;

                session.ConfigureCores(profile.AllowedCoreConfigs, profile.DefaultCoreConfig, state.ActiveCoreConfig);
                var messageEngine = _ctx.GetActiveEnginesSnapshot().OfType<Messaging.MessageEngine>().FirstOrDefault();
                if (messageEngine != null) session.SetMessageEngine(messageEngine);

                // ④ 重建输入端口 + 订阅（不重新订阅会丢消息）
                foreach (var (portName, portSpec) in restorePorts)
                {
                    if (messageEngine != null)
                    {
                        var port = messageEngine.Subscribe(portSpec.ChannelId, portSpec.SubStream, portSpec.Weight);
                        session.AddInputPort(portName, port);
                    }
                }

                // 恢复的 session 用原 SessionId 重新绑定自己的 session-scope 图实例。
                InitializeGraphMount(session);

                // ⑤ 重建上下文宿主（Engine/Context）
                try
                {
                    var channelPorts = new List<(int channelId, string? channelName, bool preload)>();
                    foreach (var (_, portSpec) in restorePorts)
                    {
                        var ch = _ctx.Session.GetChannelByIdAsync(portSpec.ChannelId).GetAwaiter().GetResult();
                        channelPorts.Add((portSpec.ChannelId, ch?.Name, portSpec.PreloadHistory));
                    }

                    var dataSource = new SystemContextDataSource(_ctx);
                    var svc = new ChannelContextService(dataSource);
                    var initialStateText = spec.InitialState is string s ? s
                        : spec.InitialState != null ? JsonConvert.SerializeObject(spec.InitialState)
                        : null;
                    var host = new SessionContextHost(channelPorts, initialStateText, svc);
                    session.SetContextHost(host);
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Engine, "恢复 Session AgentHost 失败",
                        new { sessionId = session.SessionId, error = ex.Message });
                }

                // ⑥ 灌入存档状态 + 历史
                session.RestoreFromSnapshot(state, history);

                // ⑦ 注入 persister + 启用压缩
                session.SetPersister(persister);
                session.InitCompression();

                Signal.Event(LogGroup.Engine, "Session 已恢复",
                    new { sessionId = session.SessionId, historyCount = history.Count, roundCount = state.RoundCount });

                // ⑧ 启动 RunAsync（restored 会话不立即激活，等新消息 / WaitUntil 到期 / 外部唤醒）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource();
                        await session.RunAsync(cts.Token);
                    }
                    catch (Exception ex)
                    {
                        Signal.Error(LogGroup.Engine, "恢复后 Session 崩溃",
                            new { sessionId = session.SessionId, error = ex.Message });
                    }
                    finally
                    {
                        ReleaseSessionClaims(session.SessionId);
                    }
                });

                // ⑨ 重调度 WaitUntil：恢复的 waitUntil 是旧的墙上时钟（可能已超时很久），
                //    且 RestoreFromSnapshot 已直接把 WaitUntil 灌进会话——若超时立即触发一轮
                //    wait timeout 注入（可接受）。但若 waitUntil 在未来很远（重启发生在 wait 期间），
                //    RunAsync 的 WaitForActivationAsync 轮询会报 TimeOutException（Task.Delay 超长溢出），
                //    把会话打 Crashed。这里对未来 wait 统一转成"等待到期时长"，到期后注入 timeout 唤醒。
                if (state.WaitUntil != null && state.WaitUntil > DateTime.UtcNow)
                {
                    restoredWait.Add((session.SessionId, state.WaitUntil.Value));
                }

                // 记录待恢复的 Full claim（供阶段 3 统一落地）：仅 restoreMode==Full 且端口非空。
                // escalate 路径（spec.InputPorts 空、从 state 推断端口）也在此覆盖，与旧实现对齐。
                if (restoreMode == ConnectionMode.Full && restorePorts.Count > 0)
                    restoreClaims.Add((session.SessionId, restorePorts[0].spec.ChannelId));
            }
            catch (Exception ex)
            {
                // 单个会话恢复失败：标 Completed 防下次重启再试，跳过（不产生 claim）。
                Signal.Error(LogGroup.Engine, "Session 恢复失败（跳过）",
                    new { sessionId = state.SessionId, error = ex.Message, stack = ex.StackTrace });
                try { persister.UpdateStatusAsync(SessionStatus.Completed).GetAwaiter().GetResult(); }
                catch { }
                continue;
            }
        }

        // ── 阶段 3：全部会话重建成功后统一恢复频道占用（先建后 claim，杜绝幽灵占用）──
        foreach (var (sid, cid) in restoreClaims)
        {
            // 会话在阶段 2 中恢复失败：未登记到 _sessions 则不建立 claim，原频道回无占用。
            if (!_sessions.ContainsKey(sid)) continue;
            var claimed = ClaimChannel(sid, cid, ClaimLevel.Full);
            Signal.Event(LogGroup.Engine, "恢复 Session 频道占用",
                new { sessionId = sid, channelId = cid, claimed });
        }

        // WaitUntil 到期重调度：用新会话唤醒回调投递 timeout（不注入消息历史，只触发激活判定）
        foreach (var (sid, waitUntil) in restoredWait)
        {
            if (!_sessions.TryGetValue(sid, out var rs)) continue;
            var delay = waitUntil - DateTime.UtcNow;
            if (delay <= TimeSpan.Zero) continue;
            var delayCap = TimeSpan.FromDays(100);
            var t = delay > delayCap ? delayCap : delay;
            _ = Task.Delay(t).ContinueWith(_ =>
            {
                if (_sessions.TryGetValue(sid, out var s)
                    && s is RuntimeSession r && r.WaitUntil == waitUntil)
                    r.InjectWaitTimeoutExpired();
            });
        }

        // 第二遍：重建子 agent 的池树。父 session 可能晚于子 session 恢复，必须等全部 session 就位后
        // 才能把子 agent 挂到父池（否则父池不存在，子池建不出）。
        foreach (var (_, session) in _sessions)
        {
            var specParent = session.ParentSessionId;
            if (!string.IsNullOrEmpty(specParent) && !_poolByMember.ContainsKey(session.SessionId))
            {
                // 优先用 effectiveParentId（compact 后的当前父）；无则用 spec.ParentSessionId（原始父）
                var parentId = session.EffectiveParentId ?? specParent;
                CreateSubPool(session.SessionId, parentId);
            }
        }

        // RestoreAll 完成：放行消息路由中等待的 claim 检查
        _restoreTcs.TrySetResult();
    }
}
