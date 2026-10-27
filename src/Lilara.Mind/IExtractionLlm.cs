using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 提取用的轻量 LLM 调用入口（单轮、无工具、无状态）。
    /// 隔离模型客户端细节：本接口由 MindEngine 装配时用现有模型客户端实现
    /// （参考 AgentCoreProcessor.Client 的 IModelClient / CoreBase.GenerateOnceAsync 的单轮调用模式）。
    /// 返回原始文本；null 或非 JSON 由调用方（ExpressExtractor）容错处理。
    /// </summary>
    internal interface IExtractionLlm
    {
        /// <summary>给定上下文（攒批消息的对话文本），返回提取产物 JSON 原始文本。</summary>
        Task<string?> ExtractClaimsAsync(string context);
    }
}
