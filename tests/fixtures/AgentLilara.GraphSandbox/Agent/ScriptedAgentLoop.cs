using AgentLilara.GraphModules.Agent;

namespace AgentLilara.GraphSandbox.Agent;

/// <summary>
/// <see cref="IAgentLoop"/> 的**参考实现**：用脚本化的"模型"代替真模型，
/// 走完"多轮 + 工具调用"的完整形状，让图这一侧可以先跑通而不用真实网络。
///
/// 刻意保持确定性：同一输入永远同一回复、同一工具轨迹，便于断言与重放。
/// </summary>
public sealed class ScriptedAgentLoop : IAgentLoop
{
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    /// <summary>会话轨迹：每次调用追加一行，供测试观察"模块内部发生了什么"。</summary>
    public List<string> Journal { get; } = new();

    public string? Model { get; set; }

    public int ToolBudget { get; set; } = 4;

    public string? SystemPrompt { get; set; }

    public int ActiveSessions => _active.Count;

    public string State { get; private set; } = "idle";

    public bool Mounted { get; private set; }

    public bool Killed { get; private set; }

    public Task OnMountAsync()
    {
        Mounted = true;
        return Task.CompletedTask;
    }

    public Task OnKillAsync()
    {
        Killed = true;
        _active.Clear();
        State = "idle";
        return Task.CompletedTask;
    }

    public Task<AgentReply> RunAsync(string sessionRef, string text, CancellationToken ct = default)
    {
        _active.Add(sessionRef);
        State = "busy";
        Journal.Add($"run {sessionRef}: {text}");

        try
        {
            if (ct.IsCancellationRequested)
            {
                State = "cancelled";
                return Task.FromResult(
                    new AgentReply(sessionRef, "已取消", Array.Empty<AgentToolCall>(), AgentUsage.Zero, 0));
            }

            var calls = new List<AgentToolCall>();
            var promptTokens = 8 + text.Length;
            var usage = new AgentUsage(promptTokens, 0, promptTokens);
            string reply;

            if (TryParseAdd(text, out var a, out var b))
            {
                if (ToolBudget <= 0)
                {
                    reply = "预算不足，未调用工具";
                }
                else
                {
                    var result = (a + b).ToString();
                    calls.Add(new AgentToolCall("add", $"{a}+{b}", result, IsError: false));
                    reply = $"= {result}";
                }
            }
            else
            {
                reply = $"收到：{text}";
            }

            usage = usage.Add(new AgentUsage(0, reply.Length, reply.Length));
            Journal.Add($"reply {sessionRef}: {reply}");

            // 一轮对话 + 一轮工具结果 = 两轮；没用工具就是一轮
            var rounds = calls.Count > 0 ? 2 : 1;
            return Task.FromResult(new AgentReply(sessionRef, reply, calls, usage, rounds));
        }
        finally
        {
            _active.Remove(sessionRef);
            if (State == "busy")
            {
                State = "idle";
            }
        }
    }

    public Task<bool> CancelAsync(string sessionRef, CancellationToken ct = default)
    {
        var removed = _active.Remove(sessionRef);
        State = "cancelled";
        Journal.Add($"cancel {sessionRef}: {(removed ? "aborted" : "nothing running")}");
        return Task.FromResult(removed);
    }

    /// <summary>脚本化"模型"：只认 <c>算 a+b</c> 这一种要调工具的输入。</summary>
    private static bool TryParseAdd(string text, out int a, out int b)
    {
        a = 0;
        b = 0;

        var trimmed = text.Trim();
        if (!trimmed.StartsWith("算", StringComparison.Ordinal))
        {
            return false;
        }

        var body = trimmed[1..].Trim();
        var parts = body.Split('+', StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && int.TryParse(parts[0], out a)
               && int.TryParse(parts[1], out b);
    }
}
