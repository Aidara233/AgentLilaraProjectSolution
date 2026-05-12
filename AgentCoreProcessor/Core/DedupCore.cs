using System.Text;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 记忆去重核心。输入种子记忆 + 关联集群，输出 merge/discard 决策。
    /// </summary>
    internal class DedupCore : CoreBase
    {
        protected override bool UsePersona => false;

        public async Task<string> DedupAsync(string input)
        {
            ResetProcessor();
            return await GenerateOnceAsync(input);
        }
    }
}
