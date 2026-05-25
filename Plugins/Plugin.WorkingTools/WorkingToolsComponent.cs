// Plugins/Plugin.WorkingTools/WorkingToolsComponent.cs
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.WorkingTools;

[Component(Name = "working-tools", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Disabled)]
public class WorkingToolsComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;
    private ThinkingNotesTool? _thinkingNotes;
    private PinboardTool? _pinboard;
    private RetainListTool? _retainList;
    private TaskListTool? _taskList;
    private MarkForReviewTool? _markForReview;

    public override ComponentMeta Meta => new()
    {
        Name = "working-tools",
        Description = "思考笔记、便签板、缓存列表、复盘标记",
        DefaultEnabled = true,
        PromptPriority = 45
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_thinkingNotes != null) yield return _thinkingNotes;
            if (_pinboard != null) yield return _pinboard;
            if (_retainList != null) yield return _retainList;
            if (_taskList != null) yield return _taskList;
            if (_markForReview != null) yield return _markForReview;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;
        _thinkingNotes = new ThinkingNotesTool(context.Storage, context.LoopId);
        _pinboard = new PinboardTool(context.Storage);
        _retainList = new RetainListTool(context.Storage);
        _taskList = new TaskListTool(context.Storage);

        var beacon = context.GetService<IBeaconAccess>();
        if (beacon != null)
            _markForReview = new MarkForReviewTool(beacon);

        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        var sections = new List<string>();

        var notes = _thinkingNotes?.BuildSection();
        if (notes != null) sections.Add(notes);

        var board = _pinboard?.BuildSection();
        if (board != null) sections.Add(board);

        var retain = _retainList?.BuildSection();
        if (retain != null) sections.Add(retain);

        var tasks = _taskList?.BuildSection();
        if (tasks != null) sections.Add(tasks);

        return sections.Count > 0 ? string.Join("\n", sections) : null;
    }
}
