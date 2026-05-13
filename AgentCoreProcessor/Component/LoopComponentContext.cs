// AgentCoreProcessor/Component/LoopComponentContext.cs
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

internal class LoopComponentContext : ILoopComponentContext
{
    private readonly string _loopId;
    private readonly string _loopType;
    private readonly ComponentEventBus _eventBus;
    private readonly IServiceProvider _services;
    private readonly IPluginStorage _storage;
    private readonly Action _wakeLoop;
    private readonly Action<string, bool> _setEnabled;

    private bool _isEnabled;

    public LoopComponentContext(
        string loopId,
        string loopType,
        ComponentEventBus eventBus,
        IServiceProvider services,
        IPluginStorage storage,
        Action wakeLoop,
        Action<string, bool> setEnabled,
        bool initialEnabled)
    {
        _loopId = loopId;
        _loopType = loopType;
        _eventBus = eventBus;
        _services = services;
        _storage = storage;
        _wakeLoop = wakeLoop;
        _setEnabled = setEnabled;
        _isEnabled = initialEnabled;
    }

    public string LoopId => _loopId;
    public string LoopType => _loopType;
    public bool IsEnabled => _isEnabled;
    public IPluginStorage Storage => _storage;

    public void Enable()
    {
        if (_isEnabled) return;
        _isEnabled = true;
        _setEnabled(_loopId, true);
    }

    public void Disable()
    {
        if (!_isEnabled) return;
        _isEnabled = false;
        _setEnabled(_loopId, false);
    }

    public void WakeLoop() => _wakeLoop();

    public void PublishLocal<TEvent>(TEvent e) where TEvent : class
        => _ = _eventBus.PublishLocalAsync(_loopId, e);

    public void PublishGlobal<TEvent>(TEvent e) where TEvent : class
        => _ = _eventBus.PublishGlobalAsync(e);

    public void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
        => _eventBus.SubscribeLocal<TEvent>(_loopId, handler);

    public void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
        => _eventBus.UnsubscribeLocal<TEvent>(_loopId, handler);

    public T? GetService<T>() where T : class
        => _services.GetService(typeof(T)) as T;

    internal void SetEnabledDirect(bool enabled) => _isEnabled = enabled;
}
