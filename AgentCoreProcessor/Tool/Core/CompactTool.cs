// AgentCoreProcessor/Tool/Core/CompactTool.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    /// <summary>
    /// Session 上下文压缩工具：模型提供摘要作为参数，触发新 session 接管旧 session。
    /// 旧 session 调用 compact(summary) → 退出循环；新 session 创建后 claim 同一频道、
    /// InitialState = "[上一会话摘要]\n{summary}\n\n请继续工作"，轮次接续。
    ///
    /// 工具表锁定：RuntimeSession 在 L3 触发后清空 _tools 只留 compact，模型别无选择只能调用。
    /// </summary>
    [ToolMeta(ContinueLoop = false)]
    internal class CompactTool : ITool
    {
        private readonly RuntimeSession _session;
        private readonly SessionEngine _sessionEngine;

        public string Name => "compact";
        public string Description =>
            "压缩当前会话：用你提供的摘要启动一个新会话承接当前工作。" +
            "参数应是会话的完整总结（做了什么、得出了什么、下一步要做什么），" +
            "新会话基于你的总结继续，本会话将结束。";
        public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
        {
            new("summary",
                "对当前会话的完整总结：已完成的工作、关键结论、未完成的任务、下一步计划。新会话将基于此摘要继续工作。",
                0, isRequired: true)
        };
        public TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public CompactTool(RuntimeSession session, SessionEngine sessionEngine)
        {
            _session = session;
            _sessionEngine = sessionEngine;
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs == null || resolvedInputs.Count == 0
                || string.IsNullOrWhiteSpace(resolvedInputs[0]))
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = "compact 需要提供摘要参数（你此次会话的完整总结）"
                };
            }

            var summary = resolvedInputs[0];

            try
            {
                // 触发新 session 创建（同步），新 session 在 CreateFromCompact 内部
                // 通过标准 CreateSession 走 AutoClaim 同步接管频道
                await _sessionEngine.CreateFromCompact(_session, summary);

                Signal.Event(LogGroup.Engine, "compact 已触发新 session",
                    new { oldSession = _session.SessionId, summaryLength = summary.Length });

                // 返回成功：旧 session 退出循环（EndSession 由 ApplyToolEffectAsync 检测 compact 触发）
                return new ToolResult
                {
                    Status = "success",
                    Data = "新会话已创建并接管频道，本会话即将退出。"
                };
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "compact 创建新 session 失败",
                    new { oldSession = _session.SessionId, error = ex.Message });
                return new ToolResult
                {
                    Status = "failed",
                    Error = $"compact 失败：{ex.Message}"
                };
            }
        }
    }
}