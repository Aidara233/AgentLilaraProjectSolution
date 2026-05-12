using System.Collections.Generic;

namespace AgentCoreProcessor.Tool.Contract.Services
{
    /// <summary>
    /// 引擎管理接口。
    /// </summary>
    public interface IEngineAccess
    {
        /// <summary>获取活跃引擎摘要。</summary>
        List<EngineSummary> GetActiveEngines();

        /// <summary>按类型停止引擎。</summary>
        void RequestStopByType(string engineType);

        /// <summary>查询是否有指定类型的活跃引擎。</summary>
        bool HasActive(string engineType);
    }

    public class EngineSummary
    {
        public string Type { get; set; } = "";
        public int Count { get; set; }
        public bool IsInfrastructure { get; set; }
    }
}
