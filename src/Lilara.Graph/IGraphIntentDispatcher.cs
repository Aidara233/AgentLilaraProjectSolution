using AgentLilara.GraphRuntime.Graph;

namespace AgentCoreProcessor.Engine.Graph;

/// <summary>图动作执行器：把图产生的意图交给 ALP 骨架。图不直接碰适配器或数据库。</summary>
internal interface IGraphIntentDispatcher
{
    Task DispatchAsync(ActionIntent intent, CancellationToken cancellationToken = default);
}
