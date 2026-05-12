// 类型转发：让 using AgentCoreProcessor.Tool 的旧代码能找到已迁移到 Contract 的类型。
// 过渡期使用，后续逐步把各文件的 using 改为直接引用 Contract 命名空间后删除此文件。

global using ITool = AgentCoreProcessor.Tool.Contract.ITool;
global using ToolCall = AgentCoreProcessor.Tool.Contract.ToolCall;
global using ToolResult = AgentCoreProcessor.Tool.Contract.ToolResult;
global using ToolParameter = AgentCoreProcessor.Tool.Contract.ToolParameter;
global using ToolDefinition = AgentCoreProcessor.Tool.Contract.ToolDefinition;
global using ToolMetaAttribute = AgentCoreProcessor.Tool.Contract.ToolMetaAttribute;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// ITool 扩展方法：从 ToolMetaAttribute 读取旧接口的属性。
    /// 过渡期使用，让旧代码能编译。后续引擎集成时移除。
    /// </summary>
    internal static class ToolExtensions
    {
        public static bool GetContinueLoop(this Contract.ITool tool)
            => ToolRegistry.GetMeta(tool.Name)?.ContinueLoop ?? false;

        public static bool GetRetainResult(this Contract.ITool tool)
            => false; // RetainResult 概念已废弃，由 IToolHistoryAccess 替代

        public static bool GetAllowSubAgent(this Contract.ITool tool)
            => ToolRegistry.GetMeta(tool.Name)?.AllowSubAgent ?? true;

        public static Contract.ToolPermission GetPermission(this Contract.ITool tool)
            => ToolRegistry.GetMeta(tool.Name)?.Permission ?? Contract.ToolPermission.Default;

        public static string? GetToolGroup(this Contract.ITool tool)
            => ToolRegistry.GetMeta(tool.Name)?.Group;

        public static string? GetCapabilitySummary(this Contract.ITool tool)
            => ToolRegistry.GetMeta(tool.Name)?.CapabilitySummary;
    }
}
