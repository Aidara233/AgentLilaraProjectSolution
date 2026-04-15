using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 引擎状态管理。
    /// 用法: /engine [list] | stop <类型>
    /// </summary>
    internal class EngineCommand : ICommand
    {
        public string Name => "engine";
        public string Description => "引擎管理 (list/stop)";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        public Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var parts = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            var sub = parts.Length > 0 ? parts[0].ToLower() : "list";
            var ctx = context.SystemContext;

            return sub switch
            {
                "list" => Task.FromResult(ListEngines(ctx)),
                "stop" => Task.FromResult(StopEngine(parts, ctx)),
                _ => Task.FromResult(CommandResult.Fail($"未知子命令: {sub}"))
            };
        }

        private static CommandResult ListEngines(Engine.ISystemContext ctx)
        {
            var summary = ctx.GetActiveEngineSummary();
            if (summary.Count == 0)
                return CommandResult.Ok($"无活跃引擎。系统空闲: {ctx.IsIdle}");
            var sb = new StringBuilder();
            sb.AppendLine($"活跃引擎 (空闲={ctx.IsIdle}):");
            foreach (var (type, count) in summary.OrderBy(s => s.Type))
                sb.AppendLine($"  {type}: {count}");
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }

        private static CommandResult StopEngine(string[] parts, Engine.ISystemContext ctx)
        {
            if (parts.Length < 2)
                return CommandResult.Fail("用法: /engine stop <类型>");
            var type = parts[1];
            if (!ctx.HasActiveEngine(type))
                return CommandResult.Fail($"没有活跃的 {type} 引擎。");
            ctx.RequestStopEnginesByType(type);
            return CommandResult.Ok($"已请求停止所有 {type} 引擎。");
        }
    }
}
