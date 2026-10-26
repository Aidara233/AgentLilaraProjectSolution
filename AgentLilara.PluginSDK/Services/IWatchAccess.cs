namespace AgentLilara.PluginSDK.Services;

/// <summary>
/// 订阅访问面：让 agent 把"我待会儿发给你个邮件，你盯着点"变成一条框架长期执行的机械规则。
/// <para>只暴露**创建 / 取消 / 列举**——模型不能写图，只能给预制图填参数（D62）。</para>
/// <para>所有操作以频道为单位：订阅绑频道而不绑会话，会话死了下次命中时重建。</para>
/// </summary>
public interface IWatchAccess
{
    WatchResult Create(int channelId, WatchRequest request);

    bool Cancel(int channelId, string watchId);

    IReadOnlyList<WatchInfo> List(int channelId);

    /// <summary>
    /// 当前有哪些信源可用、各自支持哪些字段。
    /// <para>工具描述与错误提示都读它——否则模型只能猜 `source` 该填什么，
    /// 猜错的表现是"订阅成功但永远不命中"。</para>
    /// </summary>
    IReadOnlyList<SourceCapability> DescribeSources();
}
