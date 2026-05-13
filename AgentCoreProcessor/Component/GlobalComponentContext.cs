// AgentCoreProcessor/Component/GlobalComponentContext.cs
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

internal class GlobalComponentContext : IGlobalComponentContext
{
    private readonly ComponentEventBus _eventBus;
    private readonly IServiceProvider _services;
    private readonly IPluginStorage _storage;
    private readonly Action<string> _wakeLoop;
    private readonly Action<bool> _setEnabled;

    private bool _isEnabled;

    public GlobalComponentContext(
        ComponentEventBus eventBus,
        IServiceProvider services,
        IPluginStorage storage,
        Action<string> wakeLoop,
        Action<bool> setEnabled,
        bool initialEnabled)
    {
        _eventBus = eventBus;
        _services = services;
        _storage = storage;
        _wakeLoop = wakeLoop;
        _setEnabled = setEnabled;
        _isEnabled = initialEnabled;
    }

    public bool IsEnabled => _isEnabled;
    public IPluginStorage Storage => _storage;

    public void Enable()
    {
        if (_isEnabled) return;
        _isEnabled = true;
        _setEnabled(true);
    }

    public void Disable()
    {
        if (!_isEnabled) return;
        _isEnabled = false;
        _setEnabled(false);
    }

    public void WakeLoop(string loopId) => _wakeLoop(loopId);

    public void PublishGlobal<TEvent>(TEvent e) where TEvent : class
        => _ = _eventBus.PublishGlobalAsync(e);

    public void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
        => _eventBus.SubscribeGlobal(handler);

    public void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
        => _eventBus.UnsubscribeGlobal(handler);

    public T? GetService<T>() where T : class
        => _services.GetService(typeof(T)) as T;

    internal void SetEnabledDirect(bool enabled) => _isEnabled = enabled;
}
