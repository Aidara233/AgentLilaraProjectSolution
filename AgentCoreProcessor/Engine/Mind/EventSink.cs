using System;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 框架事件接收服务（EventSink）。
    /// 订阅事件总线 → 判定 L0/L1 → 落 FrameworkEvents 表。
    /// 首版只落 L0/L1 事件、不带情绪信号；事件 → 情绪目标值映射表暂不做（06 待定）。
    /// 本服务只负责落库：level/type/detail 由调用方决定，不做事件映射判定。
    /// </summary>
    internal class EventSink
    {
        private readonly FrameworkEventRepository _repo;

        public EventSink(FrameworkEventRepository repo) => _repo = repo;

        /// <summary>
        /// 提交一条框架事件（通用入口）。调用方自行决定 level/type/detail，
        /// EventSink 只负责构造实体并落库。
        /// </summary>
        /// <param name="level">记录层级（L0/L1）。</param>
        /// <param name="type">事件类型（ProcessStarted/ProcessStopped/工具失败/超时等）。</param>
        /// <param name="detailJson">结构化详情 JSON（事件上下文与参数）。</param>
        /// <param name="emotionSignalsJson">情绪信号 JSON（L1 可携带，首版暂不传）。</param>
        public Task SubmitEventAsync(
            string level,
            string type,
            string? detailJson = null,
            string? emotionSignalsJson = null)
        {
            var evt = new FrameworkEvent
            {
                Level = level,
                Type = type,
                DetailJson = detailJson,
                EmotionSignalsJson = emotionSignalsJson,
                Timestamp = DateTime.Now,
            };
            return _repo.CreateAsync(evt);
        }

        /// <summary>
        /// 提交 L0 ProcessStarted 框架事件（已定稿）。DetailJson 含 {version, pid}。
        /// </summary>
        public Task SubmitProcessStartedAsync(string version, int pid)
        {
            var detail = new { version, pid };
            return SubmitEventAsync("L0", "ProcessStarted", JsonConvert.SerializeObject(detail));
        }

        /// <summary>
        /// 提交 L0 ProcessStopped 框架事件（已定稿）。DetailJson 含 {version, pid, reason, uptimeMs}。
        /// </summary>
        public Task SubmitProcessStoppedAsync(string version, int pid, string? reason = null, long? uptimeMs = null)
        {
            var detail = new { version, pid, reason, uptimeMs };
            return SubmitEventAsync("L0", "ProcessStopped", JsonConvert.SerializeObject(detail));
        }

        /// <summary>
        /// 响应系统 Started/Stopping，转为对应的 L0 框架事件落库（fire-and-forget）。
        /// version/pid 由调用方提供（事件映射判定不在本服务职责内）。
        /// </summary>
        public void HandleSystemEvent(SystemEvent e, string version, int pid)
        {
            switch (e.Action)
            {
                case SystemAction.Started:
                    _ = SafeSubmitAsync(() => SubmitProcessStartedAsync(version, pid));
                    break;
                case SystemAction.Stopping:
                    _ = SafeSubmitAsync(() => SubmitProcessStoppedAsync(version, pid));
                    break;
            }
        }

        /// <summary>异步落库包装：捕获异常并记录日志，避免 fire-and-forget 静默丢失事件。</summary>
        private async Task SafeSubmitAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "框架事件落库失败", new { error = ex.Message });
            }
        }
    }
}
