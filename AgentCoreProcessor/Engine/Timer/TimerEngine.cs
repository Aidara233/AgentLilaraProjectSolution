using System.Threading.Tasks;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Engine
{
    internal class TimerEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Timer";

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx) => Task.CompletedTask;

        public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx) => Task.FromResult(false);

        public ISubEngine Create(ISystemContext ctx) => new TimerEngine(ctx);
    }

    /// <summary>
    /// 心跳引擎。定期发布 TimerEvent("tick")，驱动其他引擎的周期性检查。
    /// Phase 8: 监控 SystemEngine 心跳，超时则报警（不触发睡觉）。
    /// </summary>
    internal class TimerEngine : ISubEngine
    {
        public string EngineType => "Timer";
        public bool IsAlive { get; private set; } = true;
        public bool IsInfrastructure => true;

        private readonly ISystemContext ctx;
        private int intervalSeconds = 30;
        private System.DateTime lastSystemHeartbeat = System.DateTime.Now;
        private bool alarmSent = false;
        private CancellationTokenSource? _stopCts;

        public TimerEngine(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public async Task RunAsync()
        {
            _stopCts = new CancellationTokenSource();
            var ct = _stopCts.Token;

            var parentCtx = SignalContext.Current;
            var lifeCtx = Signal.Continue(
                parentCtx?.SignalId ?? Signal.NewId(), parentCtx?.CurrentSpanId,
                "timer:heartbeat", LogGroup.Engine, "引擎运行",
                new { engineType = EngineType });

            try
            {
                while (IsAlive && !ct.IsCancellationRequested)
                {
                    try { await Task.Delay(intervalSeconds * 1000, ct); }
                    catch (OperationCanceledException) { break; }
                    if (!IsAlive) break;

                    // 每次心跳创建独立信号源，下游工作继承此信号
                    using var tickSignal = Signal.Begin(LogGroup.Engine, "timer:heartbeat", "心跳");
                    ctx.EventBus.Publish(new TimerEvent { TimerName = "tick" });

                    // Phase 8: 检查 SystemEngine 心跳
                    if (ctx.HasActiveEngine("System"))
                    {
                        lastSystemHeartbeat = System.DateTime.Now;
                        alarmSent = false;
                    }
                    else
                    {
                        var elapsed = (System.DateTime.Now - lastSystemHeartbeat).TotalHours;
                        if (elapsed > 1.0 && !alarmSent)
                        {
                            await SendSystemCrashAlarmAsync();
                            alarmSent = true;
                        }
                    }
                }
            }
            finally
            {
                IsAlive = false;
                lifeCtx.Close(new { engineType = EngineType, reason = "shutdown" });
            }
        }

        private async Task SendSystemCrashAlarmAsync()
        {
            try
            {
                var allUsers = await ctx.Session.GetAllUsersAsync();
                var admins = allUsers.Where(u => u.PermissionLevel == Database.PermissionLevel.Admin).ToList();

                if (admins.Count == 0) return;

                var allChannels = await ctx.Session.GetAllChannelsAsync();
                var message = "[系统严重故障]\n" +
                              "SystemEngine（系统循环）已停止响应超过 1 小时。\n" +
                              "这是严重事故，需要立即人工介入检查。\n" +
                              "可能原因：崩溃、死锁、资源耗尽。\n\n" +
                              "建议操作：\n" +
                              "1. 检查日志文件\n" +
                              "2. 重启系统\n" +
                              "3. 联系技术支持";

                foreach (var channel in allChannels)
                {
                    try
                    {
                        var parts = channel.Name.Split(':', 2);
                        if (parts.Length != 2) continue;

                        await ctx.Adapters.SendMessageAsync(parts[0], new Adapter.OutgoingMessage
                        {
                            ChannelId = parts[1],
                            Content = message
                        });
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
            }
        }

        public void OnEvent(EngineEvent e)
        {
            // 可响应配置变更信号调整间隔
            if (e is SignalEvent signal && signal.SignalName == "timer-interval"
                && signal.Payload is string s && int.TryParse(s, out var val) && val > 0)
            {
                intervalSeconds = val;
            }
        }

        public void RequestStop()
        {
            IsAlive = false;
            _stopCts?.Cancel();
        }
    }
}
