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
}
