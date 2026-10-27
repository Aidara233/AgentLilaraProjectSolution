using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>宿主路径适配；三文件存储与恢复实现由 Lilara.Session 所有。</summary>
internal sealed class SessionPersistence : SessionFileStore
{
    public SessionPersistence(string sessionId)
        : base(Path.Combine(PathConfig.StoragePath, "Sessions"), sessionId) { }
}
