using AgentCoreProcessor.Adapter;

namespace AgentCoreProcessor.Engine
{
    internal class WorkerEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Worker";

        /// <summary>缓存最近的消息事件，供 Create 使用。</summary>
        private IncomingMessage? pendingMessage;

        public void OnEvent(EngineEvent e, ISystemContext ctx) { }

        public bool ShouldSpawn(EngineEvent e, ISystemContext ctx)
        {
            if (e is MessageEvent msg)
            {
                pendingMessage = msg.Message;
                return true;
            }
            return false;
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            var msg = pendingMessage!;
            pendingMessage = null;
            return new WorkerEngine(ctx, msg);
        }
    }
}
