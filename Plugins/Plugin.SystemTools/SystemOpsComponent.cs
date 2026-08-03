// Plugins/Plugin.SystemTools/SystemOpsComponent.cs
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

/// <summary>
/// 父侧子 agent 管理组件。挂载于所有 channel 会话（RuntimeSession）。
/// 工具：create_sub_agent / stop_sub_agent / send_instruction / list_sub_agents / wait_sub / get_sub_info。
/// </summary>
[Component(Name = "system-ops", Scope = ComponentScope.Loop)]
public class SystemOpsComponent : LoopComponentBase
{
    private ILoopComponentContext? _ctx;
    private ISubAgentAccess? _subAgents;
    private CreateSubAgentTool? _createTool;
    private StopSubAgentTool? _stopTool;
    private SendInstructionTool? _sendTool;
    private ListSubAgentsTool? _listTool;
    private WaitSubTool? _waitTool;
    private GetSubInfoTool? _getInfoTool;

    public override ComponentMeta Meta => new()
    {
        Name = "system-ops",
        Description = "子 agent 管理（创建/停止/追加指令/列举/等待/查询）",
        DefaultEnabled = true,
        PromptPriority = 50
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_createTool != null) yield return _createTool;
            if (_stopTool != null) yield return _stopTool;
            if (_sendTool != null) yield return _sendTool;
            if (_listTool != null) yield return _listTool;
            if (_waitTool != null) yield return _waitTool;
            if (_getInfoTool != null) yield return _getInfoTool;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;
        _subAgents = context.GetService<ISubAgentAccess>();
        var parentLoopId = context.LoopId;

        if (_subAgents != null)
        {
            _createTool = new CreateSubAgentTool(_subAgents, parentLoopId);
            _stopTool = new StopSubAgentTool(_subAgents);
            _sendTool = new SendInstructionTool(_subAgents);
            _listTool = new ListSubAgentsTool(_subAgents, parentLoopId);
            _waitTool = new WaitSubTool(_subAgents);
            _getInfoTool = new GetSubInfoTool(_subAgents);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 每轮注入活跃子 agent 摘要：让模型感知自己派生的子 agent 当前状态。
    /// 不重复 list_sub_agents 工具调用，仅作上下文提示。
    /// </summary>
    public override string? BuildPromptSection()
    {
        if (_subAgents == null || _ctx == null) return null;
        var list = _subAgents.List(_ctx.LoopId);
        if (list.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("[子 agent 概览]");
        foreach (var info in list)
        {
            var status = info.IsAlive ? "运行中" : "已终止";
            sb.AppendLine($"- {info.SessionId} ({status})");
        }
        sb.AppendLine("若需要更多详情，调用 get_sub_info；等待完成调用 wait_sub。");
        return sb.ToString();
    }
}

/// <summary>
/// 子侧回报组件。挂载于子 agent session，仅含 report_to_parent 工具。
/// SubAgentAccessAdapter.Create 默认组件列表包含本组件，使子 agent 可主动回报。
/// </summary>
[Component(Name = "sub-agent-runtime", Scope = ComponentScope.Loop)]
public class SubAgentRuntimeComponent : LoopComponentBase
{
    private ReportToParentTool? _reportTool;

    public override ComponentMeta Meta => new()
    {
        Name = "sub-agent-runtime",
        Description = "子 agent 回报父会话工具（report_to_parent）",
        DefaultEnabled = true,
        PromptPriority = 60
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_reportTool != null) yield return _reportTool;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        var subAccess = context.GetService<ISubAgentAccess>();
        if (subAccess != null)
            _reportTool = new ReportToParentTool(subAccess, context.LoopId);
        return Task.CompletedTask;
    }

    /// <summary>注入子 agent 启动提醒，告知模型可调用 report_to_parent。</summary>
    public override string? BuildPromptSection()
    {
        return "你是另一个会话创建的子 agent。完成任务后调用 report_to_parent(state=done, content=结果摘要) " +
               "回报父会话。需要中间进度更新也可调用 report_to_parent(state=progress)。";
    }
}