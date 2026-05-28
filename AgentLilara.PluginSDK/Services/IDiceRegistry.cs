using System.Collections.Generic;
using AgentLilara.PluginSDK.Dice;

namespace AgentLilara.PluginSDK.Services;

/// <summary>
/// 骰子面注册接口。插件通过 IComponentContext.GetService&lt;IDiceRegistry&gt;()
/// 获取此接口，往大骰子里塞面。
/// </summary>
public interface IDiceRegistry
{
    /// <summary>注册一个面。</summary>
    void Register(DiceFace face);

    /// <summary>移除一个面。</summary>
    void Unregister(string faceId);

    /// <summary>获取所有已注册的面（供骰子实现读取）。</summary>
    IReadOnlyList<DiceFace> GetAllFaces();
}
