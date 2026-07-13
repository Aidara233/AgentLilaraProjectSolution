// AgentLilara.PluginSDK/IRuntimeSession.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK;

/// <summary>
/// 统一会话接口。行为差异由 CreateSessionSpec（组件列表 + 端口配置 + 超限参数）驱动。
/// </summary>
public interface IRuntimeSession
{
    // 标识
    string SessionId { get; }
    SessionStatus Status { get; }
    bool IsAlive { get; }

    // 组件与工具
    IReadOnlyList<string> ComponentIds { get; }
    IReadOnlyList<string> ActiveTools { get; }

    // 端口 I/O
    IReadOnlyDictionary<string, IInputPort> Inputs { get; }
    IReadOnlyDictionary<string, IOutputPort> Outputs { get; }

    // 生命周期
    string? ParentSessionId { get; }
    string? EffectiveParentId { get; }
    IReadOnlyList<string> ChildSessionIds { get; }
    Task RunAsync(CancellationToken ct);
    void RequestStop(string reason);
    DateTime? WaitUntil { get; }

    // 持久化
    Task PersistStateAsync();

    // 组件管理
    void EnableComponent(string componentId);
    void DisableComponent(string componentId);
}
