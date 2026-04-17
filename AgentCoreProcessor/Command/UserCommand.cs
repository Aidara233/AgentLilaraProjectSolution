using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 用户管理。
    /// 用法: /user [list] | permission <platform:id> <level>
    /// </summary>
    internal class UserCommand : ICommand
    {
        public string Name => "user";
        public string Description => "用户管理 (list/permission/memo)";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        public async Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sub = parts.Length > 0 ? parts[0].ToLower() : "list";
            var ctx = context.SystemContext;

            return sub switch
            {
                "list" => await ListAsync(ctx),
                "permission" => await SetPermissionAsync(parts, ctx),
                "memo" => await SetMemoAsync(parts, ctx),
                _ => CommandResult.Fail($"未知子命令: {sub}")
            };
        }

        private static async Task<CommandResult> ListAsync(Engine.ISystemContext ctx)
        {
            var users = await ctx.Session.GetAllUsersAsync();
            if (users.Count == 0)
                return CommandResult.Ok("无用户。");
            var sb = new StringBuilder();
            sb.AppendLine($"用户列表 ({users.Count}):");
            foreach (var u in users.OrderBy(u => u.Id))
                sb.AppendLine($"  [{u.Id}] {u.Platform}:{u.PlatformId} P{u.PersonId} 权限={u.PermissionLevel}");
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }

        private static async Task<CommandResult> SetPermissionAsync(
            string[] parts, Engine.ISystemContext ctx)
        {
            if (parts.Length < 3)
                return CommandResult.Fail("用法: /user permission <platform:id> <level>\n可选等级: Blocked, Restricted, Default, Trusted, Admin");

            var target = parts[1];
            var colonIdx = target.IndexOf(':');
            if (colonIdx <= 0)
                return CommandResult.Fail("格式: platform:platformId (如 qq:12345)");

            var platform = target[..colonIdx];
            var platformId = target[(colonIdx + 1)..];

            if (!Enum.TryParse<PermissionLevel>(parts[2], true, out var level))
                return CommandResult.Fail($"未知权限等级: {parts[2]}\n可选: Blocked, Restricted, Default, Trusted, Admin");

            var user = await ctx.Session.FindUserAsync(platform, platformId);
            if (user == null)
                return CommandResult.Fail($"用户 {platform}:{platformId} 不存在。");

            user.PermissionLevel = level;
            await ctx.Session.UpdateUserAsync(user);
            return CommandResult.Ok($"用户 {platform}:{platformId} 权限已设为 {level}。");
        }

        private static async Task<CommandResult> SetMemoAsync(
            string[] parts, Engine.ISystemContext ctx)
        {
            if (parts.Length < 3 || !int.TryParse(parts[1], out var personId))
                return CommandResult.Fail("用法: /user memo <personId> <文本>");

            var person = await ctx.Session.GetPersonByIdAsync(personId);
            if (person == null)
                return CommandResult.Fail($"Person [{personId}] 不存在。");

            var text = string.Join(' ', parts.Skip(2));
            person.FastMemory = text;
            await ctx.Session.UpdatePersonAsync(person);
            return CommandResult.Ok($"Person [{personId}] 快速记忆已更新: {text}");
        }
    }
}
