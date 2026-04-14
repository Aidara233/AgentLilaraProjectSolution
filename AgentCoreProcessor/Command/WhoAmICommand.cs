using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>查看调用者身份信息。</summary>
    internal class WhoAmICommand : ICommand
    {
        public string Name => "whoami";
        public string Description => "查看你的身份信息";
        public PermissionLevel RequiredPermission => PermissionLevel.Default;

        public Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var u = context.User;
            var p = context.Person;
            var sb = new StringBuilder();
            sb.AppendLine("身份信息：");
            sb.AppendLine($"  平台: {u.Platform}");
            sb.AppendLine($"  平台ID: {u.PlatformId}");
            sb.AppendLine($"  权限: {u.PermissionLevel}");
            sb.AppendLine($"  PersonId: {p.Id}");
            sb.AppendLine($"  信任等级: {p.TrustLevel}");

            return Task.FromResult(CommandResult.Ok(sb.ToString().TrimEnd()));
        }
    }
}
