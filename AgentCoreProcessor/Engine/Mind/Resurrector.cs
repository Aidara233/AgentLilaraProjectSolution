using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 会话善后服务（Resurrector）。
    /// 订阅会话完成（RuntimeSession.OnCompleted）→ 写一条 L3 Reflection 总结 Journal，
    /// 并在 EventSink 可用时落一条 L1 SessionResurrected 框架事件。
    /// 首版为轻量实现：完整"从存档重建 session 跑多轮整理"是后续精细项（见 03-memory-engine.md）。
    /// </summary>
    internal class Resurrector
    {
        private readonly MindEngine _mind;
        private readonly EventSink? _eventSink;

        /// <summary>已订阅完成事件的会话（供 DetachAll 统一清理）。</summary>
        private readonly ConcurrentDictionary<RuntimeSession, byte> _attached = new();

        /// <summary>
        /// 构造注入心智引擎（取其 Writer 统一写入口）与可选事件接收器。
        /// </summary>
        /// <param name="mind">心智引擎：善后总结经 <see cref="MindEngine.Writer"/> 落库。</param>
        /// <param name="eventSink">事件接收器；为 null 时回退到 <paramref name="mind"/>.Events。</param>
        public Resurrector(MindEngine mind, EventSink? eventSink = null)
        {
            _mind = mind ?? throw new ArgumentNullException(nameof(mind));
            _eventSink = eventSink ?? mind.Events;
        }

        /// <summary>
        /// 订阅会话完成事件。handler 内触发后主动退订，避免事件源长期持有本服务引用（防泄漏）。
        /// </summary>
        public void Attach(IRuntimeSession session)
        {
            if (session is RuntimeSession rs)
            {
                _attached.TryAdd(rs, 0);
                rs.OnCompleted += OnSessionCompleted;
            }
        }

        /// <summary>会话完成回调：先退订，再异步跑善后整理（fire-and-forget，不阻塞结束路径）。</summary>
        private void OnSessionCompleted(IRuntimeSession session)
        {
            // 及时退订，防泄漏
            if (session is RuntimeSession rs)
            {
                rs.OnCompleted -= OnSessionCompleted;
                _attached.TryRemove(rs, out _);
            }

            // OnCompleted 是同步委托，HandleCompletedAsync 是异步——fire-and-forget
            _ = HandleCompletedAsync(session);
        }

        /// <summary>
        /// 完成处理：
        /// 1) Writer 未就绪直接返回；
        /// 2) 写一条 L3 Reflection Journal；
        /// 3) EventSink 可用时落一条 L1 SessionResurrected 事件；
        /// 4) 异常捕获 + Signal.Error 记录，不阻断会话结束流程。
        /// </summary>
        public async Task HandleCompletedAsync(IRuntimeSession session)
        {
            try
            {
                // 1. 写入口未就绪则跳过整理
                if (_mind.Writer == null)
                    return;

                var sessionId = session.SessionId ?? "";
                var sessionType = (session as RuntimeSession)?.Type ?? "session";

                // 2. 写一条 L3 Reflection 总结 Journal（正文第一人称，Lilara 为主体）
                var journal = new WriteJournalRequest
                {
                    Content = $"刚刚结束的 {sessionType} 会话（{sessionId}）已归档整理，本轮经历沉淀为反思记录。",
                    Kind = "Reflection",
                    Source = "Framework",
                    OccurredAt = DateTime.Now,
                };
                var batch = new WriteBatchRequest
                {
                    Journals = new List<WriteJournalRequest> { journal },
                };
                await _mind.Writer.WriteBatchAsync(batch);

                // 3. 若 EventSink 可用，落一条 L1 复活动作事件
                if (_eventSink != null)
                {
                    var detail = new { sessionId, sessionType };
                    await _eventSink.SubmitEventAsync("L1", "SessionResurrected", JsonConvert.SerializeObject(detail));
                }
            }
            catch (Exception ex)
            {
                // 4. 异常不阻断会话结束流程，仅记录
                Signal.Error(LogGroup.Memory, "Resurrector 善后整理失败",
                    new { sessionId = session.SessionId, error = ex.Message });
            }
        }

        /// <summary>清理：退订所有已订阅会话（重载/停机时调用）。</summary>
        public void DetachAll()
        {
            foreach (var rs in _attached.Keys)
                rs.OnCompleted -= OnSessionCompleted;
            _attached.Clear();
        }
    }
}
