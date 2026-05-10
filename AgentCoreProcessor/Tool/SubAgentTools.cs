using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 创建子 agent 工具：创建并启动异步任务子 agent。
    /// 系统循环专用。子 agent 后台执行，完成后通过通知回报。
    /// </summary>
    internal class CreateSubAgentTool : ITool
    {
        public string Name => "创建子agent";
        public string Description => "创建异步任务子 agent。子 agent 后台执行，完成后通过通知回报结果";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("任务描述", "子 agent 的任务目标和详细指令", 0)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public bool ContinueLoop => true;
        public bool AllowSubAgent => false;

        private readonly Func<string, IAgentSession> sessionFactory;

        public CreateSubAgentTool(Func<string, IAgentSession> sessionFactory)
        {
            this.sessionFactory = sessionFactory;
        }

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "任务描述不能为空"
                });
            }

            // 模型可能把任务 ID 和描述分开传入多个参数，拼接所有非空参数作为完整指令
            var instruction = string.Join("\n", resolvedInputs.Where(s => !string.IsNullOrWhiteSpace(s)));

            try
            {
                var session = sessionFactory(instruction);

                return Task.FromResult(new ToolResult
                {
                    Status = "success",
                    Data = $"子 agent 已创建并启动: {session.SessionId}\n任务: {instruction}"
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"创建子 agent 失败: {ex.Message}"
                });
            }
        }
    }

    /// <summary>
    /// 发送指令给子 agent 工具（追加指令到队列）。
    /// 系统循环专用。
    /// </summary>
    internal class SendToSubAgentTool : ITool
    {
        public string Name => "发送指令给子agent";
        public string Description => "向子 agent 追加新指令（子 agent 空闲时处理）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("会话ID", "子 agent 的会话 ID", 0),
            new("指令", "要执行的指令", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public bool ContinueLoop => true;
        public bool AllowSubAgent => false;

        private readonly Func<string, IAgentSession?> sessionGetter;

        public SendToSubAgentTool(Func<string, IAgentSession?> sessionGetter)
        {
            this.sessionGetter = sessionGetter;
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 2 || string.IsNullOrWhiteSpace(resolvedInputs[0]) || string.IsNullOrWhiteSpace(resolvedInputs[1]))
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = "会话 ID 和指令不能为空"
                };
            }

            var sessionId = resolvedInputs[0];
            var instruction = resolvedInputs[1];

            var session = sessionGetter(sessionId);
            if (session == null)
                return new ToolResult { Status = "failed", Error = $"会话 {sessionId} 不存在" };
            if (!session.IsAlive)
                return new ToolResult { Status = "failed", Error = $"会话 {sessionId} 已停止" };

            var success = await session.SendInstructionAsync(instruction);
            return success
                ? new ToolResult { Status = "success", Data = $"指令已追加到 {sessionId} 的队列" }
                : new ToolResult { Status = "failed", Error = $"会话 {sessionId} 拒绝接收指令" };
        }
    }

    /// <summary>
    /// 停止子 agent 工具。
    /// 系统循环专用。
    /// </summary>
    internal class StopSubAgentTool : ITool
    {
        public string Name => "停止子agent";
        public string Description => "停止指定的子 agent";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("会话ID", "子 agent 的会话 ID", 0)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public bool ContinueLoop => true;
        public bool AllowSubAgent => false;

        private readonly Func<string, IAgentSession?> sessionGetter;

        public StopSubAgentTool(Func<string, IAgentSession?> sessionGetter)
        {
            this.sessionGetter = sessionGetter;
        }

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "会话 ID 不能为空"
                });
            }

            var sessionId = resolvedInputs[0];
            var session = sessionGetter(sessionId);
            if (session == null)
                return Task.FromResult(new ToolResult { Status = "failed", Error = $"会话 {sessionId} 不存在" });

            session.RequestStop();
            return Task.FromResult(new ToolResult { Status = "success", Data = $"已请求停止 {sessionId}" });
        }
    }
}
