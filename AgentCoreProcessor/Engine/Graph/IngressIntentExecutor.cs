using AgentLilara.GraphRuntime.Graph;

namespace AgentCoreProcessor.Engine.Graph;

/// <summary>
/// 骨架侧的意图执行器：图只产出意图，这里决定怎么执行、怎么记账。
/// <para>只依赖三个委托（投递到会话 / 默认投递 / 发送），不碰 <c>ISystemContext</c>，
/// 因此可以直接用假实现单测——这些规则正是最容易回归的地方。</para>
/// </summary>
internal sealed class IngressIntentExecutor
{
    private readonly HashSet<string> _deliveredKeys;
    private readonly string _deliveryKey;
    private readonly Func<Task<string?>> _deliverDefault;
    private readonly Func<string, Task<string?>> _route;
    private readonly Func<ActionIntent, Task<string?>> _send;

    /// <param name="deliveredKeys">投递账本，与调用方共享（默认路径也用同一份）。</param>
    /// <param name="deliveryKey">本拍的投递键，约定为 <c>频道:消息id</c>。</param>
    /// <param name="deliverDefault">按默认规则投递到频道会话；成功返回 null，失败返回原因。</param>
    /// <param name="route">按会话类型路由；成功返回 null，失败返回原因。</param>
    /// <param name="send">直接发送一条消息；成功返回 null，失败返回原因。</param>
    public IngressIntentExecutor(
        HashSet<string> deliveredKeys,
        string deliveryKey,
        Func<Task<string?>> deliverDefault,
        Func<string, Task<string?>> route,
        Func<ActionIntent, Task<string?>> send)
    {
        _deliveredKeys = deliveredKeys;
        _deliveryKey = deliveryKey;
        _deliverDefault = deliverDefault;
        _route = route;
        _send = send;
    }

    /// <summary>本拍的投递键（诊断用）。</summary>
    public string DeliveryKey => _deliveryKey;

    /// <summary>是否已经有一条投递成功（同一消息只投一次）。</summary>
    public bool AlreadyDelivered => _deliveredKeys.Contains(_deliveryKey);

    /// <summary>执行一组意图，返回失败原因（空集合表示全部成功或无动作）。</summary>
    public async Task<IReadOnlyList<string>> ExecuteAsync(IReadOnlyList<ActionIntent> intents)
    {
        var failures = new List<string>();

        foreach (var intent in intents)
        {
            var failure = await ExecuteOneAsync(intent);
            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        return failures;
    }

    /// <summary>
    /// 默认投递（SessionPrompt 与“未挂图”的默认路径共用）。
    /// <para>去重语义：已投递就跳过；**只有成功才占键**——失败必须留给兜底图重试。</para>
    /// </summary>
    public async Task<string?> DeliverDefaultAsync()
    {
        if (AlreadyDelivered)
        {
            return null;
        }

        var failure = await _deliverDefault();
        if (failure is null)
        {
            _deliveredKeys.Add(_deliveryKey);
        }

        return failure;
    }

    /// <summary>按会话类型路由；去重语义同 <see cref="DeliverDefaultAsync"/>。</summary>
    public async Task<string?> RouteAsync(string? sessionType)
    {
        if (AlreadyDelivered)
        {
            return null;
        }

        var failure = await _route(sessionType ?? string.Empty);
        if (failure is null)
        {
            _deliveredKeys.Add(_deliveryKey);
        }

        return failure;
    }

    private Task<string?> ExecuteOneAsync(ActionIntent intent) => intent.Kind switch
    {
        IntentKind.SendMessage => _send(intent),

        IntentKind.SessionRoute => RouteAsync(
            intent.Args.TryGetValue("sessionType", out var cell) ? cell.As<string>() : null),

        // 历史语义：请骨架按默认规则驱动频道会话
        IntentKind.SessionPrompt => DeliverDefaultAsync(),

        // 目录里声明了但消息链上还没有执行器的动作：跳过即可，不算失败。
        _ => Task.FromResult<string?>(null),
    };
}
