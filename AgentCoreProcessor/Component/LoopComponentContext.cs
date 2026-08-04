// AgentCoreProcessor/Component/LoopComponentContext.cs
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

internal class LoopComponentContext : ILoopComponentContext
{
    private readonly string _sessionId;
    private readonly string _loopType;
    private readonly ModuleBus _moduleBus;
    private readonly IServiceProvider _services;
    private readonly IPluginStorage _storage;
    private readonly Action _wakeLoop;
    private readonly Action<string, bool> _setEnabled;
    private readonly Dictionary<object, IDisposable> _subscriptions = new();

    private bool _isEnabled;

    public LoopComponentContext(
        string sessionId,
        string loopType,
        ModuleBus moduleBus,
        IServiceProvider services,
        IPluginStorage storage,
        Action wakeLoop,
        Action<string, bool> setEnabled,
        bool initialEnabled)
    {
        _sessionId = sessionId;
        _loopType = loopType;
        _moduleBus = moduleBus;
        _services = services;
        _storage = storage;
        _wakeLoop = wakeLoop;
        _setEnabled = setEnabled;
        _isEnabled = initialEnabled;
    }

    public string SessionId => _sessionId;
    public string LoopType => _loopType;
    public bool IsEnabled => _isEnabled;
    public IPluginStorage Storage => _storage;

    public void Enable()
    {
        if (_isEnabled) return;
        _isEnabled = true;
        _setEnabled(_sessionId, true);
    }

    public void Disable()
    {
        if (!_isEnabled) return;
        _isEnabled = false;
        _setEnabled(_sessionId, false);
    }

    public void WakeLoop() => _wakeLoop();

    public void PublishLocal<TEvent>(TEvent e) where TEvent : class
        => _moduleBus.Publish(e);

    public void PublishGlobal<TEvent>(TEvent e) where TEvent : class
        => _moduleBus.Publish(e);

    public void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
    {
        var sub = _moduleBus.Subscribe<TEvent>(e => { _ = handler(e); });
        _subscriptions[handler] = sub;
    }

    public void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
    {
        if (_subscriptions.TryGetValue(handler, out var sub))
        {
            sub.Dispose();
            _subscriptions.Remove(handler);
        }
    }

    public T? GetService<T>() where T : class
        => _services.GetService(typeof(T)) as T;

    internal void SetEnabledDirect(bool enabled) => _isEnabled = enabled;
}
