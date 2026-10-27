using System.Reflection;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.Engine.Mind;
using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class WaitJournalPortTests
{
    private sealed class Writer : IWriterAccess
    {
        public List<WriteBatchDto> Batches { get; } = new();
        public bool Throws;
        public Task<List<int>> WriteBatchAsync(WriteBatchDto batch)
        {
            if (Throws) throw new InvalidOperationException("test failure");
            Batches.Add(batch);
            return Task.FromResult(new List<int>());
        }
        public Task<int> WriteClaimAsync(ClaimDto claim) => throw new NotSupportedException();
        public Task<int> WriteJournalAsync(JournalDto journal) => throw new NotSupportedException();
    }
    private static RuntimeSession Session() => new(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
    private static Task Write(RuntimeSession session, string raw) => (Task)typeof(RuntimeSession)
        .GetMethod("WriteJournalFromWaitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, new object[] { raw })!;

    [Fact]
    public async Task PreservesEmotionPrefixAndLocalOccurrenceTimeWithoutSessionEngine()
    {
        var writer = new Writer();
        var session = Session();
        session.SetWriterAccess(() => writer);
        var before = DateTime.Now;
        await Write(session, " [情绪:joy:0.4][情绪:excitement:0.3] 正文 ");
        var after = DateTime.Now;
        var batch = Assert.Single(writer.Batches);
        Assert.Empty(batch.Claims);
        var journal = Assert.Single(batch.Journals);
        Assert.Equal("正文", journal.Content);
        Assert.Equal("Experience", journal.Kind);
        Assert.Equal("Session", journal.Source);
        Assert.Null(journal.ChannelId);
        Assert.Null(journal.SessionId);
        Assert.NotNull(journal.OccurredAt);
        Assert.InRange(journal.OccurredAt.Value, before, after);
        Assert.Equal(DateTimeKind.Local, journal.OccurredAt.Value.Kind);
        var signals = JArray.Parse(journal.EmotionSignalsJson!);
        Assert.Equal(new[] { "joy", "excitement" }, signals.Select(s => (string?)s["Axis"]));
        Assert.Equal(0.4, (double)signals[0]["Target"]!, 6);
        Assert.All(signals, signal => Assert.Equal("wait journal", (string?)signal["Reason"]));
    }

    [Theory]
    [InlineData(" plain text ", "plain text")]
    [InlineData("[情绪:joy:invalid] body", "body")]
    [InlineData("[情绪:joy:0.4", "[情绪:joy:0.4")]
    public async Task ExistingPlainInvalidAndUnclosedPrefixSemanticsRemain(string raw, string content)
    {
        var writer = new Writer();
        var session = Session();
        session.SetWriterAccess(() => writer);
        await Write(session, raw);
        var journal = Assert.Single(Assert.Single(writer.Batches).Journals);
        Assert.Equal(content, journal.Content);
        Assert.Null(journal.EmotionSignalsJson);
    }

    [Fact]
    public async Task ProviderIsResolvedDynamicallyAndFailuresDoNotEscape()
    {
        var session = Session();
        await Write(session, "no provider");
        IWriterAccess? current = null;
        session.SetWriterAccess(() => current);
        await Write(session, "not ready");
        var first = new Writer();
        current = first;
        await Write(session, "first");
        var second = new Writer();
        current = second;
        await Write(session, "second");
        Assert.Equal("first", Assert.Single(Assert.Single(first.Batches).Journals).Content);
        Assert.Equal("second", Assert.Single(Assert.Single(second.Batches).Journals).Content);
        second.Throws = true;
        await Write(session, "writer failure");
        session.SetWriterAccess(() => throw new InvalidOperationException("provider failure"));
        await Write(session, "provider failure");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SdkMappingPreservesOptionalOccurrenceTime(bool supplied)
    {
        var time = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var dto = new JournalDto { Content = "test", OccurredAt = supplied ? time : null };
        var map = typeof(WriterAccessImpl).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "ToInternal" && m.GetParameters()[0].ParameterType == typeof(JournalDto));
        var request = Assert.IsType<WriteJournalRequest>(map.Invoke(null, new object[] { dto }));
        Assert.Equal(dto.OccurredAt, request.OccurredAt);
        if (supplied) Assert.Equal(DateTimeKind.Utc, request.OccurredAt!.Value.Kind);
    }
}
