using AgentCoreProcessor.Engine.Messaging;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class MessageInputPortTests
{
    [Fact]
    public async Task ActivationCountResetDoesNotConsumeQueueAndReceiveDoesNotDecrementCount()
    {
        var port = new MessageInputPort(42, "normal", 5);
        Assert.Equal("msg:42:normal", port.Name);
        Assert.Equal(typeof(object), port.DataType);
        Assert.Equal(5, port.Weight);
        Assert.Equal(42, port.ChannelId);
        Assert.Equal("normal", port.SubStream);
        port.Enqueue("first");
        port.Enqueue("second");
        Assert.Equal(2, port.QueueCount);
        Assert.True(port.TryPeek(out var peek));
        Assert.Equal("first", peek);
        Assert.Equal("first", await port.ReceiveAsync(default));
        Assert.Equal(2, port.QueueCount);
        port.ResetQueueCount();
        Assert.Equal(0, port.QueueCount);
        Assert.Equal("second", await port.ReceiveAsync(default));
        Assert.False(port.TryPeek(out _));
        port.Enqueue("third");
        Assert.Equal(1, port.QueueCount);
        Assert.Equal("third", await port.ReceiveAsync(default));
        Assert.Equal("Lilara.Session", typeof(MessageInputPort).Assembly.GetName().Name);
    }

    [Fact]
    public async Task CancellationReleasesPendingReadWithoutLosingLaterMessages()
    {
        var port = new MessageInputPort(1, "mention", 100);
        using var cancellation = new CancellationTokenSource();
        var pending = port.ReceiveAsync(cancellation.Token);
        cancellation.Cancel();
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, port.QueueCount);
        port.Enqueue(42);
        Assert.Equal(42, await port.ReceiveAsync(default));
        Assert.Equal(1, port.QueueCount);
    }

    [Fact]
    public async Task ConcurrentEnqueuesKeepEveryMessageAndCount()
    {
        var port = new MessageInputPort(1, "normal");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            for (var index = 0; index < 100; index++) port.Enqueue(writer * 100 + index);
        })));
        Assert.Equal(800, port.QueueCount);
        port.ResetQueueCount();
        var received = new HashSet<int>();
        while (port.TryPeek(out _)) received.Add((int)(await port.ReceiveAsync(default))!);
        Assert.Equal(800, received.Count);
        Assert.Equal(0, port.QueueCount);
    }
}
