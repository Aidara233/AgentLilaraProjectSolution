using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ChannelRenderMappingTests
{
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void SnapshotPreservesFieldsWithoutNormalization(DateTimeKind kind)
    {
        var message = new UserMessage
        {
            Id = 17, UserId = 23, ChannelId = 42, Content = " 原文\r\n<& ", SenderName = " 甲 ",
            Time = new DateTime(2026, 2, 3, 4, 5, 6, kind), IsFromBot = true,
            PlatformMessageId = " id ", ReplyToPlatformMessageId = " reply ",
            ImageHashes = " a ,b,, ", MentionedPlatformIds = " bot,other "
        };
        var snapshot = ChannelRenderMapping.Snapshot(message);
        Assert.Equal(message.Id, snapshot.Id);
        Assert.Equal(message.UserId, snapshot.UserId);
        Assert.Equal(message.ChannelId, snapshot.ChannelId);
        Assert.Equal(message.Content, snapshot.Content);
        Assert.Equal(message.SenderName, snapshot.SenderName);
        Assert.Equal(message.Time.Ticks, snapshot.Time.Ticks);
        Assert.Equal(kind, snapshot.Time.Kind);
        Assert.Equal(message.IsFromBot, snapshot.IsFromBot);
        Assert.Equal(message.PlatformMessageId, snapshot.PlatformMessageId);
        Assert.Equal(message.ReplyToPlatformMessageId, snapshot.ReplyToPlatformMessageId);
        Assert.Equal(message.ImageHashes, snapshot.ImageHashes);
        Assert.Equal(message.MentionedPlatformIds, snapshot.MentionedPlatformIds);
    }

    [Fact]
    public void RenderedMappingPreservesNullEmptyAndOrderedParts()
    {
        Assert.Null(ChannelRenderMapping.ModelMessage(new()).ContentParts);
        Assert.Empty(ChannelRenderMapping.ModelMessage(new() { ContentParts = new() }).ContentParts!);
        var message = ChannelRenderMapping.ModelMessage(new()
        {
            Content = "全文\r\n", ContentParts = new()
            {
                ChannelRenderedPart.FromText("甲"), ChannelRenderedPart.FromImagePath(" image.png "),
                ChannelRenderedPart.FromText("")
            }
        });
        Assert.Equal("user", message.Role);
        Assert.Null(message.Name);
        Assert.Equal("全文\r\n", message.Content);
        var parts = Assert.IsType<List<AgentCoreProcessor.Models.ContentPart>>(message.ContentParts);
        Assert.Equal(new[] { "text", "image", "text" }, parts.Select(p => p.Type));
        Assert.Equal(" image.png ", parts[1].ImagePath);
        Assert.Equal("", parts[2].Text);
        Assert.All(parts, part => { Assert.Null(part.ImageBase64); Assert.Null(part.ToolName); });
    }

    [Fact]
    public void ChannelAssemblyHasNoHostDataOrLlmReference()
    {
        var names = typeof(ChannelRenderer).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("AgentCoreProcessor", names);
        Assert.DoesNotContain("Lilara.Data", names);
        Assert.DoesNotContain("Lilara.Llm", names);
    }
}
