using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AgentCoreProcesser.Core
{
    // 这个核心主要负责轻量化工作
    internal class WorkingCore
    {
        public Processer processer = new("workingCore");

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
