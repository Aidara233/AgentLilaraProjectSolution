using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 创建子 agent 工具：系统循环创建一次性任务子 agent。
    /// 系统循环专用。
    /// </summary>
    internal class CreateSubAgentTool : ITool
    {
        public string Name => "创建子agent";
        public string Description => "创建一次性任务子 agent 执行复杂操作";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("任务描述", "子 agent 的任务目标", 0)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public bool AllowSubAgent => false;

        private readonly Func<IAgentSession> sessionFactory;

        public CreateSubAgentTool(Func<IAgentSession> sessionFactory)
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

            var taskDescription = resolvedInputs[0];

            try
            {
                // 创建子 agent
                var session = sessionFactory();

                return Task.FromResult(new ToolResult
                {
                    Status = "success",
                    Data = $"子 agent 已创建: {session.SessionId}\n任务: {taskDescription}"
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
    /// 发送指令给子 agent 工具。
    /// 系统循环专用。
    /// </summary>
    internal class SendToSubAgentTool : ITool
    {
        public string Name => "发送指令";
        public string Description => "向子 agent 发送指令";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("会话ID", "子 agent 的会话 ID", 0),
            new("指令", "要执行的指令", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(60);
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

            try
            {
                var session = sessionGetter(sessionId);
                if (session == null)
                {
                    return new ToolResult
                    {
                        Status = "failed",
                        Error = $"会话 {sessionId} 不存在"
                    };
                }

                if (!session.IsAlive)
                {
                    return new ToolResult
                    {
                        Status = "failed",
                        Error = $"会话 {sessionId} 已停止"
                    };
                }

                var success = await session.SendInstructionAsync(instruction);
                if (!success)
                {
                    return new ToolResult
                    {
                        Status = "failed",
                        Error = $"会话 {sessionId} 不支持接收指令（可能是频道会话）"
                    };
                }

                return new ToolResult
                {
                    Status = "success",
                    Data = $"指令已发送到 {sessionId}"
                };
            }
            catch (Exception ex)
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = $"发送指令失败: {ex.Message}"
                };
            }
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

            try
            {
                var session = sessionGetter(sessionId);
                if (session == null)
                {
                    return Task.FromResult(new ToolResult
                    {
                        Status = "failed",
                        Error = $"会话 {sessionId} 不存在"
                    });
                }

                session.RequestStop();

                return Task.FromResult(new ToolResult
                {
                    Status = "success",
                    Data = $"已请求停止 {sessionId}"
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"停止子 agent 失败: {ex.Message}"
                });
            }
        }
    }
}
