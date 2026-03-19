using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AgentCoreProcesser.Core
{
    // 这个核心主要负责人格模拟
    internal class ExpressCore
    {
        public Processer processer = new("expressCore");

        public string Generate()
        {
            string result = "";
            processer.ProcessAsync((response) =>
            {
                result += response.Choices[0].Delta?.ReasoningContent;
                result += response.Choices[0].Delta?.Content;
            }).Wait();
            return result;
        }
    }
}
