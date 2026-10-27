using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host;

/// <summary>按 SDK 端口发现会话服务并转发，不依赖具体会话引擎。</summary>
internal sealed class SessionAccessImpl(ISystemContext context) : ISessionAccess
{
    private ISessionAccess? Access => context.GetActiveEnginesSnapshot().OfType<ISessionAccess>().FirstOrDefault();

    public Task<List<SessionSummary>> ListSessionsAsync() =>
        Access?.ListSessionsAsync() ?? Task.FromResult(new List<SessionSummary>());

    public bool SendMessageAsync(string targetSessionId, string senderSessionId, string content) =>
        Access?.SendMessageAsync(targetSessionId, senderSessionId, content) ?? false;
}
