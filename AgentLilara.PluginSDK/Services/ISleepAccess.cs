namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 睡眠状态访问接口。
    /// </summary>
    public interface ISleepAccess
    {
        /// <summary>当前睡眠状态。</summary>
        SleepLevel CurrentState { get; }

        /// <summary>设置睡眠状态。</summary>
        void SetState(SleepLevel level);
    }

    public enum SleepLevel
    {
        None = 0,
        Wandering = 1,
        Napping = 2,
        DeepSleep = 3
    }
}
