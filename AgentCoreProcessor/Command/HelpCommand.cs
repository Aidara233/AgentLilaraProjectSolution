using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>显示可用命令列表。</summary>
    internal class HelpCommand : ICommand
    {
        public string Name => "help";
        public string Description => "显示可用命令";
        public PermissionLevel RequiredPermission => PermissionLevel.Default;

        public Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine("可用命令：");

            foreach (var cmd in CommandRegistry.All.Values.OrderBy(c => c.Name))
            {
                if (context.User.PermissionLevel >= cmd.RequiredPermission)
                {
                    var tag = cmd.RequiredPermission >= PermissionLevel.Admin ? " [管理员]" : "";
                    sb.AppendLine($"  /{cmd.Name} — {cmd.Description}{tag}");
                }
            }

            return Task.FromResult(CommandResult.Ok(sb.ToString().TrimEnd()));
        }
    }
}
