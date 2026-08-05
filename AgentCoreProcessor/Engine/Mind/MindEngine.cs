using System;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 心智引擎（MindEngine）。前额叶式执行中枢：统合记忆、情绪、可信度仲裁、念头、写闸门。
    /// 持有全部记忆服务：EmotionService（唯一情绪写入者）+ Writer（唯一写入口）
    /// + EvidenceEngine + ConflictService + ReviewQueueService + ReviewStage + RetrieveService。
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

        /// <summary>唯一写入口。</summary>
        public Writer Writer { get; }

        /// <summary>证据聚合（noisy-OR + logit）。</summary>
        public EvidenceEngine Evidence { get; }

        /// <summary>冲突检测 + 建组/入组。</summary>
        public ConflictService Conflicts { get; }

        /// <summary>待验证队列（统一工作源）。</summary>
        public ReviewQueueService Queue { get; }

        /// <summary>检索服务（关键词 + c_actual 排序 + 二跳子证据）。</summary>
        public RetrieveService Retrieve { get; }

        /// <summary>事件接收（框架事件 → L0/L1 落库）。</summary>
        public EventSink Events { get; }

        public MindEngine(ISystemContext ctx)
        {
            _ctx = ctx;

            // 证据引擎（依赖断言 + 冲突组仓库）
            Evidence = new EvidenceEngine(ctx.Claims, ctx.ConflictGroups);

            // 冲突服务
            Conflicts = new ConflictService(
                ctx.Claims, ctx.ConflictGroups, ctx.Keywords, Evidence);

            // 队列
            Queue = new ReviewQueueService(ctx.ReviewQueue);

            // 情绪
            Emotion = new EmotionService(ctx.EmotionSnapshots);

            // 写入口（依赖各服务 + 情绪）
            Writer = new Writer(
                ctx.Claims, ctx.Journals, ctx.Keywords, ctx.FragmentKeywords,
                Conflicts, Evidence, Queue, Emotion);

            // 检索
            Retrieve = new RetrieveService(
                ctx.Claims, ctx.Journals, ctx.Keywords, ctx.FragmentKeywords);

            // 事件接收
            Events = new EventSink(ctx.FrameworkEvents);
        }

        public bool IsAlive => _isAlive;

        public bool IsInfrastructure => true;

        public bool IsBusy => false; // 常驻服务，不主动占用

        public void OnEvent(EngineEvent e)
        {
            // 框架事件 → L0/L1 落库（EventSink）
            if (e is SystemEvent se && Events != null)
            {
                var version = typeof(MindEngine).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
                var pid = Environment.ProcessId;
                Events.HandleSystemEvent(se, version, pid);
            }
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
