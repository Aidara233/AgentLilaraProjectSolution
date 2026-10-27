// AgentCoreProcessor/Engine/Session/RuntimeSession.CoreSwitch.cs
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
/// <see cref="RuntimeSession"/> 的部件：core-switch（多核白名单与切换）（partial 文件，仅物理拆分，类型仍是一个）。
/// <para>⚠️ 拆文件是为了让一个部件一个文件可读、可对照 <c>session-decomposition.md</c> 的 20 部件表；
/// 真正的类型边界（各自独立类）要到 C 阶段拆 csproj 时才动。</para>
/// </summary>
internal partial class RuntimeSession
{
    private void CaptureCoreSnapshot(SnapshotWriter writer)
    {
        writer.Set(s => s.ActiveCoreConfig, _activeCoreConfig);
    }

    /// <summary>
    /// 配置本 session 可用的 Core 白名单并选定活跃核。取代旧的 SetAgentCore。
    /// 白名单来自会话类型配置（Storage/Session/{type}.json 的 AllowedCores），实例在列表内切换。
    /// 核实例懒建缓存：切核时 get-or-create new AgentCore(name)（fixedMode 锁配置）。
    /// </summary>
    public void ConfigureCores(IReadOnlyList<string>? allowedCoreConfigs, string defaultCore, string? activeOverride = null)
    {
        _allowedCoreConfigs.Clear();
        if (allowedCoreConfigs != null && allowedCoreConfigs.Count > 0)
            _allowedCoreConfigs.AddRange(allowedCoreConfigs);
        if (_allowedCoreConfigs.Count == 0)
            _allowedCoreConfigs.Add(defaultCore);

        var active = activeOverride != null
            && _allowedCoreConfigs.Contains(activeOverride, StringComparer.OrdinalIgnoreCase)
            ? activeOverride : defaultCore;
        if (!_allowedCoreConfigs.Contains(active, StringComparer.OrdinalIgnoreCase))
            active = _allowedCoreConfigs[0];

        _activeCoreConfig = active;
        _agentCore = GetOrCreateCore(active);
        _sessionPrompt ??= BuildSessionPrompt();
        AttachSessionPrompt(_agentCore);

        // 多核时注入 switch_core 工具（channel 黑名单默认放行；单核类型不注入，行为不变）
        if (_allowedCoreConfigs.Count > 1 && _switchCoreTool == null)
        {
            _switchCoreTool = new Tool.Core.SwitchCoreTool(this);
            if (!_tools.ContainsKey(_switchCoreTool.Name))
            {
                _tools[_switchCoreTool.Name] = _switchCoreTool;
                _activeTools.Add(_switchCoreTool.Name);
            }
        }
    }

    /// <summary>会话本体提示词（测试访问器）。</summary>
    internal string? SessionPrompt => _sessionPrompt;

    /// <summary>从会话类型配置的 PromptFiles 加载拼接提示词（Storage/Session/Prompts → Core/Prompts 双目录）。</summary>
    private string? BuildSessionPrompt()
    {
        var profile = SessionTypeProfiles.Get(Type);
        if (profile.PromptFiles == null || profile.PromptFiles.Length == 0) return null;
        var templatesDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates"));
        var parts = new List<string>();
        foreach (var file in profile.PromptFiles)
        {
            var content = PromptLoader.Load(file, PathConfig.StoragePath, templatesDir);
            if (!string.IsNullOrEmpty(content))
                parts.Add(content);
        }
        return parts.Count > 0 ? string.Join("\n\n", parts) : null;
    }

    /// <summary>把会话提示词绑定到核实例（换核不换提示词的关键注入点）。</summary>
    private void AttachSessionPrompt(AgentCore core)
    {
        if (core != null && !string.IsNullOrEmpty(_sessionPrompt))
            core.SessionPrompt = _sessionPrompt;
    }

    /// <summary>当前活跃 Core 配置名。</summary>
    public string ActiveCoreConfig => _activeCoreConfig;

    /// <summary>本 session 可用的 Core 配置名白名单（来自会话类型配置）。</summary>
    public IReadOnlyList<string> AllowedCoreConfigs => _allowedCoreConfigs;

    /// <summary>
    /// 运行时切换活跃 Core。模型（switch_core 工具）与外部（命令/WebUI）共用。
    /// 校验在白名单内 → 换核（懒建缓存）→ 注入系统消息 + 持久化 + 日志。
    /// </summary>
    public bool SwitchCore(string configName)
    {
        if (string.IsNullOrEmpty(configName)
            || !_allowedCoreConfigs.Contains(configName, StringComparer.OrdinalIgnoreCase))
            return false;

        _activeCoreConfig = configName;
        _agentCore = GetOrCreateCore(configName);
        AttachSessionPrompt(_agentCore);

        // 立即重设工具装配（外置切换发生在两轮之间也安全；轮中切换由 roundCore 硬化隔离）
        // ⚠️ H2：不再在切换核时写 _agentCore 的轮次字段；每轮调用的参数由 RunAsync 经
        // AgentInvokeRequest 传入（见下方 BuildInvokeRequest）。这里保留字段写入只为兼容
        // 不经 InvokeAsync 的路径（ChatAsync / 旧调用方），值为"当前默认"。
        _agentCore.EngineType = _componentLoopType;
        _agentCore.AdditionalTools = _tools.Values.ToList();

        // 经 _pendingInjections 注入切换通知：下一轮开头 flush 进 _history 并持久化，
        // 不会像直接 _history.Add 那样被轮内 FormatAssistant 的 _history[^1] 覆盖。
        InjectExternal($"[核心切换] 已切换到 {_activeCoreConfig}，可用核心：{string.Join(", ", _allowedCoreConfigs)}");
        PersistStateAsync().GetAwaiter().GetResult();

        Signal.Event(LogGroup.Engine, "Session 切换 Core",
            new { sessionId = SessionId, core = _activeCoreConfig, allowed = _allowedCoreConfigs.ToArray() });
        return true;
    }

    /// <summary>静默换核（恢复 / compact 承接）：不注入历史消息、不持久化。非法名忽略。</summary>
    internal void SetActiveCore(string configName)
    {
        if (string.IsNullOrEmpty(configName)
            || !_allowedCoreConfigs.Contains(configName, StringComparer.OrdinalIgnoreCase))
            return;
        _activeCoreConfig = configName;
        _agentCore = GetOrCreateCore(configName);
        AttachSessionPrompt(_agentCore);
    }

    /// <summary>按配置名 get-or-create 核实例（懒建：首次切到该核才 new AgentCore）。</summary>
    private AgentCore GetOrCreateCore(string configName)
    {
        if (_coreInstances.TryGetValue(configName, out var existing))
            return existing;
        var core = new AgentCore(configName); // fixedMode 锁配置到该核
        // CallerTag 标识调用来源：Session:{id}（express/working）/ Task:{id}（task）。
        // 落进模型调用 span detail（caller）与 JSON 模型日志，token 统计据此归因。
        core.CallerTag = Type == "task" ? $"Task:{SessionId}" : $"Session:{SessionId}";
        _coreInstances[configName] = core;
        return core;
    }
}
