using System;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Core
{
    internal class PreprocessingCore : CoreBase
    {
        /// <summary>
        /// 对用户消息进行分类。
        /// 返回值：1=聊天, 2=需要额外知识, 3=任务, 4=大型任务。
        /// </summary>
        public async Task<int> ClassifyAsync(string content)
        {
            var result = await GenerateOnceAsync(content);
            result = result.Trim();

            if (int.TryParse(result, out var category) && category >= 1 && category <= 4)
                return category;

            return 1; // 无法解析时默认为聊天
        }
    }
}
