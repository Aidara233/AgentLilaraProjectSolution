using AgentCoreProcessor.Adapter;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class PlatformMediaTests
{
    private sealed class Media : IAdapterMediaStore
    {
        public List<string> Calls { get; } = new();
        public Task<(string LocalPath, string Hash)> DownloadAndSaveAsync(string url, HttpClient client)
        {
            Calls.Add("download:" + url);
            return Task.FromResult(("image.png", "image-hash"));
        }
        public Task SetCategoryAsync(string hash, string category)
        {
            Calls.Add("category:" + hash + ":" + category);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OneBotParserUsesMediaPortOrSkipsUnavailableImage(bool configured)
    {
        var media = new Media();
        var adapter = new OneBotAdapter("test", new OneBotConfig { FilterMode = "blacklist" }, configured ? media : null);
        try
        {
            var parser = new OneBotMessageParser(adapter);
            var message = await parser.HandleEventAsync(JObject.Parse("""
                {"post_type":"message","message_type":"group","group_id":42,"user_id":7,"message_id":1,
                 "sender":{"nickname":"sender"},"message":[{"type":"text","data":{"text":"hello"}},
                 {"type":"image","data":{"url":"https://example.invalid/a.png","sub_type":1}}]}
                """));
            Assert.NotNull(message);
            Assert.Equal(configured ? "hello[IMG:0]" : "hello", message.Content);
            if (configured)
            {
                var image = Assert.Single(message.Attachments!);
                Assert.Equal("sticker", image.Category);
                Assert.Equal("image-hash", image.Hash);
                Assert.Equal(0, image.SegmentIndex);
                Assert.Equal(new[] { "download:https://example.invalid/a.png", "category:image-hash:sticker" }, media.Calls);
            }
            else { Assert.Null(message.Attachments); Assert.Empty(media.Calls); }
        }
        finally { adapter.HttpClient.Dispose(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IcalinguaParserUsesMediaPortOrSkipsUnavailableImage(bool configured)
    {
        var media = new Media();
        var adapter = new IcalinguaAdapter("test", new IcalinguaConfig { FilterMode = "blacklist" }, configured ? media : null);
        try
        {
            var parser = new IcalinguaMessageParser(adapter);
            var message = await parser.HandleAddMessage(-42, JObject.Parse("""
                {"content":"hello","senderId":7,"senderName":"sender","files":[
                 {"type":"image/png","url":"https://example.invalid/a.png","isFace":true}]}
                """));
            Assert.NotNull(message);
            Assert.Equal(configured ? "hello[IMG:0]" : "hello", message.Content);
            if (configured)
            {
                var image = Assert.Single(message.Attachments!);
                Assert.Equal("sticker", image.Category);
                Assert.Equal("image-hash", image.Hash);
                Assert.Equal(new[] { "download:https://example.invalid/a.png", "category:image-hash:sticker" }, media.Calls);
            }
            else { Assert.True(message.Attachments is null || message.Attachments.Count == 0); Assert.Empty(media.Calls); }
        }
        finally { await adapter.StopAsync(); adapter.HttpClient.Dispose(); }
    }
}
