using System.Threading.Channels;
using AgentCoreProcessor.Engine.Session;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class SessionPortTests
{
    [Fact]
    public async Task MailboxPeeksWithoutConsumingAndDrainsBeforeClosing()
    {
        var mailbox = new MailboxInputPort();
        Assert.Equal("mailbox", mailbox.Name);
        Assert.Equal(typeof(string), mailbox.DataType);
        Assert.Equal(100, mailbox.Weight);
        Assert.False(mailbox.TryPeek(out var empty));
        Assert.Null(empty);
        Assert.True(mailbox.TryWrite("first"));
        Assert.True(mailbox.TryWrite("second"));
        mailbox.Complete();
        Assert.False(mailbox.TryWrite("late"));
        Assert.True(mailbox.TryPeek(out var peek));
        Assert.Equal("first", peek);
        Assert.Equal("first", await mailbox.ReceiveAsync(default));
        Assert.Equal("second", await mailbox.ReceiveAsync(default));
        Assert.Null(await mailbox.ReceiveAsync(default));
        mailbox.Complete();
    }

    [Fact]
    public async Task MailboxCancellationAndCloseReleasePendingReceivers()
    {
        var mailbox = new MailboxInputPort();
        using var cancellation = new CancellationTokenSource();
        var pending = mailbox.ReceiveAsync(cancellation.Token);
        cancellation.Cancel();
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        var closing = mailbox.ReceiveAsync(default);
        mailbox.Complete();
        Assert.Null(await closing.WaitAsync(TimeSpan.FromSeconds(5)));
        var external = Channel.CreateUnbounded<string>();
        var injected = new MailboxInputPort(external);
        Assert.True(external.Writer.TryWrite("injected"));
        Assert.Equal("injected", await injected.ReceiveAsync(default));
        injected.Complete();
        Assert.False(external.Writer.TryWrite("closed"));
    }

    [Fact]
    public async Task TypedChannelPreservesOrderPeekAndCancellation()
    {
        var port = new ChannelInputPort<int>("numbers", 9);
        Assert.Equal("numbers", port.Name);
        Assert.Equal(typeof(int), port.DataType);
        Assert.Equal(9, port.Weight);
        port.Weight = 12;
        Assert.Equal(12, port.Weight);
        Assert.False(port.TryPeek(out _));
        port.Write(10);
        port.Write(20);
        Assert.True(port.TryPeek(out var peek));
        Assert.Equal(10, peek);
        Assert.Equal(10, await port.ReceiveAsync(default));
        Assert.Equal(20, await port.ReceiveAsync(default));
        using var cancellation = new CancellationTokenSource();
        var pending = port.ReceiveAsync(cancellation.Token);
        cancellation.Cancel();
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        port.Write(30);
        Assert.Equal(30, await port.ReceiveAsync(default));
    }

    [Fact]
    public async Task DelegateOutputFiltersTypesAndAwaitsHandlerCompletion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<string>();
        var port = new DelegateOutputPort<string>("text", async value =>
        {
            received.Add(value);
            await completion.Task;
        });
        Assert.Equal("text", port.Name);
        Assert.Equal(typeof(string), port.DataType);
        await port.SendAsync(42);
        Assert.Empty(received);
        var sending = port.SendAsync("value");
        Assert.Equal(new[] { "value" }, received);
        Assert.False(sending.IsCompleted);
        completion.SetResult();
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
        var failure = new DelegateOutputPort<string>("failure", _ => Task.FromException(new InvalidOperationException("handler")));
        Assert.Equal("handler", (await Assert.ThrowsAsync<InvalidOperationException>(() => failure.SendAsync("value"))).Message);
        var assembly = typeof(MailboxInputPort).Assembly;
        Assert.Equal("Lilara.Session", assembly.GetName().Name);
        Assert.Same(assembly, typeof(ChannelInputPort<int>).Assembly);
        Assert.Same(assembly, typeof(DelegateOutputPort<string>).Assembly);
    }
}
