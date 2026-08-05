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

            // 经心智引擎检索服务召回（全局混合 Claim/Journal，无 person/频道偏置）。
            var mind = ctx.Mind;
            if (mind?.Retrieve == null)
                return CommandResult.Fail("心智引擎未启动。");

            var result = await mind.Retrieve.SearchAsync(query, 10);
            var results = result.Hits;

            if (results.Count == 0)
                return CommandResult.Ok($"召回 \"{query}\" — 无结果。");

            var sb = new StringBuilder();
            sb.AppendLine($"召回 \"{query}\" — {results.Count} 条 (person={personId}):");
            foreach (var m in results)
            {
                var flags = new StringBuilder();
                if (m.IsInference) flags.Append("[推断] ");
                if (m.Credibility < 0.5f) flags.Append("[低可信] ");
                sb.AppendLine($"  [{m.Kind}:{m.Id}] (可信度 {m.Credibility:F3}) {flags}{m.Raw}");
            }
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }
    }
}
