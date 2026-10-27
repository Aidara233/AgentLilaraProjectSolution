using System.Reflection;
using AgentCoreProcessor.Adapter;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class HttpBridgeHostTests
{
    private sealed class Host : IAdapterHostAccess
    {
        public bool IsReady { get; set; } = true;
        public bool IsIdle => false;
        public bool HasBusyWorker() => true;
        public List<string> Calls { get; } = new();
        public Task<AdapterChannelView?> FindChannelByNameAsync(string name)
        { Calls.Add("find:" + name); return Task.FromResult<AdapterChannelView?>(new(42, name)); }
        public Task<List<AdapterChannelView>> GetAllChannelsAsync() => Task.FromResult(new List<AdapterChannelView> { new(42, "http_test") });
        private Task<List<AdapterMessageView>> Messages(string call)
        {
            Calls.Add(call);
            return Task.FromResult(new List<AdapterMessageView> {
                new(2, 7, "乙", "second", true, new DateTime(2026, 2, 3), "p2"),
                new(1, 6, "甲", "first", false, new DateTime(2026, 2, 3), "p1") });
        }
        public Task<List<AdapterMessageView>> GetContextByChannelAsync(int id, int limit) => Messages("recent:" + limit);
        public Task<List<AdapterMessageView>> GetMessagesAfterIdAsync(int id, int afterId, int limit) => Messages("after:" + afterId);
        public Task<List<AdapterMessageView>> GetMessagesBeforeIdAsync(int id, int beforeId, int limit) => Messages("before:" + beforeId);
        public Task<List<AdapterMessageView>> SearchMessagesByChannelAsync(int id, string keyword, int offset, int limit) => Messages("search:" + keyword + ":" + offset);
    }

    private static async Task<JObject> Request(HttpBridgeAdapter adapter, string handler, string query = "", string? payload = null)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload ?? ""));
        context.Request.Body = input;
        using var body = new MemoryStream();
        context.Response.Body = body;
        await (Task)typeof(HttpBridgeAdapter).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(adapter, new object[] { context })!;
        body.Position = 0;
        using var json = new Newtonsoft.Json.JsonTextReader(new StringReader(await new StreamReader(body).ReadToEndAsync()))
        {
            DateParseHandling = Newtonsoft.Json.DateParseHandling.None
        };
        return JObject.Load(json);
    }

    [Theory]
    [InlineData("?channel=http_test&keyword=hello&offset=3", "search:hello:3", 1)]
    [InlineData("?channel=http_test&beforeId=8", "before:8", 1)]
    [InlineData("?channel=http_test", "recent:50", 1)]
    [InlineData("?channel=http_test&afterId=8", "after:8", 2)]
    public async Task QueriesPreserveExistingOrderingAndInjectedTimeConversion(string query, string expectedCall, int firstId)
    {
        var adapter = new HttpBridgeAdapter("test", new(), toLocal: time => time.AddHours(3));
        var host = new Host();
        adapter.AttachHost(host);
        var result = await Request(adapter, "HandleMessagesAsync", query);
        Assert.True((bool)result["ok"]!);
        Assert.Equal(new[] { "find:http_test", expectedCall }, host.Calls);
        Assert.Equal(firstId, (int)result["messages"]![0]!["id"]!);
        Assert.Equal(new DateTime(2026, 2, 3, 3, 0, 0).ToString("O"), (string?)result["messages"]![0]!["time"]);
    }

    [Fact]
    public async Task HostReadinessChannelsAndStatusUsePort()
    {
        var adapter = new HttpBridgeAdapter("test", new());
        Assert.Equal("engine-not-ready", (string?)(await Request(adapter, "HandleChannelsAsync"))["error"]);
        var host = new Host { IsReady = false };
        adapter.AttachHost(host);
        Assert.Equal("engine-not-ready", (string?)(await Request(adapter, "HandleMessagesAsync"))["error"]);
        host.IsReady = true;
        Assert.Equal(42, (int)(await Request(adapter, "HandleChannelsAsync"))["channels"]![0]!["id"]!);
        var status = await Request(adapter, "HandleStatusAsync");
        Assert.False((bool)status["engine"]!["isIdle"]!);
        Assert.True((bool)status["engine"]!["busyWorker"]!);
    }

    [Fact]
    public async Task SendQueriesBaselineBeforeDeliveringAndAcceptsWithoutWaiting()
    {
        var adapter = new HttpBridgeAdapter("test", new());
        var host = new Host();
        adapter.AttachHost(host);
        IncomingMessage? received = null;
        adapter.OnMessageReceived += message =>
        {
            Assert.Equal(new[] { "find:http_test", "recent:1" }, host.Calls);
            host.Calls.Add("received");
            received = message;
        };
        var result = await Request(adapter, "HandleSendAsync", payload: """{"channel":"http_test","content":"hello","wait":false}""");
        Assert.True((bool)result["accepted"]!);
        Assert.Equal("hello", received!.Content);
        Assert.Equal(new[] { "find:http_test", "recent:1", "received" }, host.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendWithoutReadyHostIsAccepted(bool attach)
    {
        var adapter = new HttpBridgeAdapter("test", new());
        var host = new Host { IsReady = false };
        if (attach) adapter.AttachHost(host);
        var count = 0;
        adapter.OnMessageReceived += _ => count++;
        var result = await Request(adapter, "HandleSendAsync", payload: """{"content":"hello","wait":true}""");
        Assert.True((bool)result["accepted"]!);
        Assert.Equal(1, count);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task ZeroTimeoutReturnsBurstShapeWithoutPolling()
    {
        var adapter = new HttpBridgeAdapter("test", new());
        var host = new Host();
        adapter.AttachHost(host);
        var result = await Request(adapter, "HandleSendAsync", payload: """{"channel":"http_test","content":"hello","wait":true,"timeoutMs":0}""");
        Assert.True((bool)result["ok"]!);
        Assert.True((bool)result["timedOut"]!);
        Assert.False((bool)result["burstSeen"]!);
        Assert.Empty((JArray)result["replies"]!);
        Assert.NotNull(result["durationMs"]);
        Assert.Null(result["accepted"]);
        Assert.Equal(new[] { "find:http_test", "recent:1" }, host.Calls);
    }

    [Fact]
    public async Task EmptyContentIsRejectedBeforeQueryOrDelivery()
    {
        var adapter = new HttpBridgeAdapter("test", new());
        var host = new Host();
        adapter.AttachHost(host);
        var count = 0;
        adapter.OnMessageReceived += _ => count++;
        var result = await Request(adapter, "HandleSendAsync", payload: """{"content":"  ","wait":false}""");
        Assert.False((bool)result["ok"]!);
        Assert.Equal("content required", (string?)result["error"]);
        Assert.Equal(0, count);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task ReloadUsesInjectedConfigPathAndKeepsBoundPort()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"settings":{"port":4321,"defaultChannel":"changed"}}""");
            var config = new HttpBridgeConfig { Port = 1234 };
            var adapter = new HttpBridgeAdapter("test", config, path);
            Assert.True(await adapter.ReloadConfigAsync());
            Assert.Equal(1234, config.Port);
            Assert.Equal("changed", config.DefaultChannel);
        }
        finally { File.Delete(path); }
    }
}
