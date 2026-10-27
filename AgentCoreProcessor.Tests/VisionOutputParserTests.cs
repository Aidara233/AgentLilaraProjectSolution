using AgentCoreProcessor.Engine.Vision;
using Newtonsoft.Json;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class VisionOutputParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public void EmptyContentKeepsPlaceholder(string source) =>
        Assert.Equal(("unknown", "[视觉模型返回空内容]"), VisionOutputParser.Parse(source));

    [Theory]
    [InlineData("info-screenshot")]
    [InlineData("info-photo")]
    [InlineData("share-photo")]
    [InlineData("share-art")]
    [InlineData("meme")]
    [InlineData("sticker")]
    [InlineData("unknown")]
    public void KnownClassificationsAndDescriptionAreTrimmed(string classification)
    {
        var source = JsonConvert.SerializeObject(new { classification = " " + classification + " ", description = " 描述 " });
        Assert.Equal((classification, "描述"), VisionOutputParser.Parse(source));
    }

    [Theory]
    [InlineData("MEME")]
    [InlineData("other")]
    [InlineData("")]
    public void UnknownOrDifferentCaseClassificationKeepsUnknown(string classification)
    {
        var source = JsonConvert.SerializeObject(new { classification, description = "描述" });
        Assert.Equal(("unknown", "描述"), VisionOutputParser.Parse(source));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"description\":\"\"}")]
    [InlineData("{\"classification\":null}")]
    public void EmptyObjectIsAcceptedAfterClassificationNormalization(string source) =>
        Assert.Equal(("unknown", ""), VisionOutputParser.Parse(source));

    [Theory]
    [InlineData("```json\n{\"classification\":\"meme\",\"description\":\"图\"}\n```")]
    [InlineData("```\n{\"classification\":\"meme\",\"description\":\"图\"}\n```")]
    [InlineData("说明\n```json\n{\"classification\":\"meme\",\"description\":\"图\"}\n```\n结束")]
    public void FencedJsonKeepsExistingExtraction(string source) =>
        Assert.Equal(("meme", "图"), VisionOutputParser.Parse(source));

    [Theory]
    [InlineData("  普通描述  ", "普通描述")]
    [InlineData(" {broken ", "{broken")]
    [InlineData("[]", "[]")]
    [InlineData("```JSON\n{\"classification\":\"meme\"}\n```", "```JSON\n{\"classification\":\"meme\"}\n```")]
    public void InvalidJsonFallsBackToTrimmedWholeText(string source, string expected) =>
        Assert.Equal(("unknown", expected), VisionOutputParser.Parse(source));
}
