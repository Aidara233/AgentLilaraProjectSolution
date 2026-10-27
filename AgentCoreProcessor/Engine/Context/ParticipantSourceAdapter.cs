namespace AgentCoreProcessor.Engine.Context;

/// <summary>将宿主数据库查询结果投影为频道领域值。</summary>
internal sealed class ParticipantSourceAdapter(IChannelDataSource source) : IParticipantSource
{
    public async Task<IReadOnlyList<ParticipantMessage>> GetRecentAsync(int channelId, int limit) =>
        (await source.GetRecentMessagesAsync(channelId, limit))
            .Select(m => new ParticipantMessage(m.UserId, m.IsFromBot)).ToArray();

    public async Task<ParticipantUser?> GetUserAsync(int userId)
    {
        var user = await source.GetUserByIdAsync(userId);
        return user is null ? null : new ParticipantUser(user.Id, user.PersonId, user.PlatformId, user.DisplayName);
    }

    public async Task<ParticipantPerson?> GetPersonAsync(int personId)
    {
        var person = await source.GetPersonByIdAsync(personId);
        return person is null ? null : new ParticipantPerson(person.Name, person.Aliases,
            (int)person.TrustLevel, person.FastMemory);
    }
}
