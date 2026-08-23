// Plugins/Plugin.HomeAssistant/HaLoopComponent.cs
using AgentLilara.PluginSDK;
using Plugin.HomeAssistant.Tools;

namespace Plugin.HomeAssistant;

/// <summary>
/// Home Assistant Loop 组件：每个会话装配 6 个工具。
/// BuildPromptSection 注入轻量提示（HA 可用 + 关键工具说明），不拉实时数据（避免每轮请求开销）。
/// 实时查询交给模型按需调用 ha_get_states / ha_list_devices 等。
/// </summary>
[Component(Name = "home-assistant", Scope = ComponentScope.Loop)]
public class HaLoopComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;

    private HaGetStatesTool? _getStates;
    private HaCallServiceTool? _callService;
    private HaSearchEntitiesTool? _searchEntities;
    private HaListDevicesTool? _listDevices;
    private HaTriggerTool? _trigger;
    private HaGetHistoryTool? _getHistory;

    public override ComponentMeta Meta => new()
    {
        Name = "home-assistant",
        Description = "Home Assistant 智能家居：状态查询、服务调用、实体搜索、自动化触发、历史查询",
        DefaultEnabled = true,
        PromptPriority = 45
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_getStates != null) yield return _getStates;
            if (_callService != null) yield return _callService;
            if (_searchEntities != null) yield return _searchEntities;
            if (_listDevices != null) yield return _listDevices;
            if (_trigger != null) yield return _trigger;
            if (_getHistory != null) yield return _getHistory;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;

        var global = HaToolsAccessor.Global
            ?? throw new InvalidOperationException("HaGlobalComponent 未初始化");

        var api = new HaApiClient(global.Http, global.Config);

        _getStates = new HaGetStatesTool(api);
        _callService = new HaCallServiceTool(api);
        _searchEntities = new HaSearchEntitiesTool(api);
        _listDevices = new HaListDevicesTool(api);
        _trigger = new HaTriggerTool(api);
        _getHistory = new HaGetHistoryTool(api);

        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        var global = HaToolsAccessor.Global;
        if (global == null) return null;

        var problem = global.ValidateReady();
        if (problem != null)
            return null; // 未配置就不注入，避免误导

        return "[Home Assistant 可用] 你有 ha_get_states（查状态）/ha_call_service（调服务，如调温）/ha_search_entities（搜实体）/ha_list_devices（列设备）/ha_trigger（触发自动化/脚本）/ha_get_history（查历史）工具。需要时可查询当前家居状态或按要求调节。";
    }
}