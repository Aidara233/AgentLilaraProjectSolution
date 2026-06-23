using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class SystemMonitor : IDisposable
    {
        private readonly MasterEngine engine;
        private readonly AlertService alertService;
        private readonly Timer timer;

        public event Action? OnStateChanged;

        public SystemMonitor(MasterEngine engine, AlertService alertService)
        {
            this.engine = engine;
            this.alertService = alertService;
            timer = new Timer(_ => CollectSnapshot(), null, 1000, 2000);
        }

        private void CollectSnapshot()
        {
            try
            {
                // 收集告警（不再依赖 SystemSnapshot DTO）
                alertService.CollectAlerts();

                OnStateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "系统快照采集失败", new { error = ex.Message });
            }
        }

        public void Dispose()
        {
            timer.Dispose();
        }
    }
}
