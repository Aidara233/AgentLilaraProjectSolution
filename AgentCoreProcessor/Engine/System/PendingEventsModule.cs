using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 待处理事件模块。收集跨循环请求，格式化注入 prompt。
    /// </summary>
    internal class PendingEventsModule : EngineModule
    {
        public override string Name => "待处理事件";

        private readonly List<CrossRequest> pendingCrossRequests = new();

        public void SetPendingCrossRequests(List<CrossRequest> requests)
        {
            pendingCrossRequests.Clear();
            pendingCrossRequests.AddRange(requests);
            if (requests.Count > 0)
                Signal.Event(LogGroup.Engine, "系统循环收到委托",
                    new { count = requests.Count, titles = string.Join(", ", requests.Select(r => r.Title)) });
        }

        public override void Attach(ILoopBus bus) { }

        public override Task<string?> BuildRoundInjectAsync(InjectContext ctx)
        {
            if (pendingCrossRequests.Count == 0)
                return Task.FromResult<string?>(null);

            var sb = new StringBuilder();
            sb.AppendLine("[跨循环请求]");
            foreach (var r in pendingCrossRequests)
            {
                var targetStr = r.TargetId ?? "广播";
                sb.AppendLine($"- 请求 request_id={r.RequestId}: {r.Title}");
                sb.AppendLine($"  发起者: {r.InitiatorId} | 目标: {targetStr} | 超时: {r.ExpiresAt:HH:mm:ss}");
                sb.AppendLine($"  内容: {r.Content.Truncate(200)}");
                if (r.Responses.Count > 0)
                {
                    var lastResp = r.Responses.Last();
                    sb.AppendLine($"  最近回应: [{lastResp.Type}] {lastResp.Content.Truncate(100)}");
                }
            }
            sb.AppendLine();

            var text = sb.ToString();
            pendingCrossRequests.Clear();
            return Task.FromResult<string?>(text);
        }
    }

}
