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

class FakeDataSource : IChannelDataSource
{
    public List<UserMessage> Messages = new();
    public Dictionary<int, User> Users = new();
    public Dictionary<int, Person> Persons = new();
    public string? BotPlatformId { get; set; }

    public Task<List<UserMessage>> GetRecentMessagesAsync(int channelId, int limit)
        => Task.FromResult(Messages.FindAll(m => m.ChannelId == channelId));
    public Task<User?> GetUserByIdAsync(int userId)
        => Task.FromResult(Users.TryGetValue(userId, out var u) ? u : null);
    public Task<Person?> GetPersonByIdAsync(int personId)
        => Task.FromResult(Persons.TryGetValue(personId, out var p) ? p : null);

    public Task<List<UserMessage>> GetContextByChannelAsync(int channelId, int limit)
        => Task.FromResult(Messages.FindAll(m => m.ChannelId == channelId).Take(limit).ToList());
    public Task<List<UserMessage>> GetLatestMessagesAfterIdAsync(int channelId, int afterId, int limit)
        => Task.FromResult(Messages.FindAll(m => m.ChannelId == channelId && m.Id > afterId).TakeLast(limit).ToList());
    public Task<UserMessage?> GetByPlatformMessageIdAsync(int channelId, string platformMessageId)
        => Task.FromResult(Messages.FirstOrDefault(m => m.ChannelId == channelId && m.PlatformMessageId == platformMessageId));
    public Task<List<UserMessage>> GetContextAroundAsync(int messageId, int channelId, int radius)
        => Task.FromResult(Messages.FindAll(m => m.ChannelId == channelId && Math.Abs(m.Id - messageId) <= radius));
    public string? GetBotPlatformId(string platform) => BotPlatformId;
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

public class FormatIncomingTests
{
    private static FakeDataSource MakeData(string? botId = "10001") => new() { BotPlatformId = botId };

    private static ChannelContextState MakeState(int channelId = 1) => new()
    {
        ChannelId = channelId,
        ChannelName = "group_12345",
        Mode = "express"
    };

    [Fact]
    public async Task FormatIncoming_WrapsInNewMessagesTag()
    {
        var data = MakeData();
        var svc = new ChannelContextService(data);
        var m = new UserMessage { Id = 1, ChannelId = 1, SenderName = "张三", Content = "你好" };
        var msg = await svc.FormatIncomingAsync(MakeState(), m);
        Assert.NotNull(msg);
        Assert.Contains("<new_messages>", msg!.Content);
        Assert.Contains("</new_messages>", msg.Content);
    }

    [Fact]
    public async Task FormatIncoming_MentionedByBot_HasMentionedAttr()
    {
        var data = MakeData(botId: "10001");
        var svc = new ChannelContextService(data);
        var m = new UserMessage
        {
            Id = 1, ChannelId = 1, SenderName = "张三",
            Content = "@bot 帮我", MentionedPlatformIds = "10001"
        };
        var msg = await svc.FormatIncomingAsync(MakeState(), m);
        Assert.NotNull(msg);
        Assert.Contains("mentioned=\"true\"", msg!.Content);
    }

    [Fact]
    public async Task FormatIncoming_NotMentioned_NoMentionedAttr()
    {
        var data = MakeData(botId: "10001");
        var svc = new ChannelContextService(data);
        var m = new UserMessage
        {
            Id = 1, ChannelId = 1, SenderName = "张三",
            Content = "普通消息", MentionedPlatformIds = null
        };
        var msg = await svc.FormatIncomingAsync(MakeState(), m);
        Assert.NotNull(msg);
        Assert.DoesNotContain("mentioned=\"true\"", msg!.Content);
    }

    [Fact]
    public async Task FormatIncoming_WithReply_HasReplyAttr()
    {
        var data = MakeData();
        var svc = new ChannelContextService(data);
        var m = new UserMessage
        {
            Id = 2, ChannelId = 1, SenderName = "李四",
            Content = "回复你", ReplyToPlatformMessageId = "msgABC"
        };
        var msg = await svc.FormatIncomingAsync(MakeState(), m);
        Assert.NotNull(msg);
        Assert.Contains("reply=\"msgABC\"", msg!.Content);
    }

    [Fact]
    public async Task FormatIncoming_SenderNameEscaped()
    {
        var data = MakeData();
        var svc = new ChannelContextService(data);
        var m = new UserMessage
        {
            Id = 3, ChannelId = 1, SenderName = "用户<test>",
            Content = "消息"
        };
        var msg = await svc.FormatIncomingAsync(MakeState(), m);
        Assert.NotNull(msg);
        // 名字应当被 XML 转义
        Assert.Contains("&lt;test&gt;", msg!.Content);
    }

    [Fact]
    public void IsBotMentionedInMessage_CorrectlyDetects()
    {
        var data = MakeData(botId: "10001");
        var svc = new ChannelContextService(data);

        var mentioned = new UserMessage { MentionedPlatformIds = "10001,99999" };
        var notMentioned = new UserMessage { MentionedPlatformIds = "99999" };
        var noMentions = new UserMessage { MentionedPlatformIds = null };

        Assert.True(svc.IsBotMentionedInMessage(mentioned));
        Assert.False(svc.IsBotMentionedInMessage(notMentioned));
        Assert.False(svc.IsBotMentionedInMessage(noMentions));
    }
}
