using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 记忆管理命令（单库模式：Claims + Journals）。
    /// 用法: /memory list | search <关键词> | delete <id> | temp
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
                return CommandResult.Fail("用法: /memory list | search <关键词> | delete <id> | temp");

            var ctx = context.SystemContext;
            return parts[0].ToLower() switch
            {
                "list" => await ListAsync(ctx),
                "search" => await SearchAsync(parts, ctx),
                "delete" => await DeleteAsync(parts, ctx),
                "temp" => await TempAsync(parts, ctx),
                _ => CommandResult.Fail($"未知子命令: {parts[0]}")
            };
        }

        private static async Task<CommandResult> ListAsync(Engine.ISystemContext ctx)
        {
            var sb = new StringBuilder();
            var claimCount = await ctx.Claims.CountAsync();
            var journalCount = await ctx.Journals.CountAsync();
            sb.AppendLine($"断言(Claims): {claimCount} 条 | 经历(Journals): {journalCount} 条");
            sb.AppendLine("--- 最近 10 条断言 ---");

            // 新仓库无 GetRecentAsync，取全量后按记录时间倒序。
            var claims = await ctx.Claims.GetAllAsync();
            var recent = claims.OrderByDescending(c => c.RecordedAt).Take(10).ToList();

            foreach (var m in recent)
            {
                var tags = new StringBuilder();
                if (m.Credibility < 0.5f) tags.Append("[低可信] ");
                if (m.IsInference) tags.Append("[推断] ");
                sb.AppendLine($"  [{m.Id}] {tags}{m.Raw}");
            }
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }

        private static async Task<CommandResult> SearchAsync(string[] parts, Engine.ISystemContext ctx)
        {
            if (parts.Length < 2)
                return CommandResult.Fail("用法: /memory search <关键词>");
            var keyword = string.Join(" ", parts.Skip(1));

            // 经心智引擎检索服务召回（Claim + Journal 混合，带可信度）。
            var mind = ctx.Mind;
            if (mind?.Retrieve == null)
                return CommandResult.Fail("心智引擎未启动。");

            var result = await mind.Retrieve.SearchAsync(keyword, 10);
            var hits = result.Hits;
            if (hits.Count == 0)
                return CommandResult.Ok($"未找到包含 \"{keyword}\" 的记忆。");
            var sb = new StringBuilder();
            sb.AppendLine($"搜索 \"{keyword}\" — {hits.Count} 条结果:");
            foreach (var m in hits)
            {
                var tags = new StringBuilder();
                if (m.IsInference) tags.Append("[推断] ");
                sb.AppendLine($"  [{m.Kind}:{m.Id}] (可信度 {m.Credibility:F2}) {tags}{m.Raw}");
            }
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }

        private static async Task<CommandResult> DeleteAsync(string[] parts, Engine.ISystemContext ctx)
        {
            if (parts.Length < 2 || !int.TryParse(parts[1], out var id))
                return CommandResult.Fail("用法: /memory delete <id>");
            var entry = await ctx.Claims.GetByIdAsync(id);
            if (entry == null)
                return CommandResult.Fail($"断言 [{id}] 不存在。");
            await ctx.Claims.DeleteAsync(entry);
            return CommandResult.Ok($"已删除断言 [{id}]。");
        }

        private static async Task<CommandResult> TempAsync(string[] parts, Engine.ISystemContext ctx)
        {
            // 临时库概念已并入单库（Claims/Journals），无独立临时库可清空。
            if (parts.Length > 1 && parts[1].ToLower() == "clear")
                return CommandResult.Ok("临时库已并入单库（Claims/Journals），无需清空。");

            var journals = await ctx.Journals.GetRecentAsync(10);
            if (journals.Count == 0)
                return CommandResult.Ok("临时库已并入单库（Claims/Journals）。当前无最近经历。");
            var sb = new StringBuilder();
            sb.AppendLine("临时库已并入单库（Claims/Journals）。最近经历:");
            foreach (var j in journals)
                sb.AppendLine($"  [{j.Id}] {j.Content}");
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }
    }
}
