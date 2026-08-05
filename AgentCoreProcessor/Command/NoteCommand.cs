using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 快速写经历（Journal）。
    /// 用法: /note <内容>
    /// </summary>
    internal class NoteCommand : ICommand
    {
        public string Name => "note";
        public string Description => "快速写经历 (用法: /note <内容>)";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        public async Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            if (string.IsNullOrWhiteSpace(args))
                return CommandResult.Fail("用法: /note <内容>");

            var ctx = context.SystemContext;
            var mind = ctx.Mind;
            if (mind?.Writer == null)
                return CommandResult.Fail("心智引擎未启动。");

            // 经心智引擎唯一写入口落一条经历（Journal）。
            // ChannelId 保留原语义 channelId: 0（无频道锚点）；Journal 无 person 字段，不映射 Person.Id。
            await mind.Writer.WriteBatchAsync(new Engine.Mind.WriteBatchRequest
            {
                Journals = new List<Engine.Mind.WriteJournalRequest>
                {
                    new()
                    {
                        Content = args.Trim(),
                        Kind = "Experience",
                        Source = "UserCommand",
                        ChannelId = 0,
                    }
                }
            });

            return CommandResult.Ok($"已写入经历: {args.Trim()}");
        }
    }
}
