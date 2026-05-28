using AgentLilara.PluginSDK.Dice;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SkillTools;

internal static class SkillDiceFaces
{
    private static readonly Random Rng = new();

    public static void Register(IDiceRegistry registry, Dictionary<string, SkillEntry> skills)
    {
        registry.Register(new DiceFace
        {
            Id = "skill:random_skill",
            Label = "随机技能",
            Category = "skill",
            Weight = 0.7,
            Roll = ct =>
            {
                if (skills.Count == 0)
                    return Task.FromResult(NoResult("没有可用技能"));

                var entries = skills.Values.ToList();
                var entry = entries[Rng.Next(entries.Count)];

                var body = entry.GetBody();
                var preview = body.Length > 200 ? body[..200] + "..." : body;

                return Task.FromResult(new DiceResult
                {
                    Meta = $"[skill | {entry.Name} | -]",
                    Content = $"**{entry.Name}** — {entry.Description}\n\n{preview}",
                    FollowUp = $"可 invoke_skill name={entry.Name} 加载完整技能指导"
                });
            }
        });
    }

    private static DiceResult NoResult(string reason) => new()
    {
        Meta = "[skill | - | -]",
        Content = $"({reason})",
        FollowUp = ""
    };
}
