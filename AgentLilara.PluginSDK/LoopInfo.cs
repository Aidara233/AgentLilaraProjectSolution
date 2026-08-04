// AgentLilara.PluginSDK/LoopInfo.cs
namespace AgentLilara.PluginSDK;

/// <summary>会话上下文信息（供全局组件 BuildPromptSection）。LoopId 概念已移除，改为裸 sessionId。</summary>
public record LoopInfo(string SessionId, string LoopType);
