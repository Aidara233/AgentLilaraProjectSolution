using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool;

/// <summary>宿主统一装配工具查询、全局禁用与会话类型权限规则。</summary>
internal static class ToolExecutorFactory
{
    public static ToolExecutor Create(Func<string, ITool?>? resolver = null) => new(
        resolver ?? ToolRegistry.Get,
        ToolRegistry.IsDisabled,
        ToolRegistry.GetDisableReason,
        Engine.Session.SessionTypeProfiles.IsAllowed);
}
