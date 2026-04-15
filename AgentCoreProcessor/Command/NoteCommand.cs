using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 快速写临时记忆。
    /// 用法: /note <内容>
    /// </summary>
    internal class NoteCommand : ICommand
    {
        public string Name => "note";
        public string Description => "快速写临时记忆 (用法: /note <内容>)";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        public async Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            if (string.IsNullOrWhiteSpace(args))
                return CommandResult.Fail("用法: /note <内容>");

            var ctx = context.SystemContext;
            await ctx.MemorySvc.StoreAsync(args.Trim(),
                context.Person.Id, channelId: 0, topicId: 0);

            return CommandResult.Ok($"已写入临时记忆: {args.Trim()}");
        }
    }
}
