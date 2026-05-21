using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine
{
    internal class ReviewEngine : ISubEngine, IAgentHost
    {
        public string EngineType => "Review";
        public bool IsAlive { get; private set; } = true;

        private readonly ISystemContext _ctx;
        private readonly ReviewMode _mode;
        private readonly string _preInjectedContext;
        private readonly DreamConfig _cfg;
        private readonly DreamProgress _progress;

        private readonly AgentCore _core;
        private Agent? _agent;
        private ReviewControlImpl? _reviewControl;
        private readonly CancellationTokenSource _cts = new();

        private static readonly HashSet<string> AuthorizedTools = new()
        {
            "review_search_memory", "review_read_messages", "review_view_links",
            "review_write_memory", "review_update_person", "review_update_affinity",
            "review_thinking_notes", "review_save_progress",
            "review_request_reinforcement", "review_complete"
        };

        public ReviewEngine(ISystemContext ctx, ReviewMode mode, string preInjectedContext,
            DreamConfig cfg, DreamProgress progress)
        {
            _ctx = ctx;
            _mode = mode;
            _preInjectedContext = preInjectedContext;
            _cfg = cfg;
            _progress = progress;
            _core = new AgentCore("ReviewCore", usePersona: false);
            _core.CallerTag = $"Review:{mode}";
        }

        public async Task RunAsync()
        {
            var parentCtx = SignalContext.Current;
            var lifeCtx = Signal.Continue(
                parentCtx?.SignalId ?? Signal.NewId(), parentCtx?.CurrentSpanId,
                "review:main", LogGroup.Engine, "Review引擎",
                new { engineType = EngineType, mode = _mode.ToString() });

            _reviewControl = new ReviewControlImpl(_cfg.ReviewReserveBudget);
            _ctx.ToolContext.Register<IReviewControl>(_reviewControl);

            try
            {
                _core.ProfileManager = _ctx.ToolProfiles;

                var agentConfig = new AgentConfig
                {
                    MaxRounds = 20,
                    BackoffSeconds = new[] { 10, 30 },
                    ModelCallMaxAttempts = 3,
                    ModelCallRetryDelaySeconds = new[] { 5, 15 },
                    ProfileName = "review"
                };

                _agent = new Agent(this, _core, agentConfig, AuthorizedTools);
                await _agent.RunAsync(_cts.Token);

                Signal.Event(LogGroup.Engine, "Review完成", new
                {
                    mode = _mode.ToString(),
                    rounds = _agent.TotalRounds,
                    stopReason = _agent.StopReason?.ToString(),
                    completed = _reviewControl.IsCompleted,
                    reserveUsed = _reviewControl.ReserveGranted
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "Review异常", new { error = ex.GetType().Name, message = ex.Message });
            }
            finally
            {
                _ctx.ToolContext.Unregister<IReviewControl>();
                IsAlive = false;
                lifeCtx.Close(new { engineType = EngineType, reason = _reviewControl?.IsCompleted == true ? "completed" : "budget_or_rounds" });
            }
        }

        public void OnEvent(EngineEvent e)
        {
            if (e is MessageEvent)
                _reviewControl?.NotifyWake();
        }

        public void RequestStop()
        {
            _cts.Cancel();
        }

        // ---- IAgentHost ----

        public Task<List<Message>?> BuildStartInjectAsync()
        {
            var msgs = new List<Message>();

            // 系统提示
            msgs.Add(new Message { Role = "user", Content = BuildSystemPrompt() });

            // 预注入上下文（ReviewModeSelector 构建的数据）
            if (!string.IsNullOrEmpty(_preInjectedContext))
                msgs.Add(new Message { Role = "user", Content = _preInjectedContext });

            // 预算信息
            var budget = _cfg.ReviewTokenBudget;
            msgs.Add(new Message
            {
                Role = "user",
                Content = $"[复盘资源] 基础预算: {budget} tokens | 备用预算: {_cfg.ReviewReserveBudget} tokens（需主动申请）"
            });

            return Task.FromResult<List<Message>?>(msgs);
        }

        public Task<List<Message>?> BuildRoundInjectAsync()
        {
            if (_reviewControl?.IsCompleted == true)
                return Task.FromResult<List<Message>?>(null);

            var msgs = new List<Message>();

            if (_reviewControl is { WakeNotified: true })
                msgs.Add(new Message { Role = "user", Content = "[系统提示] 系统即将醒来，请尽快收尾。备用预算不可用。" });

            return Task.FromResult<List<Message>?>(msgs.Count > 0 ? msgs : null);
        }

        private string BuildSystemPrompt()
        {
            var modeDesc = _mode switch
            {
                ReviewMode.ChannelDaily => "频道日报：分析频道近期活动，提炼要点，调整亲和度。",
                ReviewMode.PersonProfile => "人物回顾：聚焦某人的近期互动，更新称呼、快速记忆、好感度。",
                ReviewMode.CrossDomain => "跨域关联：跨频道发现被忽略的联系和共同趋势。",
                ReviewMode.ContradictionDetect => "矛盾检测：检查记忆库中互相矛盾的信息，清理或标注。",
                _ => "自由复盘"
            };

            return $"""
你是 Lilara 的复盘模块。当前处于深度睡眠期间，正在进行离线分析。

## 当前模式
{modeDesc}

## 可用工具
- review_search_memory：语义搜索记忆库
- review_read_messages：读取频道消息历史
- review_view_links：查看记忆关联
- review_write_memory：将发现写入主记忆
- review_update_person：更新人物信息（称呼/别称/快速记忆）
- review_update_affinity：调整频道亲和度
- review_thinking_notes：管理思考笔记（跨轮保持）
- review_save_progress：保存调查进度（下次继续）
- review_request_reinforcement：请求备用预算（仅一次）
- review_complete：标记复盘完成

## 工作指引
1. 先阅读预注入的上下文数据，理解当前任务
2. 使用工具深入调查，记录思考笔记
3. 将有价值的发现写入主记忆
4. 完成后调用 review_complete，或预算不足时保存进度
5. 不要浪费 token 在无意义的重复搜索上
""";
        }
    }
}
