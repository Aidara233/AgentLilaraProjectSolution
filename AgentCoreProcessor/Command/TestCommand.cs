using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 调试命令：对指定文本跑一次记忆召回。
    /// 用法: /test recall <文本>
    /// </summary>
    internal class TestCommand : ICommand
    {
        public string Name => "test";
        public string Description => "调试工具 (用法: /test recall <文本>)";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        public async Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var parts = args.Split(' ', 2, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return CommandResult.Fail("用法: /test recall <文本>");

            return parts[0].ToLower() switch
            {
                "recall" => await RecallAsync(parts.Length > 1 ? parts[1] : "", context),
                _ => CommandResult.Fail($"未知子命令: {parts[0]}")
            };
        }

        private static async Task<CommandResult> RecallAsync(string query, CommandContext context)
        {
            if (string.IsNullOrWhiteSpace(query))
                return CommandResult.Fail("用法: /test recall <文本>");

            var ctx = context.SystemContext;
            var personId = context.Person.Id;

            var results = await ctx.MemorySvc.RecallAsync(
                personId, channelId: 0,
                query, topK: 10, includeLinks: true, includePersona: true);

            if (results.Count == 0)
                return CommandResult.Ok($"召回 \"{query}\" — 无结果。");

            var sb = new StringBuilder();
            sb.AppendLine($"召回 \"{query}\" — {results.Count} 条 (person={personId}):");
            foreach (var m in results)
            {
                var flags = new StringBuilder();
                if (m.IsTemp) flags.Append("[临时] ");
                if (m.IsPersona) flags.Append("[人设] ");
                if (m.Confidence == "low") flags.Append("[低置信] ");
                sb.AppendLine($"  {m.Score:F3} {flags}{m.Content}");
            }
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }
    }
}
