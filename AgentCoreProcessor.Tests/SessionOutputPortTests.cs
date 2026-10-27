using System.Reflection;
using AgentCoreProcessor.Engine.Messaging;
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class SessionOutputPortTests
{
    private static RuntimeSession Session() => new(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
    private static Task Send(RuntimeSession session, string text) => (Task)typeof(RuntimeSession)
        .GetMethod("SendOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, new object[] { text })!;

    [Fact]
    public async Task HandlerReceivesFirstMessagePortsChannelAndOriginalTextAndIsAwaited()
    {
        var session = Session();
        session.AddInputPort("mailbox", new MailboxInputPort());
        session.AddInputPort("first", new MessageInputPort(42, "normal"));
        session.AddInputPort("second", new MessageInputPort(43, "mention"));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<(int, string)>();
        session.SetOutputHandler((channel, text) => { calls.Add((channel, text)); return completion.Task; });
        var sending = Send(session, " original text ");
        Assert.Equal(new[] { (42, " original text ") }, calls);
        Assert.False(sending.IsCompleted);
        completion.SetResult();
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MissingHandlerEmptyTextAndAbsentOrInvalidFirstChannelSkipSending()
    {
        var session = Session();
        await Send(session, "no handler");
        var calls = 0;
        session.SetOutputHandler((_, _) => { calls++; return Task.CompletedTask; });
        await Send(session, "no channel");
        session.AddInputPort("invalid", new MessageInputPort(0, "normal"));
        session.AddInputPort("valid", new MessageInputPort(42, "normal"));
        await Send(session, "invalid first channel");
        Assert.Equal(0, calls);
        var valid = Session();
        valid.AddInputPort("channel", new MessageInputPort(42, "normal"));
        valid.SetOutputHandler((_, _) => { calls++; return Task.CompletedTask; });
        await Send(valid, "");
        Assert.Equal(0, calls);
        await Send(valid, "valid");
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AsyncHandlerFailurePropagates()
    {
        var session = Session();
        session.AddInputPort("channel", new MessageInputPort(42, "normal"));
        session.SetOutputHandler((_, _) => Task.FromException(new InvalidOperationException("controlled")));
        Assert.Equal("controlled", (await Assert.ThrowsAsync<InvalidOperationException>(() => Send(session, "text"))).Message);
    }
}
