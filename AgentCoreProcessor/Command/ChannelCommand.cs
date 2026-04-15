using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 频道管理。
    /// 用法: /channel [list] | affinity <id> <值>
    /// </summary>
    internal class ChannelCommand : ICommand
    {
        public string Name => "channel";
        public string Description => "频道管理 (list/affinity)";
        public PermissionLevel RequiredPermission => PermissionLevel.Default;

        public async Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var parts = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            var sub = parts.Length > 0 ? parts[0].ToLower() : "list";
            var ctx = context.SystemContext;

            return sub switch
            {
                "list" => await ListAsync(ctx),
                "affinity" => await SetAffinityAsync(parts, ctx, context),
                _ => CommandResult.Fail($"未知子命令: {sub}")
            };
        }

        private static async Task<CommandResult> ListAsync(Engine.ISystemContext ctx)
        {
            var channels = await ctx.Session.GetAllChannelsAsync();
            if (channels.Count == 0)
                return CommandResult.Ok("无频道。");
            var sb = new StringBuilder();
            sb.AppendLine($"频道列表 ({channels.Count}):");
            foreach (var ch in channels.OrderBy(c => c.Id))
                sb.AppendLine($"  [{ch.Id}] {ch.Name} 亲和度={ch.Affinity:F2}");
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }

        private static async Task<CommandResult> SetAffinityAsync(
            string[] parts, Engine.ISystemContext ctx, CommandContext cmdCtx)
        {
            if (cmdCtx.User.PermissionLevel < PermissionLevel.Admin)
                return CommandResult.Fail("修改亲和度需要 Admin 权限。");
            if (parts.Length < 3 || !int.TryParse(parts[1], out var id)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                return CommandResult.Fail("用法: /channel affinity <id> <值>");
            var channel = await ctx.Session.GetChannelByIdAsync(id);
            if (channel == null)
                return CommandResult.Fail($"频道 [{id}] 不存在。");
            channel.Affinity = val;
            await ctx.Session.UpdateChannelAsync(channel);
            return CommandResult.Ok($"频道 [{id}] {channel.Name} 亲和度已设为 {val:F2}。");
        }
    }
}
