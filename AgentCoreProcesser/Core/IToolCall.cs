using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AgentCoreProcesser.Core
{
    internal interface IToolCall
    {
        public string ToolName { get; }

        public string ToolInput { get; }


    }

    public class ToolCall : IToolCall
    {
        public string ToolName { get; private set; }
        public string ToolInput { get; private set; }
        public ToolCall(string toolName, string toolInput)
        {
            ToolName = toolName;
            ToolInput = toolInput;
        }
    }
}
