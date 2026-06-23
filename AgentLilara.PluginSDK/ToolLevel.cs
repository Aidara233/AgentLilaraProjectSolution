// AgentLilara.PluginSDK/ToolLevel.cs
namespace AgentLilara.PluginSDK;

/// <summary>
/// 工具安全等级。L0 默认全开，L1+ 需授权。目前几乎所有工具都是 L0。
/// </summary>
public enum ToolLevel
{
    L0,  // 默认全开：聊天、文件、搜索、网络、邮件、SSH、子agent
    L1,  // 需授权（预留）：修改 bot 自身配置
    L2,  // 需 Admin 二次确认（预留）
    L3   // 预留
}
