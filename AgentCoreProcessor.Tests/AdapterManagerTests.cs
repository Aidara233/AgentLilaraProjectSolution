using AgentCoreProcessor.Adapter;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class AdapterManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "adapter-manager-" + Guid.NewGuid().ToString("N"));

    private sealed class Host : IAdapterHostAccess
    {
        public bool IsReady => true;
        public bool IsIdle => true;
        public bool HasBusyWorker() => false;
        public Task<AdapterChannelView?> FindChannelByNameAsync(string name) => Task.FromResult<AdapterChannelView?>(null);
        public Task<List<AdapterChannelView>> GetAllChannelsAsync() => Task.FromResult(new List<AdapterChannelView>());
        public Task<List<AdapterMessageView>> GetContextByChannelAsync(int id, int limit) => Task.FromResult(new List<AdapterMessageView>());
        public Task<List<AdapterMessageView>> GetMessagesAfterIdAsync(int id, int afterId, int limit) => GetContextByChannelAsync(id, limit);
        public Task<List<AdapterMessageView>> GetMessagesBeforeIdAsync(int id, int beforeId, int limit) => GetContextByChannelAsync(id, limit);
        public Task<List<AdapterMessageView>> SearchMessagesByChannelAsync(int id, string keyword, int offset, int limit) => GetContextByChannelAsync(id, limit);
    }

    private sealed class Adapter(string id) : IAdapter, IHostAwareAdapter, IChannelAwareAdapter
    {
        public string Id => id;
        public string Platform => "test";
        public event Action<IncomingMessage>? OnMessageReceived;
        public IAdapterHostAccess? Host { get; private set; }
        public int Stops { get; private set; }
        public void AttachHost(IAdapterHostAccess host) => Host = host;
        public bool OwnsChannel(string channel) => channel == id;
        public Task<string?> SendMessageAsync(OutgoingMessage message) => Task.FromResult<string?>(id);
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() { Stops++; return Task.CompletedTask; }
        public AdapterStatus GetStatus() => new() { Id = id, Platform = Platform };
        public void Receive(IncomingMessage message) => OnMessageReceived?.Invoke(message);
    }

    [Fact]
    public void HostIsInjectedIntoExistingAndFutureAdaptersAndCanBeReplaced()
    {
        var manager = new AdapterManager(_root, c => new Adapter(c.Id));
        var first = new Adapter("first");
        manager.RegisterAdapter(first);
        var host = new Host();
        manager.AttachHost(host);
        var second = new Adapter("second");
        manager.RegisterAdapter(second);
        Assert.Same(host, first.Host);
        Assert.Same(host, second.Host);
        var replacement = new Host();
        manager.AttachHost(replacement);
        Assert.Same(replacement, first.Host);
        Assert.Same(replacement, second.Host);
    }

    [Fact]
    public async Task LoadsThroughInjectedFactoryAndKeepsEnableRoutingAndPersistence()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "first.json"), """{"id":"first","type":"fake","enabled":true}""");
        File.WriteAllText(Path.Combine(_root, "second.json"), """{"id":"second","type":"fake","enabled":false}""");
        var created = new Dictionary<string, Adapter>();
        var manager = new AdapterManager(_root, c => created[c.Id] = new Adapter(c.Id));
        manager.LoadFromConfig();
        Assert.Equal(2, created.Count);
        Assert.True(manager.IsEnabled("first"));
        Assert.False(manager.IsEnabled("second"));
        Assert.Equal("first", await manager.SendMessageAsync("test", new() { ChannelId = "first", Content = "hello" }));
        await manager.EnableAsync("second");
        Assert.Equal("second", await manager.SendMessageAsync("test", new() { ChannelId = "second", Content = "hello" }));
        await manager.DisableAsync("first");
        Assert.Equal(1, created["first"].Stops);
        Assert.False((bool)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(_root, "first.json")))["Enabled"]!);
        await manager.DisableAsync("second");
        Assert.Null(await manager.SendMessageAsync("test", new() { ChannelId = "second", Content = "hello" }));
        Assert.True(await manager.RemoveAsync("first"));
        Assert.False(File.Exists(Path.Combine(_root, "first.json")));
    }

    [Fact]
    public void IncomingCallbackRetainsRegisteredAdapterIdentity()
    {
        var manager = new AdapterManager(_root, c => new Adapter(c.Id));
        var adapter = new Adapter("first");
        manager.RegisterAdapter(adapter);
        IncomingMessage? received = null;
        manager.OnIncomingMessage = message => { received = message; return Task.CompletedTask; };
        adapter.Receive(new() { Platform = "test", PlatformUserId = "user", ChannelId = "first", Content = "hello" });
        Assert.Equal("first", received!.AdapterId);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
