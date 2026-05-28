using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Dice;

/// <summary>骰子面的 Roll 回调。被选中时调用，此时才实际去拉随机数据。</summary>
public delegate Task<DiceResult> DiceRoller(CancellationToken ct);

/// <summary>
/// 骰子面。插件注册到大骰子的单位，包含面标签和摇结果的回调。
/// </summary>
public class DiceFace
{
    /// <summary>唯一标识。格式由插件自定，如 "memory:main"、"search:random"。</summary>
    public string Id { get; set; } = "";

    /// <summary>展示标签，注入上下文时显示。</summary>
    public string Label { get; set; } = "";

    /// <summary>分类："memory" / "message" / "search" / "external" / "ssh" 等。</summary>
    public string Category { get; set; } = "";

    /// <summary>选择权重，默认 1.0。昂贵的面设低点避免每次都抽。</summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>摇结果回调。只在被选中时调用。</summary>
    public DiceRoller Roll { get; set; } = null!;
}
