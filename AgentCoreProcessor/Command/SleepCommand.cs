using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 手动触发梦境。
    /// 用法: /sleep [deepsleep]
    /// 无参数时默认触发。
    /// </summary>
    internal class SleepCommand : IInteractiveCommand
    {
        public string Name => "sleep";
        public string Description => "手动触发梦境";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        public System.Collections.Generic.List<CommandStep> Steps => new()
        {
            new() { Key = "_confirm", Prompt = "输入任意内容确认触发梦境:" }
        };

        public Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            context.SystemContext.EventBus.PublishSignal("force-sleep", "deepsleep");
            return Task.FromResult(CommandResult.Ok("已发送做梦信号"));
        }

        public Task<CommandResult> ExecuteInteractiveAsync(
            System.Collections.Generic.Dictionary<string, string> data, CommandContext context)
        {
            context.SystemContext.EventBus.PublishSignal("force-sleep", "deepsleep");
            return Task.FromResult(CommandResult.Ok("已发送做梦信号"));
        }
    }
}
