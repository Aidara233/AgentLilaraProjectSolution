// AgentCoreProcessor/Engine/Session/RuntimeSession.Children.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Axes;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Engine.Messaging;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Util;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// <see cref="RuntimeSession"/> 的部件：children（会话侧父子链）（partial 文件，仅物理拆分，类型仍是一个）。
/// <para>⚠️ 拆文件是为了让一个部件一个文件可读、可对照 <c>session-decomposition.md</c> 的 20 部件表；
/// 真正的类型边界（各自独立类）要到 C 阶段拆 csproj 时才动。</para>
/// </summary>
internal partial class RuntimeSession
{
    private void CaptureChildrenSnapshot(SnapshotWriter writer)
    {
        writer.Set(s => s.ChildSessionIds, _childSessionIds.ToArray());
        writer.Set(s => s.EffectiveParentId, EffectiveParentId);
        writer.Set(s => s.ForwardedTo, ForwardedTo);
    }

    // ── 父子链辅助方法 ──

    internal void RemoveChildFromState(string childId)
    {
        lock (_childSessionIds) _childSessionIds.Remove(childId);
        _persister?.SaveStateAsync(BuildStateSnapshot()).GetAwaiter().GetResult();
    }

    internal void AddChild(string childId)
    {
        lock (_childSessionIds) _childSessionIds.Add(childId);
    }

}
