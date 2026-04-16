using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 子 agent 执行记录。由 WorkingCore 管理，记录子 agent 的状态和执行日志。
    /// </summary>
    internal class SubAgentRecord
    {
        public required string Id { get; init; }
        public required string TaskDescription { get; init; }
        public string Status { get; set; } = "running";  // running / completed / failed
        public string? Summary { get; set; }
        public List<string> Log { get; } = new();
        public Task<string>? ExecutionTask { get; set; }
    }
}
