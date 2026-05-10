namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 系统级睡眠状态（暴露给所有引擎）。
    /// 与 DreamEngine 内部的 SleepLevel 对应但独立，因为 SleepLevel 是实现细节。
    /// </summary>
    public enum SleepState
    {
        None,
        Daydream,
        Nap,
        DeepSleep,
    }
}
