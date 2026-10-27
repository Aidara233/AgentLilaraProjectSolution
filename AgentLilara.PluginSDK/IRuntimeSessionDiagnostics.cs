namespace AgentLilara.PluginSDK;

/// <summary>可选的会话只读诊断能力，避免管理面依赖具体会话实现。</summary>
public interface IRuntimeSessionDiagnostics
{
    int RoundCount { get; }
}
