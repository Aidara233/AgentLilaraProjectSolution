using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ChannelMediaTests
{
    private sealed class Media : IChannelMediaSource
    {
        public readonly List<string> Calls = new();
        public readonly Dictionary<string, string?> Paths = new();
        public readonly Dictionary<string, string?> Descriptions = new();
        public readonly Dictionary<string, ChannelOcrSnapshot?> Ocr = new();
        public int Threshold = 3;
        public Task<string?> GetModelInputPathAsync(string hash)
        { Calls.Add("path:" + hash); return Task.FromResult(Paths.GetValueOrDefault(hash)); }
        public Task<string?> GetDescriptionAsync(string hash)
        { Calls.Add("description:" + hash); return Task.FromResult(Descriptions.GetValueOrDefault(hash)); }
        public Task<ChannelOcrSnapshot?> GetOcrAsync(string hash)
        { Calls.Add("ocr:" + hash); return Task.FromResult(Ocr.GetValueOrDefault(hash)); }
        public int GetOcrRichTextThreshold() { Calls.Add("threshold"); return Threshold; }
    }

    [Fact]
    public async Task InterleavingPreservesMissingPathsDuplicatesAndQueryOrder()
    {
        var media = new Media();
        media.Paths["a"] = "a.png";
        media.Descriptions["a"] = "description";
        media.Ocr["a"] = new(true, "long text");
        var state = new ChannelContextState();
        var parts = await new ChannelContextService(new FakeDataSource(), media).BuildInterleavedContentParts(
            "A[IMG:0]B[IMG:1]C[IMG:2]D[IMG:9]E",
            new[] { new UserMessage { ImageHashes = " a ,missing,a" } }, new(), state);
        Assert.Equal(new[] { "text", "text", "image", "text", "text", "text", "text", "text", "text", "text" }, parts.Select(p => p.Type));
        Assert.Equal(new[] { "A", "[IMG:0]", null, "[图片描述] description", "[图中文字] long text", "B", "C", "[IMG:2]", "D", "E" }, parts.Select(p => p.Text));
        Assert.Equal("a.png", parts[2].ImagePath);
        Assert.Equal(new[] { "path:a", "path:missing", "path:a", "description:a", "ocr:a", "threshold" }, media.Calls);
        Assert.True(state.RoundImageHashes.SetEquals(new[] { "a" }));
        Assert.True(state.InjectedDescriptions.SetEquals(new[] { "a" }));
        Assert.True(state.InjectedOcrTexts.SetEquals(new[] { "a" }));
    }

    [Fact]
    public async Task OnlyInvalidMarkerFallsBackToOriginalText()
    {
        var service = new ChannelContextService(new FakeDataSource(), new Media());
        var parts = await service.BuildInterleavedContentParts("[IMG:99]", Array.Empty<UserMessage>(), new(), new());
        Assert.Equal("[IMG:99]", Assert.Single(parts).Text);
    }

    [Theory]
    [InlineData(null, "text", false)]
    [InlineData(false, "text", false)]
    [InlineData(true, "", false)]
    [InlineData(true, "abc", true)]
    [InlineData(true, "abcdef", true)]
    public async Task OcrThresholdIsReadOnlyForReadyTextAndExpressKeepsFullText(bool? hasText, string text, bool ready)
    {
        var media = new Media();
        media.Ocr["a"] = new(hasText, text);
        var parts = await new ChannelContextService(new FakeDataSource(), media)
            .BuildContentPartsWithImagePaths("body", new[] { "a.png" }, new() { "a" });
        Assert.Equal(ready ? 3 : 2, parts.Count);
        Assert.Equal(ready, media.Calls.Contains("threshold"));
        if (ready) Assert.Equal("[图中文字] " + text, parts[2].Text);
    }

    [Fact]
    public async Task LaggingInjectionKeepsDescriptionsBeforeOcrAndRetriesUnreadyValues()
    {
        var media = new Media();
        var state = new ChannelContextState();
        state.RoundImageHashes.UnionWith(new[] { "a", "b" });
        var service = new ChannelContextService(new FakeDataSource(), media);
        Assert.Empty(await service.BuildLaggingImageInjectionsAsync(state));
        Assert.Equal(new[] { "description:a", "description:b", "ocr:a", "ocr:b" }, media.Calls);
        media.Calls.Clear();
        media.Descriptions["a"] = "A";
        media.Descriptions["b"] = "B";
        media.Ocr["a"] = new(true, "text A");
        media.Ocr["b"] = new(true, "text B");
        var messages = await service.BuildLaggingImageInjectionsAsync(state);
        Assert.Equal(new[] { "[图片描述] 之前图片的描述已就绪：A", "[图片描述] 之前图片的描述已就绪：B", "[图中文字] text A", "[图中文字] text B" }, messages.Select(m => m.Content));
        Assert.Equal(new[] { "description:a", "description:b", "ocr:a", "threshold", "ocr:b", "threshold" }, media.Calls);
        media.Calls.Clear();
        Assert.Empty(await service.BuildLaggingImageInjectionsAsync(state));
        Assert.Empty(media.Calls);
    }
}
