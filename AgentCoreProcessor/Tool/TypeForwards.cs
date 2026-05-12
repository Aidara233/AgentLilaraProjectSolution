// 类型转发：让 using AgentCoreProcessor.Tool 的旧代码能找到已迁移到 PluginSDK 的类型。
// 过渡期使用，后续逐步把各文件的 using 改为直接引用 PluginSDK 命名空间后删除此文件。

global using ITool = AgentLilara.PluginSDK.ITool;
global using ToolCall = AgentLilara.PluginSDK.ToolCall;
global using ToolResult = AgentLilara.PluginSDK.ToolResult;
global using ToolParameter = AgentLilara.PluginSDK.ToolParameter;
global using ToolDefinition = AgentLilara.PluginSDK.ToolDefinition;
global using ToolMetaAttribute = AgentLilara.PluginSDK.ToolMetaAttribute;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// ITool 扩展方法：从 ToolMetaAttribute 读取旧接口的属性。
    /// 过渡期使用，让旧代码能编译。后续引擎集成时移除。
    /// </summary>
    internal static class ToolExtensions
    {
        public static bool GetContinueLoop(this AgentLilara.PluginSDK.ITool tool)
            => ToolRegistry.GetMeta(tool.Name)?.ContinueLoop ?? false;

        public static bool GetRetainResult(this AgentLilara.PluginSDK.ITool tool)
            => false;

        public static bool GetAllowSubAgent(this AgentLilara.PluginSDK.ITool tool)
            => ToolRegistry.GetMeta(tool.Name)?.AllowSubAgent ?? true;

        public static AgentLilara.PluginSDK.ToolPermission GetPermission(this AgentLilara.PluginSDK.ITool tool)
            => ToolRegistry.GetMeta(tool.Name)?.Permission ?? AgentLilara.PluginSDK.ToolPermission.Default;

        public static string? GetToolGroup(this AgentLilara.PluginSDK.ITool tool)
            => ToolRegistry.GetMeta(tool.Name)?.Group;

        public static string? GetCapabilitySummary(this AgentLilara.PluginSDK.ITool tool)
            => ToolRegistry.GetMeta(tool.Name)?.CapabilitySummary;
    }
}
