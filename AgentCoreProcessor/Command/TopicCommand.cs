using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 话题查看。
    /// 用法: /topic [list] | all
    /// </summary>
    internal class TopicCommand : ICommand
    {
        public string Name => "topic";
        public string Description => "查看活跃话题 (list/all)";
        public PermissionLevel RequiredPermission => PermissionLevel.Default;

        public async Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var parts = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            var sub = parts.Length > 0 ? parts[0].ToLower() : "list";
            var ctx = context.SystemContext;

            if (sub == "all")
                return await ListAllAsync(ctx);

            // list: 当前频道
            var channelId = context.Message.ChannelId;
            var channels = await ctx.Session.GetAllChannelsAsync();
            var channel = channels.FirstOrDefault(c => c.Name == channelId);
            if (channel == null)
                return CommandResult.Ok("当前频道无话题。");

            var topics = await ctx.Session.GetActiveTopicsAsync(channel.Id);
            if (topics.Count == 0)
                return CommandResult.Ok($"频道 [{channel.Name}] 无活跃话题。");

            var sb = new StringBuilder();
            sb.AppendLine($"频道 [{channel.Name}] 活跃话题:");
            foreach (var t in topics)
            {
                var chat = t.IsChatTopic ? " [闲聊]" : t.IsUnclassified ? " [未分类]" : "";
                sb.AppendLine($"  [{t.Id}]{chat} {t.Name} (消息数={t.MessageCount})");
            }
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }

        private static async Task<CommandResult> ListAllAsync(Engine.ISystemContext ctx)
        {
            var channels = await ctx.Session.GetAllChannelsAsync();
            if (channels.Count == 0)
                return CommandResult.Ok("无频道。");

            var sb = new StringBuilder();
            foreach (var ch in channels)
            {
                var topics = await ctx.Session.GetActiveTopicsAsync(ch.Id);
                if (topics.Count == 0) continue;
                sb.AppendLine($"频道 [{ch.Name}] (亲和度={ch.Affinity:F2}):");
                foreach (var t in topics)
                {
                    var tag = t.IsChatTopic ? " [闲聊]" : t.IsUnclassified ? " [未分类]" : "";
                    sb.AppendLine($"  [{t.Id}]{tag} {t.Name} (消息数={t.MessageCount})");
                }
            }
            return sb.Length == 0
                ? CommandResult.Ok("所有频道均无活跃话题。")
                : CommandResult.Ok(sb.ToString().TrimEnd());
        }
    }
}
