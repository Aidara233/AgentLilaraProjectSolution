// AgentLilara.PluginSDK/IGlobalComponentContext.cs
namespace AgentLilara.PluginSDK;

public interface IGlobalComponentContext
{
    bool IsEnabled { get; }
    void Enable();
    void Disable();

    void WakeLoop(string loopId);

    void PublishGlobal<TEvent>(TEvent e) where TEvent : class;
    void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
    void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;

    T? GetService<T>() where T : class;
    IPluginStorage Storage { get; }
}
