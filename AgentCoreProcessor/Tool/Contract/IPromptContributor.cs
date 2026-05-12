namespace AgentCoreProcessor.Tool.Contract
{
    /// <summary>
    /// 静态插件的 prompt 注入接口。
    /// 实现此接口的插件每轮被引擎回调，返回的内容注入到 prompt 中。
    /// </summary>
    public interface IPromptContributor
    {
        /// <summary>Section 标识（唯一，用于去重和排序）。</summary>
        string SectionKey { get; }

        /// <summary>注入优先级（数字越小越靠前）。</summary>
        int Priority { get; }

        /// <summary>构建本轮要注入的内容。返回 null 表示本轮不注入。</summary>
        string? BuildSection();
    }
}
