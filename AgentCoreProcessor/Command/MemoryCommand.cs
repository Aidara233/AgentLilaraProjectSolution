using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 记忆管理命令。
    /// 用法: /memory list [person_id] | search <关键词> | delete <id> | temp | temp clear
    /// </summary>
    internal class MemoryCommand : ICommand
    {
        public string Name => "memory";
        public string Description => "记忆管理 (list/search/delete/temp)";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        public async Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var parts = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return CommandResult.Fail("用法: /memory list [person_id] | search <关键词> | delete <id> | temp [clear]");

            var ctx = context.SystemContext;
            return parts[0].ToLower() switch
            {
                "list" => await ListAsync(parts, ctx),
                "search" => await SearchAsync(parts, ctx),
                "delete" => await DeleteAsync(parts, ctx),
                "temp" => await TempAsync(parts, ctx),
                _ => CommandResult.Fail($"未知子命令: {parts[0]}")
            };
        }

        private static async Task<CommandResult> ListAsync(string[] parts, Engine.ISystemContext ctx)
        {
            var sb = new StringBuilder();
            var totalCount = await ctx.Memories.GetCountAsync();
            var tempCount = (await ctx.TempMemories.GetAllAsync()).Count;
            sb.AppendLine($"主库: {totalCount} 条 | 临时库: {tempCount} 条");
            sb.AppendLine("--- 最近 10 条主库记忆 ---");

            var memories = parts.Length > 1 && int.TryParse(parts[1], out var pid)
                ? (await ctx.Memories.GetByPersonAsync(pid)).Take(10).ToList()
                : await ctx.Memories.GetRecentAsync(10);

            foreach (var m in memories)
            {
                var tags = new StringBuilder();
                if (m.PersonId != null) tags.Append($"P{m.PersonId} ");
                if (m.Confidence == "low") tags.Append("[低置信] ");
                if (m.Feedback != null) tags.Append($"[{m.Feedback}] ");
                sb.AppendLine($"  [{m.Id}] {tags}{m.Content}");
            }
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }

        private static async Task<CommandResult> SearchAsync(string[] parts, Engine.ISystemContext ctx)
        {
            if (parts.Length < 2)
                return CommandResult.Fail("用法: /memory search <关键词>");
            var keyword = string.Join(" ", parts.Skip(1));
            var results = await ctx.Memories.SearchAsync(keyword, 10);
            if (results.Count == 0)
                return CommandResult.Ok($"未找到包含 \"{keyword}\" 的记忆。");
            var sb = new StringBuilder();
            sb.AppendLine($"搜索 \"{keyword}\" — {results.Count} 条结果:");
            foreach (var m in results)
                sb.AppendLine($"  [{m.Id}] {m.Content}");
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }

        private static async Task<CommandResult> DeleteAsync(string[] parts, Engine.ISystemContext ctx)
        {
            if (parts.Length < 2 || !int.TryParse(parts[1], out var id))
                return CommandResult.Fail("用法: /memory delete <id>");
            var entry = await ctx.Memories.GetByIdAsync(id);
            if (entry == null)
                return CommandResult.Fail($"记忆 [{id}] 不存在。");
            await ctx.Memories.DeleteAsync(entry);
            return CommandResult.Ok($"已删除记忆 [{id}]。");
        }

        private static async Task<CommandResult> TempAsync(string[] parts, Engine.ISystemContext ctx)
        {
            if (parts.Length > 1 && parts[1].ToLower() == "clear")
            {
                var cleared = await ctx.TempMemories.ClearAllAsync();
                return CommandResult.Ok($"已清空 {cleared} 条临时记忆。");
            }
            var temps = await ctx.TempMemories.GetAllAsync();
            if (temps.Count == 0)
                return CommandResult.Ok("临时库为空。");
            var sb = new StringBuilder();
            sb.AppendLine($"临时记忆 ({temps.Count} 条):");
            foreach (var t in temps)
            {
                var conf = t.Confidence == "low" ? " [低置信]" : "";
                sb.AppendLine($"  [{t.Id}] P{t.PersonId}{conf} {t.Content}");
            }
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }
    }
}
