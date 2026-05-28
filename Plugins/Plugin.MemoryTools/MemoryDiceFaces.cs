using AgentLilara.PluginSDK.Dice;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools;

internal static class MemoryDiceFaces
{
    private static readonly Random Rng = new();

    public static void Register(IDiceRegistry registry, IMemoryAccess memory)
    {
        registry.Register(new DiceFace
        {
            Id = "memory:main",
            Label = "随机主记忆",
            Category = "memory",
            Weight = 1.0,
            Roll = async ct =>
            {
                var count = await memory.CountAsync();
                if (count == 0)
                    return NoResult("主记忆库为空");
                var entry = (await memory.ListAsync(Rng.Next(count), 1)).FirstOrDefault();
                if (entry == null)
                    return NoResult("主记忆抽取失败");
                return FromEntry(entry);
            }
        });

        registry.Register(new DiceFace
        {
            Id = "memory:temp",
            Label = "随机临时记忆",
            Category = "memory",
            Weight = 0.8,
            Roll = async ct =>
            {
                var count = await memory.CountTempAsync();
                if (count == 0)
                    return NoResult("临时记忆库为空");
                var entry = (await memory.ListTempAsync(Rng.Next(count), 1)).FirstOrDefault();
                if (entry == null)
                    return NoResult("临时记忆抽取失败");
                return FromTempEntry(entry);
            }
        });

        registry.Register(new DiceFace
        {
            Id = "memory:important",
            Label = "随机高重要性记忆",
            Category = "memory",
            Weight = 0.6,
            Roll = async ct =>
            {
                var results = await memory.FilterAsync(new MemoryFilter { MinImportance = 0.7f, Limit = 50 });
                if (results.Count == 0)
                    return NoResult("没有高重要性记忆");
                return FromEntry(results[Rng.Next(results.Count)]);
            }
        });
    }

    private static DiceResult FromEntry(MemoryEntry m)
    {
        var ch = m.ChannelId.HasValue ? $"ch{m.ChannelId}" : "-";
        var meta = $"[memory:main | {ch} | {m.CreatedAt:yyyy-MM-dd}]";
        var followUp = $"可 memory_get id={m.Id} 查看详情，或 memory_link_get memory_id={m.Id} 看关联";
        return new DiceResult { Meta = meta, Content = m.Content, FollowUp = followUp };
    }

    private static DiceResult FromTempEntry(TempMemoryEntry m)
    {
        var ch = m.ChannelId.HasValue ? $"ch{m.ChannelId}" : "-";
        var meta = $"[memory:temp | {ch} | {m.CreatedAt:yyyy-MM-dd}]";
        return new DiceResult { Meta = meta, Content = m.Content, FollowUp = "" };
    }

    private static DiceResult NoResult(string reason) => new()
    {
        Meta = "[memory | - | -]",
        Content = $"({reason})",
        FollowUp = ""
    };
}
