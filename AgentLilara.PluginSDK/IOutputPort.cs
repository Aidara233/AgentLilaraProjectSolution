// AgentLilara.PluginSDK/IOutputPort.cs
using System;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK;

/// <summary>
/// 输出端口。工具写端口，工具不知道自己连到哪。
/// </summary>
public interface IOutputPort
{
    string Name { get; }
    Type DataType { get; }
    Task SendAsync(object data);
}
