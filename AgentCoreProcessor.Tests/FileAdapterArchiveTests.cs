using System.Text.Json;
using AgentCoreProcessor.Adapter;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class FileAdapterArchiveTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JsonAttachmentsUseInjectedArchiveAndPreserveSuccessfulOrder(bool inject)
    {
        var root = Path.Combine(Path.GetTempPath(), "file-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var calls = new List<string>();
        var first = Path.Combine(root, "first.png");
        var broken = Path.Combine(root, "broken.png");
        var last = Path.Combine(root, "last.wav");
        foreach (var path in new[] { first, broken, last }) File.WriteAllText(path, "test");
        Task<(string LocalPath, string Hash)> Archive(string path)
        {
            calls.Add(path);
            if (path == broken) throw new IOException("test archive failure");
            return Task.FromResult((path + ".stored", path == first ? "first-hash" : "last-hash"));
        }
        var adapter = new FileAdapter("archive-test", root, 10, archive: inject ? Archive : null);
        var received = new TaskCompletionSource<IncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.OnMessageReceived += message => received.TrySetResult(message);
        using var stop = new CancellationTokenSource();
        var running = adapter.StartAsync(stop.Token);
        try
        {
            File.WriteAllText(Path.Combine(root, "input", "001.json"), JsonSerializer.Serialize(new
            {
                content = "hello", attachments = new[] {
                    new { type = "image", path = first }, new { type = "image", path = broken },
                    new { type = "image", path = Path.Combine(root, "missing.png") }, new { type = "audio", path = last } }
            }));
            var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("hello", message.Content);
            if (inject)
            {
                Assert.Equal(new[] { first, broken, last }, calls);
                Assert.NotNull(message.Attachments);
                Assert.Equal(new[] { "first-hash", "last-hash" }, message.Attachments.Select(a => a.Hash));
                Assert.Equal(new[] { AttachmentType.Image, AttachmentType.Audio }, message.Attachments.Select(a => a.Type));
                Assert.Equal(new[] { first + ".stored", last + ".stored" }, message.Attachments.Select(a => a.LocalPath));
            }
            else { Assert.Null(message.Attachments); Assert.Empty(calls); }
        }
        finally
        {
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) { }
            Directory.Delete(root, true);
        }
    }
}
