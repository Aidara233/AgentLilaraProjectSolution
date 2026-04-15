using System;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 修改 Person 信任等级。
    /// 用法: /trust <personId> <level>
    /// </summary>
    internal class TrustCommand : ICommand
    {
        public string Name => "trust";
        public string Description => "修改信任等级 (用法: /trust <personId> <level>)";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        public async Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out var personId))
                return CommandResult.Fail(
                    "用法: /trust <personId> <level>\n" +
                    "等级: Unknown, Stranger, Understanding, Familiarity, Trust, AbsoluteTrust");

            if (!Enum.TryParse<TrustLevel>(parts[1], true, out var level))
                return CommandResult.Fail(
                    $"未知信任等级: {parts[1]}\n" +
                    "可选: Unknown, Stranger, Understanding, Familiarity, Trust, AbsoluteTrust");

            var person = await context.SystemContext.Session.GetPersonByIdAsync(personId);
            if (person == null)
                return CommandResult.Fail($"Person [{personId}] 不存在。");

            var oldLevel = person.TrustLevel;
            person.TrustLevel = level;
            await context.SystemContext.Session.UpdatePersonAsync(person);

            return CommandResult.Ok($"Person [{personId}] 信任等级: {oldLevel} → {level}");
        }
    }
}
