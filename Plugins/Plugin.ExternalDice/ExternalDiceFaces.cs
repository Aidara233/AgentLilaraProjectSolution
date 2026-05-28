using AgentLilara.PluginSDK.Dice;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json.Linq;

namespace Plugin.ExternalDice;

internal static class ExternalDiceFaces
{
    private static readonly Random Rng = new();

    public static void Register(
        IDiceRegistry registry,
        IAdapterAccess? adapterAccess,
        IChannelAccess? channelAccess,
        IPersonAccess? personAccess)
    {
        // ── 适配器相关 ──

        if (adapterAccess != null && channelAccess != null)
        {
            registry.Register(new DiceFace
            {
                Id = "groupfile:random_file",
                Label = "随机群文件",
                Category = "external",
                Weight = 0.5,
                Roll = ct => RollGroupFile(adapterAccess, channelAccess)
            });

            registry.Register(new DiceFace
            {
                Id = "adapter:random_group",
                Label = "随机群",
                Category = "external",
                Weight = 0.4,
                Roll = ct => RollRandomGroup(adapterAccess, channelAccess)
            });

            registry.Register(new DiceFace
            {
                Id = "adapter:random_member",
                Label = "随机群成员",
                Category = "external",
                Weight = 0.4,
                Roll = ct => RollRandomMember(adapterAccess, channelAccess)
            });
        }

        // ── 人物 ──

        if (personAccess != null)
        {
            registry.Register(new DiceFace
            {
                Id = "person:random",
                Label = "随机人物",
                Category = "person",
                Weight = 0.6,
                Roll = ct => RollRandomPerson(personAccess)
            });
        }

        // ── 频道消息 ──

        if (channelAccess != null)
        {
            registry.Register(new DiceFace
            {
                Id = "channel:random_message",
                Label = "随机频道消息片段",
                Category = "channel",
                Weight = 0.5,
                Roll = ct => RollChannelMessage(channelAccess)
            });
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Adapter helpers
    // ═══════════════════════════════════════════════════════════

    private static async Task<string?> ResolveAdapterId(IAdapterAccess adapterAccess, IChannelAccess channelAccess)
    {
        // 单适配器场景：传任意字符串都能命中
        var id = adapterAccess.GetAdapterIdForChannel("_");
        if (id != null) return id;

        // 多适配器：尝试从频道反查
        var channels = await channelAccess.GetAllChannelsAsync();
        foreach (var ch in channels)
        {
            var detail = await channelAccess.GetChannelDetailAsync(ch.Id);
            if (detail?.PlatformChannelId == null) continue;
            id = adapterAccess.GetAdapterIdForChannel(detail.PlatformChannelId);
            if (id != null) return id;
        }

        return null;
    }

    private static async Task<JArray?> CallAdapterJson(IAdapterAccess adapterAccess, string adapterId,
        string action, string? paramsJson = null)
    {
        try
        {
            var result = await adapterAccess.ExecuteActionAsync(adapterId, action, paramsJson);
            if (string.IsNullOrEmpty(result)) return null;
            return JArray.Parse(result);
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Face: groupfile:random_file
    // ═══════════════════════════════════════════════════════════

    private static async Task<DiceResult> RollGroupFile(IAdapterAccess adapterAccess, IChannelAccess channelAccess)
    {
        var adapterId = await ResolveAdapterId(adapterAccess, channelAccess);
        if (adapterId == null)
            return NoResult("external", "没有可用的适配器");

        var groups = await CallAdapterJson(adapterAccess, adapterId, "get_group_list");
        if (groups == null || groups.Count == 0)
            return NoResult("external", "获取群列表失败或为空");

        var group = groups[Rng.Next(groups.Count)];
        var groupId = group["group_id"]?.ToString() ?? "0";
        var groupName = group["group_name"]?.ToString() ?? "未知群";

        // get_group_files 返回格式化文本摘要
        try
        {
            var paramsJson = $"{{\"group_id\": \"{groupId}\"}}";
            var summary = await adapterAccess.ExecuteActionAsync(adapterId, "get_group_files", paramsJson);
            if (string.IsNullOrEmpty(summary))
                return NoResult("external", $"群 {groupName} 没有文件");

            // 截取前 500 字符，避免太长
            var snippet = summary.Length > 500 ? summary[..500] + "\n...(截断)" : summary;

            return new DiceResult
            {
                Meta = $"[external | group:{groupName} | -]",
                Content = $"**群 {groupName}** 的文件列表:\n\n{snippet}",
                FollowUp = $"可 list_group_files group_id={groupId} 查看完整文件列表"
            };
        }
        catch
        {
            return NoResult("external", $"获取群 {groupName} 文件失败");
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Face: adapter:random_group
    // ═══════════════════════════════════════════════════════════

    private static async Task<DiceResult> RollRandomGroup(IAdapterAccess adapterAccess, IChannelAccess channelAccess)
    {
        var adapterId = await ResolveAdapterId(adapterAccess, channelAccess);
        if (adapterId == null)
            return NoResult("external", "没有可用的适配器");

        var groups = await CallAdapterJson(adapterAccess, adapterId, "get_group_list");
        if (groups == null || groups.Count == 0)
            return NoResult("external", "获取群列表失败或为空");

        var group = groups[Rng.Next(groups.Count)];
        var groupId = group["group_id"]?.ToString() ?? "?";
        var groupName = group["group_name"]?.ToString() ?? "未命名群";
        var memberCount = group["member_count"]?.ToString();
        var maxMembers = group["max_member_count"]?.ToString();

        var info = $"**{groupName}** (群号: {groupId})";
        if (memberCount != null)
            info += $"\n成员: {memberCount}" + (maxMembers != null ? $"/{maxMembers}" : "");

        return new DiceResult
        {
            Meta = $"[external | group:{groupId} | -]",
            Content = info,
            FollowUp = $"可 adapter_action action=get_group_member_list group_id={groupId} 查看成员"
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  Face: adapter:random_member
    // ═══════════════════════════════════════════════════════════

    private static async Task<DiceResult> RollRandomMember(IAdapterAccess adapterAccess, IChannelAccess channelAccess)
    {
        var adapterId = await ResolveAdapterId(adapterAccess, channelAccess);
        if (adapterId == null)
            return NoResult("external", "没有可用的适配器");

        var groups = await CallAdapterJson(adapterAccess, adapterId, "get_group_list");
        if (groups == null || groups.Count == 0)
            return NoResult("external", "获取群列表失败或为空");

        var group = groups[Rng.Next(groups.Count)];
        var groupId = group["group_id"]?.ToString() ?? "0";
        var groupName = group["group_name"]?.ToString() ?? "未知群";

        var paramsJson = $"{{\"group_id\": \"{groupId}\"}}";
        var members = await CallAdapterJson(adapterAccess, adapterId, "get_group_member_list", paramsJson);
        if (members == null || members.Count == 0)
            return NoResult("external", $"群 {groupName} 成员列表为空");

        var member = members[Rng.Next(members.Count)];
        var userId = member["user_id"]?.ToString() ?? "?";
        var nickname = member["nickname"]?.ToString() ?? "未知";
        var card = member["card"]?.ToString();
        var role = member["role"]?.ToString() ?? "member";

        var displayName = !string.IsNullOrEmpty(card) && card != nickname ? $"{nickname} (群名片: {card})" : nickname;
        var roleLabel = role switch
        {
            "owner" => "群主",
            "admin" => "管理员",
            _ => "成员"
        };

        return new DiceResult
        {
            Meta = $"[external | group:{groupName} | member:{userId}]",
            Content = $"**{displayName}** — {roleLabel}\n来自群: {groupName}",
            FollowUp = $"可 adapter_action action=get_group_member_list group_id={groupId} 查看完整成员列表"
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  Face: person:random
    // ═══════════════════════════════════════════════════════════

    private static async Task<DiceResult> RollRandomPerson(IPersonAccess personAccess)
    {
        var all = await personAccess.GetAllAsync();
        if (all.Count == 0)
            return NoResult("person", "人物库为空");

        var person = all[Rng.Next(all.Count)];
        var name = person.Name ?? "未命名";
        var aliases = !string.IsNullOrEmpty(person.Aliases) ? $" (别名: {person.Aliases})" : "";
        var trust = $"信任: {person.TrustLevel} ({person.TrustProgress:F0}%)";
        var alert = person.AlertLevel > 0 ? $" ⚠ 警戒: {person.AlertLevel}" : "";

        var content = $"**{name}**{aliases}\n{trust}{alert}";
        if (!string.IsNullOrEmpty(person.FastMemory))
            content += $"\n\n笔记: {person.FastMemory}";

        return new DiceResult
        {
            Meta = $"[person | id:{person.Id} | {name}]",
            Content = content,
            FollowUp = $"可 memory_search keyword={name} 查看相关记忆，或 memory_filter person_id={person.Id} 过滤"
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  Face: channel:random_message
    // ═══════════════════════════════════════════════════════════

    private static async Task<DiceResult> RollChannelMessage(IChannelAccess channelAccess)
    {
        var channels = await channelAccess.GetAllChannelsAsync();
        if (channels.Count == 0)
            return NoResult("channel", "没有可用频道");

        // 优先选有消息的频道，最多试 3 次
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var candidates = channels.Where(c => c.MessageCount > 0).ToList();
            if (candidates.Count == 0)
                candidates = channels; // fallback: 随便选

            var channel = candidates[Rng.Next(candidates.Count)];
            var messages = await channelAccess.GetMessagesAsync(channel.Id, count: 10);
            if (messages.Count == 0) continue;

            // 取连续 3~5 条消息作为片段
            var windowSize = Math.Min(Rng.Next(3, 6), messages.Count);
            var startIdx = Rng.Next(0, messages.Count - windowSize + 1);
            var window = messages.Skip(startIdx).Take(windowSize).ToList();

            var channelName = channel.Name ?? $"频道{channel.Id}";
            var timeRange = window.Count > 0
                ? $"{window[0].Timestamp:MM-dd HH:mm} ~ {window[^1].Timestamp:HH:mm}"
                : "-";

            var lines = window.Select(m =>
            {
                var preview = m.Content.Length > 200 ? m.Content[..200] + "..." : m.Content;
                return $"**{m.UserName}**: {preview}";
            });

            return new DiceResult
            {
                Meta = $"[channel | {channelName} | {timeRange}]",
                Content = $"**#{channelName}** — {timeRange}\n\n{string.Join("\n", lines)}",
                FollowUp = $"可 channel_detail id={channel.Id} 查看频道详情，或 browse_messages channel_id={channel.Id}"
            };
        }

        return NoResult("channel", "没有找到有消息的频道");
    }

    // ═══════════════════════════════════════════════════════════

    private static DiceResult NoResult(string category, string reason) => new()
    {
        Meta = $"[{category} | - | -]",
        Content = $"({reason})",
        FollowUp = ""
    };
}
