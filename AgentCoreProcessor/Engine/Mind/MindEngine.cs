using System;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 心智引擎壳（MindEngine）。前额叶式执行中枢：统合记忆、情绪、可信度仲裁、念头、写闸门。
    /// 当前持有 EmotionService（唯一情绪写入者），后续阶段挂 Writer/Conflict/Evidence/Queue/Retrieve 等服务。
    /// 常驻引擎（IsInfrastructure=true，不影响 IsIdle 判定）。
    /// </summary>
    internal class MindEngine : ISubEngine
    {
        public string EngineType => "Mind";

        private readonly ISystemContext _ctx;
        private readonly CancellationTokenSource _cts = new();
        private bool _isAlive = true;

        /// <summary>情绪服务（唯一情绪写入者）。</summary>
        public EmotionService Emotion { get; }

        public MindEngine(ISystemContext ctx)
        {
            _ctx = ctx;
            Emotion = new EmotionService(ctx.EmotionSnapshots);
        }

        public bool IsAlive => _isAlive;

        public bool IsInfrastructure => true;

        public bool IsBusy => false; // 常驻服务，不主动占用

        public void OnEvent(EngineEvent e)
        {
            // 暂不响应事件；后续 EventSink 接入
        }

        public async Task RunAsync()
        {
            try
            {
                // 常驻等待，直到停止请求
                await Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            finally
            {
                _isAlive = false;
            }
        }

        public void RequestStop()
        {
            _isAlive = false;
            _cts.Cancel();
        }
    }
}
