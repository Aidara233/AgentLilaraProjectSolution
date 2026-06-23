// AgentCoreProcessor/Engine/Context/ChannelContextService.cs
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine.Context;

/// <summary>频道上下文的一个参与者视图（DB 派生，无累积状态）。</summary>
internal record ParticipantView(
    string DisplayName, string? Aliases, string PlatformId,
    int PersonId, int TrustLevel, string? Memo);

/// <summary>
/// 频道上下文构建服务。ChannelEngine 与 RuntimeSession 共用。
/// 纯格式化方法静态；依赖 DB 的方法在后续 Task 加入（实例方法，注入 IChannelDataSource）。
/// </summary>
internal partial class ChannelContextService
{
    private readonly IChannelDataSource _data;
    public ChannelContextService(IChannelDataSource data) => _data = data;

    /// <summary>从频道最近消息派生当前参与者（distinct 发送者，排除 bot）。无累积状态。</summary>
    public async Task<List<ParticipantView>> GetParticipantsAsync(int channelId, int limit = 30)
    {
        var result = new List<ParticipantView>();
        var seen = new HashSet<int>();
        var msgs = await _data.GetRecentMessagesAsync(channelId, limit);
        foreach (var m in msgs)
        {
            if (m.IsFromBot || m.UserId <= 0 || !seen.Add(m.UserId)) continue;
            var user = await _data.GetUserByIdAsync(m.UserId);
            if (user == null) continue;
            var person = await _data.GetPersonByIdAsync(user.PersonId);
            var displayName = !string.IsNullOrEmpty(person?.Name) ? person!.Name
                : !string.IsNullOrEmpty(user.DisplayName) ? user.DisplayName
                : user.PlatformId;
            result.Add(new ParticipantView(
                displayName, person?.Aliases, user.PlatformId,
                user.PersonId, (int)(person?.TrustLevel ?? Database.TrustLevel.Unknown), person?.FastMemory));
        }
        return result;
    }

    /// <summary>渲染频道信息行（群号/私聊对象/频道ID）。channelName 格式 "group_xxx"/"private_xxx"。</summary>
    public static string RenderChannelInfo(string? channelName, int channelId)
    {
        if (string.IsNullOrEmpty(channelName)) return "";
        var parts = channelName.Split('_', 2);
        if (parts.Length != 2) return "";
        var id = parts[1];
        return parts[0] switch
        {
            "group" => $"当前频道：群聊，群号: {id}，频道ID: {channelId}。对群成员操作时需提供 group_id={id}。",
            "private" => $"当前频道：私聊，对方QQ: {id}，频道ID: {channelId}。",
            _ => ""
        };
    }

    /// <summary>渲染参与者列表块。空列表返回空串。</summary>
    public static string RenderParticipants(
        string? channelName, int channelId, IReadOnlyList<ParticipantView> participants)
    {
        if (participants.Count == 0) return "";
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(channelName))
        {
            var p = channelName.Split('_', 2);
            if (p.Length == 2)
                sb.AppendLine($"[频道] 类型: {p[0]}, 平台ID: {p[1]}, 频道ID: {channelId}");
        }
        sb.AppendLine("[当前参与者]");
        foreach (var p in participants)
        {
            sb.Append($"- {p.DisplayName}");
            if (!string.IsNullOrEmpty(p.Aliases)) sb.Append($" (别称:{p.Aliases})");
            sb.Append($" [platform_id:{p.PlatformId}, person_id:{p.PersonId}, trust:{p.TrustLevel}]");
            if (!string.IsNullOrEmpty(p.Memo)) sb.Append($" — {p.Memo}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

