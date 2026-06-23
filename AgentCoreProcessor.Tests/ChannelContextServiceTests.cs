// AgentCoreProcessor.Tests/ChannelContextServiceTests.cs
using Xunit;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Tests;

public class ChannelContextServiceTests
{
    [Fact]
    public void RenderChannelInfo_Group_IncludesGroupId()
    {
        var s = ChannelContextService.RenderChannelInfo("group_12345", 7);
        Assert.Contains("群聊", s);
        Assert.Contains("12345", s);
        Assert.Contains("频道ID: 7", s);
    }

    [Fact]
    public void RenderChannelInfo_Private_IncludesPeerId()
    {
        var s = ChannelContextService.RenderChannelInfo("private_999", 3);
        Assert.Contains("私聊", s);
        Assert.Contains("999", s);
    }

    [Fact]
    public void RenderChannelInfo_Malformed_ReturnsEmpty()
    {
        Assert.Equal("", ChannelContextService.RenderChannelInfo("garbage", 1));
        Assert.Equal("", ChannelContextService.RenderChannelInfo(null, 1));
    }

    [Fact]
    public void RenderParticipants_ListsEachWithTrustAndMemo()
    {
        var ps = new List<ParticipantView>
        {
            new("张三", "三儿", "111", 5, 2, "老熟人"),
            new("李四", null, "222", 6, 0, null),
        };
        var s = ChannelContextService.RenderParticipants("group_42", 9, ps);
        Assert.Contains("[当前参与者]", s);
        Assert.Contains("张三", s);
        Assert.Contains("别称:三儿", s);
        Assert.Contains("platform_id:111", s);
        Assert.Contains("person_id:5", s);
        Assert.Contains("trust:2", s);
        Assert.Contains("老熟人", s);
        Assert.Contains("李四", s);
    }

    [Fact]
    public void RenderParticipants_Empty_ReturnsEmpty()
    {
        Assert.Equal("", ChannelContextService.RenderParticipants("group_1", 1, new List<ParticipantView>()));
    }
}

file class FakeDataSource : IChannelDataSource
{
    public List<UserMessage> Messages = new();
    public Dictionary<int, User> Users = new();
    public Dictionary<int, Person> Persons = new();
    public Task<List<UserMessage>> GetRecentMessagesAsync(int channelId, int limit)
        => Task.FromResult(Messages.FindAll(m => m.ChannelId == channelId));
    public Task<User?> GetUserByIdAsync(int userId)
        => Task.FromResult(Users.TryGetValue(userId, out var u) ? u : null);
    public Task<Person?> GetPersonByIdAsync(int personId)
        => Task.FromResult(Persons.TryGetValue(personId, out var p) ? p : null);
}

public class ParticipantDerivationTests
{
    [Fact]
    public async Task GetParticipants_DistinctSendersFromRecentMessages()
    {
        var data = new FakeDataSource();
        data.Messages.Add(new UserMessage { ChannelId = 1, UserId = 10, IsFromBot = false });
        data.Messages.Add(new UserMessage { ChannelId = 1, UserId = 10, IsFromBot = false }); // 重复发送者
        data.Messages.Add(new UserMessage { ChannelId = 1, UserId = 20, IsFromBot = false });
        data.Messages.Add(new UserMessage { ChannelId = 1, UserId = 0,  IsFromBot = true });  // bot 跳过
        data.Users[10] = new User { Id = 10, PersonId = 100, PlatformId = "p10", DisplayName = "甲" };
        data.Users[20] = new User { Id = 20, PersonId = 200, PlatformId = "p20", DisplayName = "乙" };
        data.Persons[100] = new Person { Id = 100, Name = "甲名", TrustLevel = TrustLevel.Understanding }; // Understanding = 2
        data.Persons[200] = new Person { Id = 200, Name = "乙名", TrustLevel = TrustLevel.Stranger };      // Stranger = 1

        var svc = new ChannelContextService(data);
        var ps = await svc.GetParticipantsAsync(1);

        Assert.Equal(2, ps.Count);                       // distinct，bot 排除
        Assert.Contains(ps, p => p.PlatformId == "p10" && p.DisplayName == "甲名" && p.TrustLevel == 2);
        Assert.Contains(ps, p => p.PlatformId == "p20" && p.DisplayName == "乙名");
    }
}
