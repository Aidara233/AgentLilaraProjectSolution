using System.Globalization;
using System.Text;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using Newtonsoft.Json;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class SessionContextClockTests
{
    [Fact]
    public async Task FrozenClockPreservesCompleteRoundOrderStateAndBytes()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var now = new DateTime(2026, 2, 4, 12, 34, 56, DateTimeKind.Local);
            var reads = 0;
            SessionContextHost NewHost() => new(new[] { (42, (string?)"group_42", false) }, null,
                new ChannelContextService(new FakeDataSource()), () => { reads++; return now; });
            var first = NewHost();
            var second = NewHost();
            var input = new[]
            {
                (42, new UserMessage { Id = 1, SenderName = "甲", Content = "first", Time = new DateTime(2026, 2, 3, 23, 59, 0, DateTimeKind.Local) }, false),
                (42, new UserMessage { Id = 2, SenderName = "乙", Content = "second", Time = new DateTime(2026, 2, 4, 0, 1, 0, DateTimeKind.Local) }, true)
            };
            var a = await first.BuildRoundAsync(input);
            var b = await second.BuildRoundAsync(input);
            Assert.Equal(2, reads);
            Assert.Equal(3, a.Count);
            var offset = TimeZoneInfo.Local.GetUtcOffset(now);
            var anchor = $"UTC{(offset.TotalHours >= 0 ? "+" : "-")}{offset.Hours:00}:{offset.Minutes:00} ({TimeZoneInfo.Local.Id})";
            Assert.Equal("[系统] 当前时间: 2026-02-04 12:34:56 Wednesday " + anchor, a[0].Content);
            Assert.Equal("[新消息]\n[23:59]甲: first", a[1].Content);
            Assert.Equal("[新消息]\n2026-2-4\n⚠️ [!! @提及 !!] ⚠️\n[00:01]乙: second", a[2].Content);
            Assert.All(a, message => { Assert.Equal("user", message.Role); Assert.Null(message.ContentParts); });
            Assert.Equal(new DateTime(2026, 2, 4), first.StateFor(42).LastRenderedLocalDate);
            Assert.Equal(first.StateFor(42).LastRenderedLocalDate, second.StateFor(42).LastRenderedLocalDate);
            Assert.Equal(0, first.StateFor(42).LastConsumedMessageId);
            Assert.Equal(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(a)), Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(b)));
            var empty = await first.BuildRoundAsync(Array.Empty<(int, UserMessage, bool)>());
            Assert.Single(empty);
            Assert.Equal(a[0].Content, empty[0].Content);
            Assert.Equal(3, reads);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
