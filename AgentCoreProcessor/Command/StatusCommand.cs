using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>查看系统运行状态。</summary>
    internal class StatusCommand : ICommand
    {
        public string Name => "status";
        public string Description => "查看系统状态";
        public PermissionLevel RequiredPermission => PermissionLevel.Default;

        public Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var ctx = context.SystemContext;
            var sb = new StringBuilder();
            sb.AppendLine("系统状态：");
            sb.AppendLine($"  空闲: {(ctx.IsIdle ? "是" : "否")}");
            sb.AppendLine($"  空闲时长: {ctx.IdleDuration:hh\\:mm\\:ss}");
            sb.AppendLine($"  上次消息: {ctx.LastMessageTime:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"  活跃 Topic: {ctx.GetActiveEngineCount("Topic")}");
            sb.AppendLine($"  活跃 Worker: {ctx.GetActiveEngineCount("Worker")}");
            sb.AppendLine($"  活跃 Dream: {ctx.GetActiveEngineCount("Dream")}");

            return Task.FromResult(CommandResult.Ok(sb.ToString().TrimEnd()));
        }
    }
}
