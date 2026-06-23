// AgentLilara.PluginSDK/IInputPort.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK;

/// <summary>
/// 输入端口。注入到 agent 上下文，每轮 drain 新数据。
/// Weight 决定单条消息的激活权重：100=立即激活，5=攒多了激活。
/// </summary>
public interface IInputPort
{
    string Name { get; }
    Type DataType { get; }
    int Weight { get; }
    Task<object?> ReceiveAsync(CancellationToken ct);
    bool TryPeek(out object? data);
}
