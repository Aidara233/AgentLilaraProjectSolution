namespace AgentLilara.GraphSandbox.Legacy;

/// <summary>旧代码里"要不要激活会话"的判定结果。</summary>
public enum ActivationReason
{
    None,
    SystemPort,
    Mention,
    WeightAccumulated,
    Timeout,
    ExternalSignal,
}

/// <summary>
/// **从旧代码等价抽出的判定规则**，来源：`AgentCoreProcessor/Engine/Session/RuntimeSession.cs`
/// 的 <c>WaitForActivationAsync</c>（约 1359–1411 行）。
///
/// 原实现是"轮询端口队列"，这里抽成纯函数，目的只有一个：**给新接线图做对照基准**。
/// 覆盖规则 ①②③；④（WaitUntil 到期）与 ⑤（外部唤醒信号）不属于本切片。
/// </summary>
public static class LegacyActivationPolicy
{
    public const int MentionWeight = 100;
    public const int NormalWeight = 5;
    public const int Threshold = 100;

    /// <param name="systemPortHasData">非消息端口（系统注入/定时/信号）是否有待处理数据</param>
    /// <param name="mentionQueueCount">mention 端口（Weight=100）积压条数</param>
    /// <param name="normalQueueCount">normal 端口（Weight=5）积压条数</param>
    public static ActivationReason Decide(
        bool systemPortHasData,
        int mentionQueueCount,
        int normalQueueCount)
    {
        // ① 非消息端口有数据 → 立即
        if (systemPortHasData)
        {
            return ActivationReason.SystemPort;
        }

        // ② mention 端口有数据 → 立即
        if (mentionQueueCount > 0)
        {
            return ActivationReason.Mention;
        }

        // ③ normal 端口累积权重达标（Weight=5 → 20 条）
        if (normalQueueCount * NormalWeight >= Threshold)
        {
            return ActivationReason.WeightAccumulated;
        }

        return ActivationReason.None;
    }
}
