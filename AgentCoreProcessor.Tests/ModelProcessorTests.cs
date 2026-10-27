using AgentCoreProcessor.Client;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Models;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ModelProcessorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lilara-model-" + Guid.NewGuid().ToString("N"));
    public ModelProcessorTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private void Config(string name, string json) => File.WriteAllText(Path.Combine(_directory, name + ".json"), json);

    [Fact]
    public void CacheRetainsClientHistoryAndReloadsPromptsInDeclarationOrder()
    {
        Config("First", """{"model":"first","promptFiles":["a","missing","b"]}""");
        Config("Second", """{"model":"second"}""");
        var created = new List<FakeClient>();
        var loaded = new List<string>();
        var processor = new ModelProcessor(_directory, file =>
        {
            loaded.Add(file);
            return file == "missing" ? null : file.ToUpperInvariant();
        }, createClient: cfg => { var client = new FakeClient { Config = cfg }; created.Add(client); return client; },
            defaultClient: new FakeClient(), configurationName: "First");
        var first = processor.Client;
        Assert.Equal("A\n\nB", processor.BasePrompt);
        first.AddUserMessage("retained");
        processor.CfgName = "Second";
        Assert.Equal("A\n\nB", processor.BasePrompt); // Existing no-prompt behavior remains unchanged.
        processor.CfgName = "First";
        Assert.Same(first, processor.Client);
        Assert.Equal(2, created.Count);
        Assert.Contains(first.GetConversationHistory(), message => message.Content == "retained");
        Assert.Equal(new[] { "a", "missing", "b", "a", "missing", "b" }, loaded);
        Assert.Equal(2, first.GetConversationHistory().Count(message => message.Role == "system"));
        Config("First", """{"model":"changed"}""");
        processor.CfgName = "First";
        Assert.Equal("first", processor.Client.Config.Model);
        Assert.Equal("Lilara.Llm", typeof(ModelProcessor).Assembly.GetName().Name);
    }

    [Fact]
    public void MissingConfigurationKeepsCurrentClientAndReportsBaseName()
    {
        var fallback = new FakeClient();
        var warnings = new List<string>();
        var processor = new ModelProcessor(_directory, _ => null,
            warn: (message, _) => warnings.Add(message), defaultClient: fallback, configurationName: "missing");
        Assert.Same(fallback, processor.Client);
        Assert.Equal("Base", processor.CfgName);
        Config("Valid", "{}");
        var valid = new FakeClient();
        var loaded = new ModelProcessor(_directory, _ => null, createClient: _ => valid,
            defaultClient: fallback, configurationName: "Valid", warn: (message, _) => warnings.Add(message));
        loaded.CfgName = "missing";
        Assert.Same(valid, loaded.Client);
        Assert.Equal("Base", loaded.CfgName);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public async Task RetryResetsPartialAccumulationExactlyOnceAndPreservesCallbackAndToken()
    {
        var client = new FakeClient();
        var response = new ApiResponse();
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();
        client.Stream = (callback, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            callback(response);
            return client.Attempts == 1
                ? Task.FromException<string>(new InvalidOperationException("first")) : Task.FromResult("done");
        };
        var processor = new ModelProcessor(_directory, _ => null, defaultClient: client);
        await processor.ProcessAsync(value => { Assert.Same(response, value); events.Add("delta"); },
            cancellation.Token, () => events.Add("reset"));
        Assert.Equal(new[] { "delta", "reset", "delta" }, events);
        Assert.Equal(2, client.Attempts);
    }

    [Fact]
    public async Task SecondFailurePropagatesAndResetFailurePreventsSecondAttempt()
    {
        var client = new FakeClient();
        var final = new InvalidOperationException("second");
        client.Stream = (_, _) => Task.FromException<string>(client.Attempts == 1 ? new Exception("first") : final);
        var processor = new ModelProcessor(_directory, _ => null, defaultClient: client);
        var resets = 0;
        Assert.Same(final, await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(_ => { }, onRetryReset: () => resets++)));
        Assert.Equal(1, resets);
        Assert.Equal(2, client.Attempts);
        client.Attempts = 0;
        var resetFailure = new ArgumentException("reset");
        Assert.Same(resetFailure, await Assert.ThrowsAsync<ArgumentException>(() => processor.ProcessAsync(_ => { }, onRetryReset: () => throw resetFailure)));
        Assert.Equal(1, client.Attempts);
    }

    [Fact]
    public async Task SuccessfulStreamDoesNotResetOrRetry()
    {
        var client = new FakeClient();
        var processor = new ModelProcessor(_directory, _ => null, defaultClient: client);
        await processor.ProcessAsync(_ => { }, onRetryReset: () => throw new Exception("unexpected reset"));
        Assert.Equal(1, client.Attempts);
    }

    private sealed class FakeClient : IModelClient
    {
        public ApiClientCfg Config { get; set; } = new();
        private List<Message> _history = new();
        private List<ToolDefinition>? _tools;
        public int Attempts;
        public Func<Action<ApiResponse>, CancellationToken, Task<string>> Stream = (_, _) => Task.FromResult("");
        public Task<string> StreamChatAsync(Action<ApiResponse> onDelta, CancellationToken ct = default)
        { Attempts++; return Stream(onDelta, ct); }
        public IModelClient AddMessage(string role, string content, string? name = null)
        { _history.Add(new Message { Role = role, Content = content, Name = name }); return this; }
        public IModelClient AddUserMessage(string content, string? name = null) => AddMessage("user", content, name);
        public IModelClient AddAssistantMessage(string content, string? name = null) => AddMessage("assistant", content, name);
        public IModelClient AddSystemMessage(string content, string? name = null) => AddMessage("system", content, name);
        public IModelClient AddMultimodalMessage(string role, string text, List<string> imagePaths) => AddMessage(role, text);
        public IModelClient ClearConversationHistory() { _history.Clear(); return this; }
        public List<Message> GetConversationHistory() => _history;
        public IModelClient SetConversationHistory(List<Message> history) { _history = history; return this; }
        public IModelClient RemoveLastMessage() { _history.RemoveAt(_history.Count - 1); return this; }
        public int GetHistoryCount() => _history.Count;
        public IModelClient SetTools(List<ToolDefinition> tools) { _tools = tools; return this; }
        public List<ToolDefinition>? GetTools() => _tools;
        public Task StreamChatWithToolsAsync(Action<StreamEvent> onEvent, CancellationToken ct = default) => throw new NotSupportedException();
        public void AddToolResult(string toolUseId, string result, bool isError = false, List<string>? imagePaths = null) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
