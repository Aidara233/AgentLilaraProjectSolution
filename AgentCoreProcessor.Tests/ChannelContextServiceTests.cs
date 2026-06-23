// AgentCoreProcessor.Tests/ChannelContextServiceTests.cs
using Xunit;
using AgentCoreProcessor.Engine.Context;

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
