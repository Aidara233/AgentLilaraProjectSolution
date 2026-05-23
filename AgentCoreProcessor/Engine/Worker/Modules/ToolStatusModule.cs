using System.Linq;
using System.Text;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 工具状态模块。当有工具被禁用时，在动态区域注入提示让模型知道。
    /// </summary>
    internal class ToolStatusModule : EngineModule
    {
        public override string Name => "工具状态";

        public override void Attach(ILoopBus bus) { }
    }
}
