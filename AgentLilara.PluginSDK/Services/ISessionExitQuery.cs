namespace AgentLilara.PluginSDK.Services;

/// <summary>退出判定所需的只读会话事实，不执行停止或清理。</summary>
public interface ISessionExitQuery
{
    IReadOnlyCollection<string>? GetActiveChildren(string sessionId);
    bool AnyResourceBlocksExit(string sessionId);
}
