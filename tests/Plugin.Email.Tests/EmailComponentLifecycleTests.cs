using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using AgentLilara.PluginSDK.Sources;
using Plugin.Email;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// T2：邮件组件的启停接线。
/// <para>上一轮修过的 bug 就在这里——组件被禁用后信源仍在轮询，等于把用户关掉的开关当成没关。
/// 当时是手工发现的，没有回归测试。这组测试把它钉住。</para>
/// <para>⚠️ 不测 <c>OnEnabledAsync</c>：它会真的 <c>StartAsync</c> 去连 IMAP，
/// 而测试环境既没有账号也不该发网络请求。那条路径的语义由 SourcePollingLoopTests 覆盖。</para>
/// </summary>
public class EmailComponentLifecycleTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-email-component-" + Guid.NewGuid().ToString("N"));

    public EmailComponentLifecycleTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task DoesNotRegisterWhenAccountIsNotConfigured()
    {
        WriteConfig(username: "", eventSourceEnabled: true);

        var registrar = new FakeRegistrar();
        var component = new EmailComponent();

        await component.OnInitAsync(new FakeComponentContext(_root, registrar), InitReason.Fresh);

        Assert.Empty(registrar.Registered);
    }

    [Fact]
    public async Task DoesNotRegisterWhenEventSourceIsDisabled()
    {
        WriteConfig(username: "me@corp", eventSourceEnabled: false);

        var registrar = new FakeRegistrar();
        var component = new EmailComponent();

        await component.OnInitAsync(new FakeComponentContext(_root, registrar), InitReason.Fresh);

        Assert.Empty(registrar.Registered);
    }

    [Fact]
    public async Task RegistersSourceWithDeclaredFields()
    {
        WriteConfig(username: "me@corp", eventSourceEnabled: true);

        var registrar = new FakeRegistrar();
        var component = new EmailComponent();

        await component.OnInitAsync(new FakeComponentContext(_root, registrar), InitReason.Fresh);

        var source = Assert.Single(registrar.Registered);
        Assert.Equal("mail", source.Kind);
        Assert.Contains("from", source.Fields);
    }

    [Fact]
    public async Task DisablingUnregistersTheSource()
    {
        WriteConfig(username: "me@corp", eventSourceEnabled: true);

        var registrar = new FakeRegistrar();
        var component = new EmailComponent();

        await component.OnInitAsync(new FakeComponentContext(_root, registrar), InitReason.Fresh);
        var source = Assert.Single(registrar.Registered);

        await component.OnDisabledAsync();

        var unregistered = Assert.Single(registrar.Unregistered);
        Assert.Equal(source.Id, unregistered);
    }

    [Fact]
    public async Task ShutdownIsSafeEvenIfNeverStarted()
    {
        WriteConfig(username: "me@corp", eventSourceEnabled: true);

        var registrar = new FakeRegistrar();
        var component = new EmailComponent();

        await component.OnInitAsync(new FakeComponentContext(_root, registrar), InitReason.Fresh);

        // 未启动过就关停：StopAsync 必须无害（内部以 _cts == null 短路）
        await component.OnShutdownAsync(ShutdownReason.Destroy);

        Assert.Single(registrar.Registered);
    }

    [Fact]
    public async Task MissingRegistrarDoesNotThrow()
    {
        WriteConfig(username: "me@corp", eventSourceEnabled: true);

        // 宿主没提供登记面：不该抛，也不该静默地假装注册成功
        var component = new EmailComponent();
        await component.OnInitAsync(new FakeComponentContext(_root, registrar: null), InitReason.Fresh);

        await component.OnDisabledAsync();
    }

    private void WriteConfig(string username, bool eventSourceEnabled)
    {
        var json = $$"""
        {
          "Smtp": { "Host": "smtp.example", "Username": "{{username}}" },
          "Imap": { "Host": "imap.example", "Username": "{{username}}" },
          "EventSource": { "Enabled": {{(eventSourceEnabled ? "true" : "false")}}, "PollSeconds": 60, "Folder": "INBOX" }
        }
        """;

        File.WriteAllText(Path.Combine(_root, "Email.json"), json);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class FakeRegistrar : ISourceRegistrar
    {
        public List<IEventSource> Registered { get; } = new();

        public List<string> Unregistered { get; } = new();

        public bool Register(IEventSource source)
        {
            Registered.Add(source);
            return true;
        }

        public bool Unregister(string sourceId)
        {
            Unregistered.Add(sourceId);
            return true;
        }
    }

    private sealed class FakeComponentContext : IGlobalComponentContext
    {
        private readonly ISourceRegistrar? _registrar;

        public FakeComponentContext(string globalDirectory, ISourceRegistrar? registrar)
        {
            _registrar = registrar;
            Storage = new FakeStorage(globalDirectory);
        }

        public bool IsEnabled { get; private set; } = true;

        public IPluginStorage Storage { get; }

        public IWorkspaceAccess Workspace => null!;

        public IPluginDataAccess PluginData => null!;

        public void Enable() => IsEnabled = true;

        public void Disable() => IsEnabled = false;

        public void WakeLoop(string sessionId)
        {
        }

        public void PublishGlobal<TEvent>(TEvent e) where TEvent : class
        {
        }

        public void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
        {
        }

        public void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
        {
        }

        public T? GetService<T>() where T : class => _registrar as T;
    }

    private sealed class FakeStorage : IPluginStorage
    {
        public FakeStorage(string globalDirectory) => GlobalDirectory = globalDirectory;

        public string GlobalDirectory { get; }

        public string InstanceDirectory => GlobalDirectory;

        public string WorkspaceDirectory => GlobalDirectory;
    }
}
