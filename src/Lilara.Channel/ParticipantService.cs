namespace AgentCoreProcessor.Engine.Context;

internal record ParticipantView(string DisplayName, string? Aliases, string PlatformId,
    int PersonId, int TrustLevel, string? Memo, int UserId = 0);

internal record ParticipantMessage(int UserId, bool IsFromBot);
internal record ParticipantUser(int Id, int PersonId, string PlatformId, string? DisplayName);
internal record ParticipantPerson(string? Name, string? Aliases, int TrustLevel, string? Memo);

internal interface IParticipantSource
{
    Task<IReadOnlyList<ParticipantMessage>> GetRecentAsync(int channelId, int limit);
    Task<ParticipantUser?> GetUserAsync(int userId);
    Task<ParticipantPerson?> GetPersonAsync(int personId);
}

/// <summary>从查询快照派生参与者；顺序与数据源一致，不持有数据库实体。</summary>
internal sealed class ParticipantService(IParticipantSource source)
{
    public async Task<List<ParticipantView>> GetParticipantsAsync(int channelId, int limit = 30)
    {
        var result = new List<ParticipantView>();
        var seen = new HashSet<int>();
        foreach (var message in await source.GetRecentAsync(channelId, limit))
        {
            if (message.IsFromBot || message.UserId <= 0 || !seen.Add(message.UserId)) continue;
            var user = await source.GetUserAsync(message.UserId);
            if (user is null) continue;
            var person = await source.GetPersonAsync(user.PersonId);
            var name = !string.IsNullOrEmpty(person?.Name) ? person.Name
                : !string.IsNullOrEmpty(user.DisplayName) ? user.DisplayName : user.PlatformId;
            result.Add(new ParticipantView(name, person?.Aliases, user.PlatformId, user.PersonId,
                person?.TrustLevel ?? 0, person?.Memo, user.Id));
        }
        return result;
    }
}
