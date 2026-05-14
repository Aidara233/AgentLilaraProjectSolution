namespace AgentLilara.PluginSDK.Logging;

public interface ISignalLogger
{
    IDisposable Open(string group, string name, object? detail = null);
    void Event(string group, string name, object? detail = null);
    void Debug(string group, string name, object? detail = null);
    void Warn(string group, string name, object? detail = null);
    void Error(string group, string name, object? detail = null);
}
