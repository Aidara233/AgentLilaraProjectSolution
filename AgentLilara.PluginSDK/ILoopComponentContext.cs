// AgentLilara.PluginSDK/ILoopComponentContext.cs
namespace AgentLilara.PluginSDK;

public interface ILoopComponentContext
{
    string LoopId { get; }
    string LoopType { get; }

    bool IsEnabled { get; }
    void Enable();
    void Disable();

    void WakeLoop();

    void PublishLocal<TEvent>(TEvent e) where TEvent : class;
    void PublishGlobal<TEvent>(TEvent e) where TEvent : class;
    void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
    void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;

    T? GetService<T>() where T : class;
    IPluginStorage Storage { get; }
}
