// Plugins/Plugin.SystemTools/CreateSubAgentTool.cs
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.SystemTools;

[ToolMeta(ContinueLoop = true)]
public class CreateSubAgentTool : ITool
{
    private readonly ISubAgentAccess _subAgents;
    private readonly string _parentLoopId;

    public CreateSubAgentTool(ISubAgentAccess subAgents, string parentLoopId)
    {
        _subAgents = subAgents;
        _parentLoopId = parentLoopId;
    }

    public string Name => "create_sub_agent";
    public string Description =>
        "创建并启动子 agent 执行指定任务。子 agent 异步运行，本工具立即返回。" +
        "子 agent 完成后会自动通知你（系统消息注入），可使用 list_sub_agents / get_sub_info 查询状态、" +
        "send_instruction 追加指令、wait_sub 等待完成。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("instruction", "子 agent 执行指令（建议清晰描述目标、约束、产出格式）", 0),
        new("components", "子 agent 启用的组件列表，逗号分隔（可选）。默认：basic-tools,file-tools,memory-tools,sub-agent-runtime", 1, isRequired: false),
        new("initial_state", "覆盖初始状态文本（可选，默认等于 instruction）。可塞入额外上下文", 2, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var instruction = resolvedInputs.Count > 0 ? resolvedInputs[0] : null;
        var components = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;
        var initialState = resolvedInputs.Count > 2 ? resolvedInputs[2] : null;

        if (string.IsNullOrWhiteSpace(instruction))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "instruction 不能为空" });

        var options = new SubAgentOptions
        {
            ParentLoopId = _parentLoopId
        };

        // 解析 components
        if (!string.IsNullOrWhiteSpace(components))
        {
            options.ComponentIds = components
                .Split(',', ';')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
        }

        if (!string.IsNullOrWhiteSpace(initialState))
            options.InitialState = initialState;

        var info = _subAgents.Create(instruction, options);

        if (string.IsNullOrEmpty(info.SessionId) || !info.IsAlive)
            return Task.FromResult(new ToolResult { Status = "failed", Error = "子 agent 创建失败" });

        var result = new ToolResult
        {
            Status = "success",
            Data = $"子 agent 已创建并启动。\n" +
                   $"- session_id: {info.SessionId}\n" +
                   $"- 父 loopId: {_parentLoopId}\n" +
                   $"提示：使用 send_instruction 可追加指令，wait_sub 可阻塞等待完成，" +
                   $"list_sub_agents 可枚举所有子 agent。子 agent 完成时你将自动收到通知。"
        };
        return Task.FromResult(result);
    }

    public JsonNode GetInputSchema() => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "instruction": { "type": "string", "description": "子 agent 执行指令" },
            "components": { "type": "string", "description": "启用的组件，逗号分隔（可选）" },
            "initial_state": { "type": "string", "description": "覆盖初始状态（可选）" }
        },
        "required": ["instruction"]
    }
    """)!;
}