using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.WebUI.Services;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// DreamEngine 的创建条件检查。仅响应 force-sleep / force-wake 信号。
    /// 定期触发已移除，由外部（WebUI/命令）显式触发。
    /// </summary>
    internal class DreamEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Dream";

        private static string DreamConfigPath =>
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamConfig.json");

        private DreamConfig cfg = DreamConfig.Load(
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamConfig.json"));

        private volatile bool forceFlag = false;

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx)
        {
            if (e is SignalEvent signal)
            {
                switch (signal.SignalName)
                {
                    case "force-sleep":
                        forceFlag = true;
                        break;
                    case "force-wake":
                        activeDreamEngine?.ForceWake(signal.Payload as string ?? "signal");
                        break;
                    case "dream-config":
                        if (signal.Payload is string json)
                        {
                            try
                            {
                                var c = JsonConvert.DeserializeObject<DreamConfig>(json);
                                if (c != null) { cfg = c; cfg.Save(DreamConfigPath); }
                            }
                            catch (Exception ex)
                            {
                                Signal.Warn(LogGroup.Engine, "梦境配置信号解析失败", new { error = ex.Message });
                            }
                        }
                        break;
                }
            }

            return Task.CompletedTask;
        }

        public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            if (forceFlag)
            {
                forceFlag = false;
                return Task.FromResult(!ctx.HasActiveEngine("Dream"));
            }

            return Task.FromResult(false);
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            var engine = new DreamEngine(ctx, this);
            activeDreamEngine = engine;
            return engine;
        }

        // ---- 供 DreamEngine 实例访问 ----

        private DreamEngine? activeDreamEngine;

        internal DreamConfig GetConfig() => cfg;

        internal WebUI.Services.DreamStateSnapshot GetDreamSnapshot(bool hasActiveDream)
        {
            var active = hasActiveDream ? activeDreamEngine : null;
            var lastRec = active?.LastCompletedRecord;
            return new()
            {
                HasActiveDream = hasActiveDream,
                CurrentFragment = active?.CurrentPhase,
                FragmentsCompleted = active?.StepsCompleted ?? 0,
                FragmentsTotal = active?.StepsTotal ?? 0,
                CurrentInputDescription = active?.CurrentInputDescription,
                LastFragmentType = lastRec?.Type,
                LastFragmentSummary = lastRec?.Summary,
                LastFragmentDetails = lastRec?.Details.Select(d => new WebUI.Services.FragmentDetailSnapshot
                {
                    Action = d.Action,
                    MemoryId = d.MemoryId,
                    OldValue = d.OldValue,
                    NewValue = d.NewValue,
                    Note = d.Note
                }).ToList(),
                CompletedFragments = active?.CompletedFragments.ToList(),
                MainBudget = cfg.MainTokenBudget,
                ReserveBudget = cfg.ReserveTokenBudget,
            };
        }

        internal void OnDreamCompleted(int processed)
        {
            activeDreamEngine = null;
        }
    }
}
