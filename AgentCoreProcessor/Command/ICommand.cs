using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 框架命令接口。所有命令实现此接口并注册到 CommandRegistry。
    /// </summary>
    internal interface ICommand
    {
        /// <summary>命令名（不含前缀），如 "help"</summary>
        string Name { get; }

        /// <summary>简短描述，/help 输出用</summary>
        string Description { get; }

        /// <summary>执行此命令所需的最低权限等级</summary>
        PermissionLevel RequiredPermission { get; }

        /// <summary>执行命令</summary>
        Task<CommandResult> ExecuteAsync(string args, CommandContext context);
    }

    /// <summary>
    /// 命令执行上下文。
    /// </summary>
    internal class CommandContext
    {
        /// <summary>发送命令的用户</summary>
        public required User User { get; init; }

        /// <summary>用户对应的自然人</summary>
        public required Person Person { get; init; }

        /// <summary>原始消息</summary>
        public required IncomingMessage Message { get; init; }

        /// <summary>系统上下文（访问仓库、适配器等）</summary>
        public required ISystemContext SystemContext { get; init; }
    }

    /// <summary>
    /// 命令执行结果。
    /// </summary>
    internal class CommandResult
    {
        public bool Success { get; set; }
        public string? Response { get; set; }

        public static CommandResult Ok(string response) => new() { Success = true, Response = response };
        public static CommandResult Fail(string error) => new() { Success = false, Response = $"失败: {error}" };
    }
}
