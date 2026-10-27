// AgentCoreProcessor/Engine/Session/RuntimeSession.Components.cs
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
/// <see cref="RuntimeSession"/> 的部件：components（组件宿主：加载、工具表刷新、启停）。
/// <para>⚠️ partial 文件，仅物理拆分，类型仍是一个；真正的类型边界到 C 阶段拆 csproj 时才动。</para>
/// </summary>
internal partial class RuntimeSession
{
    /// <summary>
    /// 从 ComponentRegistry 加载指定组件，构建 ComponentHost。
    /// sessionId 为裸 GUID；loopType（权限组）用于组件白名单查找。
    /// </summary>
    public void InitComponents(string sessionId, ModuleBus moduleBus, IServiceProvider services, Action wakeLoop)
    {
        // 会话类型名即权限作用域（无 PermissionGroup 间接层）：组件装载 + 工具白黑名单都按 Type 走
        _componentLoopType = Type;
        _services = services;
        // 注入 per-session 连接状态：发送工具据此决定目标频道并校验发言权限
        var perLoopServices = new Dictionary<Type, object>
        {
            [typeof(IConnectedChannel)] = _connected
        };
        // 注入 spec 声明的 per-session 服务（如 review 会话的 IReviewStageAccess）
        if (_spec.SessionServices != null)
        {
            foreach (var (svcType, instance) in _spec.SessionServices)
                perLoopServices[svcType] = instance;
        }
        _componentHost = new ComponentHost(sessionId, _componentLoopType, moduleBus, services, wakeLoop, perLoopServices);
        _componentHost.InitAsync(_componentIds).GetAwaiter().GetResult();

        RefreshToolList();
    }

    /// <summary>
    /// session 工具表合并 Loop 组件工具 + ToolRegistry 全部注册工具（全局/独立工具）。
    /// 工具是否对当前会话可用由权限组（Storage/Session/{group}.json 黑白名单）在
    /// AgentCore 装配期 + ToolExecutor 执行期双重裁决，这里只做集合。
    /// </summary>
    private void RefreshToolList()
    {
        _activeTools.Clear();
        _tools.Clear();
        if (_componentHost == null) return;

        foreach (var tool in _componentHost.GetVisibleTools())
        {
            _tools[tool.Name] = tool;
            _activeTools.Add(tool.Name);
        }
        // 合并 ToolRegistry 独立工具（权限组裁决在 AgentCore 装配期 + ToolExecutor 执行期）
        foreach (var (name, tool) in ToolRegistry.All)
        {
            if (!_tools.ContainsKey(name))
            {
                _tools[name] = tool;
                _activeTools.Add(name);
            }
        }
        // Phase 5 后注入的 compact 工具会出现在 _tools；Phase 3 暂不注入，保持空判断安全
        if (_compactTool != null && !_tools.ContainsKey("compact"))
        {
            _tools["compact"] = _compactTool;
            _activeTools.Add("compact");
        }
        // 多核会话的 switch_core 工具（权限裁决在 AgentCore 装配期 + ToolExecutor 执行期）
        if (_switchCoreTool != null && !_tools.ContainsKey(_switchCoreTool.Name))
        {
            _tools[_switchCoreTool.Name] = _switchCoreTool;
            _activeTools.Add(_switchCoreTool.Name);
        }
    }
}
