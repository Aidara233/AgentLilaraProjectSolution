using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 唤醒命令。如果正在做梦，停止 DreamEngine。
    /// 用法: /wake
    /// </summary>
    internal class WakeCommand : ICommand
    {
        public string Name => "wake";
        public string Description => "唤醒 (停止做梦)";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        public Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var ctx = context.SystemContext;
            if (!ctx.HasActiveEngine("Dream"))
                return Task.FromResult(CommandResult.Ok("当前没有在做梦。"));

            ctx.RequestStopEnginesByType("Dream");
            return Task.FromResult(CommandResult.Ok("已唤醒，正在停止做梦引擎。"));
        }
    }
}
