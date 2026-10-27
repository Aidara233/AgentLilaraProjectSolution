// AgentCoreProcessor/Engine/Session/RuntimeSession.Output.cs
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

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// <see cref="RuntimeSession"/> 的部件：Output（partial 文件，仅物理拆分，类型仍是一个）。
/// <para>⚠️ 拆文件的目的是让一个部件一个文件可读、可对照 <c>session-decomposition.md</c> 的 20 部件表；
/// 真正的类型边界（各自独立类）要到 C 阶段拆 csproj 时才动。</para>
/// </summary>
internal partial class RuntimeSession
{
    private Task SendOutput(string text)
    {
        if (_sendOutput == null || string.IsNullOrEmpty(text))
            return Task.CompletedTask;
        int channelId = 0;
        foreach (var (_, port) in _inputs)
        {
            if (port is MessageInputPort mp) { channelId = mp.ChannelId; break; }
        }
        if (channelId <= 0)
            return Task.CompletedTask;

        return _sendOutput(channelId, text);
    }
}
