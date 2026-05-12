using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 授权管理命令。管理工具使用权限。
    /// /auth grant 工具名 — 授予权限
    /// /auth revoke 工具名 — 撤销权限
    /// /auth list — 查看当前授权状态
    /// </summary>
    internal class AuthCommand : ICommand
    {
        public string Name => "auth";
        public string Description => "管理工具授权（grant/revoke/list）";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        public Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var parts = args.Trim().Split(' ', 2);
            var action = parts[0].ToLowerInvariant();

            return action switch
            {
                "grant" when parts.Length >= 2 => GrantAsync(parts[1].Trim(), context),
                "revoke" when parts.Length >= 2 => RevokeAsync(parts[1].Trim(), context),
                "list" => ListAsync(context),
                _ => Task.FromResult(CommandResult.Fail("用法: /auth grant <工具名> | /auth revoke <工具名> | /auth list"))
            };
        }

        private Task<CommandResult> GrantAsync(string toolName, CommandContext context)
        {
            var tool = ToolRegistry.Get(toolName);
            if (tool == null)
                return Task.FromResult(CommandResult.Fail($"未找到工具「{toolName}」"));

            if (tool.GetPermission() <= AgentCoreProcessor.Tool.Contract.ToolPermission.Default)
                return Task.FromResult(CommandResult.Ok($"「{toolName}」不需要授权，可自由使用"));

            AuthStore.Grant(context.Message.ChannelId, toolName);
            return Task.FromResult(CommandResult.Ok($"已授权「{toolName}」在此频道使用"));
        }

        private Task<CommandResult> RevokeAsync(string toolName, CommandContext context)
        {
            AuthStore.Revoke(context.Message.ChannelId, toolName);
            return Task.FromResult(CommandResult.Ok($"已撤销「{toolName}」在此频道的授权"));
        }

        private Task<CommandResult> ListAsync(CommandContext context)
        {
            var granted = AuthStore.GetGranted(context.Message.ChannelId);
            var restricted = ToolRegistry.All.Values
                .Where(t => t.GetPermission() > AgentCoreProcessor.Tool.Contract.ToolPermission.Default)
                .ToList();

            if (restricted.Count == 0)
                return Task.FromResult(CommandResult.Ok("没有需要授权的工具"));

            var sb = new StringBuilder("工具授权状态：\n");
            foreach (var tool in restricted)
            {
                var status = granted.Contains(tool.Name) ? "已授权" : "未授权";
                sb.AppendLine($"- {tool.Name}（{tool.GetPermission()}）：{status}");
            }
            return Task.FromResult(CommandResult.Ok(sb.ToString().TrimEnd()));
        }
    }

    /// <summary>
    /// 频道级授权存储。内存级，生命周期 = 进程。
    /// </summary>
    internal static class AuthStore
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.HashSet<string>>
            _store = new();

        public static void Grant(string channelId, string toolName)
        {
            var set = _store.GetOrAdd(channelId, _ => new System.Collections.Generic.HashSet<string>());
            lock (set) set.Add(toolName);
        }

        public static void Revoke(string channelId, string toolName)
        {
            if (_store.TryGetValue(channelId, out var set))
                lock (set) set.Remove(toolName);
        }

        public static System.Collections.Generic.HashSet<string> GetGranted(string channelId)
        {
            if (_store.TryGetValue(channelId, out var set))
                lock (set) return new System.Collections.Generic.HashSet<string>(set);
            return new System.Collections.Generic.HashSet<string>();
        }
    }
}
