namespace AgentLilara.PluginSDK.Dice;

/// <summary>
/// 骰子摇出的结果——落地的那一面。
/// </summary>
public class DiceResult
{
    /// <summary>元信息行：[memory | ch3 | 2026-01-15]</summary>
    public string Meta { get; set; } = "";

    /// <summary>主条目内容。</summary>
    public string Content { get; set; } = "";

    /// <summary>跟进提示（可为空），提醒模型可以深挖的方向，不写具体工具名。</summary>
    public string FollowUp { get; set; } = "";
}
