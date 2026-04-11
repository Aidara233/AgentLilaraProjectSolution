using System.Threading.Tasks;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 表达核心，负责人格化输出。
    /// 两种工作模式：
    /// - 自由模式：直接调用 GenerateOnceAsync(userMessage)，自由发挥回复
    /// - 润色模式：调用 PolishAsync(context, content)，基于上下文润色 WorkingCore 的输出
    /// </summary>
    internal class ExpressCore : CoreBase
    {
        /// <summary>
        /// 润色模式：基于用户原始需求的上下文，润色 WorkingCore 给出的回复内容。
        /// </summary>
        /// <param name="originalRequest">用户的原始需求</param>
        /// <param name="rawContent">WorkingCore 通过说话工具要传达的内容</param>
        public async Task<string> PolishAsync(string originalRequest, string rawContent)
        {
            ResetProcessor();
            var prompt = $"用户需求：{originalRequest}\n回复内容：{rawContent}";
            return await GenerateOnceAsync(prompt);
        }
    }
}
