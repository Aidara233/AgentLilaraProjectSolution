using AgentLilara.GraphRuntime.Graph;

namespace AgentCoreProcessor.Engine.Graph;

/// <summary>一次兜底尝试的结果。</summary>
internal sealed record FallbackResult(
    bool Attempted,
    int Depth,
    IReadOnlyList<string> Failures);

/// <summary>
/// 兜底层级规则：失败派生错误事件 → 错误图跑一拍 → 到此为止。
/// <para>层级是**固定一跳**，不靠计数器逐层爬：<c>depth</c> 达到 <see cref="MaxDepth"/> 就不再派生新事件。
/// 这样"兜底图自己失败"不会引出第二条、第三条兜底。</para>
/// </summary>
internal sealed class GraphFallbackRunner
{
    /// <summary>允许的最大兜底深度：0 = 原始失败；1 = 兜底那一拍。</summary>
    public const int MaxDepth = 1;

    private readonly Func<int, IReadOnlyList<string>, Task<IReadOnlyList<ActionIntent>>> _evaluate;
    private readonly Func<IReadOnlyList<ActionIntent>, Task<IReadOnlyList<string>>> _execute;

    /// <param name="evaluate">给定深度与失败原因，跑一次错误图，返回意图。</param>
    /// <param name="execute">执行意图，返回失败原因列表。</param>
    public GraphFallbackRunner(
        Func<int, IReadOnlyList<string>, Task<IReadOnlyList<ActionIntent>>> evaluate,
        Func<IReadOnlyList<ActionIntent>, Task<IReadOnlyList<string>>> execute)
    {
        _evaluate = evaluate;
        _execute = execute;
    }

    /// <summary>当前深度是否还允许兜底。</summary>
    public static bool CanFallback(int depth) => depth < MaxDepth;

    /// <summary>
    /// 跑一次兜底。返回的 <see cref="FallbackResult.Failures"/> 是**兜底之后仍然失败**的原因，
    /// 调用方只记录、不再递归。
    /// </summary>
    public async Task<FallbackResult> RunAsync(IReadOnlyList<string> failures, int depth = 0)
    {
        if (!CanFallback(depth))
        {
            // 已到顶：不再派生错误事件，把原失败原样带回，由调用方审计。
            return new FallbackResult(Attempted: false, depth, failures);
        }

        var errorDepth = depth + 1;
        var intents = await _evaluate(errorDepth, failures);
        if (intents.Count == 0)
        {
            return new FallbackResult(Attempted: true, errorDepth, Array.Empty<string>());
        }

        var stillFailing = await _execute(intents);
        return new FallbackResult(Attempted: true, errorDepth, stillFailing);
    }
}
